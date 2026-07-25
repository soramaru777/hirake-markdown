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

    public CanvasTab(string rootFolder, IDocumentTabHost host, string assetsDirectory, string tempDirectory)
        : base(rootFolder, host, assetsDirectory, tempDirectory)
    {
        RootFolder = Path.GetFullPath(rootFolder);
        string label = Path.GetFileName(
            RootFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        FileName = "キャンバス: " + (string.IsNullOrEmpty(label) ? RootFolder : label);
    }

    /// <summary>スナップショット表示のためファイル監視しない。</summary>
    protected override void StartWatcher()
    {
    }

    protected override async Task LoadContentAsync()
    {
        string html;
        try
        {
            string theme = RefreshEffectiveTheme();

            string dataJson = await Task.Run(
                () => BuildDataJson(RootFolder, _buildCts.Token))
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

    public override void Dispose()
    {
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

    private static string BuildDataJson(string rootFolder, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();

        LinkGraph graph = LinkGraphService.BuildCached(rootFolder, token);
        CanvasLayout layout = CanvasLayoutStore.Load(rootFolder);

        // サムネイル置場の掃除（tmp 残骸 + 容量バジェット LRU）。結果は待たない。
        _ = Task.Run(CanvasLayoutStore.CleanupThumbnails, CancellationToken.None);

        string folderLabel = Path.GetFileName(
            rootFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrEmpty(folderLabel))
        {
            folderLabel = rootFolder;
        }

        var nodes = new List<object>(graph.Files.Count);
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

            nodes.Add(new
            {
                id = file,
                rel,
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

        return JsonSerializer.Serialize(new
        {
            root = rootFolder,
            folderLabel,
            // 依存グラフ基盤の走査上限（500 ファイル）に到達している場合は明示する。
            truncated = graph.Files.Count >= FolderSearchService.MaxFiles,
            nodes,
            edges,
            view = layout.View == null
                ? null
                : (object)new { x = layout.View.X, y = layout.View.Y, k = layout.View.K },
        });
    }

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
