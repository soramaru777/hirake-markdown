using System.IO;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Hirake;

/// <summary>
/// ナレッジグラフ・ビューのタブ。1 タブ = 1 起点フォルダ。
/// LinkGraphService（依存グラフ基盤）の解析結果を graph-template.html + graph.js
/// （d3-force）で描画する。ファイル監視は行わない（表示時点のスナップショット）。
/// </summary>
public sealed class GraphTab : DocumentTab
{
    /// <summary>グラフの起点フォルダ（フルパス）。</summary>
    public string RootFolder { get; }

    public GraphTab(string rootFolder, IDocumentTabHost host, string assetsDirectory, string tempDirectory)
        : base(rootFolder, host, assetsDirectory, tempDirectory)
    {
        RootFolder = Path.GetFullPath(rootFolder);
        string label = Path.GetFileName(
            RootFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        FileName = "グラフ: " + (string.IsNullOrEmpty(label) ? RootFolder : label);
    }

    /// <summary>グラフはスナップショット表示のためファイル監視しない。</summary>
    protected override void StartWatcher()
    {
    }

    protected override Task LoadContentAsync()
    {
        string html;
        try
        {
            string theme = RefreshEffectiveTheme();
            LinkGraph graph = LinkGraphService.Build(RootFolder);
            string dataJson = LinkGraphService.ToJson(graph);
            html = ComposeHtml(dataJson, theme);
        }
        catch (Exception ex)
        {
            WebView.CoreWebView2.NavigateToString(
                MarkdownRenderer.RenderMessage($"グラフの生成に失敗しました: {ex.Message}"));
            return Task.CompletedTask;
        }

        NavigateHtml(html);
        return Task.CompletedTask;
    }

    private string ComposeHtml(string dataJson, string theme)
    {
        string templatePath = Path.Combine(AssetsDirectory, "graph-template.html");
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
