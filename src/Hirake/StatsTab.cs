using System.IO;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Hirake;

/// <summary>
/// フォルダ統計ダッシュボードのタブ。1 タブ = 1 起点フォルダ。
/// StatsService の集計結果を stats-template.html + stats.js（d3）で表示する。
/// ファイル監視は行わない（表示時点のスナップショット）。
/// </summary>
public sealed class StatsTab : DocumentTab
{
    /// <summary>集計の起点フォルダ（フルパス）。</summary>
    public string RootFolder { get; }

    // タブ破棄時にバックグラウンド走査を打ち切るためのキャンセルソース。
    private readonly CancellationTokenSource _buildCts = new();

    public StatsTab(string rootFolder, IDocumentTabHost host, string assetsDirectory, string tempDirectory)
        : base(rootFolder, host, assetsDirectory, tempDirectory)
    {
        RootFolder = Path.GetFullPath(rootFolder);
        string label = Path.GetFileName(
            RootFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        FileName = "統計: " + (string.IsNullOrEmpty(label) ? RootFolder : label);
    }

    /// <summary>スナップショット表示のためファイル監視しない。</summary>
    protected override void StartWatcher()
    {
    }

    /// <summary>stats.js はズームバッジを公開しない。</summary>
    protected override bool ProvidesZoomBadge => false;

    protected override async Task LoadContentAsync()
    {
        string html;
        try
        {
            string theme = RefreshEffectiveTheme();

            // 全ファイルの読込 + AST 解析は CPU バウンドのためバックグラウンドで行う。
            StatsResult result = await Task.Run(
                () => StatsService.Build(RootFolder, _buildCts.Token))
                .ConfigureAwait(true);

            if (IsDisposed)
            {
                return;
            }

            string dataJson = StatsService.ToJson(result);
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
                    MarkdownRenderer.RenderMessage($"統計の集計に失敗しました: {ex.Message}"));
            }
            return;
        }

        NavigateHtml(html);
    }

    public override void Dispose()
    {
        // 進行中のフォルダ走査を止めてから既定の破棄処理へ（Cancel のみ・冪等）。
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
        string templatePath = Path.Combine(AssetsDirectory, "stats-template.html");
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
