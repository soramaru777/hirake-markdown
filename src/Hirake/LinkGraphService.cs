using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Hirake;

/// <summary>解決できなかったリンクの理由（ISSUE #56）。表示文言はこの 4 種類に閉じる。</summary>
public enum BrokenLinkReason
{
    /// <summary>リンク先のファイルが無い。</summary>
    TargetMissing,

    /// <summary>実在するが走査範囲（ルート配下）の外にある。</summary>
    OutsideRoot,

    /// <summary><c>[[名前]]</c> に一致するファイルが索引に無い。</summary>
    WikiLinkNotFound,

    /// <summary>索引が上限に達しており「無い」と言い切れない。</summary>
    Unverifiable,
}

/// <summary>解決できなかったリンク 1 本（ISSUE #56）。</summary>
public sealed class BrokenLink
{
    /// <summary>参照元ファイル（フルパス）。</summary>
    public string SourceFile { get; init; } = string.Empty;

    /// <summary>ソース行番号（1 始まり。ソースマップ基盤の data-src-line と同じ規約）。</summary>
    public int Line { get; init; }

    /// <summary>書かれていたままのリンク先（"../old/notes.md" / "[[廃止]]"）。</summary>
    public string RawTarget { get; init; } = string.Empty;

    /// <summary>解決を試みた絶対パス。wikilink（探すのは名前）では null。</summary>
    public string? ResolvedPath { get; init; }

    /// <summary>未解決の理由。</summary>
    public BrokenLinkReason Reason { get; init; }
}

/// <summary>
/// 依存グラフ基盤（全体ルール R5 の共有コンポーネント）。
/// フォルダ内の全 Markdown ファイルを解析し、md 間リンクの正引き・逆引きを提供する。
/// ナレッジグラフ・ビュー、バックリンクパネル、リンク切れ検出（ISSUE #56）が利用する。
/// </summary>
public sealed class LinkGraph
{
    private readonly Dictionary<string, List<string>> _forward;
    private readonly Dictionary<string, List<string>> _backward;

    /// <summary>解析対象となった全ファイル（フルパス）。</summary>
    public IReadOnlyList<string> Files { get; }

    /// <summary>グラフの起点フォルダ（フルパス）。</summary>
    public string RootFolder { get; }

    /// <summary>解決できなかったリンク（ISSUE #56）。件数上限は <see cref="LinkGraphService.MaxBrokenLinks"/>。</summary>
    public IReadOnlyList<BrokenLink> BrokenLinks { get; }

    /// <summary>ファイル数の上限に達し、一部が未走査のとき true。</summary>
    public bool Truncated { get; }

    /// <summary>リンク切れの件数上限に達し、記録を打ち切ったとき true。</summary>
    public bool BrokenLinksCapped { get; }

    /// <summary>
    /// 大きすぎる（2MB 超）または読めなかったため<b>中身を見ていない</b>ファイル数。
    /// このファイルからのリンクは 1 本も検査していないので、0 件表示の根拠にしてはいけない。
    /// </summary>
    public int UnreadableFileCount { get; }

    /// <summary>
    /// 読めない・辿れないため<b>中を見ていない</b>フォルダ数。
    /// その中にある文書は 1 件も走査していない（設計上の除外は数えない）。
    /// </summary>
    public int SkippedDirectoryCount { get; }

    internal LinkGraph(
        string rootFolder,
        IReadOnlyList<string> files,
        Dictionary<string, List<string>> forward,
        Dictionary<string, List<string>> backward,
        IReadOnlyList<BrokenLink>? brokenLinks = null,
        bool truncated = false,
        bool brokenLinksCapped = false,
        int unreadableFileCount = 0,
        int skippedDirectoryCount = 0)
    {
        RootFolder = rootFolder;
        Files = files;
        _forward = forward;
        _backward = backward;
        BrokenLinks = brokenLinks ?? Array.Empty<BrokenLink>();
        Truncated = truncated;
        BrokenLinksCapped = brokenLinksCapped;
        UnreadableFileCount = unreadableFileCount;
        SkippedDirectoryCount = skippedDirectoryCount;
    }

    /// <summary>指定ファイルがリンクしている md ファイル一覧（正引き）。</summary>
    public IReadOnlyList<string> GetLinksFrom(string path) =>
        _forward.TryGetValue(path, out var list) ? list : Array.Empty<string>();

    /// <summary>指定ファイルへリンクしている md ファイル一覧（逆引き＝バックリンク）。</summary>
    public IReadOnlyList<string> GetBacklinks(string path) =>
        _backward.TryGetValue(path, out var list) ? list : Array.Empty<string>();
}

public static class LinkGraphService
{
    /// <summary>
    /// 記録するリンク切れの上限。壊れた vault で無制限に文字列を抱えないための歯止め。
    /// 到達したら <see cref="LinkGraph.BrokenLinksCapped"/> を立て、画面に明示する
    /// （黙って切らない）。
    /// </summary>
    internal const int MaxBrokenLinks = 2000;

    /// <summary>
    /// 1 件のリンク切れが保持する文字列の上限（リンク先 1 本あたり）。
    /// 数十万文字の URL を書かれても <see cref="LinkGraph"/> が太らないようにする。
    /// グラフはバックリンクパネルが 10 秒キャッシュで共有するため、件数だけでなく
    /// 長さにも歯止めが要る。超過分は末尾を省略記号にして、原文のままでないことを示す。
    /// </summary>
    internal const int MaxBrokenTargetLength = 300;

    /// <summary>
    /// 記録するリンク切れの合計文字数の上限。件数上限に達しなくても、
    /// ここに達したら打ち切って <see cref="LinkGraph.BrokenLinksCapped"/> を立てる。
    /// </summary>
    internal const int MaxBrokenTotalChars = 400_000;

    // 直近に構築したグラフの単一エントリキャッシュ。
    // タブ切替や自動リロードのたびにフォルダ全体を再解析しないための短 TTL。
    private static readonly object CacheLock = new();
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(10);
    private static string? _cachedRoot;
    private static DateTime _cachedAtUtc;
    private static LinkGraph? _cachedGraph;

    /// <summary>
    /// 指定フォルダから上方向へ「md ファイルを直接含む親」が続く限り遡り、
    /// ワークスペースのルートらしきフォルダを推定する（最大 5 階層）。
    /// サブフォルダの文書を開いたときに、親フォルダ側の参照元・リンク先が
    /// 解析範囲から漏れるのを防ぐ。グラフビューとバックリンクで共通に使う。
    /// </summary>
    public static string FindWorkspaceRoot(string startDirectory)
    {
        string current = Path.GetFullPath(startDirectory);
        for (int i = 0; i < 5; i++)
        {
            DirectoryInfo? parent;
            bool parentHasMarkdown;
            try
            {
                parent = Directory.GetParent(current);
                if (parent == null)
                {
                    break;
                }
                parentHasMarkdown = parent.EnumerateFiles()
                    .Any(f => FileTreeItem.IsMarkdownFile(f.FullName));
            }
            catch (IOException)
            {
                break;
            }
            catch (UnauthorizedAccessException)
            {
                break;
            }

            if (!parentHasMarkdown)
            {
                break;
            }
            current = parent.FullName;
        }
        return current;
    }

    /// <summary>
    /// Build の短 TTL キャッシュ版。バックリンク表示など高頻度の呼び出しで使う。
    /// </summary>
    public static LinkGraph BuildCached(string rootFolder, CancellationToken token = default)
    {
        string root = Path.GetFullPath(rootFolder);

        lock (CacheLock)
        {
            if (_cachedGraph != null
                && string.Equals(_cachedRoot, root, StringComparison.OrdinalIgnoreCase)
                && DateTime.UtcNow - _cachedAtUtc < CacheTtl)
            {
                return _cachedGraph;
            }
        }

        LinkGraph graph = Build(root, token);

        lock (CacheLock)
        {
            _cachedRoot = root;
            _cachedGraph = graph;
            _cachedAtUtc = DateTime.UtcNow;
        }
        return graph;
    }

    /// <summary>
    /// フォルダ配下の md ファイルを解析して依存グラフを構築する。
    /// 列挙規則（除外フォルダ・上限）は横断検索（FolderSearchService）と共通。
    /// </summary>
    public static LinkGraph Build(string rootFolder, CancellationToken token = default)
    {
        string root = Path.GetFullPath(rootFolder);

        // ノード集合（大文字小文字を区別しないパス比較）。
        var files = new List<string>();
        var fileSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool truncated = false;

        // 読めずに飛ばしたフォルダを数える。飛ばしたことを黙っていると
        // 「リンク切れ 0 件」の根拠が崩れる（ISSUE #56）。
        var stats = new FolderSearchService.EnumerationStats();

        foreach (string file in FolderSearchService.EnumerateMarkdownFiles(root, token, stats))
        {
            if (files.Count >= FolderSearchService.MaxFiles)
            {
                // 上限より先にファイルが残っている＝一部未走査。
                // 「リンク切れ 0 件」を上限のせいで言ってしまわないよう記録する。
                truncated = true;
                break;
            }
            string full = Path.GetFullPath(file);
            if (fileSet.Add(full))
            {
                files.Add(full);
            }
        }

        var forward = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var backward = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var broken = new List<BrokenLink>();
        bool brokenCapped = false;
        int brokenChars = 0;
        int unreadable = 0;

        foreach (string file in files)
        {
            token.ThrowIfCancellationRequested();

            List<LinkRef> references = ExtractLinkRefs(file, root, out bool skipped);
            if (skipped)
            {
                // 中身を 1 行も見ていないファイル。件数を出して 0 件表示の根拠にさせない。
                unreadable++;
            }

            foreach (LinkRef reference in references)
            {
                // 1 ファイルに何万本もリンクがあり得るため、ファイル単位だけでなく
                // リンク単位でも打ち切りを見る（タブを閉じたあと走り続けない）。
                token.ThrowIfCancellationRequested();

                if (reference.IsBrokenWikiLink)
                {
                    // 解決できなかった [[...]]。LinkInline にならないため、
                    // ここで拾わないと検出から漏れる（ISSUE #56）。
                    AddBroken(broken, ref brokenCapped, ref brokenChars, new BrokenLink
                    {
                        SourceFile = file,
                        Line = reference.Line,
                        RawTarget = Shorten(reference.RawTarget),
                        Reason = reference.IndexComplete
                            ? BrokenLinkReason.WikiLinkNotFound
                            : BrokenLinkReason.Unverifiable,
                    });
                    continue;
                }

                if (reference.ResolvedPath == null)
                {
                    continue; // 対象外（外部 URL・画像・アンカーのみ・md 以外）
                }

                // 解析対象（ルート配下の実在ファイル）へのリンクのみエッジにする。
                //
                // 走査側は symlink / ジャンクションを実体パスへ直して集合に入れる
                // （ISSUE #54）。リンクに書かれた字面のままでは、同じ実体を指していても
                // 一致せず、辺が失われて「リンク切れ」と誤検出される。字面で外れたら、
                // 走査側と同じ検査（祖先のリンクまで辿る）を通してもう一度引く。
                var check = default(TraversalCheck);
                if (!fileSet.TryGetValue(reference.ResolvedPath, out string? canonical))
                {
                    // 上限に達したあとは、これ以上記録しないので高い検査もしない。
                    // 検査はパスの要素ごとにファイルシステムへ問い合わせるため、
                    // 壊れたリンクを大量に書かれると件数上限を超えて走り続けてしまう。
                    if (brokenCapped)
                    {
                        continue;
                    }

                    check = SafeCheckTraversal(reference.ResolvedPath);
                    if (check.IsAllowed && check.ResolvedPath is string realPath)
                    {
                        fileSet.TryGetValue(realPath, out canonical);
                    }
                }

                if (canonical == null)
                {
                    // ここは従来「捨てていた」場所（ISSUE #56 の起点）。
                    AddBroken(broken, ref brokenCapped, ref brokenChars, new BrokenLink
                    {
                        SourceFile = file,
                        Line = reference.Line,
                        RawTarget = Shorten(reference.RawTarget),
                        ResolvedPath = Shorten(reference.ResolvedPath!),
                        Reason = ClassifyUnresolved(reference.ResolvedPath, check, truncated),
                    });
                    continue;
                }
                if (string.Equals(canonical, file, StringComparison.OrdinalIgnoreCase))
                {
                    continue; // 自己リンクは除外
                }
                AddUnique(forward, file, canonical);
                AddUnique(backward, canonical, file);
            }
        }

        return new LinkGraph(
            root, files, forward, backward, broken, truncated, brokenCapped, unreadable,
            stats.SkippedDirectories);
    }

    /// <summary>
    /// ルート配下に無いリンク先を「無い」「範囲外」「確認できない」に分ける。
    ///
    /// <b>ネットワーク先には触れない。</b> UNC を指すリンクに <see cref="File.Exists"/> を
    /// 呼ぶと、その場で SMB へ出てしまう（ISSUE #54 と同じ罠）。判定前に弾く。
    ///
    /// <b>上限で打ち切ったときは「範囲外」と断定しない。</b> 走査を途中でやめた以上、
    /// 実在するリンク先が「範囲の外だから見つからなかった」のか「範囲内だが未走査
    /// だった」のかは<b>区別できない</b>。区別できないものを断定するのは嘘になるので
    /// 「確認できない」にする。
    ///
    /// <para>
    /// パスの前方一致で「ルート配下かどうか」を判定しない。列挙側（FolderSearchService）は
    /// ジャンクション・シンボリックリンクを実体パスへ解決して返すため、字面のルートと
    /// 走査済みパスは別の名前空間になり得る（リンクで束ねた vault では前方一致が外れる）。
    /// 打ち切りの有無だけで決めれば、その食い違いに依存しない。
    /// </para>
    /// </summary>
    private static BrokenLinkReason ClassifyUnresolved(
        string resolvedPath, TraversalCheck check, bool truncated)
    {
        // 走査側と同じ検査（FileTreeItem.CheckTraversal）の結果で決める。
        // 字面のパスだけを見ると、途中のフォルダがジャンクションで UNC を指している
        // 場合に気づけず、存在確認でそのまま SMB へ出てしまう（ISSUE #54 と同じ罠）。
        switch (check.Verdict)
        {
            case TraversalVerdict.BlockedByNetwork:
                // ネットワーク先。触らずに範囲外とする。
                return BrokenLinkReason.OutsideRoot;

            case TraversalVerdict.BlockedByLink:
                // ローカルのリンク越えで、飛び先は実在する（検査側が確認済み）。
                // リンクを辿らない設定なので、走査範囲には最初から入っていない。
                return BrokenLinkReason.OutsideRoot;
        }

        // 検査を通らなかった（判定できなかった）ものは、そこで止める。
        // 元の字面へ戻して確認すると、祖先のジャンクションが UNC を指していた場合に
        // その場で SMB へ出てしまう。確認できないものは「確認できない」と言う。
        if (!check.IsAllowed || check.ResolvedPath is not string probePath)
        {
            return BrokenLinkReason.Unverifiable;
        }

        // File.Exists は「無い」と「見に行けない」を同じ false に畳んでしまう。
        // アクセス拒否のフォルダにある文書を「リンク先が無い」と断定しないよう、
        // 例外で区別する。
        try
        {
            File.GetAttributes(probePath);
        }
        catch (FileNotFoundException)
        {
            return BrokenLinkReason.TargetMissing;
        }
        catch (DirectoryNotFoundException)
        {
            return BrokenLinkReason.TargetMissing;
        }
        catch (UnauthorizedAccessException)
        {
            return BrokenLinkReason.Unverifiable; // 見に行けないので「無い」と言えない
        }
        catch (IOException)
        {
            return BrokenLinkReason.Unverifiable;
        }
        catch (ArgumentException)
        {
            return BrokenLinkReason.Unverifiable;
        }

        // 実在する。走査を打ち切っている場合、「範囲の外だから見つからなかった」のか
        // 「範囲内だが未走査だった」のかは区別できない。
        return truncated
            ? BrokenLinkReason.Unverifiable
            : BrokenLinkReason.OutsideRoot;
    }

    /// <summary>
    /// 走査側と同じ検査を、外から来たパスへ適用する（祖先のリンクまで辿る）。
    /// 判定できなければ「見つからない」扱いにして、後段の存在確認へ回す。
    /// </summary>
    private static TraversalCheck SafeCheckTraversal(string path)
    {
        try
        {
            return FileTreeItem.CheckTraversal(path, isDirectory: false);
        }
        catch
        {
            return TraversalCheck.Blocked(TraversalVerdict.NotFound);
        }
    }

    /// <summary>
    /// 表示・保持用に長すぎる文字列を切る。原文のままでないことが分かるよう
    /// 省略記号を付ける（数十万文字の URL でグラフを太らせないため）。
    /// </summary>
    private static string Shorten(string value)
    {
        if (value.Length <= MaxBrokenTargetLength)
        {
            return value;
        }
        // 省略記号を足しても上限を超えないよう、1 文字ぶん残して切る。
        int cut = MaxBrokenTargetLength - 1;
        if (char.IsHighSurrogate(value[cut - 1]))
        {
            cut--; // サロゲートペアの途中で切らない
        }
        return value[..cut] + "…";
    }

    /// <summary>
    /// 上限まで記録する。到達したら以降は捨て、捨てたことをフラグで残す。
    /// 件数と合計文字数の両方を見る（1 件が巨大なら件数だけでは歯止めにならない）。
    /// </summary>
    private static void AddBroken(
        List<BrokenLink> broken, ref bool capped, ref int totalChars, BrokenLink link)
    {
        // 数えるのは 1 件ごとに新しく作られる文字列だけ。SourceFile は
        // ファイルごとに 1 つの文字列を共有するので、件数ぶんは増えない。
        int cost = link.RawTarget.Length + (link.ResolvedPath?.Length ?? 0);

        // これから足すぶんまで見て判定する（足した後に気づくと上限を超える）。
        if (broken.Count >= MaxBrokenLinks || totalChars + cost > MaxBrokenTotalChars)
        {
            capped = true;
            return;
        }

        broken.Add(link);
        totalChars += cost;
    }

    /// <summary>
    /// グラフをナレッジグラフ・ビュー（graph.js）用の JSON に変換する。
    /// System.Text.Json の既定エンコーダは &lt; 等を < にエスケープするため、
    /// &lt;script&gt; への直接埋め込みに対して安全。
    /// </summary>
    public static string ToJson(LinkGraph graph)
    {
        string folderLabel = Path.GetFileName(
            graph.RootFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrEmpty(folderLabel))
        {
            folderLabel = graph.RootFolder;
        }

        var edges = new List<object>();
        foreach (string file in graph.Files)
        {
            foreach (string target in graph.GetLinksFrom(file))
            {
                edges.Add(new { source = file, target });
            }
        }

        var nodes = graph.Files
            .Select(f => new
            {
                id = f,
                label = Path.GetFileName(f),
                degree = graph.GetLinksFrom(f).Count + graph.GetBacklinks(f).Count,
            })
            .ToList();

        return JsonSerializer.Serialize(new
        {
            root = graph.RootFolder,
            folderLabel,
            nodes,
            edges,
        });
    }

    /// <summary>
    /// グラフをリンク切れ・孤立ページビュー（linkcheck.js）用の JSON に変換する（ISSUE #56）。
    /// 孤立の除外規則（index.md / README.md）は表示側の切替なので、ここでは落とさず
    /// ファイル名をそのまま渡す。上限に関わるフラグは 0 件でも必ず含める。
    /// </summary>
    public static string ToLinkCheckJson(LinkGraph graph)
    {
        string folderLabel = Path.GetFileName(
            graph.RootFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrEmpty(folderLabel))
        {
            folderLabel = graph.RootFolder;
        }

        var broken = graph.BrokenLinks
            .Select(b => new
            {
                src = b.SourceFile,
                rel = MakeRelative(graph.RootFolder, b.SourceFile),
                line = b.Line,
                target = b.RawTarget,
                resolved = b.ResolvedPath,
                reason = ReasonKey(b.Reason),
            })
            .ToList();

        var orphans = graph.Files
            .Where(f => graph.GetBacklinks(f).Count == 0)
            .Select(f => new
            {
                path = f,
                rel = MakeRelative(graph.RootFolder, f),
                name = Path.GetFileName(f),
            })
            .ToList();

        return JsonSerializer.Serialize(new
        {
            root = graph.RootFolder,
            folderLabel,
            fileCount = graph.Files.Count,
            truncated = graph.Truncated,
            brokenCapped = graph.BrokenLinksCapped,
            unreadableCount = graph.UnreadableFileCount,
            skippedDirectoryCount = graph.SkippedDirectoryCount,
            maxDepth = FolderSearchService.MaxDirectoryDepth,
            maxFiles = FolderSearchService.MaxFiles,
            broken,
            orphans,
        });
    }

    /// <summary>表示側（linkcheck.js）が文言へ対応付けるためのキー。</summary>
    private static string ReasonKey(BrokenLinkReason reason) => reason switch
    {
        BrokenLinkReason.TargetMissing => "missing",
        BrokenLinkReason.OutsideRoot => "outside",
        BrokenLinkReason.WikiLinkNotFound => "wikiNotFound",
        _ => "unverifiable",
    };

    private static string MakeRelative(string root, string file)
    {
        try
        {
            string rel = Path.GetRelativePath(root, file);
            return string.IsNullOrEmpty(rel) ? file : rel;
        }
        catch (ArgumentException)
        {
            return file;
        }
    }

    /// <summary>1 本のリンクについて抽出した事実。辺にするか壊れているかの判断は Build が行う。</summary>
    private readonly record struct LinkRef(
        string RawTarget,
        string? ResolvedPath,
        int Line,
        bool IsBrokenWikiLink,
        bool IndexComplete);

    /// <summary>
    /// 1 ファイル内の md 間リンクを列挙する。
    /// 対象は LinkInline（インラインリンクと参照リンク。画像は除外）と、
    /// 解決できなかった [[wikilink]]（<see cref="BrokenWikiLinkInline"/>）。
    /// 外部 URL・md 以外の拡張子は無視する。
    ///
    /// 解決できた [[wikilink]] は WikiLinkParser が LinkInline にするため、通常リンクと
    /// 同じ経路で拾える（ISSUE #53）。そのために解析コンテキストを渡す必要がある。
    /// ルートは Build が既に知っているので再推定させない。
    ///
    /// <param name="unreadable">
    /// 大きすぎる・読めないために<b>中身を 1 行も見ていない</b>とき true。
    /// 呼び出し元はこれを数え、画面に出す（黙って 0 件と言わないため。ISSUE #56）。
    /// </param>
    /// </summary>
    private static List<LinkRef> ExtractLinkRefs(string filePath, string root, out bool unreadable)
    {
        var refs = new List<LinkRef>();
        unreadable = false;

        string text;
        try
        {
            var info = new FileInfo(filePath);
            if (info.Length > FolderSearchService.MaxFileBytes)
            {
                unreadable = true;
                return refs;
            }
            text = MarkdownRenderer.ReadFileText(filePath);
        }
        catch (IOException)
        {
            unreadable = true;
            return refs;
        }
        catch (UnauthorizedAccessException)
        {
            unreadable = true;
            return refs;
        }

        MarkdownDocument document;
        try
        {
            document = Markdown.Parse(
                text, MarkdownRenderer.Pipeline, MarkdownRenderer.CreateParserContext(filePath, root));
        }
        catch (OperationCanceledException)
        {
            throw; // タブ破棄によるキャンセルは上へ返す。
        }
        catch
        {
            // 1 ファイルの解析失敗で一覧全体を落とさない。ただし黙って無視すると
            // 「リンク切れ 0 件」の根拠が崩れるので、見ていないファイルとして数える。
            unreadable = true;
            return refs;
        }

        string baseDirectory = Path.GetDirectoryName(filePath) ?? string.Empty;

        foreach (MarkdownObject item in document.Descendants())
        {
            switch (item)
            {
                case BrokenWikiLinkInline wiki:
                    refs.Add(new LinkRef(
                        RawTarget: wiki.RawText.Length > 0 ? wiki.RawText : wiki.Target,
                        ResolvedPath: null,
                        Line: wiki.Line + 1,
                        IsBrokenWikiLink: true,
                        IndexComplete: wiki.IndexComplete));
                    break;

                case LinkInline { IsImage: false } link:
                    refs.Add(new LinkRef(
                        RawTarget: link.Url ?? string.Empty,
                        ResolvedPath: ResolveLocalMarkdownTarget(link.Url, baseDirectory),
                        Line: link.Line + 1,
                        IsBrokenWikiLink: false,
                        IndexComplete: true));
                    break;
            }
        }

        return refs;
    }

    /// <summary>
    /// リンク URL をローカルの md ファイルパスへ解決する。対象外は null。
    /// 相対パスはファイルのフォルダ基準。フラグメント・クエリは除去し、
    /// パーセントエンコード（日本語ファイル名等）はデコードする。
    /// </summary>
    private static string? ResolveLocalMarkdownTarget(string? url, string baseDirectory)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        // 外部 URL（http: / mailto: 等のスキーム付き）は対象外。
        // Windows のドライブレター（C:\...）は Uri として絶対扱いになるため除外してから判定する。
        bool looksLikeDrivePath = url.Length >= 2 && char.IsLetter(url[0]) && url[1] == ':';
        if (!looksLikeDrivePath && Uri.TryCreate(url, UriKind.Absolute, out _))
        {
            return null;
        }

        // フラグメント・クエリを除去する。
        int cut = url.IndexOfAny(new[] { '#', '?' });
        string pathPart = cut >= 0 ? url[..cut] : url;
        if (pathPart.Length == 0)
        {
            return null; // ページ内アンカー（#section のみ）
        }

        pathPart = Uri.UnescapeDataString(pathPart);

        string ext = Path.GetExtension(pathPart);
        bool isMarkdown = ext.Equals(".md", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".markdown", StringComparison.OrdinalIgnoreCase);
        if (!isMarkdown)
        {
            return null;
        }

        try
        {
            return Path.IsPathRooted(pathPart)
                ? Path.GetFullPath(pathPart)
                : Path.GetFullPath(Path.Combine(baseDirectory, pathPart));
        }
        catch (ArgumentException)
        {
            return null; // 不正なパス文字
        }
        catch (PathTooLongException)
        {
            // 数十万文字のリンク先が書かれている場合。1 本のリンクのために
            // フォルダ全体の解析（バックリンク・グラフを含む）を落とさない。
            return null;
        }
        catch (NotSupportedException)
        {
            return null; // ドライブレターを伴わないコロンなど
        }
    }

    private static void AddUnique(Dictionary<string, List<string>> map, string key, string value)
    {
        if (!map.TryGetValue(key, out var list))
        {
            list = new List<string>();
            map[key] = list;
        }
        if (!list.Contains(value, StringComparer.OrdinalIgnoreCase))
        {
            list.Add(value);
        }
    }
}
