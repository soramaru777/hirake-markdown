using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Markdig;
using Markdig.Renderers;
using Markdig.Renderers.Html;
using Markdig.Syntax;

namespace Hirake;

/// <summary>
/// Markdown ファイルを読み込み、Assets/template.html と合成した完全な HTML を生成する。
/// 画像・リンクの絶対パスは仮想ホスト &lt;ドライブ小文字&gt;.doc.hirake 形式へ書き換える。
/// </summary>
public static class MarkdownRenderer
{
    /// <summary>
    /// 仮想ホストのドメインサフィックス。実際のホストはドライブごとに
    /// &lt;ドライブ小文字&gt;.doc.hirake（例: c.doc.hirake）となる。
    /// C# 側がドライブごとに仮想ホストをマッピングする。
    /// </summary>
    public const string DocHost = "doc.hirake";

    // LinkGraphService（依存グラフ基盤）と共有するため internal。
    internal static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
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
    /// <param name="theme">"dark" でダークテーマ、それ以外はライトテーマ。</param>
    public static string Render(string filePath, string assetsDirectory, string theme = "light")
    {
        string markdown = ReadFileText(filePath);

        // 先頭の YAML フロントマターを分離する（本文文字数・カード表示に使う）。
        (string? frontMatterYaml, string bodyMarkdown) = SplitFrontMatter(markdown);

        // Markdig 側は UseYamlFrontMatter によりフロントマターを HTML 出力しない。
        string bodyHtml = RenderBodyWithSourceMap(markdown);
        bodyHtml = RewriteAbsolutePaths(bodyHtml, filePath);

        string fileName = Path.GetFileName(filePath);
        string baseUrl = BuildBaseUrl(filePath);
        string template = LoadTemplate(assetsDirectory);

        string normalizedTheme =
            string.Equals(theme, "dark", StringComparison.OrdinalIgnoreCase) ? "dark" : "light";
        string frontMatterHtml = BuildFrontMatterCard(frontMatterYaml);
        string metaHtml = BuildMetaBar(bodyMarkdown, filePath);

        // プレースホルダ→値の辞書。単一パスで置換することで、先に注入した
        // ユーザー由来コンテンツ（本文・フロントマター）にリテラル "{{META}}" 等が
        // 含まれても再置換されない（逐次 .Replace だと後段のキーで壊れる）。
        var replacements = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["TITLE"] = WebUtility.HtmlEncode(fileName),
            ["BASE"] = baseUrl,
            ["THEME"] = normalizedTheme,
            ["FRONTMATTER"] = frontMatterHtml,
            ["BODY"] = bodyHtml,
            ["META"] = metaHtml,
        };

        return Regex.Replace(
            template,
            @"\{\{(TITLE|BASE|THEME|FRONTMATTER|BODY|META)\}\}",
            match => replacements[match.Groups[1].Value]);
    }

    /// <summary>
    /// Markdown を HTML に変換し、全ブロック要素に data-src-line 属性
    /// （フロントマター込みの原文における 1 始まりの行番号）を付与する。
    /// viewer.js の sourceToDom / domToSource（ソースマップ基盤）が参照する。
    /// HTML ブロックは Markdig のレンダラが属性を出力しないため対象外。
    /// </summary>
    private static string RenderBodyWithSourceMap(string markdown)
    {
        MarkdownDocument document = Markdown.Parse(markdown, Pipeline);

        foreach (MarkdownObject item in document.Descendants())
        {
            if (item is not Block block)
            {
                continue;
            }
            // Line は 0 始まり。ファイル行と揃えるため 1 始まりで出力する。
            block.GetAttributes().AddProperty(
                "data-src-line", (block.Line + 1).ToString(CultureInfo.InvariantCulture));
        }

        var writer = new StringWriter();
        var renderer = new HtmlRenderer(writer);
        Pipeline.Setup(renderer);
        renderer.Render(document);
        writer.Flush();
        return writer.ToString();
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

    internal static string ReadFileText(string filePath)
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
    /// ドライブ文字（小文字）から仮想ホスト名を組み立てる（例: 'C' → "c.doc.hirake"）。
    /// ホスト命名規則の唯一の真実源。DocumentTab 側もこれを参照する。
    /// </summary>
    public static string BuildDocHost(char driveLetter)
        => $"{char.ToLowerInvariant(driveLetter)}.{DocHost}";

    /// <summary>
    /// 仮想ホスト名 "&lt;letter&gt;.doc.hirake" からドライブ文字（大文字）を取り出す。
    /// サブドメイン無しの旧 "doc.hirake" は該当しない（false を返す）。
    /// </summary>
    public static bool TryGetDriveFromDocHost(string host, out char drive)
    {
        drive = '\0';
        if (string.IsNullOrEmpty(host))
        {
            return false;
        }
        string suffix = "." + DocHost;
        if (!host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        string prefix = host.Substring(0, host.Length - suffix.Length);
        if (prefix.Length == 1 && char.IsLetter(prefix[0]))
        {
            drive = char.ToUpperInvariant(prefix[0]);
            return true;
        }
        return false;
    }

    /// <summary>
    /// {{BASE}} 用: ドライブルートからファイルのフォルダまでの相対パスを
    /// https://&lt;ドライブ小文字&gt;.doc.hirake/ 形式の URL に変換する
    /// （各セグメントを URL エスケープ）。
    /// </summary>
    private static string BuildBaseUrl(string filePath)
    {
        string fullPath = Path.GetFullPath(filePath);

        // ドライブ文字を取り出し、ホストのサブドメインに用いる。
        string host = DocHost;
        if (fullPath.Length >= 2 && fullPath[1] == ':' && char.IsLetter(fullPath[0]))
        {
            host = BuildDocHost(fullPath[0]);
        }

        string? directory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrEmpty(directory))
        {
            return $"https://{host}/";
        }

        string root = Path.GetPathRoot(fullPath) ?? string.Empty;
        string relative = directory.Length > root.Length
            ? directory.Substring(root.Length)
            : string.Empty;

        var segments = relative.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
        var sb = new StringBuilder($"https://{host}/");
        foreach (var segment in segments)
        {
            sb.Append(Uri.EscapeDataString(segment));
            sb.Append('/');
        }
        return sb.ToString();
    }

    /// <summary>
    /// 変換後 HTML の src / href に含まれる Windows 絶対パス（C:\... や file:///C:/...）を
    /// https://&lt;ドライブ小文字&gt;.doc.hirake/... 形式に書き換える。
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

        char drive = driveMatch.Groups["drive"].Value[0];
        string rest = driveMatch.Groups["rest"].Value;
        var segments = rest.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
        var sb = new StringBuilder($"https://{BuildDocHost(drive)}/");
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

    /// <summary>
    /// 先頭の YAML フロントマター（--- ～ --- または ...）を分離する。
    /// 戻り値は (フロントマター本文, フロントマターを除いた残り本文)。
    /// フロントマターが無ければ (null, 元テキスト)。
    /// </summary>
    private static (string? Yaml, string Body) SplitFrontMatter(string markdown)
    {
        if (string.IsNullOrEmpty(markdown))
        {
            return (null, markdown ?? string.Empty);
        }

        // 文頭の "---" 行から、次の "---" または "..." 行までを 1 ブロックとする。
        // （BOM は ReadFileText で除去済みのため考慮不要。）
        var match = Regex.Match(
            markdown,
            "\\A---[ \\t]*\\r?\\n(?<yaml>.*?)\\r?\\n(?:---|\\.\\.\\.)[ \\t]*(?:\\r?\\n|\\z)",
            RegexOptions.Singleline);

        if (!match.Success)
        {
            return (null, markdown);
        }

        string yaml = match.Groups["yaml"].Value;
        string body = markdown.Substring(match.Length);
        return (yaml, body);
    }

    /// <summary>
    /// フロントマター本文を折りたたみカード（details）に整形する。
    /// トップレベルの key: value はテーブル、解釈できない構造は pre フォールバック。
    /// フロントマターが無ければ空文字を返す。必ず HTML エンコードする。
    /// </summary>
    private static string BuildFrontMatterCard(string? yaml)
    {
        if (string.IsNullOrWhiteSpace(yaml))
        {
            return string.Empty;
        }

        var rows = new List<KeyValuePair<string, string>>();
        bool parseable = true;

        var lines = yaml.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }
            // コメント行はスキップ。
            if (line.TrimStart().StartsWith("#"))
            {
                continue;
            }
            // トップレベルのみ対象（インデント行はネスト構造とみなす）。
            if (line[0] == ' ' || line[0] == '\t')
            {
                parseable = false;
                break;
            }

            int colon = line.IndexOf(':');
            if (colon <= 0)
            {
                parseable = false;
                break;
            }

            string key = line.Substring(0, colon).Trim();
            string value = line.Substring(colon + 1).Trim();
            if (key.Length == 0 || value.Length == 0)
            {
                // 値が空 = ネストブロックの開始などとみなし、そのまま pre 表示に回す。
                parseable = false;
                break;
            }

            rows.Add(new KeyValuePair<string, string>(key, value));
        }

        var sb = new StringBuilder();
        sb.Append("<details class=\"mdv-frontmatter\" open>");
        sb.Append("<summary>フロントマター</summary>");
        sb.Append("<div class=\"mdv-frontmatter-body\">");

        if (parseable && rows.Count > 0)
        {
            sb.Append("<table><tbody>");
            foreach (var row in rows)
            {
                sb.Append("<tr><th>");
                sb.Append(WebUtility.HtmlEncode(row.Key));
                sb.Append("</th><td>");
                sb.Append(WebUtility.HtmlEncode(row.Value));
                sb.Append("</td></tr>");
            }
            sb.Append("</tbody></table>");
        }
        else
        {
            sb.Append("<pre>");
            sb.Append(WebUtility.HtmlEncode(yaml.TrimEnd('\r', '\n')));
            sb.Append("</pre>");
        }

        sb.Append("</div></details>");
        return sb.ToString();
    }

    /// <summary>
    /// 画面右下のメタ情報チップ（文字数・読了目安・最終更新日時）を組み立てる。
    /// 文字数はフロントマターを除いた本文の非空白文字数。読了目安は 600字/分で切り上げ。
    /// </summary>
    private static string BuildMetaBar(string bodyMarkdown, string filePath)
    {
        int charCount = 0;
        if (!string.IsNullOrEmpty(bodyMarkdown))
        {
            foreach (char ch in bodyMarkdown)
            {
                if (!char.IsWhiteSpace(ch))
                {
                    charCount++;
                }
            }
        }

        int minutes = charCount == 0 ? 0 : (int)Math.Ceiling(charCount / 600.0);

        string lastWrite;
        try
        {
            lastWrite = File.GetLastWriteTime(filePath).ToString("yyyy/MM/dd HH:mm", CultureInfo.InvariantCulture);
        }
        catch
        {
            lastWrite = string.Empty;
        }

        var sb = new StringBuilder();
        sb.Append("<div class=\"mdv-meta\" aria-hidden=\"true\">");
        sb.Append("<span class=\"mdv-meta-item\">");
        sb.Append(charCount.ToString("N0", CultureInfo.InvariantCulture));
        sb.Append(" 文字</span>");
        sb.Append("<span class=\"mdv-meta-sep\">|</span>");
        sb.Append("<span class=\"mdv-meta-item\">約");
        sb.Append(minutes.ToString(CultureInfo.InvariantCulture));
        sb.Append("分</span>");
        if (lastWrite.Length > 0)
        {
            sb.Append("<span class=\"mdv-meta-sep\">|</span>");
            sb.Append("<span class=\"mdv-meta-item\">");
            sb.Append(WebUtility.HtmlEncode(lastWrite));
            sb.Append("</span>");
        }
        sb.Append("</div>");
        return sb.ToString();
    }
}
