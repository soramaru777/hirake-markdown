using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace Hirake;

/// <summary>DocumentTab から MainWindow へ依頼するための最小インターフェース。</summary>
public interface IDocumentTabHost
{
    void OpenFileInNewTab(string path);
    void ShortcutCloseActive();
    void ShortcutNextTab();
    void ShortcutPrevTab();
    void ShortcutOpenFile();
    void ShortcutQuickPaste();
    void ShortcutGlobalSearch();
    void ShortcutToggleSidebar();
    void ShortcutExportPdf();
    void ShortcutCycleTheme();
    void OnTabZoomChanged(DocumentTab source, double zoomFactor);
}

/// <summary>
/// 1 タブ = 1 ファイル。専用の WebView2 を保持し、レンダリング・自動リロード・
/// リンク制御・スクロール位置復元・プロセス間ショートカット転送を担う。
/// </summary>
public sealed class DocumentTab : IDisposable
{
    private const string AssetsHost = "assets.hirake";
    private const string TempHost = "temp.hirake";

    // doc 仮想ホストの命名規則（"doc.hirake" / "&lt;letter&gt;.doc.hirake"）は
    // MarkdownRenderer を唯一の真実源とし、そこから参照する（重複実装を避ける）。

    // マップ対象ドライブのルートパス列挙はプロセス内で1回だけ行う（タブごとの再列挙を避ける）。
    // IsReady な全種別ドライブを対象にし、ネットワーク/USB への絶対パスリンクも解決可能にする。
    private static readonly Lazy<IReadOnlyList<string>> ReadyDriveRoots =
        new(ComputeReadyDriveRoots);

    // NavigateToString の約 2MB 制限を避けるための閾値。
    private const int NavigateToStringByteLimit = 1_500_000;

    private readonly IDocumentTabHost _host;
    private readonly string _assetsDirectory;
    private readonly string _tempDirectory;
    private readonly string _driveRoot;

    private FileSystemWatcher? _watcher;
    private System.Threading.Timer? _debounceTimer;

    private string? _restoreScrollRaw;
    private string? _lastTempFile;
    private string _effectiveTheme = "light";
    private bool _initialized;
    private bool _initialScrollRestored;
    private bool _zoomHandlerAttached;
    private bool _suppressZoomNotify;
    private bool _disposed;

    public WebView2 WebView { get; }
    public string FilePath { get; }
    public string FileName { get; }

    public DocumentTab(string filePath, IDocumentTabHost host, string assetsDirectory, string tempDirectory)
    {
        FilePath = Path.GetFullPath(filePath);
        FileName = Path.GetFileName(FilePath);
        _host = host;
        _assetsDirectory = assetsDirectory;
        _tempDirectory = tempDirectory;
        _driveRoot = Path.GetPathRoot(FilePath) ?? string.Empty;

        _effectiveTheme = SettingsStore.GetEffectiveTheme(SettingsStore.Instance.Theme);

        WebView = new WebView2();

        // ダーク時の白フラッシュ防止のため、初期化前に既定背景色を設定する。
        SetDefaultBackground(_effectiveTheme);
    }

    public async Task InitializeAsync()
    {
        if (_initialized || _disposed)
        {
            return;
        }
        _initialized = true;

        await EnsureLoadedAsync().ConfigureAwait(true);

        var environment = await App.GetEnvironmentAsync().ConfigureAwait(true);
        await WebView.EnsureCoreWebView2Async(environment).ConfigureAwait(true);

        var core = WebView.CoreWebView2;

        // 仮想ホストマッピング（マップ先フォルダが存在しないと DirectoryNotFoundException になる）。
        Directory.CreateDirectory(_tempDirectory);
        core.SetVirtualHostNameToFolderMapping(
            AssetsHost, _assetsDirectory, CoreWebView2HostResourceAccessKind.Allow);
        core.SetVirtualHostNameToFolderMapping(
            TempHost, _tempDirectory, CoreWebView2HostResourceAccessKind.Allow);

        // ドキュメント用: 各固定ドライブを "<ドライブ文字小文字>.doc.hirake" にマップし、
        // 別ドライブへの絶対パスリンクも解決できるようにする。
        MapDocumentDrives(core);

        core.Settings.AreDefaultContextMenusEnabled = true;
        core.Settings.IsStatusBarEnabled = false;

        core.NavigationStarting += OnNavigationStarting;
        core.NavigationCompleted += OnNavigationCompleted;
        core.NewWindowRequested += OnNewWindowRequested;
        core.WebMessageReceived += OnWebMessageReceived;

        // ズーム倍率の復元と変更監視（初期設定後に購読して初回通知を避ける）。
        try
        {
            WebView.ZoomFactor = SettingsStore.Instance.ZoomFactor;
        }
        catch
        {
            // ズーム適用失敗は無視する。
        }
        WebView.ZoomFactorChanged += OnZoomFactorChanged;
        _zoomHandlerAttached = true;

        // 初期化時点の実効テーマで背景色を確定する。
        SetDefaultBackground(_effectiveTheme);

        StartWatcher();
        await LoadContentAsync().ConfigureAwait(true);
    }

    /// <summary>IsReady な全ドライブのルートパスを列挙する（プロセス内で1回のみ実行）。</summary>
    private static IReadOnlyList<string> ComputeReadyDriveRoots()
    {
        var roots = new List<string>();
        try
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                try
                {
                    // 固定に限らず、ネットワーク/USB 等も含めて Ready なものは対象にする。
                    if (drive.IsReady)
                    {
                        roots.Add(drive.RootDirectory.FullName);
                    }
                }
                catch
                {
                    // 個別ドライブの参照失敗（IsReady の例外含む）は無視して続行する。
                }
            }
        }
        catch
        {
            // ドライブ列挙自体の失敗は無視する。
        }
        return roots;
    }

    /// <summary>Ready な各ドライブを "&lt;letter&gt;.doc.hirake" にマップする。</summary>
    private void MapDocumentDrives(CoreWebView2 core)
    {
        var mapped = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void MapDrive(string? root)
        {
            if (string.IsNullOrEmpty(root) || !char.IsLetter(root[0]))
            {
                return;
            }
            string host = MarkdownRenderer.BuildDocHost(root[0]);
            if (!mapped.Add(host))
            {
                return;
            }
            try
            {
                core.SetVirtualHostNameToFolderMapping(
                    host, root, CoreWebView2HostResourceAccessKind.Allow);
            }
            catch
            {
                // 未準備ドライブ等のマップ失敗は無視する。
            }
        }

        // プロセス内キャッシュ済みの Ready ドライブ群をマップする（列挙は1回のみ）。
        foreach (var root in ReadyDriveRoots.Value)
        {
            MapDrive(root);
        }

        // 自ファイルのドライブは必ずマップする（列挙後にドライブが増えた場合の保険）。
        MapDrive(_driveRoot);

        // 旧 doc.hirake（サブドメイン無し）の後方互換マッピング。
        if (!string.IsNullOrEmpty(_driveRoot))
        {
            try
            {
                core.SetVirtualHostNameToFolderMapping(
                    MarkdownRenderer.DocHost, _driveRoot, CoreWebView2HostResourceAccessKind.Allow);
            }
            catch
            {
                // 無視する。
            }
        }
    }

    private Task EnsureLoadedAsync()
    {
        if (WebView.IsLoaded)
        {
            return Task.CompletedTask;
        }

        var tcs = new TaskCompletionSource();
        void Handler(object s, RoutedEventArgs e)
        {
            WebView.Loaded -= Handler;
            tcs.TrySetResult();
        }
        WebView.Loaded += Handler;
        return tcs.Task;
    }

    private async Task LoadContentAsync()
    {
        if (_disposed)
        {
            return;
        }

        if (!File.Exists(FilePath))
        {
            WebView.CoreWebView2.NavigateToString(
                MarkdownRenderer.RenderMessage($"ファイルが見つかりません: {FilePath}"));
            return;
        }

        string html;
        try
        {
            _effectiveTheme = SettingsStore.GetEffectiveTheme(SettingsStore.Instance.Theme);
            SetDefaultBackground(_effectiveTheme);
            html = MarkdownRenderer.Render(FilePath, _assetsDirectory, _effectiveTheme);
        }
        catch (Exception ex)
        {
            WebView.CoreWebView2.NavigateToString(
                MarkdownRenderer.RenderMessage($"読み込みに失敗しました: {ex.Message}"));
            return;
        }

        int byteCount = Encoding.UTF8.GetByteCount(html);
        if (byteCount <= NavigateToStringByteLimit)
        {
            WebView.CoreWebView2.NavigateToString(html);
        }
        else
        {
            // 2MB 制限超過時は一時ファイルへ書き出して temp 仮想ホストから読み込む。
            NavigateViaTempFile(html);
        }
    }

    private void NavigateViaTempFile(string html)
    {
        try
        {
            Directory.CreateDirectory(_tempDirectory);
            string name = Guid.NewGuid().ToString("N") + ".html";
            string fullPath = Path.Combine(_tempDirectory, name);
            File.WriteAllText(fullPath, html, new UTF8Encoding(false));

            DeleteLastTempFile();
            _lastTempFile = fullPath;

            WebView.CoreWebView2.Navigate($"https://{TempHost}/{name}");
        }
        catch (Exception ex)
        {
            WebView.CoreWebView2.NavigateToString(
                MarkdownRenderer.RenderMessage($"表示に失敗しました: {ex.Message}"));
        }
    }

    private void DeleteLastTempFile()
    {
        if (_lastTempFile != null)
        {
            try
            {
                if (File.Exists(_lastTempFile))
                {
                    File.Delete(_lastTempFile);
                }
            }
            catch
            {
                // ignore
            }
            _lastTempFile = null;
        }
    }

    // ---- 印刷 ---------------------------------------------------------

    /// <summary>
    /// ブラウザの印刷プレビュー（プリンタ選択・PDF保存可）を表示する。
    /// viewer.js 側の __mdvPrint がスクロールバー等を退避してから window.print() を
    /// 呼ぶ。未定義のページ（エラー表示等）では ShowPrintUI にフォールバックする。
    /// </summary>
    public async void ShowPrintUI()
    {
        var core = WebView.CoreWebView2;
        if (core == null)
        {
            return;
        }

        try
        {
            string hasHelper = await core
                .ExecuteScriptAsync("typeof window.__mdvPrint === 'function'")
                .ConfigureAwait(true);
            if (hasHelper == "true")
            {
                _ = core.ExecuteScriptAsync("window.__mdvPrint();");
                return;
            }
        }
        catch
        {
            // スクリプト実行に失敗した場合はフォールバックへ。
        }

        core.ShowPrintUI(CoreWebView2PrintDialogKind.Browser);
    }

    /// <summary>現在のページを PDF ファイルへ書き出す。成功で true。</summary>
    public async Task<bool> ExportPdfAsync(string path)
    {
        var core = WebView.CoreWebView2;
        if (core == null)
        {
            return false;
        }

        try
        {
            return await core.PrintToPdfAsync(path, null).ConfigureAwait(true);
        }
        catch
        {
            return false;
        }
    }

    // ---- テーマ -------------------------------------------------------

    /// <summary>実効テーマ（"light"/"dark"）をページと既定背景色へ反映する。</summary>
    public void ApplyTheme(string effectiveTheme)
    {
        _effectiveTheme = effectiveTheme == "dark" ? "dark" : "light";
        SetDefaultBackground(_effectiveTheme);

        var core = WebView.CoreWebView2;
        if (core == null)
        {
            return;
        }

        string theme = _effectiveTheme;
        _ = core.ExecuteScriptAsync(
            "(function(){try{if(typeof window.__mdvSetTheme==='function'){window.__mdvSetTheme('"
            + theme + "');}}catch(e){}})();");
    }

    private void SetDefaultBackground(string effectiveTheme)
    {
        try
        {
            WebView.DefaultBackgroundColor = effectiveTheme == "dark"
                ? System.Drawing.Color.FromArgb(0x1E, 0x1E, 0x1E)
                : System.Drawing.Color.White;
        }
        catch
        {
            // 背景色設定失敗は無視する。
        }
    }

    // ---- ズーム -------------------------------------------------------

    /// <summary>他タブからの通知でズーム倍率を反映する（ホストへ再通知しない）。</summary>
    public void ApplyZoom(double zoomFactor)
    {
        _suppressZoomNotify = true;
        try
        {
            WebView.ZoomFactor = zoomFactor;
        }
        catch
        {
            // ズーム適用失敗は無視する。
        }
        finally
        {
            _suppressZoomNotify = false;
        }
    }

    private void OnZoomFactorChanged(object? sender, EventArgs e)
    {
        double zoom = WebView.ZoomFactor;
        SettingsStore.Instance.ZoomFactor = zoom;

        if (_suppressZoomNotify)
        {
            return;
        }
        _host.OnTabZoomChanged(this, zoom);
    }

    // ---- 自動リロード -------------------------------------------------

    private void StartWatcher()
    {
        string? directory = Path.GetDirectoryName(FilePath);
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
        {
            return;
        }

        _watcher = new FileSystemWatcher(directory, FileName)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            EnableRaisingEvents = true,
        };
        _watcher.Changed += OnFileChanged;
        _watcher.Created += OnFileChanged;
        _watcher.Deleted += OnFileChanged;
        _watcher.Renamed += OnFileRenamed;
    }

    private void OnFileChanged(object sender, FileSystemEventArgs e) => ScheduleReload();

    private void OnFileRenamed(object sender, RenamedEventArgs e) => ScheduleReload();

    private void ScheduleReload()
    {
        // 300ms デバウンス。連続イベントを 1 回のリロードに束ねる。
        _debounceTimer ??= new System.Threading.Timer(_ => OnDebounceElapsed());
        _debounceTimer.Change(300, Timeout.Infinite);
    }

    private void OnDebounceElapsed()
    {
        if (_disposed)
        {
            return;
        }

        WebView.Dispatcher.BeginInvoke(async () =>
        {
            if (_disposed || WebView.CoreWebView2 == null)
            {
                return;
            }
            await ReloadPreservingScrollAsync().ConfigureAwait(true);
        });
    }

    private async Task ReloadPreservingScrollAsync()
    {
        try
        {
            string raw = await WebView.CoreWebView2.ExecuteScriptAsync("window.scrollY").ConfigureAwait(true);
            _restoreScrollRaw = raw;
        }
        catch
        {
            _restoreScrollRaw = null;
        }

        await LoadContentAsync().ConfigureAwait(true);
    }

    // ---- WebView2 イベント -------------------------------------------

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        string uriString = e.Uri;
        if (!Uri.TryCreate(uriString, UriKind.Absolute, out var uri))
        {
            return;
        }

        // 仮想ホスト（assets/temp）や doc の非 .md リソースは通常どおり許可する。
        if (uri.Scheme is "http" or "https")
        {
            string host = uri.Host;

            if (IsDocHost(host))
            {
                if (IsMarkdownPath(uri.AbsolutePath))
                {
                    e.Cancel = true;
                    string realPath = ConvertDocUriToPath(uri);
                    OpenInNewTab(realPath);
                }
                return;
            }

            if (IsInternalHost(host))
            {
                return;
            }

            // 外部 URL は既定ブラウザで開く。
            e.Cancel = true;
            OpenExternal(uriString);
        }
    }

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        var core = WebView.CoreWebView2;
        if (core == null)
        {
            return;
        }

        // 自動リロード時の位置復元が最優先（既存挙動を維持する）。
        if (_restoreScrollRaw != null)
        {
            string raw = _restoreScrollRaw;
            _restoreScrollRaw = null;
            _initialScrollRestored = true;
            if (!string.IsNullOrEmpty(raw) && raw != "null"
                && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double y))
            {
                // 初回ロードと同じ復元経路を使い、mermaid 再描画後のレイアウト変動にも追従させる。
                RestoreSavedScroll(core, y);
            }
            return;
        }

        // 初回ロード時のみ、保存済みスクロール位置があれば復元する。
        if (!_initialScrollRestored)
        {
            _initialScrollRestored = true;
            double? saved = SettingsStore.Instance.GetScrollPosition(FilePath);
            if (saved.HasValue && saved.Value > 0)
            {
                RestoreSavedScroll(core, saved.Value);
            }
        }
    }

    private static void RestoreSavedScroll(CoreWebView2 core, double y)
    {
        string value = y.ToString(CultureInfo.InvariantCulture);
        // __mdvRestoreScroll が未定義でも window.scrollTo にフォールバックする。
        string script =
            "(function(){try{if(typeof window.__mdvRestoreScroll==='function'){"
            + "window.__mdvRestoreScroll(" + value + ");}else{window.scrollTo(0," + value + ");}}"
            + "catch(e){try{window.scrollTo(0," + value + ");}catch(e2){}}})();";
        _ = core.ExecuteScriptAsync(script);
    }

    private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;

        if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri))
        {
            return;
        }

        if (uri.Scheme is "http" or "https")
        {
            if (IsDocHost(uri.Host)
                && IsMarkdownPath(uri.AbsolutePath))
            {
                OpenInNewTab(ConvertDocUriToPath(uri));
                return;
            }

            if (IsInternalHost(uri.Host))
            {
                return;
            }

            OpenExternal(e.Uri);
        }
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var typeEl))
            {
                return;
            }

            string? type = typeEl.GetString();

            // スクロール位置の保存（viewer.js 側で 300ms デバウンス済み）。
            if (type == "scroll")
            {
                if (root.TryGetProperty("y", out var yEl)
                    && yEl.ValueKind == JsonValueKind.Number
                    && yEl.TryGetDouble(out double y))
                {
                    SettingsStore.Instance.UpdateScrollPosition(FilePath, y);
                }
                return;
            }

            if (type != "shortcut")
            {
                return;
            }

            if (!root.TryGetProperty("action", out var actionEl))
            {
                return;
            }

            switch (actionEl.GetString())
            {
                case "closeTab":
                    _host.ShortcutCloseActive();
                    break;
                case "nextTab":
                    _host.ShortcutNextTab();
                    break;
                case "prevTab":
                    _host.ShortcutPrevTab();
                    break;
                case "openFile":
                    _host.ShortcutOpenFile();
                    break;
                case "quickPaste":
                    _host.ShortcutQuickPaste();
                    break;
                case "globalSearch":
                    _host.ShortcutGlobalSearch();
                    break;
                case "toggleSidebar":
                    _host.ShortcutToggleSidebar();
                    break;
                case "exportPdf":
                    _host.ShortcutExportPdf();
                    break;
                case "cycleTheme":
                    _host.ShortcutCycleTheme();
                    break;
            }
        }
        catch (JsonException)
        {
            // 不正な JSON は無視する。
        }
    }

    // ---- ヘルパー -----------------------------------------------------

    private static bool IsInternalHost(string host) =>
        host.Equals(AssetsHost, StringComparison.OrdinalIgnoreCase)
        || host.Equals(TempHost, StringComparison.OrdinalIgnoreCase)
        || IsDocHost(host);

    /// <summary>doc.hirake 本体、または "&lt;letter&gt;.doc.hirake" を内部ホストとみなす。</summary>
    private static bool IsDocHost(string host) =>
        host.Equals(MarkdownRenderer.DocHost, StringComparison.OrdinalIgnoreCase)
        || host.EndsWith("." + MarkdownRenderer.DocHost, StringComparison.OrdinalIgnoreCase);

    private static bool IsMarkdownPath(string absolutePath)
    {
        string ext = Path.GetExtension(absolutePath);
        return ext.Equals(".md", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".markdown", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>doc 仮想ホスト URL を実ファイルパスへ逆変換する。</summary>
    private string ConvertDocUriToPath(Uri uri)
    {
        string root = GetDriveRootFromHost(uri.Host);
        var segments = uri.AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(Uri.UnescapeDataString);
        string relative = string.Join(Path.DirectorySeparatorChar, segments);
        return Path.Combine(root, relative);
    }

    /// <summary>
    /// ホスト名からドライブルートを求める。
    /// "&lt;letter&gt;.doc.hirake" → "&lt;LETTER&gt;:\"、
    /// 旧 "doc.hirake" は表示ファイルのドライブルート（後方互換）。
    /// </summary>
    private string GetDriveRootFromHost(string host)
    {
        if (MarkdownRenderer.TryGetDriveFromDocHost(host, out char drive))
        {
            return drive + ":\\";
        }
        return _driveRoot;
    }

    private void OpenInNewTab(string path)
    {
        WebView.Dispatcher.BeginInvoke(() => _host.OpenFileInNewTab(path));
    }

    private static void OpenExternal(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            // ブラウザ起動失敗は握りつぶす（ビューアを止めない）。
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        try
        {
            if (_watcher != null)
            {
                _watcher.EnableRaisingEvents = false;
                _watcher.Changed -= OnFileChanged;
                _watcher.Created -= OnFileChanged;
                _watcher.Deleted -= OnFileChanged;
                _watcher.Renamed -= OnFileRenamed;
                _watcher.Dispose();
                _watcher = null;
            }
        }
        catch
        {
            // ignore
        }

        try
        {
            _debounceTimer?.Dispose();
            _debounceTimer = null;
        }
        catch
        {
            // ignore
        }

        try
        {
            if (_zoomHandlerAttached)
            {
                WebView.ZoomFactorChanged -= OnZoomFactorChanged;
                _zoomHandlerAttached = false;
            }

            if (WebView.CoreWebView2 != null)
            {
                WebView.CoreWebView2.NavigationStarting -= OnNavigationStarting;
                WebView.CoreWebView2.NavigationCompleted -= OnNavigationCompleted;
                WebView.CoreWebView2.NewWindowRequested -= OnNewWindowRequested;
                WebView.CoreWebView2.WebMessageReceived -= OnWebMessageReceived;
            }
            WebView.Dispose();
        }
        catch
        {
            // ignore
        }

        DeleteLastTempFile();
    }
}
