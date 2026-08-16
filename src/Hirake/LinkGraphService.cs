using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Hirake;

/// <summary>
/// 依存グラフ基盤（全体ルール R5 の共有コンポーネント）。
/// フォルダ内の全 Markdown ファイルを解析し、md 間リンクの正引き・逆引きを提供する。
/// ナレッジグラフ・ビューのほか、将来のバックリンクパネル・リンク切れ検出が利用する。
/// </summary>
public sealed class LinkGraph
{
    private readonly Dictionary<string, List<string>> _forward;
    private readonly Dictionary<string, List<string>> _backward;

    /// <summary>解析対象となった全ファイル（フルパス）。</summary>
    public IReadOnlyList<string> Files { get; }

    /// <summary>グラフの起点フォルダ（フルパス）。</summary>
    public string RootFolder { get; }

    internal LinkGraph(
        string rootFolder,
        IReadOnlyList<string> files,
        Dictionary<string, List<string>> forward,
        Dictionary<string, List<string>> backward)
    {
        RootFolder = rootFolder;
        Files = files;
        _forward = forward;
        _backward = backward;
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

        foreach (string file in FolderSearchService.EnumerateMarkdownFiles(root, token))
        {
            if (files.Count >= FolderSearchService.MaxFiles)
            {
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

        foreach (string file in files)
        {
            token.ThrowIfCancellationRequested();

            foreach (string target in ExtractMarkdownLinkTargets(file))
            {
                // 解析対象（ルート配下の実在ファイル）へのリンクのみエッジにする。
                if (!fileSet.TryGetValue(target, out string? canonical))
                {
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

        return new LinkGraph(root, files, forward, backward);
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
    /// 1 ファイル内の md 間リンクのターゲット（解決済みフルパス）を列挙する。
    /// 対象は LinkInline（インラインリンクと参照リンク。画像は除外）。
    /// 外部 URL・md 以外の拡張子は無視する。
    /// </summary>
    private static IEnumerable<string> ExtractMarkdownLinkTargets(string filePath)
    {
        string text;
        try
        {
            var info = new FileInfo(filePath);
            if (info.Length > FolderSearchService.MaxFileBytes)
            {
                yield break;
            }
            text = MarkdownRenderer.ReadFileText(filePath);
        }
        catch (IOException)
        {
            yield break;
        }
        catch (UnauthorizedAccessException)
        {
            yield break;
        }

        MarkdownDocument document = Markdown.Parse(text, MarkdownRenderer.Pipeline);
        string baseDirectory = Path.GetDirectoryName(filePath) ?? string.Empty;

        foreach (MarkdownObject item in document.Descendants())
        {
            if (item is not LinkInline { IsImage: false } link)
            {
                continue;
            }
            string? resolved = ResolveLocalMarkdownTarget(link.Url, baseDirectory);
            if (resolved != null)
            {
                yield return resolved;
            }
        }
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
