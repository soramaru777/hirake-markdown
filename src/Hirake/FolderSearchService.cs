using System.IO;
using System.Text;

namespace Hirake;

/// <summary>検索結果 1 ヒット行。</summary>
public sealed class SearchHit
{
    public int LineNumber { get; init; }
    public string Preview { get; init; } = string.Empty;
    public string Display => $"L{LineNumber}: {Preview}";
}

/// <summary>1 ファイル分の検索結果（ファイル情報 + 先頭数件のヒット行 + ヒット総数）。</summary>
public sealed class SearchFileResult
{
    public string FullPath { get; init; } = string.Empty;
    public string FileName { get; init; } = string.Empty;
    public string RelativePath { get; init; } = string.Empty;
    public List<SearchHit> Hits { get; init; } = new();
    public int TotalHits { get; init; }

    /// <summary>ヒット総数表示（例: "3 件" / "12 件"）。</summary>
    public string HitCountLabel => $"{TotalHits} 件";
}

/// <summary>
/// ルートフォルダ配下の *.md / *.markdown を再帰走査し、大文字小文字を無視した
/// 部分一致検索を行う。バックグラウンド（Task.Run）実行・キャンセル対応。
/// </summary>
public static class FolderSearchService
{
    // 走査上限（過剰なコストを避けるためのガード）。
    private const int MaxFiles = 500;
    private const long MaxFileBytes = 2L * 1024 * 1024; // 2MB 超はスキップ
    private const int MaxHitsPerFilePreview = 3;        // ファイルごとのプレビュー行数
    private const int PreviewMaxLength = 100;           // プレビュー 1 行の最大文字数
    private const int MaxDirectoryDepth = 32;

    /// <summary>検索をバックグラウンドで実行する。</summary>
    public static Task<List<SearchFileResult>> SearchAsync(
        string rootFolder, string query, CancellationToken token)
        => Task.Run(() => Search(rootFolder, query, token), token);

    private static List<SearchFileResult> Search(
        string rootFolder, string query, CancellationToken token)
    {
        var results = new List<SearchFileResult>();
        if (string.IsNullOrEmpty(query)
            || string.IsNullOrEmpty(rootFolder)
            || !Directory.Exists(rootFolder))
        {
            return results;
        }

        int scanned = 0;
        foreach (var file in EnumerateMarkdownFiles(rootFolder, token))
        {
            token.ThrowIfCancellationRequested();

            if (scanned >= MaxFiles)
            {
                break;
            }
            scanned++;

            SearchFileResult? fileResult = SearchInFile(rootFolder, file, query, token);
            if (fileResult != null)
            {
                results.Add(fileResult);
            }
        }

        return results;
    }

    /// <summary>隠しフォルダ・node_modules・.git をスキップしつつ Markdown を再帰列挙する。</summary>
    private static IEnumerable<string> EnumerateMarkdownFiles(string root, CancellationToken token)
    {
        var stack = new Stack<(string Dir, int Depth)>();
        stack.Push((root, 0));

        while (stack.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            var (dir, depth) = stack.Pop();

            string[] files;
            try
            {
                files = Directory.GetFiles(dir);
            }
            catch
            {
                continue;
            }

            foreach (var file in files)
            {
                if (FileTreeItem.IsMarkdownFile(file))
                {
                    yield return file;
                }
            }

            if (depth >= MaxDirectoryDepth)
            {
                continue;
            }

            string[] subdirs;
            try
            {
                subdirs = Directory.GetDirectories(dir);
            }
            catch
            {
                continue;
            }

            foreach (var sub in subdirs)
            {
                if (!FileTreeItem.ShouldSkipDirectory(sub))
                {
                    stack.Push((sub, depth + 1));
                }
            }
        }
    }

    private static SearchFileResult? SearchInFile(
        string root, string file, string query, CancellationToken token)
    {
        try
        {
            var info = new FileInfo(file);
            if (info.Length > MaxFileBytes)
            {
                return null;
            }
        }
        catch
        {
            return null;
        }

        string[] lines;
        try
        {
            lines = File.ReadAllLines(file, Encoding.UTF8);
        }
        catch
        {
            return null;
        }

        var hits = new List<SearchHit>();
        int totalHits = 0;

        for (int i = 0; i < lines.Length; i++)
        {
            if ((i & 0x3FF) == 0)
            {
                token.ThrowIfCancellationRequested();
            }

            int index = lines[i].IndexOf(query, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                continue;
            }

            totalHits++;
            if (hits.Count < MaxHitsPerFilePreview)
            {
                hits.Add(new SearchHit
                {
                    LineNumber = i + 1,
                    Preview = BuildPreview(lines[i], index, query.Length),
                });
            }
        }

        if (totalHits == 0)
        {
            return null;
        }

        return new SearchFileResult
        {
            FullPath = file,
            FileName = Path.GetFileName(file),
            RelativePath = MakeRelative(root, file),
            Hits = hits,
            TotalHits = totalHits,
        };
    }

    /// <summary>ヒット位置を中心に前後をトリムした 100 文字程度のプレビューを作る。</summary>
    private static string BuildPreview(string line, int matchIndex, int matchLength)
    {
        string trimmed = line.Trim();
        if (trimmed.Length <= PreviewMaxLength)
        {
            return trimmed;
        }

        // 元行の前方空白ぶんだけマッチ位置を補正する。
        int leading = line.Length - line.TrimStart().Length;
        int matchInTrimmed = Math.Max(0, matchIndex - leading);

        int contextBefore = Math.Max(0, (PreviewMaxLength - matchLength) / 2);
        int start = Math.Max(0, matchInTrimmed - contextBefore);
        int length = Math.Min(PreviewMaxLength, trimmed.Length - start);

        string slice = trimmed.Substring(start, length);
        if (start > 0)
        {
            slice = "…" + slice;
        }
        if (start + length < trimmed.Length)
        {
            slice += "…";
        }
        return slice;
    }

    private static string MakeRelative(string root, string file)
    {
        try
        {
            string rel = Path.GetRelativePath(root, file);
            return string.IsNullOrEmpty(rel) ? file : rel;
        }
        catch
        {
            return file;
        }
    }
}
