using System.IO;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Hirake;

/// <summary>
/// 構造クエリビューのタブ。1 タブ = 1 起点フォルダ。
/// StructureQueryService の抽出結果を structure-template.html + structure.js で表示し、
/// クエリの切替（見出し / タスク / キーワード）はブラウザ内で完結する。
/// ファイル監視は行わない（表示時点のスナップショット）。
/// </summary>
public sealed class StructureQueryTab : DocumentTab
{
    /// <summary>抽出の起点フォルダ（フルパス）。</summary>
    public string RootFolder { get; }

    // タブ破棄時にバックグラウンド走査を打ち切るためのキャンセルソース。
    private readonly CancellationTokenSource _buildCts = new();

    public StructureQueryTab(string rootFolder, IDocumentTabHost host, string assetsDirectory, string tempDirectory)
        : base(rootFolder, host, assetsDirectory, tempDirectory)
    {
        RootFolder = Path.GetFullPath(rootFolder);
        string label = Path.GetFileName(
            RootFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        FileName = "構造: " + (string.IsNullOrEmpty(label) ? RootFolder : label);
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

            // 全ファイルの AST 解析は CPU バウンドのためバックグラウンドで行う。
            // タブが破棄されたら走査自体を打ち切る。
            StructureQueryResult result = await Task.Run(
                () => StructureQueryService.Build(RootFolder, _buildCts.Token))
                .ConfigureAwait(true);

            if (IsDisposed)
            {
                return;
            }

            string dataJson = StructureQueryService.ToJson(result);
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
                    MarkdownRenderer.RenderMessage($"構造クエリの生成に失敗しました: {ex.Message}"));
            }
            return;
        }

        NavigateHtml(html);
    }

    public override void Dispose()
    {
        // 進行中のフォルダ走査を止めてから既定の破棄処理へ。
        // Cancel のみ行い Dispose はしない（走査タスクがトークンを参照中でも安全、
        // かつ再入時も冪等。DocumentTab の _recommendCts と同じ扱い）。
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
        string templatePath = Path.Combine(AssetsDirectory, "structure-template.html");
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
