using System.IO;
using System.Text.Json;

namespace Hirake;

/// <summary>重複グループの 1 メンバー（文書内の 1 ブロック位置）。</summary>
public sealed class FingerprintMember
{
    public string FullPath { get; init; } = string.Empty;
    public string FileName { get; init; } = string.Empty;
    public string RelativePath { get; init; } = string.Empty;

    /// <summary>ソース行番号（1 始まり。ソースマップ基盤の data-src-line と同じ規約）。</summary>
    public int Line { get; init; }

    /// <summary>
    /// グループ内に正規化テキストが同一のメンバーが他にもいる（=完全一致対の一員）とき true。
    /// 「完全一致のみ」フィルタはこのフラグでメンバーを絞り込む。
    /// </summary>
    public bool ExactDup { get; init; }
}

/// <summary>正規化テキストが同一（完全一致）または類似のブロックの集まり。</summary>
public sealed class FingerprintGroup
{
    /// <summary>全メンバーの正規化テキストが同一（コピペそのまま）なら true。</summary>
    public bool Exact { get; init; }

    /// <summary>
    /// 完全一致対（同一正規化テキストの 2 箇所以上）を 1 つでも含むとき true。
    /// 類似連鎖で Exact=false になっても、完全一致の見逃しを防ぐために使う。
    /// </summary>
    public bool HasExactPair { get; init; }

    /// <summary>代表テキスト（先頭メンバーの平文）。表示側でさらに切り詰める。</summary>
    public string Preview { get; init; } = string.Empty;

    public List<FingerprintMember> Members { get; init; } = new();
}

/// <summary>文書指紋の実行結果。</summary>
public sealed class FingerprintResult
{
    public string RootFolder { get; init; } = string.Empty;
    public List<FingerprintGroup> Groups { get; init; } = new();

    /// <summary>走査またはグループ数の上限に到達し、一部が未反映のとき true。</summary>
    public bool Truncated { get; init; }
}

/// <summary>
/// フォルダ配下の全 Markdown から重複記述（コピペ由来・編集済みコピペ）を検出する。
/// ブロック抽出は StructureQueryService（Markdig AST・走査上限・行番号規約）を共用し、
/// 検出は決定的なテキスト指紋のみで行う（意味索引・モデルには一切依存しない）。
///
/// - 完全一致: 正規化テキスト（空白除去 + 小文字化）のハッシュ一致
/// - 類似一致: 文字 5-gram 集合の Jaccard 類似度が閾値以上（候補は MinHash LSH で絞る）
/// </summary>
public static class FingerprintService
{
    // 正規化後がこの文字数未満のブロックは対象外（定型句・短文の誤検出防止）。
    private const int MinNormalizedLength = 40;

    // 類似判定の文字 n-gram サイズと Jaccard 閾値。
    // 文字 5-gram の Jaccard は短い段落の数文字編集でも大きく下がる
    // （70 文字段落の 3 文字差で約 0.79）ため、「編集済みコピペ」を拾える 0.75 とする。
    private const int GramSize = 5;
    private const double SimilarityThreshold = 0.75;

    // MinHash LSH（BandCount × RowsPerBand = 署名長）。バンドが一致したペアのみ
    // 正確な Jaccard を計算する。8x2 は閾値 0.75 付近の候補検出率が 99% 超
    // （4x4 だと境界域で 2 割超を取りこぼす）。候補が増えても最終判定は
    // 正確な Jaccard なので誤検出は増えない。
    private const int BandCount = 8;
    private const int RowsPerBand = 2;
    private const int SignatureLength = BandCount * RowsPerBand;

    // 表示するグループ数の上限（超過分は Truncated で明示する）。
    private const int MaxGroups = 500;

    // 代表テキスト（Preview）の最大長。表示側はさらに 240 文字へ切り詰める。
    private const int PreviewMaxLength = 300;

    // LSH の 1 バケツあたりの候補上限。極端な偏り（テンプレ半改変が大量等）による
    // バケツ内 O(n^2) Jaccard の暴走を防ぎ、超過時は Truncated で明示する。
    private const int MaxBucketCandidates = 1000;

    /// <summary>rootFolder 配下を走査して重複グループを検出する（同期・CPU バウンド）。</summary>
    public static FingerprintResult Build(string rootFolder, CancellationToken token)
    {
        // ブロック抽出は構造クエリと同じパイプラインを共用する（上限・行番号規約も同一）。
        StructureQueryResult structure = StructureQueryService.Build(rootFolder, token);

        var blocks = CollectBlocks(structure, token);
        int[] parent = Enumerable.Range(0, blocks.Count).ToArray();

        UnionExactMatches(blocks, parent, token);
        bool bucketsTruncated = UnionSimilarMatches(blocks, parent, token);

        (List<FingerprintGroup> groups, bool groupsTruncated) = BuildGroups(blocks, parent, token);

        return new FingerprintResult
        {
            RootFolder = structure.RootFolder,
            Groups = groups,
            Truncated = structure.Truncated || bucketsTruncated || groupsTruncated,
        };
    }

    // ---- ブロック収集 --------------------------------------------------

    private sealed class Block
    {
        public required string FullPath;
        public required string FileName;
        public required string RelativePath;
        public required int Line;
        public required string Text;
        public required string Normalized;
        public required HashSet<int> Grams;
    }

    private static List<Block> CollectBlocks(StructureQueryResult structure, CancellationToken token)
    {
        var blocks = new List<Block>();
        foreach (StructureFileResult file in structure.Files)
        {
            token.ThrowIfCancellationRequested();
            foreach (StructureItem item in file.Items)
            {
                // 見出しは短く定型的なため対象外（本文ブロックのみ）。
                if (item.Kind == "heading")
                {
                    continue;
                }

                string normalized = Normalize(item.Text);
                if (normalized.Length < MinNormalizedLength)
                {
                    continue;
                }

                blocks.Add(new Block
                {
                    FullPath = file.FullPath,
                    FileName = file.FileName,
                    RelativePath = file.RelativePath,
                    Line = item.Line,
                    Text = item.Text,
                    Normalized = normalized,
                    Grams = BuildGrams(normalized),
                });
            }
        }
        return blocks;
    }

    /// <summary>照合用の正規化: 空白（半角/全角/タブ）を除去し小文字化する。</summary>
    private static string Normalize(string text)
    {
        var sb = new System.Text.StringBuilder(text.Length);
        foreach (char c in text)
        {
            if (c is ' ' or '　' or '\t' or '\r' or '\n')
            {
                continue;
            }
            sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }

    private static HashSet<int> BuildGrams(string normalized)
    {
        var grams = new HashSet<int>();
        for (int i = 0; i + GramSize <= normalized.Length; i++)
        {
            grams.Add(string.GetHashCode(normalized.AsSpan(i, GramSize)));
        }
        return grams;
    }

    // ---- Union-Find ----------------------------------------------------

    private static int Find(int[] parent, int i)
    {
        while (parent[i] != i)
        {
            parent[i] = parent[parent[i]]; // 経路圧縮（半分）
            i = parent[i];
        }
        return i;
    }

    private static void Union(int[] parent, int a, int b)
    {
        int ra = Find(parent, a);
        int rb = Find(parent, b);
        if (ra != rb)
        {
            parent[rb] = ra;
        }
    }

    // ---- 完全一致（コピペ） -------------------------------------------

    private static void UnionExactMatches(List<Block> blocks, int[] parent, CancellationToken token)
    {
        var byNormalized = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < blocks.Count; i++)
        {
            if ((i & 0x3FF) == 0)
            {
                token.ThrowIfCancellationRequested();
            }
            if (byNormalized.TryGetValue(blocks[i].Normalized, out int first))
            {
                Union(parent, first, i);
            }
            else
            {
                byNormalized[blocks[i].Normalized] = i;
            }
        }
    }

    // ---- 類似一致（編集済みコピペ、MinHash LSH + Jaccard） -------------

    /// <returns>バケツ上限超過で一部の候補ペアをスキップしたとき true。</returns>
    private static bool UnionSimilarMatches(List<Block> blocks, int[] parent, CancellationToken token)
    {
        // バンドごとに「バンド署名 → ブロック索引リスト」のバケツを作り、
        // 同一バケツ内のペアだけ正確な Jaccard を計算する。
        var buckets = new Dictionary<long, List<int>>();
        bool truncated = false;

        int[][] signatures = new int[blocks.Count][];
        for (int i = 0; i < blocks.Count; i++)
        {
            if ((i & 0xFF) == 0)
            {
                token.ThrowIfCancellationRequested();
            }
            signatures[i] = ComputeMinHash(blocks[i].Grams);
        }

        for (int band = 0; band < BandCount; band++)
        {
            token.ThrowIfCancellationRequested();
            buckets.Clear();

            for (int i = 0; i < blocks.Count; i++)
            {
                long key = ComputeBandKey(signatures[i], band);
                if (!buckets.TryGetValue(key, out List<int>? list))
                {
                    list = new List<int>();
                    buckets[key] = list;
                }
                list.Add(i);
            }

            foreach (List<int> candidates in buckets.Values)
            {
                if (candidates.Count < 2)
                {
                    continue;
                }

                // 極端に偏ったバケツは打ち切る（O(n^2) 暴走防止。truncated として明示）。
                if (candidates.Count > MaxBucketCandidates)
                {
                    truncated = true;
                    continue;
                }

                for (int a = 0; a < candidates.Count; a++)
                {
                    token.ThrowIfCancellationRequested();
                    for (int b = a + 1; b < candidates.Count; b++)
                    {
                        int i = candidates[a];
                        int j = candidates[b];
                        if (Find(parent, i) == Find(parent, j))
                        {
                            continue; // 既に同一グループ
                        }

                        // gram 数の比は Jaccard の上限（J <= min/max）。
                        // 閾値未満が確定するペアは重い集合演算をスキップする（見逃しなし）。
                        int ci = blocks[i].Grams.Count;
                        int cj = blocks[j].Grams.Count;
                        int min = Math.Min(ci, cj);
                        int max = Math.Max(ci, cj);
                        if (max > 0 && (double)min / max < SimilarityThreshold)
                        {
                            continue;
                        }

                        if (Jaccard(blocks[i].Grams, blocks[j].Grams) >= SimilarityThreshold)
                        {
                            Union(parent, i, j);
                        }
                    }
                }
            }
        }
        return truncated;
    }

    private static int[] ComputeMinHash(HashSet<int> grams)
    {
        var signature = new int[SignatureLength];
        for (int s = 0; s < SignatureLength; s++)
        {
            int min = int.MaxValue;
            foreach (int gram in grams)
            {
                int mixed = Mix(gram, s);
                if (mixed < min)
                {
                    min = mixed;
                }
            }
            signature[s] = min;
        }
        return signature;
    }

    /// <summary>gram ハッシュをシードで攪拌する（簡易 xorshift-乗算ミキサ）。</summary>
    private static int Mix(int value, int seed)
    {
        unchecked
        {
            uint x = (uint)value ^ (0x9E3779B9u * (uint)(seed + 1));
            x ^= x >> 16;
            x *= 0x85EBCA6Bu;
            x ^= x >> 13;
            x *= 0xC2B2AE35u;
            x ^= x >> 16;
            return (int)x;
        }
    }

    private static long ComputeBandKey(int[] signature, int band)
    {
        unchecked
        {
            long key = band * 0x100000001B3L;
            for (int r = 0; r < RowsPerBand; r++)
            {
                key = (key * 31) ^ signature[band * RowsPerBand + r];
            }
            return key;
        }
    }

    private static double Jaccard(HashSet<int> a, HashSet<int> b)
    {
        if (a.Count == 0 || b.Count == 0)
        {
            return 0;
        }
        HashSet<int> smaller = a.Count <= b.Count ? a : b;
        HashSet<int> larger = ReferenceEquals(smaller, a) ? b : a;

        int intersection = 0;
        foreach (int gram in smaller)
        {
            if (larger.Contains(gram))
            {
                intersection++;
            }
        }
        int union = a.Count + b.Count - intersection;
        return union == 0 ? 0 : (double)intersection / union;
    }

    // ---- グループ構築 --------------------------------------------------

    private static (List<FingerprintGroup> Groups, bool Truncated) BuildGroups(
        List<Block> blocks, int[] parent, CancellationToken token)
    {
        var byRoot = new Dictionary<int, List<int>>();
        for (int i = 0; i < blocks.Count; i++)
        {
            if ((i & 0x3FF) == 0)
            {
                token.ThrowIfCancellationRequested();
            }
            int root = Find(parent, i);
            if (!byRoot.TryGetValue(root, out List<int>? list))
            {
                list = new List<int>();
                byRoot[root] = list;
            }
            list.Add(i);
        }

        var groups = new List<FingerprintGroup>();
        foreach (List<int> members in byRoot.Values)
        {
            token.ThrowIfCancellationRequested();
            if (members.Count < 2)
            {
                continue; // 重複していないブロック
            }

            // グループ内の正規化テキスト出現数（メンバー単位の完全一致対の判定に使う）。
            var normalizedCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (int m in members)
            {
                normalizedCounts[blocks[m].Normalized] =
                    normalizedCounts.GetValueOrDefault(blocks[m].Normalized) + 1;
            }

            bool exact = normalizedCounts.Count == 1;
            bool hasExactPair = normalizedCounts.Values.Any(c => c >= 2);

            var memberList = members
                .Select(m => blocks[m])
                .OrderBy(b => b.RelativePath, StringComparer.OrdinalIgnoreCase)
                .ThenBy(b => b.Line)
                .Select(b => new FingerprintMember
                {
                    FullPath = b.FullPath,
                    FileName = b.FileName,
                    RelativePath = b.RelativePath,
                    Line = b.Line,
                    ExactDup = normalizedCounts[b.Normalized] >= 2,
                })
                .ToList();

            string preview = blocks[members[0]].Text;
            if (preview.Length > PreviewMaxLength)
            {
                preview = preview[..PreviewMaxLength] + "…";
            }

            groups.Add(new FingerprintGroup
            {
                Exact = exact,
                HasExactPair = hasExactPair,
                Preview = preview,
                Members = memberList,
            });
        }

        // 重複箇所が多いグループを先頭に。
        groups.Sort((x, y) => y.Members.Count.CompareTo(x.Members.Count));

        bool truncated = false;
        if (groups.Count > MaxGroups)
        {
            groups.RemoveRange(MaxGroups, groups.Count - MaxGroups);
            truncated = true;
        }
        return (groups, truncated);
    }

    // ---- JSON ----------------------------------------------------------

    /// <summary>
    /// テンプレートの &lt;script&gt; へ直接埋め込める JSON を生成する
    /// （System.Text.Json の既定エスケープにより &lt; などは \uXXXX 化される）。
    /// </summary>
    public static string ToJson(FingerprintResult result)
    {
        string folderLabel = Path.GetFileName(
            result.RootFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrEmpty(folderLabel))
        {
            folderLabel = result.RootFolder;
        }

        var groups = result.Groups
            .Select(g => new
            {
                exact = g.Exact,
                hasExactPair = g.HasExactPair,
                preview = g.Preview,
                members = g.Members.Select(m => new
                {
                    path = m.FullPath,
                    name = m.FileName,
                    rel = m.RelativePath,
                    line = m.Line,
                    exactDup = m.ExactDup,
                }).ToList(),
            })
            .ToList();

        return JsonSerializer.Serialize(new
        {
            root = result.RootFolder,
            folderLabel,
            truncated = result.Truncated,
            groups,
        });
    }
}
