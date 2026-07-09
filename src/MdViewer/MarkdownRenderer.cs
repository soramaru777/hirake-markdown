using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Markdig;

namespace MdViewer;

/// <summary>
/// Markdown ファイルを読み込み、Assets/template.html と合成した完全な HTML を生成する。
/// 画像・リンクの絶対パスは仮想ホスト doc.mdviewer 形式へ書き換える。
/// </summary>
public static class MarkdownRenderer
{
    public const string DocHost = "doc.mdviewer";

    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .UseYamlFrontMatter()
        .Build();

    static MarkdownRenderer()
    {
        // Shift-JIS フォールバック用にコードページを登録する。
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    /// <summary>
    /// 指定ファイルをレンダリングして完全な HTML 文字列を返す。
    /// </summary>
    public static string Render(string filePath, string assetsDirectory)
    {
        string markdown = ReadFileText(filePath);
        string bodyHtml = Markdown.ToHtml(markdown, Pipeline);
        bodyHtml = RewriteAbsolutePaths(bodyHtml, filePath);

        string fileName = Path.GetFileName(filePath);
        string baseUrl = BuildBaseUrl(filePath);
        string template = LoadTemplate(assetsDirectory);

        return template
            .Replace("{{TITLE}}", WebUtility.HtmlEncode(fileName))
            .Replace("{{BASE}}", baseUrl)
            .Replace("{{BODY}}", bodyHtml);
    }

    /// <summary>ファイルが見つからない場合などに表示する簡素な HTML。</summary>
    public static string RenderMessage(string message)
    {
        string encoded = WebUtility.HtmlEncode(message);
        return "<!DOCTYPE html><html><head><meta charset=\"utf-8\">" +
               "<style>body{font-family:'Segoe UI','Meiryo',sans-serif;color:#888;" +
               "display:flex;align-items:center;justify-content:center;height:100vh;margin:0;}</style>" +
               "</head><body><div>" + encoded + "</div></body></html>";
    }

    private static string ReadFileText(string filePath)
    {
        byte[] bytes = File.ReadAllBytes(filePath);

        // BOM 判定を含む UTF-8 デコードを試み、失敗時に Shift-JIS へフォールバックする。
        var strictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
        try
        {
            int offset = 0;
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            {
                offset = 3;
            }
            return strictUtf8.GetString(bytes, offset, bytes.Length - offset);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.GetEncoding(932).GetString(bytes);
        }
    }

    private static string LoadTemplate(string assetsDirectory)
    {
        try
        {
            string templatePath = Path.Combine(assetsDirectory, "template.html");
            if (File.Exists(templatePath))
            {
                return File.ReadAllText(templatePath, Encoding.UTF8);
            }
        }
        catch
        {
            // フォールバックへ。
        }

        // template.html 未配置時（並行開発中）の内蔵フォールバック。
        return "<!DOCTYPE html><html><head><meta charset=\"utf-8\">" +
               "<title>{{TITLE}}</title><base href=\"{{BASE}}\"></head>" +
               "<body>{{BODY}}</body></html>";
    }

    /// <summary>
    /// {{BASE}} 用: ドライブルートからファイルのフォルダまでの相対パスを
    /// https://doc.mdviewer/ 形式の URL に変換する（各セグメントを URL エスケープ）。
    /// </summary>
    private static string BuildBaseUrl(string filePath)
    {
        string fullPath = Path.GetFullPath(filePath);
        string? directory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrEmpty(directory))
        {
            return $"https://{DocHost}/";
        }

        string root = Path.GetPathRoot(fullPath) ?? string.Empty;
        string relative = directory.Length > root.Length
            ? directory.Substring(root.Length)
            : string.Empty;

        var segments = relative.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
        var sb = new StringBuilder($"https://{DocHost}/");
        foreach (var segment in segments)
        {
            sb.Append(Uri.EscapeDataString(segment));
            sb.Append('/');
        }
        return sb.ToString();
    }

    /// <summary>
    /// 変換後 HTML の src / href に含まれる Windows 絶対パス（C:\... や file:///C:/...）を
    /// 同一ドライブなら https://doc.mdviewer/... 形式に書き換える。
    /// </summary>
    private static string RewriteAbsolutePaths(string html, string filePath)
    {
        // (src|href)="..." / '...' の属性値を対象にする。
        var attrRegex = new Regex(
            "(?<attr>\\b(?:src|href))\\s*=\\s*(?<quote>[\"'])(?<value>[^\"']*)(?<quote2>[\"'])",
            RegexOptions.IgnoreCase);

        return attrRegex.Replace(html, match =>
        {
            string attr = match.Groups["attr"].Value;
            string quote = match.Groups["quote"].Value;
            string value = match.Groups["value"].Value;

            string? rewritten = TryRewriteValue(value);
            if (rewritten == null)
            {
                return match.Value;
            }
            return $"{attr}={quote}{rewritten}{quote}";
        });
    }

    private static string? TryRewriteValue(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string path = value;

        // file:///C:/... 形式を Windows パスへ戻す。
        if (path.StartsWith("file:///", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                path = new Uri(path).LocalPath;
            }
            catch
            {
                return null;
            }
        }

        // ドライブレター付き絶対パス（C:\ または C:/）のみ対象。
        var driveMatch = Regex.Match(path, "^(?<drive>[A-Za-z]):[\\\\/](?<rest>.*)$");
        if (!driveMatch.Success)
        {
            return null;
        }

        string rest = driveMatch.Groups["rest"].Value;
        var segments = rest.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
        var sb = new StringBuilder($"https://{DocHost}/");
        for (int i = 0; i < segments.Length; i++)
        {
            sb.Append(Uri.EscapeDataString(segments[i]));
            if (i < segments.Length - 1)
            {
                sb.Append('/');
            }
        }
        return sb.ToString();
    }
}
