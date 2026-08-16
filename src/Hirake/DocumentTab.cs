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
    void ShortcutToggleCanvasView();
    void ShortcutToggleWorkspaceMenu();
    void ShortcutNewWindow();
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
    private const string ThumbsHost = "thumbs.hirake";
    private const string PreviewsHost = "previews.hirake";

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

    // 一度でもページの読み込みが完了したか。「関数が無い」を初期化待ちとみなすか
    // 本当の異常とみなすかの判定に使う（ISSUE #48）。
    // 表示中のページの読み込みが完了しているか（NavigationStarting で false に戻す）。
    private bool _pageLoaded;

    // 許可した（＝実際に遷移する）最新のトップレベル・ナビゲーションの ID。
    // 完了イベントは開始と同じ ID を持つため、これと突き合わせることで
    // 「キャンセルしたナビゲーションの完了」「追い越された古い完了」を捨てられる。
    // bool 1 つでは A 開始 → B 開始 → A 完了 の交錯を表現できない。
    private ulong? _currentNavigationId;

    // ページの世代。スクリプトの実行開始から完了までの間にページが替わっていないかを見る。
    // すべて UI スレッドからのみ触る（WebView2 のイベントと ConfigureAwait(true)）。
    private int _navigationGeneration;
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

    /// <summary>
    /// hirake:// のディープリンクを作れるタブか（実ファイルを持つ Markdown タブのみ）。
    /// キャンバス・構造クエリ等の仮想タブは FilePath がフォルダなので false になる。
    /// </summary>
    public bool SupportsDeepLink { get; }

    public DocumentTab(string filePath, IDocumentTabHost host, string assetsDirectory, string tempDirectory)
    {
        FilePath = Path.GetFullPath(filePath);
        FileName = Path.GetFileName(FilePath);
        _host = host;
        _assetsDirectory = assetsDirectory;
        _tempDirectory = tempDirectory;
        _driveRoot = Path.GetPathRoot(FilePath) ?? string.Empty;

        try
        {
            string extension = Path.GetExtension(FilePath);
            SupportsDeepLink = File.Exists(FilePath)
                && (extension.Equals(".md", StringComparison.OrdinalIgnoreCase)
                    || extension.Equals(".markdown", StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            SupportsDeepLink = false;
        }

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

        // キャンバスのサムネイル置場（無くても他機能に影響しないため失敗は無視）。
        try
        {
            Directory.CreateDirectory(CanvasLayoutStore.ThumbsDirectory);
            core.SetVirtualHostNameToFolderMapping(
                ThumbsHost, CanvasLayoutStore.ThumbsDirectory, CoreWebView2HostResourceAccessKind.Allow);
        }
        catch
        {
            // ignore
        }

        // キャンバス近景プレビュー HTML の置場（#32）。
        // iframe の src はこのホスト配下の不透明 ID ファイル名のみで、実パスは載せない。
        // プレビューは文書本文そのものを含むため、必要とするタブ（CanvasTab）の
        // WebView だけにマップし、通常文書タブからは参照できないようにする。
        if (MapPreviewsHost)
        {
            try
            {
                Directory.CreateDirectory(CanvasLayoutStore.PreviewsDirectory);
                core.SetVirtualHostNameToFolderMapping(
                    PreviewsHost, CanvasLayoutStore.PreviewsDirectory, CoreWebView2HostResourceAccessKind.Allow);
            }
            catch
            {
                // ignore
            }
        }

        // ドキュメント用: 各固定ドライブを "<ドライブ文字小文字>.doc.hirake" にマップし、
        // 別ドライブへの絶対パスリンクも解決できるようにする。
        MapDocumentDrives(core);

        core.Settings.AreDefaultContextMenusEnabled = true;
        core.Settings.IsStatusBarEnabled = false;

        core.NavigationStarting += OnNavigationStarting;
        core.NavigationCompleted += OnNavigationCompleted;
        core.NewWindowRequested += OnNewWindowRequested;
        core.WebMessageReceived += OnWebMessageReceived;

        // ズーム倍率の復元と変更監視。
        // 復元でズーム倍率バッジを出さないための抑止（ArmHostZoom）は「通知が
        // 必ずハンドラへ届いて消費される」ことが前提なので、購読を先に済ませる。
        // こうすると通知が同期・非同期のどちらで届いても ConsumeHostZoom が
        // 抑止を使い切り、抑止が残留して後のユーザー操作を飲み込むことがない。
        WebView.ZoomFactorChanged += OnZoomFactorChanged;
        _zoomHandlerAttached = true;

        try
        {
            double restored = SettingsStore.Instance.ZoomFactor;
            ArmHostZoom(restored);
            WebView.ZoomFactor = restored;
        }
        catch
        {
            // ズーム適用失敗は無視する。通知が来ないため抑止は解除しておく。
            _hostAppliedZoom = double.NaN;
        }

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
    /// previews.hirake（近景プレビュー HTML）をこのタブの WebView へマップするか。
    /// 既定は false。プレビューは文書本文を含むため、必要な CanvasTab だけが true にする。
    /// </summary>
    protected virtual bool MapPreviewsHost => false;

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

    // PDF の縮小拡大率（CoreWebView2PrintSettings.ScaleFactor）の有効範囲。
    // 範囲外を代入すると ArgumentException になるため、必ずこの範囲へ丸める。
    private const double MinPrintScale = 0.1;
    private const double MaxPrintScale = 2.0;

    /// <summary>
    /// 現在のページを PDF ファイルへ書き出す。成功で true。
    /// 画面のズーム倍率をそのまま出力倍率（ScaleFactor）として適用する
    /// （Chromium の印刷レイアウトは既定ではズームを反映しないため明示的に渡す）。
    /// </summary>
    public async Task<bool> ExportPdfAsync(string path)
    {
        var core = WebView.CoreWebView2;
        if (core == null)
        {
            return false;
        }

        try
        {
            // 画面ズーム倍率を PDF の縮小拡大率として使う。Chromium のズームは
            // 2.0 超も設定できるため、有効範囲へクランプしてから渡す。
            CoreWebView2PrintSettings? settings = null;
            try
            {
                settings = core.Environment.CreatePrintSettings();
                settings.ScaleFactor = Math.Clamp(WebView.ZoomFactor, MinPrintScale, MaxPrintScale);
            }
            catch
            {
                // 印刷設定を作れない場合は既定倍率で出力する（PDF 出力自体は継続）。
                settings = null;
            }

            if (settings != null)
            {
                try
                {
                    return await core.PrintToPdfAsync(path, settings).ConfigureAwait(true);
                }
                catch
                {
                    // ランタイムがカスタム印刷設定を受け付けない場合でも、
                    // 従来（既定設定）の書き出しまで失敗させない。
                }
            }

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

        SafeFireAndForget(ApplyThemeToPageAsync(), "theme");
    }

    /// <summary>
    /// ページ側へテーマを反映する。失敗は診断へ記録する（ISSUE #48）。
    /// </summary>
    private async Task ApplyThemeToPageAsync()
    {
        ScriptResult result = await RunScriptAsync("__mdvSetTheme", _effectiveTheme).ConfigureAwait(true);
        if (result.ShouldRecord)
        {
            Diagnostics.Record("theme", result.Describe("テーマの適用"), result.Detail);
        }
    }

    /// <summary>
    /// 結果を待たない非同期処理を、失敗が消えない形で投げる。
    ///
    /// イベントハンドラの境界（ボタン・キー入力・WebView2 からの転送）で await すると
    /// async void になり、例外が UI スレッドの未処理例外＝エラーダイアログになる。
    /// 見た目だけの操作でそれが出るのは避けたいが、単に捨てると #48 の状態に戻るため、
    /// 失敗を診断へ回してから捨てる。
    /// </summary>
    protected static void SafeFireAndForget(Task task, string category)
    {
        _ = task.ContinueWith(
            t => Diagnostics.Record(category, "非同期処理が失敗しました", t.Exception),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
    }

    // ---- WebView2 へのスクリプト実行（共通） ---------------------------

    /// <summary>
    /// ページ側で公開されている関数を呼び、結果を 3 状態で返す。
    ///
    /// 従来はどの呼び出しも <c>_ = ExecuteScriptAsync(...)</c> で Task を捨てており、
    /// さらにスクリプト側も <c>try{...}catch(e){}</c> で握りつぶしていたため、
    /// ホストからは常に成功に見えていた（ISSUE #48）。
    ///
    /// ここでは呼び出しラッパーをホスト側で組み立て、成功／失敗を JSON で返させる。
    /// スクリプト側の公開関数では例外を握らないこと（握るとホストへ届かない）。
    ///
    /// ■ 対象は同期関数のみ
    /// 関数の戻り値を待たずに <c>{ok:true}</c> を返すため、Promise を返す関数に使うと
    /// 後から起きた失敗を成功と report する。現在の対象（__mdvSetTheme /
    /// __mdvShowZoomBadge）はいずれも同期関数。非同期の公開関数を足すときは、
    /// ラッパーを見直すこと。
    ///
    /// ■ 現在の適用範囲
    /// ISSUE #48 ではテーマとズームバッジの 2 経路だけを載せ替えている。
    /// ExecuteScriptAsync で Task を捨てている箇所は他にも残っており（スクロール復元・
    /// 検索・目次・キャンバスのビューポートなど）、それらは順次この関数へ寄せる。
    /// 「共通」と名乗ってはいるが、全経路が観測可能になったわけではない。
    /// </summary>
    /// <param name="functionName">ページ側の関数名（例: <c>__mdvSetTheme</c>）。</param>
    /// <param name="args">引数。文字列・数値・真偽値のみ。JSON として埋め込む。</param>
    protected async Task<ScriptResult> RunScriptAsync(string functionName, params object?[] args)
    {
        if (!IsValidFunctionName(functionName))
        {
            // 呼び出し側の誤り。名前を JS ソースへ連結するため、書式を厳密に制限する。
            throw new ArgumentException("関数名として使えない文字が含まれています。", nameof(functionName));
        }

        var core = WebView.CoreWebView2;
        if (core == null)
        {
            // 初期化前。テーマ等は初回ロード時にテンプレートへ埋め込まれるため失敗ではない。
            return ScriptResult.Skipped;
        }

        string argList = string.Join(",", args.Select(arg => JsonSerializer.Serialize(arg)));

        // 関数名も文字列として渡し、window[name] で引く（識別子として連結しない）。
        string nameLiteral = JsonSerializer.Serialize(functionName);

        // 関数が無い場合は 'function_missing' という「符号」を返す。
        // 関数自身が投げたメッセージと区別できないと、viewer.js の読み込み失敗や
        // 関数名の変更を初期化待ちと取り違えて永久に見逃す（ISSUE #48）。
        // 呼び出しコンテキストは従来の window.__mdvXxx(...) に合わせる
        // （strict mode の関数では this が undefined になり、挙動が変わるため）。
        string call = argList.Length == 0 ? "f.call(window);" : $"f.call(window,{argList});";

        string script =
            "(function(){try{"
            + $"var f=window[{nameLiteral}];"
            + "if(typeof f!=='function')"
            + "{return JSON.stringify({ok:false,code:'function_missing'});}"
            + call
            + "return JSON.stringify({ok:true});"
            + "}catch(e){return JSON.stringify({ok:false,error:String((e&&e.message)||e)});}})();";

        // 実行前のページ世代。完了時に替わっていたら、判定材料（_pageLoaded）が
        // 実行対象のページのものではなくなるため、結果を採用しない。
        int generation = _navigationGeneration;

        string raw;
        try
        {
            raw = await core.ExecuteScriptAsync(script).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            // 実行中にタブが閉じられた場合の例外は正常な打ち切り。
            return IsDisposed
                ? ScriptResult.Skipped
                : ScriptResult.HostError(ex.GetType().Name + ": " + ex.Message);
        }

        if (IsDisposed || generation != _navigationGeneration)
        {
            return ScriptResult.Skipped;
        }

        return ParseScriptResult(raw, _pageLoaded);
    }

    /// <summary>JS の識別子として安全な名前か。</summary>
    private static bool IsValidFunctionName(string name) =>
        !string.IsNullOrEmpty(name)
        && System.Text.RegularExpressions.Regex.IsMatch(name, @"^[A-Za-z_$][A-Za-z0-9_$]*$");

    /// <summary>
    /// ExecuteScriptAsync の戻り値（JSON 文字列として二重に符号化されている）を解釈する。
    /// </summary>
    /// <param name="pageLoaded">
    /// 一度でもページの読み込みが完了しているか。完了前に関数が無いのは初期化待ちで
    /// 正常だが、完了後に無いのは viewer.js の読み込み失敗や関数名の変更であり異常。
    /// </param>
    private static ScriptResult ParseScriptResult(string? raw, bool pageLoaded)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw == "null")
        {
            return ScriptResult.ScriptError("戻り値なし");
        }

        try
        {
            // 戻り値は「JSON 文字列を JSON 化したもの」。まず外側を外す。
            string? inner = JsonSerializer.Deserialize<string>(raw);
            if (string.IsNullOrWhiteSpace(inner))
            {
                return ScriptResult.ScriptError("戻り値なし");
            }

            using JsonDocument document = JsonDocument.Parse(inner);
            JsonElement root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("ok", out JsonElement okElement))
            {
                return ScriptResult.ScriptError(Truncate(inner));
            }

            if (okElement.ValueKind == JsonValueKind.True)
            {
                return ScriptResult.Ok;
            }

            // 関数が無い場合。読み込み完了前なら初期化待ちで正常、完了後なら異常。
            if (root.TryGetProperty("code", out JsonElement codeElement)
                && codeElement.GetString() == "function_missing")
            {
                return pageLoaded
                    ? ScriptResult.ScriptError("関数が見つかりません")
                    : ScriptResult.Skipped;
            }

            string error = root.TryGetProperty("error", out JsonElement errorElement)
                ? errorElement.GetString() ?? string.Empty
                : string.Empty;

            return ScriptResult.ScriptError(error);
        }
        catch (JsonException)
        {
            return ScriptResult.ScriptError(Truncate(raw));
        }
    }

    private static string Truncate(string value) =>
        value.Length > 200 ? value[..200] : value;

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

    // ホスト側が設定した倍率（起動時の復元・他タブからの同期）。
    // 変更通知が同期で届くか非同期で届くかは WebView2 ラッパーの実装依存のため、
    // 「購読前に代入したから発火しない」ことに頼らず、値でも判定してバッジを抑止する。
    // 1 回の代入につき 1 回だけ抑止する（使い切りで NaN に戻す）ので、ユーザーが
    // 同じ倍率へ戻す操作をした場合はきちんとバッジが出る。
    private double _hostAppliedZoom = double.NaN;

    private void ArmHostZoom(double zoomFactor)
    {
        // 値が変わらない代入では ZoomFactorChanged が発生せず、arm が使われずに
        // 残留する。そのまま残すと、後でユーザーがその倍率へ戻したときに
        // ホスト由来と誤判定してバッジを飲み込むため、その場合は arm しない。
        double current;
        try
        {
            current = WebView.ZoomFactor;
        }
        catch
        {
            current = double.NaN;
        }

        _hostAppliedZoom = !double.IsNaN(current) && Math.Abs(current - zoomFactor) <= 0.0005
            ? double.NaN
            : zoomFactor;
    }

    /// <summary>この変更通知がホスト由来か（＝バッジを出さない）を判定し、抑止を使い切る。</summary>
    private bool ConsumeHostZoom(double zoom)
    {
        if (double.IsNaN(_hostAppliedZoom) || Math.Abs(zoom - _hostAppliedZoom) > 0.0005)
        {
            return false;
        }
        _hostAppliedZoom = double.NaN;
        return true;
    }

    /// <summary>他タブからの通知でズーム倍率を反映する（ホストへ再通知しない）。</summary>
    public void ApplyZoom(double zoomFactor)
    {
        _suppressZoomNotify = true;
        try
        {
            ArmHostZoom(zoomFactor);
            WebView.ZoomFactor = zoomFactor;
        }
        catch
        {
            // ズーム適用失敗は無視する。通知が来ないため抑止は解除しておく。
            _hostAppliedZoom = double.NaN;
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

        // 起動時の復元・他タブ同期による変更はバッジを出さず、再伝播もしない。
        // 通知が非同期に届くと _suppressZoomNotify は既に解除されているため、
        // 値による判定（ConsumeHostZoom）でも同じように打ち切る。
        if (ConsumeHostZoom(zoom) || _suppressZoomNotify)
        {
            return;
        }

        _host.OnTabZoomChanged(this, zoom);
        ShowZoomBadge(zoom);
    }

    /// <summary>
    /// 現在のズーム倍率を整数パーセントのバッジとしてページ右下へ一時表示する。
    /// バッジを持たないページでは何もしない（呼んでから「関数が無い」と気付くと、
    /// 正常な空振りが毎回の診断記録になってしまうため・ISSUE #48）。
    /// </summary>
    private void ShowZoomBadge(double zoom)
    {
        if (!ProvidesZoomBadge)
        {
            return;
        }

        SafeFireAndForget(ShowZoomBadgeAsync(zoom), "zoom");
    }

    /// <summary>
    /// このタブのページが <c>__mdvShowZoomBadge</c> を公開しているか。
    /// ズームバッジは viewer.js だけが持ち、キャンバス・統計などの専用ページは
    /// <c>__mdvSetTheme</c> しか公開していない。
    /// </summary>
    protected virtual bool ProvidesZoomBadge => true;

    private async Task ShowZoomBadgeAsync(double zoom)
    {
        int percent = (int)Math.Round(zoom * 100);
        ScriptResult result = await RunScriptAsync("__mdvShowZoomBadge", percent).ConfigureAwait(true);
        if (result.ShouldRecord)
        {
            Diagnostics.Record("zoom", result.Describe("ズーム倍率バッジの表示"), result.Detail);
        }
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

        // 内容が変わったため、次のロード完了時にサムネイルを撮り直す。
        _thumbnailCaptured = false;

        await LoadContentAsync().ConfigureAwait(true);
    }

    // ---- WebView2 イベント -------------------------------------------

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        DecideNavigation(e);

        if (e.Cancel)
        {
            // 表示中のページはそのまま。公開関数も生きているので状態を触らない。
            // この ID は採用しないため、対応する完了イベントは後で捨てられる。
            return;
        }

        // これから別のページへ移る＝公開関数はいったん無くなる。ここで戻さないと、
        // 自動リロード中に届いたテーマ・ズームを「読み込み後なのに関数が無い」と
        // 誤判定してしまう（ISSUE #48）。
        _pageLoaded = false;

        // リダイレクトは同じ ID で再度ここへ来る。同じページの続きなので世代は進めない。
        if (_currentNavigationId != e.NavigationId)
        {
            _currentNavigationId = e.NavigationId;
            _navigationGeneration++;
        }
    }

    /// <summary>ナビゲーションを許可するか・別タブや既定ブラウザへ回すかを決める。</summary>
    private void DecideNavigation(CoreWebView2NavigationStartingEventArgs e)
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
        // キャンセルしたナビゲーション（.md リンク・外部 URL）や、新しいナビゲーションに
        // 追い越された古いナビゲーションでもここへ来る。表示中のページは変わっていないので、
        // 状態も注入も触らない（触ると、復元待ちのスクロール位置を横取りしてしまう）。
        //
        // 採用済みの ID と完全に一致するものだけを受け入れる。開始を見ていない完了
        // （初期化直後の about:blank など）を救済すると、そこで注入と状態更新が走る。
        // このハンドラは最初のナビゲーションより前に購読しているため、自前の
        // Navigate / NavigateToString は必ず開始側で ID を採用できる。
        if (_currentNavigationId != e.NavigationId)
        {
            return;
        }

        // 読み込みが済んだ＝公開関数が揃っているはずの状態。これ以降に
        // 「関数が無い」のは初期化待ちではなく本当の異常（ISSUE #48）。
        _pageLoaded = e.IsSuccess;

        if (!e.IsSuccess)
        {
            // 打ち切りは失敗ではない。自動リロードが連続すると前のナビゲーションが
            // 中断されるため、これを記録すると通常操作でログが埋まる。
            if (e.WebErrorStatus is not CoreWebView2WebErrorStatus.OperationCanceled
                and not CoreWebView2WebErrorStatus.ConnectionAborted)
            {
                Diagnostics.Record("navigation", "ページの読み込みに失敗しました", e.WebErrorStatus.ToString());
            }

            // 失敗したページに関数は無い。ここで打ち切らないと、以降のテーマ・
            // ズーム操作が二次的な「関数が無い」を量産し、本命の失敗が埋もれる。
            return;
        }

        var core = WebView.CoreWebView2;
        if (core == null)
        {
            return;
        }

        // バックリンク一覧の注入（バックグラウンド解析。文書ファイルのタブのみ）。
        _ = UpdateBacklinksAsync(core);

        // 関連文書「次に読む」の注入（意味索引が準備済みのときだけ。副作用ゼロ）。
        _ = UpdateRecommendationsAsync(core);

        // キャンバス用サムネイルの取得（1 タブ 1 回・失敗は無視）。
        _ = CaptureThumbnailAsync(core);

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
    /// キャンバスビューの配置保存要求（{type:'canvasLayout', ...}）のフック。
    /// 既定は何もしない。CanvasTab がオーバーライドして永続化する。
    /// </summary>
    protected virtual void OnCanvasLayoutMessage(System.Text.Json.JsonElement message)
    {
    }

    /// <summary>
    /// キャンバス近景プレビューの要求（{type:'canvasPreview', pid, theme}）のフック。
    /// 既定は何もしない。CanvasTab がオーバーライドして HTML を供給する。
    /// </summary>
    protected virtual void OnCanvasPreviewMessage(System.Text.Json.JsonElement message)
    {
    }

    // ---- キャンバス用サムネイル ---------------------------------------

    private bool _thumbnailCaptured;
    private bool _thumbnailCapturing;

    // CapturePreviewAsync が返らないケース（撮影中の非表示化等）に備えたタイムアウト。
    private static readonly TimeSpan ThumbnailCaptureTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// 表示中の文書のサムネイルを 1 回だけ撮ってキャンバス用置場へ保存する。
    /// 失敗・非表示（Collapsed のタブでは CapturePreviewAsync が完了しないことがある）は
    /// 静かにスキップし、次回のタブ表示で再試行される。
    /// 自動リロード時は再撮影する（ReloadPreservingScrollAsync がフラグを戻す）。
    /// </summary>
    private async Task CaptureThumbnailAsync(CoreWebView2 core)
    {
        if (_thumbnailCaptured || _thumbnailCapturing || !File.Exists(FilePath))
        {
            return;
        }
        _thumbnailCapturing = true;

        try
        {
            // mermaid / KaTeX 等の描画が落ち着くのを待つ。
            await Task.Delay(1500).ConfigureAwait(true);
            if (_disposed || WebView.CoreWebView2 == null
                || WebView.Visibility != Visibility.Visible)
            {
                return;
            }

            Directory.CreateDirectory(CanvasLayoutStore.ThumbsDirectory);
            string path = CanvasLayoutStore.ThumbnailPathFor(FilePath);
            string tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            FileStream? stream = null;
            try
            {
                stream = File.Create(tmp);
                Task capture = core.CapturePreviewAsync(
                    CoreWebView2CapturePreviewImageFormat.Png, stream);
                Task completed = await Task.WhenAny(
                    capture, Task.Delay(ThumbnailCaptureTimeout)).ConfigureAwait(true);
                if (!ReferenceEquals(completed, capture))
                {
                    // タイムアウト: ストリームを閉じて撮影を失敗させ、例外は観測して捨てる。
                    stream.Dispose();
                    stream = null;
                    _ = capture.ContinueWith(
                        t => _ = t.Exception,
                        TaskContinuationOptions.OnlyOnFaulted);
                    TryDeleteFile(tmp);
                    return;
                }

                await capture.ConfigureAwait(true);
                stream.Dispose();
                stream = null;
                File.Move(tmp, path, overwrite: true);
                _thumbnailCaptured = true;
            }
            catch
            {
                stream?.Dispose();
                TryDeleteFile(tmp);
            }
        }
        catch
        {
            // サムネイルは補助機能。失敗しても本文表示に影響させない。
        }
        finally
        {
            _thumbnailCapturing = false;
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // ignore
        }
    }

    /// <summary>
    /// 現在の表示位置に対応するソース行（1 始まり）を取得する。
    /// 取得できない場合（未初期化・ソースマップなし等）は null。
    /// 「この位置へのリンクをコピー」（hirake:// ディープリンク）で使う。
    /// </summary>
    public async Task<int?> GetCurrentSourceLineAsync()
    {
        var core = WebView.CoreWebView2;
        if (core == null)
        {
            return null;
        }

        try
        {
            // __mdvGetScrollState は {y, line, offset} を返す（スクロール復元と同じ経路）。
            string raw = await core.ExecuteScriptAsync(
                "(function(){try{if(typeof window.__mdvGetScrollState==='function'){"
                + "var s=window.__mdvGetScrollState();"
                + "if(s&&typeof s.line==='number'){return s.line;}}}catch(e){}return null;})()")
                .ConfigureAwait(true);

            if (string.IsNullOrEmpty(raw) || raw == "null")
            {
                return null;
            }
            if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
            {
                return null;
            }

            int line = (int)Math.Round(value);
            return line >= 1 ? line : null;
        }
        catch
        {
            return null;
        }
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

            // キャンバスビューからの配置保存要求（CanvasTab がオーバーライドで処理）。
            if (type == "canvasLayout")
            {
                OnCanvasLayoutMessage(root);
                return;
            }

            // キャンバス近景プレビューの要求（CanvasTab がオーバーライドで処理）。
            if (type == "canvasPreview")
            {
                OnCanvasPreviewMessage(root);
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
                case "toggleCanvasView":
                    _host.ShortcutToggleCanvasView();
                    break;
                case "toggleWorkspaceMenu":
                    _host.ShortcutToggleWorkspaceMenu();
                    break;
                case "newWindow":
                    _host.ShortcutNewWindow();
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
        || host.Equals(ThumbsHost, StringComparison.OrdinalIgnoreCase)
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
