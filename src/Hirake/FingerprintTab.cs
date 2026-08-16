using System.IO;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Hirake;

/// <summary>
/// 文書指紋・類似検出ビューのタブ。1 タブ = 1 起点フォルダ。
/// FingerprintService の検出結果を fingerprint-template.html + fingerprint.js で
/// 表示し、フィルタ（完全一致のみ / 類似含む）はブラウザ内で完結する。
/// ファイル監視は行わない（表示時点のスナップショット）。
/// </summary>
public sealed class FingerprintTab : DocumentTab
{
    /// <summary>検出の起点フォルダ（フルパス）。</summary>
    public string RootFolder { get; }

    // タブ破棄時にバックグラウンド走査を打ち切るためのキャンセルソース。
    private readonly CancellationTokenSource _buildCts = new();

    public FingerprintTab(string rootFolder, IDocumentTabHost host, string assetsDirectory, string tempDirectory)
        : base(rootFolder, host, assetsDirectory, tempDirectory)
    {
        RootFolder = Path.GetFullPath(rootFolder);
        string label = Path.GetFileName(
            RootFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        FileName = "指紋: " + (string.IsNullOrEmpty(label) ? RootFolder : label);
    }

    /// <summary>スナップショット表示のためファイル監視しない。</summary>
    protected override void StartWatcher()
    {
    }

    /// <summary>fingerprint.js はズームバッジを公開しない。</summary>
    protected override bool ProvidesZoomBadge => false;

    protected override async Task LoadContentAsync()
    {
        string html;
        try
        {
            string theme = RefreshEffectiveTheme();

            // 全ファイルの AST 解析 + 指紋計算は CPU バウンドのためバックグラウンドで行う。
            // タブが破棄されたら走査自体を打ち切る。
            FingerprintResult result = await Task.Run(
                () => FingerprintService.Build(RootFolder, _buildCts.Token))
                .ConfigureAwait(true);

            if (IsDisposed)
            {
                return;
            }

            string dataJson = FingerprintService.ToJson(result);
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
                    MarkdownRenderer.RenderMessage($"文書指紋の生成に失敗しました: {ex.Message}"));
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
        string templatePath = Path.Combine(AssetsDirectory, "fingerprint-template.html");
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
