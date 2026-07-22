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

    /// <summary>
    /// 意味検索でのファイル最高チャンク類似度（0〜1）。キーワード検索では未使用（既定 0）。
    /// </summary>
    public float Score { get; init; }

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
    internal const int MaxFiles = 500;
    internal const long MaxFileBytes = 2L * 1024 * 1024; // 2MB 超はスキップ
    private const int MaxHitsPerFilePreview = 3;        // ファイルごとのプレビュー行数
    private const int PreviewMaxLength = 100;           // プレビュー 1 行の最大文字数
    private const int MaxDirectoryDepth = 32;

    // 1 検索あたりの OCR 実行時間バジェット（未キャッシュ画像の OCR に使える合計時間）。
    // キャッシュ済みテキストの照合には影響しない。超過分は今回スキップし
    // OcrPending として呼び出し側へ伝える（検索のたびにキャッシュが温まり収束する）。
    private const long MaxOcrMillisPerSearch = 4000;

    /// <summary>
    /// 検索をバックグラウンドで実行する。OcrPending は「OCR 時間バジェット超過で
    /// 未 OCR の画像が残っている」ことを示す（再検索で反映される）。
    /// </summary>
    public static Task<(List<SearchFileResult> Results, bool OcrPending)> SearchAsync(
        string rootFolder, string query, CancellationToken token)
        => Task.Run(() => Search(rootFolder, query, token), token);

    private static (List<SearchFileResult> Results, bool OcrPending) Search(
        string rootFolder, string query, CancellationToken token)
    {
        var results = new List<SearchFileResult>();
        if (string.IsNullOrEmpty(query)
            || string.IsNullOrEmpty(rootFolder)
            || !Directory.Exists(rootFolder))
        {
            return (results, false);
        }

        var ocrBudget = new OcrBudget(MaxOcrMillisPerSearch);
        try
        {
            int scanned = 0;
            foreach (var file in EnumerateMarkdownFiles(rootFolder, token))
            {
                token.ThrowIfCancellationRequested();

                if (scanned >= MaxFiles)
                {
                    break;
                }
                scanned++;

                SearchFileResult? fileResult = SearchInFile(rootFolder, file, query, ocrBudget, token);
                if (fileResult != null)
                {
                    results.Add(fileResult);
                }
            }
        }
        finally
        {
            // 検索中に実行した OCR の結果をまとめてディスクへ反映する
            // （1 枚ごとの全量書き込みを避ける。キャンセル時も温まった分は保存）。
            OcrTextService.FlushCache();
        }

        return (results, ocrBudget.Exhausted);
    }

    /// <summary>1 検索分の OCR 実行時間バジェット。超過後の OCR 要求はスキップさせる。</summary>
    private sealed class OcrBudget
    {
        private readonly long _maxMillis;
        private long _spentMillis;

        public OcrBudget(long maxMillis) => _maxMillis = maxMillis;

        /// <summary>バジェット超過で OCR をスキップした画像があるか。</summary>
        public bool Exhausted { get; private set; }

        /// <summary>OCR を実行してよければ true。超過していれば Exhausted を立てて false。</summary>
        public bool TryConsume()
        {
            if (_spentMillis >= _maxMillis)
            {
                Exhausted = true;
                return false;
            }
            return true;
        }

        public void Add(long elapsedMillis) => _spentMillis += elapsedMillis;
    }

    /// <summary>隠しフォルダ・node_modules・.git をスキップしつつ Markdown を再帰列挙する。</summary>
    internal static IEnumerable<string> EnumerateMarkdownFiles(string root, CancellationToken token)
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
        string root, string file, string query, OcrBudget ocrBudget, CancellationToken token)
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

        // 参照画像の OCR テキストに対するヒットを追加する（R2: OCR テキストは
        // 「ソース」に含める）。OCR 側の失敗はテキスト検索の結果に影響させない。
        try
        {
            AppendOcrHits(file, lines, query, ocrBudget, hits, ref totalHits, token);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // 解析・OCR の失敗は無視（本文ヒットのみ返す）。
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

    /// <summary>
    /// 文書が参照するローカル画像の OCR テキストへの部分一致ヒットを追加する。
    /// 抽出は OcrTextService（%LocalAppData%\Hirake\ocr のキャッシュ共用）に委ね、
    /// キャッシュ済みテキストは常に照合、未キャッシュ画像は 1 検索あたりの
    /// OCR 時間バジェット内でのみ OCR する（検索のたびにキャッシュが温まる）。
    /// 対象は Markdown 記法の画像参照のみ（HTML の &lt;img&gt; は意味検索の索引と
    /// 同様に対象外）。ヒットの行番号は画像参照のソース行（同・画像チャンクと同じ規約）。
    /// </summary>
    private static void AppendOcrHits(
        string file, string[] lines, string query, OcrBudget ocrBudget,
        List<SearchHit> hits, ref int totalHits, CancellationToken token)
    {
        // プリフィルタ: Markdown 画像記法（![）が無いファイルは AST 解析しない
        // （画像なしフォルダの検索速度を維持する）。
        bool mayHaveImage = false;
        foreach (string line in lines)
        {
            if (line.Contains("![", StringComparison.Ordinal))
            {
                mayHaveImage = true;
                break;
            }
        }
        if (!mayHaveImage)
        {
            return;
        }

        string baseDir = Path.GetDirectoryName(Path.GetFullPath(file)) ?? string.Empty;
        Markdig.Syntax.MarkdownDocument document;
        try
        {
            document = Markdig.Markdown.Parse(string.Join("\n", lines), MarkdownRenderer.Pipeline);
        }
        catch
        {
            return;
        }

        // 空白除去フォールバック照合は「空白を含まない非 ASCII クエリ」（日本語等）に限る。
        // Windows OCR は日本語で文字間に空白を挟む（例: 「総 務 部」）ための救済であり、
        // 英語クエリへ適用すると語境界をまたぐ誤ヒットを生むため。
        bool useCollapsedFallback =
            !query.Contains(' ') && !query.Contains('　') && query.Any(c => c > 0x7F);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach ((string imagePath, int refLine) in
                 SemanticIndexService.CollectImageReferences(document, baseDir))
        {
            token.ThrowIfCancellationRequested();
            if (seen.Count >= SemanticIndexService.MaxImagesPerFile)
            {
                break;
            }
            if (!seen.Add(imagePath))
            {
                continue; // 重複パスは 1 回だけ
            }

            // キャッシュ済みなら常に照合。未キャッシュはバジェット内でのみ OCR する。
            string? text;
            if (OcrTextService.TryGetCachedText(imagePath, out string cached))
            {
                text = cached;
            }
            else
            {
                if (!ocrBudget.TryConsume())
                {
                    continue; // 今回はスキップ（OcrPending として呼び出し側へ伝わる）
                }
                long started = Environment.TickCount64;
                text = OcrTextService.ExtractText(imagePath, token, deferCacheSave: true);
                ocrBudget.Add(Environment.TickCount64 - started);
            }

            if (string.IsNullOrEmpty(text))
            {
                continue; // 文字なし・対象外・一時失敗はヒットなし扱い
            }

            string imageName = Path.GetFileName(imagePath);
            foreach (string ocrLine in text.Split('\n'))
            {
                // まず生テキストで照合（英語等、空白が意味を持つ場合）。
                string matchSource = ocrLine;
                int index = ocrLine.IndexOf(query, StringComparison.OrdinalIgnoreCase);

                if (index < 0)
                {
                    if (!useCollapsedFallback)
                    {
                        continue;
                    }
                    string collapsed = ocrLine
                        .Replace(" ", string.Empty)
                        .Replace("　", string.Empty);
                    index = collapsed.IndexOf(query, StringComparison.OrdinalIgnoreCase);
                    if (index < 0)
                    {
                        continue;
                    }
                    matchSource = collapsed;
                }

                totalHits++;
                if (hits.Count < MaxHitsPerFilePreview)
                {
                    hits.Add(new SearchHit
                    {
                        LineNumber = refLine,
                        Preview = $"画像: {imageName}: {BuildPreview(matchSource, index, query.Length)}",
                    });
                }
            }
        }
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
