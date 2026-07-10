using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace MdViewer;

/// <summary>DocumentTab から MainWindow へ依頼するための最小インターフェース。</summary>
public interface IDocumentTabHost
{
    void OpenFileInNewTab(string path);
    void ShortcutCloseActive();
    void ShortcutNextTab();
    void ShortcutPrevTab();
    void ShortcutOpenFile();
}

/// <summary>
/// 1 タブ = 1 ファイル。専用の WebView2 を保持し、レンダリング・自動リロード・
/// リンク制御・スクロール位置復元・プロセス間ショートカット転送を担う。
/// </summary>
public sealed class DocumentTab : IDisposable
{
    private const string AssetsHost = "assets.mdviewer";
    private const string DocHost = "doc.mdviewer";
    private const string TempHost = "temp.mdviewer";

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
    private bool _initialized;
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

        WebView = new WebView2();
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
        if (!string.IsNullOrEmpty(_driveRoot))
        {
            core.SetVirtualHostNameToFolderMapping(
                DocHost, _driveRoot, CoreWebView2HostResourceAccessKind.Allow);
        }

        core.Settings.AreDefaultContextMenusEnabled = true;
        core.Settings.IsStatusBarEnabled = false;

        core.NavigationStarting += OnNavigationStarting;
        core.NavigationCompleted += OnNavigationCompleted;
        core.NewWindowRequested += OnNewWindowRequested;
        core.WebMessageReceived += OnWebMessageReceived;

        StartWatcher();
        await LoadContentAsync().ConfigureAwait(true);
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
            html = MarkdownRenderer.Render(FilePath, _assetsDirectory);
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

            if (host.Equals(DocHost, StringComparison.OrdinalIgnoreCase))
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
        if (_restoreScrollRaw != null && WebView.CoreWebView2 != null)
        {
            string raw = _restoreScrollRaw;
            _restoreScrollRaw = null;
            if (string.IsNullOrEmpty(raw) || raw == "null")
            {
                return;
            }
            _ = WebView.CoreWebView2.ExecuteScriptAsync($"window.scrollTo(0, {raw});");
        }
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
            if (uri.Host.Equals(DocHost, StringComparison.OrdinalIgnoreCase)
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
            if (!root.TryGetProperty("type", out var typeEl)
                || typeEl.GetString() != "shortcut")
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
        || host.Equals(DocHost, StringComparison.OrdinalIgnoreCase)
        || host.Equals(TempHost, StringComparison.OrdinalIgnoreCase);

    private static bool IsMarkdownPath(string absolutePath)
    {
        string ext = Path.GetExtension(absolutePath);
        return ext.Equals(".md", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".markdown", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>doc 仮想ホスト URL を実ファイルパスへ逆変換する。</summary>
    private string ConvertDocUriToPath(Uri uri)
    {
        var segments = uri.AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(Uri.UnescapeDataString);
        string relative = string.Join(Path.DirectorySeparatorChar, segments);
        return Path.Combine(_driveRoot, relative);
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
