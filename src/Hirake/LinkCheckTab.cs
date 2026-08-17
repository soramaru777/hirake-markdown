using System.IO;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;

namespace Hirake;

/// <summary>
/// リンク切れ・孤立ページの検出ビューのタブ（ISSUE #56）。1 タブ = 1 起点フォルダ。
/// 依存グラフ（<see cref="LinkGraphService"/>）が解析の途中で捨てていた未解決リンクを
/// 一覧し、参照元の該当行へジャンプできるようにする。
/// 表示・切替はブラウザ内で完結する。ファイル監視は行わない（表示時点のスナップショット）。
/// </summary>
public sealed class LinkCheckTab : DocumentTab
{
    /// <summary>クリップボードへ渡す文字数の上限。巨大な貼り付けを作らないための歯止め。</summary>
    private const int MaxCopyLength = 1_000_000;

    /// <summary>検出の起点フォルダ（フルパス）。</summary>
    public string RootFolder { get; }

    // タブ破棄時にバックグラウンド走査を打ち切るためのキャンセルソース。
    private readonly CancellationTokenSource _buildCts = new();

    public LinkCheckTab(string rootFolder, IDocumentTabHost host, string assetsDirectory, string tempDirectory)
        : base(rootFolder, host, assetsDirectory, tempDirectory)
    {
        RootFolder = Path.GetFullPath(rootFolder);
        string label = Path.GetFileName(
            RootFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        FileName = "リンク: " + (string.IsNullOrEmpty(label) ? RootFolder : label);
    }

    /// <summary>スナップショット表示のためファイル監視しない。</summary>
    protected override void StartWatcher()
    {
    }

    /// <summary>linkcheck.js はズームバッジを公開しない。</summary>
    protected override bool ProvidesZoomBadge => false;

    protected override async Task LoadContentAsync()
    {
        string html;
        try
        {
            string theme = RefreshEffectiveTheme();

            // 全ファイルの AST 解析は CPU バウンドのためバックグラウンドで行う。
            // タブが破棄されたら走査を打ち切る。<b>打ち切りの粒度はファイル単位</b>
            // （Markdig の解析自体はキャンセルできないため、いま解析中の 1 ファイルは
            // 最後まで進む）。結果の反映は IsDisposed で止める。
            // キャッシュ版を使わないのは、検出は「今の状態」を見たい操作のため
            // （バックリンク表示のような高頻度呼び出しではない）。
            LinkGraph graph = await Task.Run(
                () => LinkGraphService.Build(RootFolder, _buildCts.Token))
                .ConfigureAwait(true);

            if (IsDisposed)
            {
                return;
            }

            string dataJson = LinkGraphService.ToLinkCheckJson(graph);
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
                    MarkdownRenderer.RenderMessage($"リンク検出の生成に失敗しました: {ex.Message}"));
            }
            return;
        }

        NavigateHtml(html);
    }

    /// <summary>
    /// 一覧を <c>hirake://</c> 付きのテキストとしてクリップボードへ渡す。
    ///
    /// この受け口を基底（<see cref="DocumentTab"/>）に置かないのは、Markdown が
    /// 生の HTML と script を含められるため。基底で処理すると、任意の文書が
    /// クリップボードを書き換えられることになる。ここで扱う文字列は自分が組み立てた
    /// 一覧だけ。
    /// </summary>
    protected override void OnCopyTextMessage(JsonElement root)
    {
        if (!root.TryGetProperty("text", out JsonElement textEl)
            || textEl.ValueKind != JsonValueKind.String)
        {
            return;
        }

        string? text = textEl.GetString();
        if (string.IsNullOrEmpty(text))
        {
            ReportCopyResult("failed");
            return;
        }

        // 通常は linkcheck.js 側で件数を削って上限に収め、注意書きを必ず残している。
        // ここは最後の歯止め。切ったときは黙らず「一部だけ」と返す。
        bool truncated = false;
        if (text.Length > MaxCopyLength)
        {
            int cut = MaxCopyLength;
            if (char.IsHighSurrogate(text[cut - 1]))
            {
                cut--; // サロゲートペアの途中で切らない
            }
            text = text[..cut];
            truncated = true;
        }

        try
        {
            // 他プロセスがクリップボードを掴んでいると一発では失敗する。
            // copy: true で内容を残し、失敗は下で拾う。
            Clipboard.SetDataObject(text, copy: true);
        }
        catch (Exception ex)
        {
            // 黙って成功したことにすると、貼り付けるまで失敗に気づけない。
            Diagnostics.Record("linkcheck", "一覧のコピーに失敗しました", ex.GetType().Name);
            ReportCopyResult("failed");
            return;
        }

        ReportCopyResult(truncated ? "truncated" : "ok");
    }

    /// <summary>コピーの結果（ok / truncated / failed）を表示側へ返す。</summary>
    private void ReportCopyResult(string result)
    {
        if (IsDisposed || WebView.CoreWebView2 == null)
        {
            return;
        }

        string script = "(function(){try{if(window.__mdvCopyResult){window.__mdvCopyResult("
            + JsonSerializer.Serialize(result) + ");}}catch(e){}})();";
        _ = WebView.CoreWebView2.ExecuteScriptAsync(script);
    }

    public override void Dispose()
    {
        // 進行中のフォルダ走査を（ファイル単位で）止めてから既定の破棄処理へ。
        // Cancel のみ行い Dispose はしない（StructureQueryTab と同じ扱い）。
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

    private string ComposeHtml(string dataJson, string theme)
    {
        string templatePath = Path.Combine(AssetsDirectory, "linkcheck-template.html");
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
