using System.IO;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Hirake;

/// <summary>
/// 無限キャンバス・モードのタブ。1 タブ = 1 ワークスペースルート。
/// LinkGraphService（依存グラフ基盤・R5）の解析結果と CanvasLayoutStore の配置を
/// canvas-template.html + canvas.js（d3-zoom / d3-force）で表示する。
/// セマンティックズーム（遠景=ノード+エッジ / 中間=カード）は ADR-0001 の
/// 案B（単一 WebView2 の HTML キャンバス方式）による。
/// ファイル監視は行わない（表示時点のスナップショット）。
/// </summary>
public sealed class CanvasTab : DocumentTab
{
    /// <summary>キャンバスの起点フォルダ（ワークスペースルート・フルパス）。</summary>
    public string RootFolder { get; }

    // タブ破棄時にバックグラウンド解析を打ち切るためのキャンセルソース。
    private readonly CancellationTokenSource _buildCts = new();

    // pid（不透明 ID）→ 実ファイルパス。canvas.js には pid だけを渡し、
    // パス解決はホスト側のこのマップだけで行う（iframe URL に実パスを載せない）。
    // 生成はバックグラウンド、参照は UI スレッド。完成済みの辞書を丸ごと差し替える
    // （参照代入はアトミック）ため、部分構築の辞書が読まれることはない。
    private volatile IReadOnlyDictionary<string, string>? _pidToPath;

    // Ctrl+G（全体俯瞰）で開かれたか。true の場合、保存済みビューポートではなく
    // 全ノードが収まる遠景から開始する。
    private bool _overviewOnLoad;

    // 初回の描画が完了したか（俯瞰要求を JS 実行で送れるかの判定に使う）。
    private bool _contentLoaded;

    // NavigationCompleted の二重購読防止。
    private bool _navigationHooked;

    // ワークスペース復元時に適用するビューポート。初回描画で使い切る。
    // ノードの配置は CanvasLayoutStore の共有値をそのまま使い、
    // 「どこを見ていたか」だけをワークスペース側が持つ（ISSUE #42 設計 7）。
    private CanvasViewState? _initialView;

    // canvas.js から最後に届いたビューポート。ワークスペース保存時に読む。
    // 配置保存メッセージに同梱されて届くため、JS への往復は不要。
    private volatile CanvasViewState? _currentView;

    /// <summary>最後に把握しているビューポート（未取得なら null）。</summary>
    public CanvasViewState? CurrentView => _currentView;

    /// <summary>
    /// 初回描画で使うビューポートを指定する（復元用）。
    /// 描画前にのみ意味があるため、既に描画済みなら <see cref="ApplyView"/> を使う。
    /// </summary>
    public void SetInitialView(CanvasViewState view)
    {
        _initialView = view;
    }

    /// <summary>描画済みのキャンバスへビューポートを適用する。</summary>
    public void ApplyView(CanvasViewState view)
    {
        if (!view.IsValid())
        {
            return;
        }

        if (!_contentLoaded || WebView.CoreWebView2 == null)
        {
            _initialView = view;
            return;
        }

        _ = WebView.CoreWebView2.ExecuteScriptAsync(
            "(function(){try{if(typeof window.__cvSetView==='function'){"
            + $"window.__cvSetView({JsonSerializer.Serialize(view.X)},"
            + $"{JsonSerializer.Serialize(view.Y)},{JsonSerializer.Serialize(view.K)});"
            + "}}catch(e){}})();");
    }

    public CanvasTab(string rootFolder, IDocumentTabHost host, string assetsDirectory, string tempDirectory, bool overview = false)
        : base(rootFolder, host, assetsDirectory, tempDirectory)
    {
        RootFolder = Path.GetFullPath(rootFolder);
        string label = Path.GetFileName(
            RootFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        FileName = "キャンバス: " + (string.IsNullOrEmpty(label) ? RootFolder : label);
        _overviewOnLoad = overview;
    }

    /// <summary>
    /// 全体俯瞰（遠景）へ切り替える。Ctrl+G / グラフボタンから呼ばれる。
    /// ページが描画完了していない間の要求は保留し、NavigationCompleted で適用する
    /// （NavigateHtml 直後は canvas.js がまだ __cvFitToContent を公開していないため、
    ///  そこで実行すると要求が黙って消える）。
    /// </summary>
    public void ShowOverview()
    {
        if (!_contentLoaded || WebView.CoreWebView2 == null)
        {
            _overviewOnLoad = true;
            return;
        }

        ExecuteFitToContent();
    }

    private void ExecuteFitToContent()
    {
        var core = WebView.CoreWebView2;
        if (core == null)
        {
            return;
        }

        _ = core.ExecuteScriptAsync(
            "(function(){try{if(typeof window.__cvFitToContent==='function'){"
            + "window.__cvFitToContent();}}catch(e){}})();");
    }

    // ページ描画の完了を待ってから保留中の俯瞰要求を適用する。
    private void OnCanvasNavigationCompleted(
        object? sender, Microsoft.Web.WebView2.Core.CoreWebView2NavigationCompletedEventArgs e)
    {
        _contentLoaded = true;

        if (!_overviewOnLoad)
        {
            return;
        }
        _overviewOnLoad = false;
        ExecuteFitToContent();
    }

    /// <summary>スナップショット表示のためファイル監視しない。</summary>
    protected override void StartWatcher()
    {
    }

    /// <summary>近景プレビューを表示するのはこのタブだけ（他タブへは公開しない）。</summary>
    protected override bool MapPreviewsHost => true;

    protected override async Task LoadContentAsync()
    {
        string html;
        try
        {
            string theme = RefreshEffectiveTheme();

            bool overview = _overviewOnLoad;
            _overviewOnLoad = false;

            CanvasViewState? initialView = _initialView;
            _initialView = null;

            string dataJson = await Task.Run(
                () => BuildDataJson(RootFolder, overview, initialView, _buildCts.Token))
                .ConfigureAwait(true);

            if (IsDisposed)
            {
                return;
            }

            html = ComposeHtml(dataJson, theme);
        }
        catch (OperationCanceledException)
        {
            return; // タブ破棄によるキャンセル。表示は不要。
        }
        catch (Exception ex)
        {
            if (!IsDisposed && WebView.CoreWebView2 != null)
            {
                WebView.CoreWebView2.NavigateToString(
                    MarkdownRenderer.RenderMessage($"キャンバスの生成に失敗しました: {ex.Message}"));
            }
            return;
        }

        // 描画完了の検知（保留中の俯瞰要求の適用）。購読は 1 回だけ。
        if (!_navigationHooked)
        {
            _navigationHooked = true;
            WebView.NavigationCompleted += OnCanvasNavigationCompleted;
        }

        NavigateHtml(html);
    }

    // 保存要求の直列化（常に最新のスナップショットだけを保存する）。
    // 独立 Task.Run の並走だと Mutex の取得順が保証されず、古い配置が
    // 後勝ちで新しい配置を上書きし得るため、タブ単位の単一ワーカーにする。
    private readonly object _saveGate = new();
    private CanvasLayout? _pendingLayout;
    private bool _saveWorkerRunning;

    /// <summary>canvas.js からの配置保存要求（{type:'canvasLayout', layout:{...}}）。</summary>
    protected override void OnCanvasLayoutMessage(JsonElement message)
    {
        try
        {
            if (!message.TryGetProperty("layout", out JsonElement layoutEl))
            {
                return;
            }

            CanvasLayout? layout = JsonSerializer.Deserialize<CanvasLayout>(layoutEl.GetRawText());
            if (layout == null)
            {
                return;
            }

            // ワークスペース保存で読むため、最新のビューポートを控えておく。
            if (layout.View.IsValid())
            {
                _currentView = layout.View;
            }

            lock (_saveGate)
            {
                _pendingLayout = layout;
                if (_saveWorkerRunning)
                {
                    return; // 実行中のワーカーが最新分を拾う。
                }
                _saveWorkerRunning = true;
            }
            _ = Task.Run(SaveWorker);
        }
        catch
        {
            // 不正な保存要求は無視する（表示は継続）。
        }
    }

    private void SaveWorker()
    {
        string root = RootFolder;
        while (true)
        {
            CanvasLayout layout;
            lock (_saveGate)
            {
                if (_pendingLayout == null)
                {
                    _saveWorkerRunning = false;
                    return;
                }
                layout = _pendingLayout;
                _pendingLayout = null;
            }

            try
            {
                CanvasLayoutStore.Save(root, layout);
            }
            catch
            {
                // 保存失敗は無視（次の要求で再試行される）。
            }
        }
    }

    // ---- 近景プレビュー（#32） ----------------------------------------

    /// <summary>
    /// canvas.js からのプレビュー要求（{type:'canvasPreview', pid, theme}）。
    /// pid → パス解決はホスト側のマップだけで行い、生成した HTML を
    /// previews.hirake 配下のファイル名（不透明 ID）として JS へ返す。
    /// </summary>
    protected override async void OnCanvasPreviewMessage(JsonElement message)
    {
        try
        {
            if (!message.TryGetProperty("pid", out JsonElement pidEl)
                || pidEl.ValueKind != JsonValueKind.String)
            {
                return;
            }

            string? pid = pidEl.GetString();
            if (!CanvasLayoutStore.IsValidPreviewId(pid))
            {
                return; // 形式が不正な pid はマップ照合の前に捨てる。
            }

            string theme = "light";
            if (message.TryGetProperty("theme", out JsonElement themeEl)
                && themeEl.ValueKind == JsonValueKind.String
                && themeEl.GetString() == "dark")
            {
                theme = "dark";
            }

            // 応答の照合トークン（JS が発行する不透明値）。内容は解釈せずそのまま返す。
            // 長さだけは制限し、異常に長い値を埋め込まない。
            string replyToken = string.Empty;
            if (message.TryGetProperty("token", out JsonElement tokenEl)
                && tokenEl.ValueKind == JsonValueKind.String)
            {
                string? raw = tokenEl.GetString();
                if (raw != null && raw.Length <= 64)
                {
                    replyToken = raw;
                }
            }

            // 未知の pid（マップ未構築・古いページからの要求）は静かに無視する。
            IReadOnlyDictionary<string, string>? map = _pidToPath;
            if (map == null || !map.TryGetValue(pid, out string? path))
            {
                return;
            }

            string assets = AssetsDirectory;
            CancellationToken token = _buildCts.Token;

            string? name = await Task.Run(
                () => BuildPreviewFile(pid, path, assets, theme, token), token)
                .ConfigureAwait(true);

            if (name == null || IsDisposed)
            {
                return;
            }

            var core = WebView.CoreWebView2;
            if (core == null)
            {
                return;
            }

            // pid / URL / トークンは JSON 文字列として埋め込む（引用符・制御文字の混入対策）。
            // トークンを返すのは、テーマを素早く切り替えたときに古い世代の応答が後着し、
            // 逆テーマのプレビューで上書きされるのを JS 側で弾けるようにするため。
            string script =
                "(function(){try{if(typeof window.__cvSetPreview==='function'){"
                + "window.__cvSetPreview("
                + JsonSerializer.Serialize(pid) + ","
                + JsonSerializer.Serialize("https://previews.hirake/" + name) + ","
                + JsonSerializer.Serialize(replyToken)
                + ");}}catch(e){}})();";
            _ = core.ExecuteScriptAsync(script);
        }
        catch (OperationCanceledException)
        {
            // タブ破棄によるキャンセル。
        }
        catch
        {
            // プレビューは補助機能。失敗してもカード表示のまま継続する。
        }
    }

    // プレビューを閲覧専用にするための追加スタイル。
    // sandbox（allow-scripts なし）はスクリプトを止めるが、リンクによる iframe 自身の
    // 遷移は止められない。踏むと iframe 内に生の .md が表示されてしまうため、
    // プレビューではリンクのクリックを無効化する（完全操作は通常タブで行う）。
    private const string PreviewStaticStyle =
        "<style>a{pointer-events:none !important;}</style></head>";

    /// <summary>プレビュー用に閲覧専用スタイルを差し込む。</summary>
    private static string MakePreviewStatic(string html)
    {
        int index = html.IndexOf("</head>", StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return html; // テンプレート構造が想定外でも表示は継続する。
        }
        return string.Concat(
            html.AsSpan(0, index), PreviewStaticStyle, html.AsSpan(index + "</head>".Length));
    }

    /// <summary>
    /// プレビュー HTML をキャッシュから取得する（無ければ生成する）。
    /// 戻り値は previews.hirake 配下のファイル名。生成できない場合は null。
    /// </summary>
    private static string? BuildPreviewFile(
        string pid, string documentPath, string assetsDirectory, string theme, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();

        FileInfo info;
        try
        {
            info = new FileInfo(documentPath);
            if (!info.Exists)
            {
                return null;
            }
        }
        catch
        {
            return null;
        }

        long mtime = info.LastWriteTimeUtc.Ticks;
        string name = CanvasLayoutStore.PreviewNameFor(pid, mtime, theme);
        string fullPath = CanvasLayoutStore.PreviewPathFor(pid, mtime, theme);

        if (File.Exists(fullPath))
        {
            // LRU の基準となる最終更新時刻を更新する（掃除で先に消されないように）。
            try
            {
                File.SetLastWriteTimeUtc(fullPath, DateTime.UtcNow);
            }
            catch
            {
                // 触れなくても表示には影響しない。
            }
            return name;
        }

        token.ThrowIfCancellationRequested();

        string html = MakePreviewStatic(MarkdownRenderer.Render(documentPath, assetsDirectory, theme));

        // 生成途中のファイルを iframe に読ませないよう、一時ファイル経由で原子的に置く。
        string tmp = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(CanvasLayoutStore.PreviewsDirectory);
            File.WriteAllText(tmp, html, new System.Text.UTF8Encoding(false));
            File.Move(tmp, fullPath, overwrite: true);
            return name;
        }
        catch
        {
            try
            {
                if (File.Exists(tmp))
                {
                    File.Delete(tmp);
                }
            }
            catch
            {
                // ignore
            }
            return null;
        }
    }

    public override void Dispose()
    {
        if (_navigationHooked)
        {
            _navigationHooked = false;
            try
            {
                WebView.NavigationCompleted -= OnCanvasNavigationCompleted;
            }
            catch
            {
                // ignore
            }
        }

        // 進行中の解析を止めてから既定の破棄処理へ（Cancel のみ・冪等）。
        try
        {
            _buildCts.Cancel();
        }
        catch
        {
            // ignore
        }
        base.Dispose();
    }

    // ---- データ生成 ----------------------------------------------------

    private string BuildDataJson(
        string rootFolder, bool overview, CanvasViewState? initialView, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();

        LinkGraph graph = LinkGraphService.BuildCached(rootFolder, token);
        CanvasLayout layout = CanvasLayoutStore.Load(rootFolder);

        // サムネイル置場の掃除（tmp 残骸 + 容量バジェット LRU）。結果は待たない。
        _ = Task.Run(CanvasLayoutStore.CleanupThumbnails, CancellationToken.None);

        // 近景プレビュー置場の掃除（件数・容量の LRU）。結果は待たない
        // （表示中のプレビューを消さないための猶予は CleanupPreviews 側が持つ）。
        _ = Task.Run(CanvasLayoutStore.CleanupPreviews, CancellationToken.None);

        string folderLabel = Path.GetFileName(
            rootFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrEmpty(folderLabel))
        {
            folderLabel = rootFolder;
        }

        var nodes = new List<object>(graph.Files.Count);
        var pidMap = new Dictionary<string, string>(graph.Files.Count, StringComparer.Ordinal);
        foreach (string file in graph.Files)
        {
            token.ThrowIfCancellationRequested();

            string rel;
            try
            {
                rel = Path.GetRelativePath(rootFolder, file);
            }
            catch
            {
                rel = file;
            }

            CanvasNodeLayout? saved = layout.Files.GetValueOrDefault(rel);

            // サムネイルは「一度タブで開いた文書」から順次貯まる（無ければ null）。
            string? thumb = null;
            try
            {
                string thumbPath = CanvasLayoutStore.ThumbnailPathFor(file);
                var info = new FileInfo(thumbPath);
                if (info.Exists)
                {
                    // mtime をクエリに付けてブラウザキャッシュを無効化する。
                    thumb = CanvasLayoutStore.ThumbnailNameFor(file)
                        + "?t=" + info.LastWriteTimeUtc.Ticks;
                }
            }
            catch
            {
                thumb = null;
            }

            // 近景プレビュー用の不透明 ID。実パスは JS 側へ渡さない。
            string pid = CanvasLayoutStore.PreviewIdFor(file);
            pidMap[pid] = file;

            nodes.Add(new
            {
                id = file,
                rel,
                pid,
                label = Path.GetFileName(file),
                degree = graph.GetLinksFrom(file).Count + graph.GetBacklinks(file).Count,
                thumb,
                x = saved?.X,
                y = saved?.Y,
                pinned = saved?.Pinned ?? false,
            });
        }

        var edges = new List<object>();
        foreach (string file in graph.Files)
        {
            foreach (string target in graph.GetLinksFrom(file))
            {
                edges.Add(new { source = file, target });
            }
        }

        // 完成した辞書を一括で差し替える（部分構築の辞書は公開しない）。
        _pidToPath = pidMap;

        return JsonSerializer.Serialize(new
        {
            root = rootFolder,
            folderLabel,
            // 依存グラフ基盤の走査上限（500 ファイル）に到達している場合は明示する。
            truncated = graph.Files.Count >= FolderSearchService.MaxFiles,
            // Ctrl+G（全体俯瞰）で開かれた場合は保存済みビューポートを使わない。
            overview,
            nodes,
            edges,
            // ワークスペース由来のビューポートがあればそちらを優先する
            // （「どこを見ていたか」はワークスペースごとに違うため）。
            view = overview ? null : ToViewJson(initialView.IsValid() ? initialView : layout.View),
        });
    }

    private static object? ToViewJson(CanvasViewState? view) =>
        view.IsValid() ? new { x = view!.X, y = view.Y, k = view.K } : null;

    private string ComposeHtml(string dataJson, string theme)
    {
        string templatePath = Path.Combine(AssetsDirectory, "canvas-template.html");
        string template = File.ReadAllText(templatePath);

        var replacements = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["TITLE"] = WebUtility.HtmlEncode(FileName),
            ["THEME"] = theme,
            ["DATA"] = dataJson,
        };

        // MarkdownRenderer と同じく単一パス置換（データ内のリテラル {{...}} を再置換しない）。
        return Regex.Replace(
            template,
            @"\{\{(TITLE|THEME|DATA)\}\}",
            match => replacements[match.Groups[1].Value]);
    }
}
