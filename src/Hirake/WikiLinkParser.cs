using System.IO;
using System.Net;
using System.Text;
using Markdig;
using Markdig.Helpers;
using Markdig.Parsers;
using Markdig.Parsers.Inlines;
using Markdig.Renderers;
using Markdig.Renderers.Html;
using Markdig.Syntax.Inlines;

namespace Hirake;

/// <summary>
/// <c>[[wikilink]]</c> を Markdig の AST に <see cref="LinkInline"/> として載せるパーサ（ISSUE #53）。
///
/// 独自の要素を作らず <see cref="LinkInline"/> にするのが要点。こうすると
/// 描画（HtmlRenderer）・ナビゲーション制御（DocumentTab）・依存グラフ
/// （LinkGraphService）・バックリンク・キャンバスが、いずれも既存のまま追随する。
///
/// 対応する記法:
///   [[target]]           … target を開く
///   [[target|表示名]]     … 表示名を変えて target を開く
///   [[target#見出し]]     … target の該当見出しへスクロールする
/// <c>![[...]]</c>（埋め込み）は対象外。
/// </summary>
internal sealed class WikiLinkParser : InlineParser
{
    /// <summary>本体（[[ と ]] の間）の長さ上限。暴走した括弧列で走査が伸びるのを防ぐ。</summary>
    private const int MaxBodyLength = 512;

    /// <summary>解析中ファイルのフルパスを <see cref="MarkdownParserContext"/> に載せるキー。</summary>
    internal const string DocumentPathKey = "hirake.wikilink.docPath";

    /// <summary>索引の起点フォルダを <see cref="MarkdownParserContext"/> に載せるキー。</summary>
    internal const string RootKey = "hirake.wikilink.root";

    /// <summary>
    /// 「記法としては読むが、ファイルは探さない」ことを示すキー。
    /// 見出しテキストの取り出し（<see cref="HirakeUri.FindHeadingLine"/>）で使う。
    /// あちらはリンク先が要らないのに、索引を引くとフォルダ走査が UI スレッドの
    /// クリック処理に入り込んでしまう。表示テキストだけ取れれば十分。
    /// </summary>
    internal const string TextOnlyKey = "hirake.wikilink.textOnly";

    public WikiLinkParser()
    {
        OpeningCharacters = new[] { '[' };
    }

    public override bool Match(InlineProcessor processor, ref StringSlice slice)
    {
        string text = slice.Text;
        int start = slice.Start;
        int end = slice.End;

        // 条件2: "[[" で始まること。
        if (start + 1 > end || text[start + 1] != '[')
        {
            return false;
        }

        // 条件3: 直前が '!' の埋め込み記法は対象外。
        if (start > 0 && text[start - 1] == '!')
        {
            return false;
        }

        // 条件4〜6,9: 同一行内で "]]" が閉じ、内側に '[' ']' 改行を含まず、長すぎないこと。
        int bodyStart = start + 2;
        int closeIndex = -1;
        for (int i = bodyStart; i < end; i++)
        {
            char c = text[i];
            if (c == '\n' || c == '\r' || c == '[')
            {
                return false;
            }
            if (c == ']')
            {
                if (text[i + 1] != ']')
                {
                    return false;
                }
                closeIndex = i;
                break;
            }
            if (i - bodyStart >= MaxBodyLength)
            {
                return false;
            }
        }
        if (closeIndex < 0)
        {
            return false;
        }

        // 条件1: 設定でオフにされていれば素通し（記法として成立してから見るので、
        // 通常の '[' 1 文字ごとにロックを取ることはない）。
        if (!SettingsStore.Instance.WikiLinksEnabled)
        {
            return false;
        }

        string body = text[bodyStart..closeIndex];
        string raw = text[start..(closeIndex + 2)];

        if (!TryParseBody(body, out string name, out string display, out string? heading))
        {
            return false; // 条件7: 名前が空。
        }

        // 解析コンテキストが無い＝どのファイルを解析しているか分からない経路
        // （横断検索・意味索引・統計・構造クエリ）。ここで独自の要素に変えると、
        // それらのテキスト抽出から "[[...]]" の文字が消えてしまう。
        // 解決できないなら手を出さず、対応前とまったく同じ文字のまま通す。
        if (GetDocumentPath(processor) == null)
        {
            return false;
        }

        int sourcePosition = processor.GetSourcePosition(start, out int line, out int column);
        var span = new Markdig.Syntax.SourceSpan(sourcePosition, sourcePosition + raw.Length - 1);

        // 表示テキストだけが要る経路（見出しの取り出し）。ファイルは探さない。
        if (IsTextOnly(processor))
        {
            processor.Inline = new LiteralInline(display)
            {
                Span = span,
                Line = line,
                Column = column,
            };
            slice.Start = closeIndex + 2;
            return true;
        }

        // 条件8: リンクの中では入れ子の <a> を作らない。文字としてそのまま出す。
        if (IsInsideLink(processor))
        {
            processor.Inline = new LiteralInline(raw)
            {
                Span = span,
                Line = line,
                Column = column,
            };
            slice.Start = closeIndex + 2;
            return true;
        }

        WikiLinkResolution resolution = Resolve(processor, name);

        if (resolution.Verdict == WikiLinkVerdict.Unresolved || resolution.Path == null)
        {
            // 解決できないものはリンクにしない。href の無い <a> を作ると、
            // 「リンクに見えるのに動かない」要素になってしまう。
            processor.Inline = BuildBrokenInline(
                raw, span, line, column, resolution.IndexComplete);
            slice.Start = closeIndex + 2;
            return true;
        }

        string? url = BuildRelativeUrl(processor, resolution.Path, heading);
        if (url == null)
        {
            processor.Inline = BuildBrokenInline(
                raw, span, line, column, indexComplete: true);
            slice.Start = closeIndex + 2;
            return true;
        }

        var link = new LinkInline(url, string.Empty)
        {
            IsImage = false,
            IsClosed = true,
            Span = span,
            Line = line,
            Column = column,
        };
        link.AppendChild(new LiteralInline(display)
        {
            Span = span,
            Line = line,
            Column = column,
        });

        var attributes = link.GetAttributes();
        attributes.AddClass("mdv-wikilink");

        // 「これで確定」と言い切れないものは、開けるようにしたうえで点線で示す。
        // 候補が複数ある場合だけでなく、索引が走査をやり切れなかった場合も含む
        // （未列挙の範囲に別の同名があるかもしれず、候補 1 件を確定にできない）。
        if (!resolution.IsCertain)
        {
            attributes.AddClass("mdv-wikilink-ambiguous");
            link.Title = resolution.Count > 1
                ? $"同名が {resolution.Count} 件あります: {resolution.Path}"
                : $"リンク索引が最後まで作れていないため、他に同名がある可能性があります: {resolution.Path}";
        }

        processor.Inline = link;
        slice.Start = closeIndex + 2;
        return true;
    }

    /// <summary>
    /// 未解決の `[[...]]` を、原文のまま見せる span として組み立てる。
    /// 索引がやり切れていない場合は「無い」と断定しない（未列挙の範囲に
    /// あるかもしれないため、そう書く）。
    /// </summary>
    private static HtmlInline BuildBrokenInline(
        string raw, Markdig.Syntax.SourceSpan span, int line, int column, bool indexComplete)
    {
        string encoded = WebUtility.HtmlEncode(raw);
        string title = indexComplete
            ? "リンク先が見つかりません"
            : "リンク索引が最後まで作れていないため、リンク先の有無を確認できません";
        return new HtmlInline(
            "<span class=\"mdv-wikilink-broken\" title=\"" + title + "\">"
            + encoded + "</span>")
        {
            Span = span,
            Line = line,
            Column = column,
        };
    }

    /// <summary>
    /// 本体を「名前 / 表示名 / 見出し」に分解する。
    /// 見出しは名前側（'|' より前）に書く（[[target#見出し|表示名]]）。
    /// </summary>
    internal static bool TryParseBody(
        string body, out string name, out string display, out string? heading)
    {
        name = string.Empty;
        display = string.Empty;
        heading = null;

        int pipe = body.IndexOf('|');
        string namePart = pipe >= 0 ? body[..pipe] : body;
        string displayPart = pipe >= 0 ? body[(pipe + 1)..] : string.Empty;

        int hash = namePart.IndexOf('#');
        if (hash >= 0)
        {
            heading = namePart[(hash + 1)..].Trim();
            namePart = namePart[..hash];
            if (heading.Length == 0)
            {
                heading = null;
            }
        }

        name = namePart.Trim();
        if (name.Length == 0)
        {
            return false;
        }

        displayPart = displayPart.Trim();
        if (displayPart.Length > 0)
        {
            display = displayPart;
        }
        else
        {
            // 表示名の指定が無ければ、名前の最後のセグメントを見せる。
            int slash = name.LastIndexOfAny(new[] { '/', '\\' });
            display = slash >= 0 && slash + 1 < name.Length ? name[(slash + 1)..] : name;
        }
        return true;
    }

    /// <summary>
    /// リンクの内側にいるか（開いたままのリンク区切りの中か）を判定する。
    /// 画像リンクの中は対象外（画像の alt テキストに wikilink は書けてよい）。
    /// </summary>
    private static bool IsInsideLink(InlineProcessor processor)
    {
        Inline? current = processor.Inline;
        if (current == null)
        {
            return false;
        }

        // 開いたままのリンク区切り（LinkDelimiterInline）はコンテナなので、
        // その内側の要素から親をたどれば必ず見つかる。
        ContainerInline? container = current as ContainerInline ?? current.Parent;
        while (container != null)
        {
            if (container is LinkDelimiterInline { IsImage: false }
                or LinkInline { IsImage: false })
            {
                return true;
            }
            container = container.Parent;
        }
        return false;
    }

    /// <summary>
    /// 解析中ファイルのフルパスを取り出す。context を渡していない経路では null。
    /// </summary>
    /// <summary>ファイルを探さず、表示テキストだけを取り出す指定か。</summary>
    private static bool IsTextOnly(InlineProcessor processor)
        => processor.Context?.Properties.ContainsKey(TextOnlyKey) == true;

    private static string? GetDocumentPath(InlineProcessor processor)
    {
        MarkdownParserContext? context = processor.Context;
        if (context != null
            && context.Properties.TryGetValue(DocumentPathKey, out object? pathObject)
            && pathObject is string documentPath
            && documentPath.Length > 0)
        {
            return documentPath;
        }
        return null;
    }

    private static WikiLinkResolution Resolve(InlineProcessor processor, string name)
    {
        MarkdownParserContext? context = processor.Context;
        string? documentPath = GetDocumentPath(processor);
        if (context == null || documentPath == null)
        {
            return WikiLinkResolution.NotFound;
        }

        string? directory = Path.GetDirectoryName(documentPath);
        if (string.IsNullOrEmpty(directory))
        {
            return WikiLinkResolution.NotFound;
        }

        // ルートは 1 文書につき 1 回だけ推定する。推定は親フォルダを最大 5 階層
        // 列挙するため、wikilink を含まない文書では一度も走らせない
        // （呼び出し側が知っている場合は context に入っている）。
        string root;
        if (context.Properties.TryGetValue(RootKey, out object? rootObject)
            && rootObject is string cachedRoot
            && cachedRoot.Length > 0)
        {
            root = cachedRoot;
        }
        else
        {
            root = LinkGraphService.FindWorkspaceRoot(directory);
            context.Properties[RootKey] = root;
        }

        try
        {
            return WikiLinkIndex.GetOrBuild(root).Resolve(name, directory);
        }
        catch
        {
            // 索引の構築に失敗しても文書は表示する。
            return WikiLinkResolution.NotFound;
        }
    }

    /// <summary>
    /// リンク先のフルパスを、文書のフォルダからの相対 URL へ変換する。
    ///
    /// 絶対パス（C:\...）で出すと <c>MarkdownRenderer.RewriteAbsolutePaths</c> が
    /// パス全体をセグメント分割してエスケープするため、"#" まで 1 セグメントに
    /// 潰れてフラグメントが壊れる。相対パスは書き換え対象外なので "#" が残る。
    /// </summary>
    private static string? BuildRelativeUrl(
        InlineProcessor processor, string targetPath, string? heading)
    {
        string? documentPath = GetDocumentPath(processor);
        if (documentPath == null)
        {
            return null;
        }

        string? directory = Path.GetDirectoryName(documentPath);
        if (string.IsNullOrEmpty(directory))
        {
            return null;
        }

        string relative;
        try
        {
            relative = Path.GetRelativePath(directory, targetPath);
        }
        catch
        {
            return null;
        }

        if (Path.IsPathRooted(relative))
        {
            // 別ドライブなど、相対にできない場合はリンクにしない。
            return null;
        }

        var sb = new StringBuilder();
        foreach (string segment in relative.Split(
            new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (sb.Length > 0)
            {
                sb.Append('/');
            }
            sb.Append(segment == ".." ? ".." : Uri.EscapeDataString(segment));
        }
        if (sb.Length == 0)
        {
            return null;
        }

        if (!string.IsNullOrEmpty(heading))
        {
            sb.Append('#');
            sb.Append(Uri.EscapeDataString(heading));
        }
        return sb.ToString();
    }
}

/// <summary><see cref="WikiLinkParser"/> をパイプラインへ組み込む拡張。</summary>
internal sealed class WikiLinkExtension : IMarkdownExtension
{
    public void Setup(MarkdownPipelineBuilder pipeline)
    {
        if (pipeline.InlineParsers.Contains<WikiLinkParser>())
        {
            return;
        }
        // '[' を通常のリンクより先に見る必要があるため、LinkInlineParser の前に置く。
        pipeline.InlineParsers.InsertBefore<LinkInlineParser>(new WikiLinkParser());
    }

    public void Setup(MarkdownPipeline pipeline, IMarkdownRenderer renderer)
    {
        // レンダラ側の追加は不要（LinkInline / HtmlInline として出すため）。
    }
}
