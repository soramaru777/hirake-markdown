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
    /// <summary>ファイルを新タブで開く（既存タブがあればアクティブ化）。line 指定時は該当ソース行へスクロールする。</summary>
    void OpenFileInNewTab(string path, int? line = null);
    void ShortcutCloseActive();
    void ShortcutNextTab();
    void ShortcutPrevTab();
    void ShortcutOpenFile();
    void ShortcutQuickPaste();
    void ShortcutGlobalSearch();
    void ShortcutToggleSidebar();
    void ShortcutExportPdf();
    void ShortcutCycleTheme();
    void ShortcutToggleGraphView();
    void ShortcutToggleStructureView();
    void ShortcutToggleFingerprintView();
    void ShortcutToggleStatsView();
    void OnTabZoomChanged(DocumentTab source, double zoomFactor);
}

/// <summary>
/// 1 タブ = 1 ファイル。専用の WebView2 を保持し、レンダリング・自動リロード・
/// リンク制御・スクロール位置復元・プロセス間ショートカット転送を担う。
/// 文書以外の特殊タブ（グラフビュー等）は LoadContentAsync / StartWatcher を
/// オーバーライドして実装する。
/// </summary>
public class DocumentTab : IDisposable
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
    private int? _pendingScrollLine;
    private string? _lastTempFile;
    private string _effectiveTheme = "light";
    private bool _initialized;
    private bool _initialScrollRestored;
    private bool _zoomHandlerAttached;
    private bool _suppressZoomNotify;
    private bool _disposed;

    // レコメンド解析の世代管理。新しいナビゲーションが始まったら前の解析をキャンセルし、
    // 古い結果が新しいページへ注入されるのを防ぐ（UI スレッドからのみ触る）。
    private CancellationTokenSource? _recommendCts;

    public WebView2 WebView { get; }
    public string FilePath { get; }
    public string FileName { get; protected set; }

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

    protected virtual Task LoadContentAsync()
    {
        if (_disposed)
        {
            return Task.CompletedTask;
        }

        if (!File.Exists(FilePath))
        {
            WebView.CoreWebView2.NavigateToString(
                MarkdownRenderer.RenderMessage($"ファイルが見つかりません: {FilePath}"));
            return Task.CompletedTask;
        }

        string html;
        try
        {
            string theme = RefreshEffectiveTheme();
            html = MarkdownRenderer.Render(FilePath, _assetsDirectory, theme);
        }
        catch (Exception ex)
        {
            WebView.CoreWebView2.NavigateToString(
                MarkdownRenderer.RenderMessage($"読み込みに失敗しました: {ex.Message}"));
            return Task.CompletedTask;
        }

        NavigateHtml(html);
        return Task.CompletedTask;
    }

    /// <summary>設定から実効テーマを再取得し、既定背景色へ反映して返す。</summary>
    protected string RefreshEffectiveTheme()
    {
        _effectiveTheme = SettingsStore.GetEffectiveTheme(SettingsStore.Instance.Theme);
        SetDefaultBackground(_effectiveTheme);
        return _effectiveTheme;
    }

    /// <summary>アセットフォルダ（テンプレート読込などに使用）。</summary>
    protected string AssetsDirectory => _assetsDirectory;

    /// <summary>破棄済みか（派生タブが await 後の続行可否を判定するために使用）。</summary>
    protected bool IsDisposed => _disposed;

    /// <summary>
    /// HTML を表示する。NavigateToString の約 2MB 制限を超える場合は
    /// 一時ファイル + temp 仮想ホスト経由に自動で切り替える。
    /// </summary>
    protected void NavigateHtml(string html)
    {
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

    protected virtual void StartWatcher()
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
            // ソースマップ基盤の {y, line, offset} を取得する（viewer.js 未初期化なら scrollY のみ）。
            // 行ベースで復元すると、文書の途中が編集されても表示位置が維持される。
            string raw = await WebView.CoreWebView2.ExecuteScriptAsync(
                "(function(){try{return typeof window.__mdvGetScrollState==='function'"
                + "?window.__mdvGetScrollState():window.scrollY;}"
                + "catch(e){return window.scrollY;}})()").ConfigureAwait(true);
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

        // バックリンク一覧の注入（バックグラウンド解析。文書ファイルのタブのみ）。
        _ = UpdateBacklinksAsync(core);

        // 関連文書「次に読む」の注入（意味索引が準備済みのときだけ。副作用ゼロ）。
        _ = UpdateRecommendationsAsync(core);

        // 構造クエリ等からの行ジャンプ要求（新タブの初回ロード時）。
        // 保存済みスクロール位置の復元より優先する。
        if (_pendingScrollLine is int pendingLine)
        {
            _pendingScrollLine = null;
            _restoreScrollRaw = null;
            _initialScrollRestored = true;
            InjectScrollToLine(core, pendingLine);
            return;
        }

        // 自動リロード時の位置復元が最優先（既存挙動を維持する）。
        if (_restoreScrollRaw != null)
        {
            string raw = _restoreScrollRaw;
            _restoreScrollRaw = null;
            _initialScrollRestored = true;
            if (!string.IsNullOrEmpty(raw) && raw != "null")
            {
                if (raw.StartsWith("{", StringComparison.Ordinal)
                    && raw.EndsWith("}", StringComparison.Ordinal))
                {
                    // ソース行ベースの復元（{y, line, offset}）。
                    RestoreSavedScrollState(core, raw);
                }
                else if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double y))
                {
                    // 旧来のピクセル位置フォールバック。
                    RestoreSavedScroll(core, y);
                }
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

    /// <summary>
    /// 依存グラフ基盤（LinkGraphService）でバックリンクを解析し、
    /// viewer.js の __mdvSetBacklinks へ注入する。解析はバックグラウンドスレッドで行う。
    /// グラフタブ等、FilePath が実ファイルでないタブでは何もしない。
    /// </summary>
    private async Task UpdateBacklinksAsync(CoreWebView2 core)
    {
        if (!File.Exists(FilePath))
        {
            return;
        }
        string? directory = Path.GetDirectoryName(FilePath);
        if (string.IsNullOrEmpty(directory))
        {
            return;
        }

        string json;
        try
        {
            json = await Task.Run(() =>
            {
                // 親フォルダ側の参照元も拾えるよう、ワークスペースルートから解析する。
                string root = LinkGraphService.FindWorkspaceRoot(directory);
                LinkGraph graph = LinkGraphService.BuildCached(root);
                var list = new List<object>();
                foreach (string path in graph.GetBacklinks(FilePath))
                {
                    list.Add(new { path, name = Path.GetFileName(path) });
                }
                return JsonSerializer.Serialize(list);
            }).ConfigureAwait(true);
        }
        catch
        {
            return; // 解析失敗時はバックリンク非表示のまま（本文表示は妨げない）
        }

        if (_disposed)
        {
            return;
        }

        // json は System.Text.Json の出力（< は < にエスケープ済み）なので安全に埋め込める。
        string script =
            "(function(){try{if(typeof window.__mdvSetBacklinks==='function'){"
            + "window.__mdvSetBacklinks(" + json + ");}}catch(e){}})();";
        _ = core.ExecuteScriptAsync(script);
    }

    /// <summary>
    /// 意味索引が準備済みのルートを、文書のフォルダからワークスペースルートへ向かって探す。
    /// 意味検索の索引は「検索時のアクティブタブのフォルダ」単位で作られるため、
    /// どの階層で索引が作られていても拾えるよう、候補を昇順に集めて広い方から採用する。
    /// 見つからなければ null（レコメンドは何もしない）。
    /// </summary>
    private static string? FindReadyIndexRoot(string directory)
    {
        string workspaceRoot = LinkGraphService.FindWorkspaceRoot(directory);

        var candidates = new List<string>();
        string current = Path.GetFullPath(directory);
        candidates.Add(current);
        while (!string.Equals(current, workspaceRoot, StringComparison.OrdinalIgnoreCase)
               && candidates.Count < 8)
        {
            string? parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent))
            {
                break;
            }
            current = parent;
            candidates.Add(current);
        }

        // 広いスコープ（ワークスペースルート側）を優先して採用する。
        for (int i = candidates.Count - 1; i >= 0; i--)
        {
            if (SemanticIndexService.IsReadyForRecommendations(candidates[i]))
            {
                return candidates[i];
            }
        }
        return null;
    }

    /// <summary>
    /// 意味索引基盤（SemanticIndexService）で表示中文書と似た文書を解析し、
    /// viewer.js の __mdvSetRecommendations へ「次に読む」として注入する。
    /// モデル・索引が未準備なら副作用なく何もしない（ダウンロード・索引構築をしない）。
    /// バックリンクと同様、FilePath が実ファイルでないタブ（グラフタブ等）では何もしない。
    /// </summary>
    private async Task UpdateRecommendationsAsync(CoreWebView2 core)
    {
        // 進行中の解析（前回ナビゲーション分）のキャンセルと世代確立は、早期リターンより
        // 前に必ず行う。後に置くと、対象外ナビゲーション（ファイル消失等）で新しい世代が
        // 立たず、前世代の結果が現在のページへ注入されてしまう。
        // 本メソッドは UI スレッドからのみ呼ばれるため、フィールドの入れ替えに競合はない。
        _recommendCts?.Cancel();
        var cts = new CancellationTokenSource();
        _recommendCts = cts;

        if (!File.Exists(FilePath))
        {
            return;
        }
        string? directory = Path.GetDirectoryName(FilePath);
        if (string.IsNullOrEmpty(directory))
        {
            return;
        }

        string json;
        try
        {
            // 意味索引は「アクティブタブのフォルダ」単位で作られる（横断検索と同じ規則）ため、
            // 文書のフォルダからワークスペースルートまでを順に調べ、索引が準備済みの
            // 最も広いスコープを採用する。どこにも無ければ副作用なく終了する（最重要仕様）。
            string? root = FindReadyIndexRoot(directory);
            if (root == null)
            {
                return;
            }

            List<(string Path, string Name, float Score)> recommendations =
                await SemanticIndexService
                    .GetRecommendationsAsync(root, FilePath, 5, cts.Token)
                    .ConfigureAwait(true);

            var list = new List<object>(recommendations.Count);
            foreach ((string path, string name, float score) in recommendations)
            {
                list.Add(new { path, name, score });
            }
            json = JsonSerializer.Serialize(list);
        }
        catch
        {
            return; // キャンセル・解析失敗時は「次に読む」非表示のまま（本文表示は妨げない）
        }

        // 破棄済み・世代交代済み（新しいナビゲーションが開始済み）なら注入しない。
        if (_disposed || cts.IsCancellationRequested || !ReferenceEquals(_recommendCts, cts))
        {
            return;
        }

        // json は System.Text.Json の出力（< は < にエスケープ済み）なので安全に埋め込める。
        string script =
            "(function(){try{if(typeof window.__mdvSetRecommendations==='function'){"
            + "window.__mdvSetRecommendations(" + json + ");}}catch(e){}})();";
        _ = core.ExecuteScriptAsync(script);
    }

    /// <summary>
    /// ソース行ベースのスクロール復元。stateJson は自前の __mdvGetScrollState が返した
    /// JSON（ExecuteScriptAsync の戻り値そのまま）なので、JS オブジェクトリテラルとして安全に埋め込める。
    /// </summary>
    private static void RestoreSavedScrollState(CoreWebView2 core, string stateJson)
    {
        string script =
            "(function(){var s=" + stateJson + ";"
            + "try{if(typeof window.__mdvRestoreScrollState==='function'){"
            + "window.__mdvRestoreScrollState(s);}else{window.scrollTo(0,(s&&s.y)||0);}}"
            + "catch(e){try{window.scrollTo(0,(s&&s.y)||0);}catch(e2){}}})();";
        _ = core.ExecuteScriptAsync(script);
    }

    /// <summary>
    /// 指定ソース行（1 始まり）へスクロールする。ページ未ロードなら初回ロード完了時に
    /// 適用する（保留）。行が見つからない場合は viewer.js 側のフォールバックで先頭へ。
    /// </summary>
    public void ScrollToSourceLine(int line)
    {
        if (line <= 0 || _disposed)
        {
            return;
        }

        // 自動リロードが飛行中（_restoreScrollRaw 待ち）のときは即時注入すると
        // 直後の位置復元に上書きされるため、保留に回して次のロード完了時に適用する。
        var core = WebView.CoreWebView2;
        if (core != null && _initialScrollRestored && _restoreScrollRaw == null)
        {
            InjectScrollToLine(core, line);
        }
        else
        {
            _pendingScrollLine = line;
        }
    }

    /// <summary>
    /// ソースマップ基盤（viewer.js の __mdvRestoreScrollState）で行位置へスクロールする。
    /// mermaid 描画等でレイアウトが変わっても行位置に追従して再適用される。
    /// </summary>
    private static void InjectScrollToLine(CoreWebView2 core, int line)
    {
        string script =
            "(function(){try{if(typeof window.__mdvRestoreScrollState==='function'){"
            + "window.__mdvRestoreScrollState({line:" + line + ",offset:0});}}catch(e){}})();";
        _ = core.ExecuteScriptAsync(script);
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

            // グラフ/構造クエリビュー等からのファイルオープン要求
            // （{type:'openFile', path} + 省略可能な line で該当行へジャンプ）。
            if (type == "openFile")
            {
                if (root.TryGetProperty("path", out var pathEl)
                    && pathEl.ValueKind == JsonValueKind.String)
                {
                    string? path = pathEl.GetString();
                    if (!string.IsNullOrEmpty(path))
                    {
                        int? line = null;
                        if (root.TryGetProperty("line", out var lineEl)
                            && lineEl.ValueKind == JsonValueKind.Number
                            && lineEl.TryGetInt32(out int lineValue)
                            && lineValue > 0)
                        {
                            line = lineValue;
                        }
                        OpenInNewTab(path, line);
                    }
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
                case "toggleGraphView":
                    _host.ShortcutToggleGraphView();
                    break;
                case "toggleStructureView":
                    _host.ShortcutToggleStructureView();
                    break;
                case "toggleFingerprintView":
                    _host.ShortcutToggleFingerprintView();
                    break;
                case "toggleStatsView":
                    _host.ShortcutToggleStatsView();
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

    private void OpenInNewTab(string path, int? line = null)
    {
        WebView.Dispatcher.BeginInvoke(() => _host.OpenFileInNewTab(path, line));
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

    public virtual void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        // 進行中のレコメンド解析を止める（結果はもう注入されない）。
        try
        {
            _recommendCts?.Cancel();
        }
        catch
        {
            // ignore
        }

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
