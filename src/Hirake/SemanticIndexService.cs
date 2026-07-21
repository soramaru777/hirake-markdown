using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;

namespace Hirake;

/// <summary>
/// 基盤③「テキスト理解索引」の最初の実装。multilingual-e5-small（ONNX 量子化）を用いて
/// フォルダ配下 Markdown を見出し単位でベクトル化し、意味的な横断検索を提供する。
///
/// モデル管理（自動DL）・トークナイズ（XLM-RoBERTa 方式）・埋め込み（OnnxRuntime）・
/// チャンク分割・索引キャッシュ・検索 API を 1 つの static サービスに集約する。
/// ファイル列挙・読込規則は FolderSearchService / LinkGraphService と共有する（重複実装しない）。
///
/// 失敗時は例外を投げず error 文字列を返す設計で、呼び出し側はキーワード検索へフォールバックできる。
/// </summary>
public static class SemanticIndexService
{
    // ---- モデル定義 ---------------------------------------------------

    private const string ModelName = "multilingual-e5-small";
    private const int EmbeddingDim = 384;   // e5-small の隠れ層次元
    private const int MaxSeqLen = 512;      // モデルの最大系列長
    private const int BatchSize = 8;        // 埋め込みのバッチ幅
    // 1 チャンクあたりの最大ウィンドウ数（先頭優先）。見出しの無い巨大ファイルが
    // 1 チャンクになった場合の推論コスト・メモリを上限で抑える近似。
    private const int MaxWindowsPerChunk = 8;
    // トークナイズ前の文字数上限。トークナイズ自体は中断できない一括処理のため、
    // 入力を事前に切り詰めて巨大チャンクでキャンセルが長時間効かなくなるのを防ぐ。
    // 最大ウィンドウ分（8 × 510 ≒ 4,080 トークン）に対し、英語系（1 トークン ≒ 平均 4 文字強）
    // でも足りるよう 4 倍の余裕を持たせる。この長さのトークナイズは数十 ms で完了する。
    private const int MaxCharsPerChunk = 65_536;

    // fairseq / XLM-RoBERTa の特殊トークン ID。
    private const int BosId = 0;   // <s>
    private const int PadId = 1;   // <pad>
    private const int EosId = 2;   // </s>
    private const int UnkId = 3;   // <unk>

    // 索引の 1 ファイルあたりチャンク数上限（巨大ファイルでの過剰な推論コストを抑える）。
    private const int MaxChunksPerFile = 128;
    // 1 文書あたり OCR 対象にする画像参照の上限（超過分は無視）。
    private const int MaxImagesPerFile = 50;
    // 検索時に採用するチャンク上位件数。
    private const int TopChunks = 30;
    // 明らかな無関係・破損由来のスコアを弾く下限（e5 は無関係文でも 0.7 台に寄るため、
    // 通常ヒットの足切りではなく異常値のサニティフロアとして機能する）。
    private const float MinScore = 0.5f;
    // 埋め込みの生成条件（ウィンドウ上限・切り詰め等）を変えたらバージョンを上げ、
    // 生成条件の異なるベクトルが同一索引内に混在しないようにする。
    // v3: 画像 OCR チャンクの追加でチャンク生成条件が変わったため引き上げ。
    // v4: 本文/OCR チャンクの枠分離・未作成画像スタンプ・file URI 対応で再引き上げ。
    private const int IndexFormatVersion = 4;

    private sealed record ModelFile(string FileName, string Url, long Size, string Sha256);

    // URL はリポジトリの特定コミットに固定し、SHA-256 でダウンロードの完全性を検証する
    // （可変ブランチ参照だと配布元の差し替え・別ファイル化を検出できないため）。
    private static readonly ModelFile[] RequiredFiles =
    {
        new(
            "model_quantized.onnx",
            "https://huggingface.co/Xenova/multilingual-e5-small/resolve/761b726dd34fb83930e26aab4e9ac3899aa1fa78/onnx/model_quantized.onnx",
            118_308_185,
            "f80102d3f2a1229f387d3c81909990d8945513e347b0eab049f7de3c6f98c193"),
        new(
            "sentencepiece.bpe.model",
            "https://huggingface.co/intfloat/multilingual-e5-small/resolve/614241f622f53c4eeff9890bdc4f31cfecc418b3/sentencepiece.bpe.model",
            5_069_051,
            "cfc8146abe2a0488e9e2a0c56de7952f7c11ab059eca145a0a727afce0db2865"),
    };

    // ---- プロセス内で使い回す遅延初期化リソース ----------------------

    private static readonly object InitLock = new();
    private static InferenceSession? _session;
    private static bool _needsTokenTypeIds;
    private static SentencePieceTokenizer? _tokenizer;
    private static readonly HttpClient Http = CreateHttpClient();

    // モデル準備の直列化（検索の連打・トグル切替で DL が並走して一時ファイルが競合するのを防ぐ）。
    private static readonly SemaphoreSlim ModelGate = new(1, 1);
    // 既存ファイルのハッシュ検証はコストが高い（118MB）ため、プロセス内で 1 回だけ行う。
    private static readonly HashSet<string> VerifiedFiles = new(StringComparer.OrdinalIgnoreCase);

    // 直近のルートに対する索引（メモリ内）。ベクトルの base64 復号を毎回避けるための単一キャッシュ。
    private static readonly object MemoryLock = new();
    private static string? _memoryRoot;
    private static Dictionary<string, FileEntry>? _memoryIndex;

    private static HttpClient CreateHttpClient()
    {
        // リダイレクト（huggingface → CDN）追従は既定で有効。タイムアウトのみ広げる。
        var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true })
        {
            Timeout = TimeSpan.FromMinutes(30),
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Hirake/1.0");
        return client;
    }

    // ---- 保存先パス ---------------------------------------------------

    private static string ModelDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Hirake", "models", ModelName);

    private static string IndexDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Hirake", "index");

    // ================================================================
    //  検索 API
    // ================================================================

    /// <summary>
    /// 意味的横断検索を実行する。クエリ埋め込みと全チャンク埋め込みのコサイン類似度から
    /// ファイル単位の結果（既存の SearchFileResult / SearchHit 形式）を構築して返す。
    /// 失敗時は空リストと error 文字列を返す（呼び出し側でフォールバック可能）。
    /// </summary>
    public static async Task<(List<SearchFileResult> results, string? error)> SearchAsync(
        string rootFolder, string query, IProgress<string>? progress, CancellationToken ct)
    {
        var empty = new List<SearchFileResult>();
        if (string.IsNullOrWhiteSpace(query))
        {
            return (empty, null);
        }
        if (string.IsNullOrEmpty(rootFolder) || !Directory.Exists(rootFolder))
        {
            return (empty, "検索対象のフォルダがありません");
        }

        try
        {
            // 1) モデルの用意（未取得なら自動ダウンロード）。
            (bool ok, string? modelError) = await EnsureModelAsync(progress, ct).ConfigureAwait(false);
            if (!ok)
            {
                return (empty, modelError ?? "モデルを準備できませんでした");
            }

            // 2) 索引の構築 / 更新（変化したファイルのみ再埋め込み）。
            List<(string Path, string Heading, int Line, float[] Vec)> chunks =
                await Task.Run(() => BuildOrUpdateIndex(rootFolder, progress, ct), ct).ConfigureAwait(false);

            if (chunks.Count == 0)
            {
                return (empty, null);
            }

            // 3) クエリ埋め込み → 全チャンクとコサイン類似度。
            progress?.Report("検索中…");
            float[] queryVec = await Task.Run(
                () => EmbedTexts(new[] { "query: " + query.Trim() }, ct)[0], ct).ConfigureAwait(false);

            var scored = new List<(int Index, float Score)>(chunks.Count);
            for (int i = 0; i < chunks.Count; i++)
            {
                if ((i & 0x3FF) == 0)
                {
                    ct.ThrowIfCancellationRequested();
                }
                scored.Add((i, Dot(queryVec, chunks[i].Vec)));
            }

            // 4) 上位チャンクをファイル単位へ集約する（異常スコアはサニティフロアで除外）。
            var top = scored
                .Where(s => s.Score >= MinScore && float.IsFinite(s.Score))
                .OrderByDescending(s => s.Score)
                .Take(TopChunks)
                .ToList();

            List<SearchFileResult> results = GroupByFile(rootFolder, chunks, top);
            return (results, null);
        }
        catch (OperationCanceledException)
        {
            return (empty, null);
        }
        catch (Exception ex)
        {
            return (empty, "意味検索に失敗しました: " + ex.Message);
        }
    }

    // ================================================================
    //  レコメンド API（関連文書「次に読む」）
    // ================================================================

    /// <summary>
    /// レコメンド機能を副作用なしで実行できる状態かを判定する。
    /// モデル必須ファイルがすべてローカルに存在し、かつ該当ルートの索引キャッシュ
    /// （IndexFilePath(root)）が既に存在する場合のみ true を返す。
    /// この判定は一切のダウンロード・索引構築を行わない（存在確認のみ）。
    /// </summary>
    public static bool IsReadyForRecommendations(string rootFolder)
    {
        if (string.IsNullOrEmpty(rootFolder))
        {
            return false;
        }
        try
        {
            foreach (ModelFile file in RequiredFiles)
            {
                if (!File.Exists(Path.Combine(ModelDirectory, file.FileName)))
                {
                    return false;
                }
            }
            // 索引スコープの正規化は BuildOrUpdateIndex / SaveIndex と揃える（Path.GetFullPath）。
            string root = Path.GetFullPath(rootFolder);
            return File.Exists(IndexFilePath(root));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 表示中文書と意味的に似た文書を、既存の意味索引ベクトルを流用して上位 topN 件返す。
    /// モデル・索引が未準備（IsReadyForRecommendations が false）のときは
    /// 一切の副作用なく空リストを返す（ダウンロードも索引構築もしない）。
    ///
    /// 準備済みなら既存の差分更新（BuildOrUpdateIndex）でベクトルを用意し、
    /// ファイル単位ベクトル（全チャンクベクトルの平均を L2 正規化）同士のコサイン類似度で
    /// 表示中文書に近い文書を選ぶ。自分自身（パス大文字小文字無視で一致）と、
    /// サニティフロア MinScore 未満は除外する。表示中文書が索引に無い（チャンク0件等）場合は空。
    /// 例外は握って空リストを返す（SearchAsync と同方針）。OperationCanceledException は伝播する。
    /// </summary>
    public static async Task<List<(string Path, string Name, float Score)>> GetRecommendationsAsync(
        string rootFolder, string filePath, int topN, CancellationToken ct)
    {
        var empty = new List<(string Path, string Name, float Score)>();

        // 未準備なら副作用ゼロで即終了（ここが本機能の最重要仕様）。
        if (topN <= 0 || string.IsNullOrEmpty(filePath) || !IsReadyForRecommendations(rootFolder))
        {
            return empty;
        }

        try
        {
            // 直近に更新済みのメモリ索引があれば再走査せず使い回す（タブ切替・
            // 連続ナビゲーションのたびに全フォルダの差分走査が走るのを防ぐ）。
            List<(string Path, string Heading, int Line, float[] Vec)>? chunks =
                TryGetFreshMemoryChunks(rootFolder);

            // 無ければ既存索引の差分更新のみを実行する（既存索引が読めない場合は
            // requireExistingIndex により何も構築・保存されず空が返る = 副作用ゼロ）。
            chunks ??= await Task.Run(
                    () => BuildOrUpdateIndex(rootFolder, null, ct, requireExistingIndex: true), ct)
                .ConfigureAwait(false);

            if (chunks.Count == 0)
            {
                return empty;
            }

            // ファイル単位ベクトル = そのファイルの全チャンクベクトルの平均 → L2 正規化。
            // チャンク0件のファイルはそもそも chunks に現れないため自然に対象外になる。
            var fileVectors = new Dictionary<string, float[]>(StringComparer.OrdinalIgnoreCase);
            foreach ((string path, _, _, float[] vec) in chunks)
            {
                if (!fileVectors.TryGetValue(path, out float[]? acc))
                {
                    acc = new float[EmbeddingDim];
                    fileVectors[path] = acc;
                }
                int len = Math.Min(EmbeddingDim, vec.Length);
                for (int d = 0; d < len; d++)
                {
                    acc[d] += vec[d];
                }
            }
            ct.ThrowIfCancellationRequested();
            foreach (float[] acc in fileVectors.Values)
            {
                Normalize(acc);
            }

            // 表示中文書のファイルベクトル（索引に無ければ空）。
            string target = Path.GetFullPath(filePath);
            if (!fileVectors.TryGetValue(target, out float[]? targetVec))
            {
                return empty;
            }

            var scored = new List<(string Path, float Score)>(fileVectors.Count);
            foreach (KeyValuePair<string, float[]> kv in fileVectors)
            {
                // 自分自身はスコア計算前に除外する。
                if (string.Equals(kv.Key, target, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                float score = Dot(targetVec, kv.Value);
                if (float.IsFinite(score) && score >= MinScore)
                {
                    scored.Add((kv.Key, score));
                }
            }

            return scored
                .OrderByDescending(s => s.Score)
                .Take(topN)
                .Select(s => (s.Path, Path.GetFileName(s.Path), s.Score))
                .ToList();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return empty;
        }
    }

    // メモリ索引を「新鮮」とみなす期間。この間はレコメンドが差分走査を省略する。
    private const long MemoryIndexFreshMs = 60_000;

    /// <summary>
    /// 直近（MemoryIndexFreshMs 以内）に更新されたメモリ索引が同一ルートに対して
    /// 存在すれば、その全チャンクの平坦リストを返す。無ければ null。
    /// ディスク・ネットワークへのアクセスは行わない。
    /// </summary>
    private static List<(string Path, string Heading, int Line, float[] Vec)>? TryGetFreshMemoryChunks(
        string rootFolder)
    {
        string root = Path.GetFullPath(rootFolder);

        // ディスク索引が（構築後に）削除されていたら短絡しない。
        // 内容の破損まではここでは検証しない: この索引は直前（60秒以内）に自プロセスが
        // 書いたものであり、その間の外部破損は脅威モデル外。読み取り専用経路のため
        // 副作用（再構築・上書き）も発生しない。
        if (!File.Exists(IndexFilePath(root)))
        {
            return null;
        }

        lock (MemoryLock)
        {
            if (_memoryIndex == null
                || !string.Equals(_memoryRoot, root, StringComparison.OrdinalIgnoreCase)
                || Environment.TickCount64 - _memoryIndexBuiltAtTick > MemoryIndexFreshMs)
            {
                return null;
            }

            var flat = new List<(string, string, int, float[])>();
            foreach (FileEntry entry in _memoryIndex.Values)
            {
                foreach (ChunkEntry chunk in entry.Chunks)
                {
                    flat.Add((entry.Path, chunk.Heading, chunk.Line, chunk.Vec));
                }
            }
            return flat;
        }
    }

    /// <summary>上位チャンクをファイル単位にまとめ、最高チャンクスコア順で SearchFileResult に整形する。</summary>
    private static List<SearchFileResult> GroupByFile(
        string rootFolder,
        List<(string Path, string Heading, int Line, float[] Vec)> chunks,
        List<(int Index, float Score)> top)
    {
        var byFile = new Dictionary<string, (float Max, List<SearchHit> Hits)>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();

        foreach ((int index, float score) in top)
        {
            var chunk = chunks[index];
            if (!byFile.TryGetValue(chunk.Path, out var entry))
            {
                entry = (float.NegativeInfinity, new List<SearchHit>());
                order.Add(chunk.Path);
            }
            entry.Hits.Add(new SearchHit
            {
                LineNumber = chunk.Line,
                Preview = BuildHitPreview(chunk.Heading, score),
            });
            if (score > entry.Max)
            {
                entry.Max = score;
            }
            byFile[chunk.Path] = entry;
        }

        return order
            .Select(path =>
            {
                var (max, hits) = byFile[path];
                return new SearchFileResult
                {
                    FullPath = path,
                    FileName = Path.GetFileName(path),
                    RelativePath = MakeRelative(rootFolder, path),
                    Hits = hits,
                    TotalHits = hits.Count,
                    Score = max,
                };
            })
            .OrderByDescending(r => r.Score)
            .ToList();
    }

    /// <summary>ヒット行のプレビュー文（見出し + 類似度スコア）。</summary>
    private static string BuildHitPreview(string heading, float score)
    {
        string label = string.IsNullOrWhiteSpace(heading) ? "（本文冒頭）" : heading.Trim();
        return $"{label}（類似度 {score:0.00}）";
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

    // ================================================================
    //  モデル管理（自動ダウンロード）
    // ================================================================

    /// <summary>
    /// 必要なモデルファイルが揃っているか確認し、無ければダウンロードする。
    /// 一時ファイル(.tmp)に書き出してから完了後にリネームし、中断による破損を避ける。
    /// </summary>
    private static async Task<(bool ok, string? error)> EnsureModelAsync(
        IProgress<string>? progress, CancellationToken ct)
    {
        // モデル準備は全体を直列化する。待機側はキャンセル可能。
        await ModelGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(ModelDirectory);

            foreach (ModelFile file in RequiredFiles)
            {
                string dest = Path.Combine(ModelDirectory, file.FileName);
                if (File.Exists(dest)
                    && new FileInfo(dest).Length == file.Size
                    && VerifyFileHash(dest, file.Sha256, progress))
                {
                    continue;
                }

                string? error = await DownloadFileAsync(file, dest, progress, ct).ConfigureAwait(false);
                if (error != null)
                {
                    return (false, error);
                }
            }
            return (true, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (false, "モデルの準備に失敗しました: " + ex.Message);
        }
        finally
        {
            ModelGate.Release();
        }
    }

    /// <summary>
    /// 既存ファイルの SHA-256 を検証する。コストが高いためプロセス内で 1 回だけ実施し、
    /// 以降は成功結果を再利用する。不一致なら false（呼び出し側で再ダウンロード）。
    /// </summary>
    private static bool VerifyFileHash(string path, string expectedSha256, IProgress<string>? progress)
    {
        lock (VerifiedFiles)
        {
            if (VerifiedFiles.Contains(path))
            {
                return true;
            }
        }

        progress?.Report("モデルファイルを検証中…");
        using FileStream fs = File.OpenRead(path);
        byte[] hash = SHA256.HashData(fs);
        if (!Convert.ToHexStringLower(hash).Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        lock (VerifiedFiles)
        {
            VerifiedFiles.Add(path);
        }
        return true;
    }

    private static async Task<string?> DownloadFileAsync(
        ModelFile file, string dest, IProgress<string>? progress, CancellationToken ct)
    {
        // 一時ファイル名は毎回一意にする（過去の中断残骸や他試行との衝突を避ける）。
        string tmp = dest + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

            using (HttpResponseMessage response = await Http
                .GetAsync(file.Url, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();

                long total = response.Content.Headers.ContentLength ?? file.Size;
                long totalMb = total / (1024 * 1024);

                await using Stream source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using var target = new FileStream(
                    tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);

                var buffer = new byte[1 << 16];
                long read = 0;
                long lastReported = -1;
                int n;
                while ((n = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                    hasher.AppendData(buffer, 0, n);
                    read += n;
                    long readMb = read / (1024 * 1024);
                    if (readMb != lastReported)
                    {
                        lastReported = readMb;
                        progress?.Report($"モデルをダウンロード中... {readMb} MB / {totalMb} MB");
                    }
                }
            }

            // サイズ + SHA-256 検証（破損・途中終了・改ざんの検出）。
            long actual = new FileInfo(tmp).Length;
            if (actual != file.Size)
            {
                TryDelete(tmp);
                return $"モデルのダウンロードに失敗しました（サイズ不一致: {actual} / {file.Size}）";
            }
            string actualSha = Convert.ToHexStringLower(hasher.GetHashAndReset());
            if (!actualSha.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                TryDelete(tmp);
                return "モデルのダウンロードに失敗しました（ハッシュ不一致）";
            }

            File.Move(tmp, dest, overwrite: true);
            lock (VerifiedFiles)
            {
                VerifiedFiles.Add(dest);
            }
            return null;
        }
        catch (OperationCanceledException)
        {
            TryDelete(tmp);
            throw;
        }
        catch (Exception ex)
        {
            TryDelete(tmp);
            return "モデルのダウンロードに失敗しました: " + ex.Message;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // 後始末の失敗は無視する。
        }
    }

    // ================================================================
    //  トークナイズ（XLM-RoBERTa 方式）
    // ================================================================

    private static SentencePieceTokenizer GetTokenizer()
    {
        if (_tokenizer != null)
        {
            return _tokenizer;
        }
        lock (InitLock)
        {
            if (_tokenizer == null)
            {
                string modelPath = Path.Combine(ModelDirectory, "sentencepiece.bpe.model");
                using FileStream fs = File.OpenRead(modelPath);
                // BOS/EOS は手動付与するため自動付与は無効化する。
                _tokenizer = SentencePieceTokenizer.Create(
                    fs, addBeginningOfSentence: false, addEndOfSentence: false);
            }
        }
        return _tokenizer;
    }

    /// <summary>
    /// テキストを HF（fairseq オフセット済み）の語彙 ID 列へ変換する（特殊トークンは含めない）。
    /// SentencePiece の各 ID を +1 する。ただし sp の unk(=0) は fairseq の unk(=3) にマップする。
    /// </summary>
    private static List<int> EncodeContentIds(string text)
    {
        // トークナイズは一括処理で中断できないため、入力自体を上限長へ切り詰める
        // （埋め込みに使うのは最大 MaxWindowsPerChunk ウィンドウ分のみ）。
        if (text.Length > MaxCharsPerChunk)
        {
            text = text[..MaxCharsPerChunk];
        }

        SentencePieceTokenizer tokenizer = GetTokenizer();
        IReadOnlyList<int> spIds = tokenizer.EncodeToIds(
            text, addBeginningOfSentence: false, addEndOfSentence: false);

        var ids = new List<int>(spIds.Count);
        foreach (int id in spIds)
        {
            ids.Add(id == 0 ? UnkId : id + 1);
        }
        return ids;
    }

    /// <summary>
    /// 本文 ID 列を最大系列長に収まる複数ウィンドウへ分割し、各ウィンドウを
    /// [&lt;s&gt;] + 本文 + [&lt;/s&gt;] の入力 ID 列（int64）にして返す。
    /// </summary>
    private static List<long[]> BuildInputWindows(List<int> contentIds)
    {
        const int contentBudget = MaxSeqLen - 2; // 特殊トークン 2 個分を確保
        var windows = new List<long[]>();

        if (contentIds.Count == 0)
        {
            windows.Add(new long[] { BosId, EosId });
            return windows;
        }

        for (int start = 0;
             start < contentIds.Count && windows.Count < MaxWindowsPerChunk;
             start += contentBudget)
        {
            int len = Math.Min(contentBudget, contentIds.Count - start);
            var seq = new long[len + 2];
            seq[0] = BosId;
            for (int i = 0; i < len; i++)
            {
                seq[i + 1] = contentIds[start + i];
            }
            seq[len + 1] = EosId;
            windows.Add(seq);
        }
        return windows;
    }

    // ================================================================
    //  埋め込み計算（OnnxRuntime）
    // ================================================================

    private static InferenceSession GetSession()
    {
        if (_session != null)
        {
            return _session;
        }
        lock (InitLock)
        {
            if (_session == null)
            {
                string onnxPath = Path.Combine(ModelDirectory, "model_quantized.onnx");
                var session = new InferenceSession(onnxPath);
                _needsTokenTypeIds = session.InputMetadata.ContainsKey("token_type_ids");
                _session = session;
            }
        }
        return _session;
    }

    /// <summary>
    /// 複数テキスト（e5 のプレフィックス付与済みを想定）を埋め込みベクトル（L2 正規化済み）へ変換する。
    /// 長いテキストは複数ウィンドウに分割し、mean pooling 後にウィンドウ平均を取ってから正規化する。
    /// </summary>
    private static float[][] EmbedTexts(IReadOnlyList<string> texts, CancellationToken ct)
    {
        // 各テキストを入力ウィンドウ列へ展開し、フラットに埋め込んでから集約する。
        var windowsPerText = new List<long[]>[texts.Count];
        var flat = new List<long[]>();
        var owner = new List<int>();

        for (int t = 0; t < texts.Count; t++)
        {
            // トークナイズは重い処理なのでテキストごとにキャンセルを確認する。
            ct.ThrowIfCancellationRequested();
            List<int> contentIds = EncodeContentIds(texts[t]);
            List<long[]> windows = BuildInputWindows(contentIds);
            windowsPerText[t] = windows;
            foreach (long[] w in windows)
            {
                flat.Add(w);
                owner.Add(t);
            }
        }

        var flatVectors = new float[flat.Count][];
        for (int offset = 0; offset < flat.Count; offset += BatchSize)
        {
            ct.ThrowIfCancellationRequested();
            int count = Math.Min(BatchSize, flat.Count - offset);
            RunBatch(flat, offset, count, flatVectors);
        }

        // テキストごとに、そのウィンドウ埋め込みの平均を取り再正規化する。
        var result = new float[texts.Count][];
        int cursor = 0;
        for (int t = 0; t < texts.Count; t++)
        {
            int wc = windowsPerText[t].Count;
            var acc = new float[EmbeddingDim];
            for (int w = 0; w < wc; w++)
            {
                float[] v = flatVectors[cursor + w];
                for (int d = 0; d < EmbeddingDim; d++)
                {
                    acc[d] += v[d];
                }
            }
            Normalize(acc);
            result[t] = acc;
            cursor += wc;
        }
        return result;
    }

    /// <summary>1 バッチ分（可変長）をパディングして推論し、mean pooling + L2 正規化した結果を書き込む。</summary>
    private static void RunBatch(List<long[]> sequences, int offset, int count, float[][] output)
    {
        // token_type_ids の要否は初回のセッション初期化で確定するため、先に済ませる。
        InferenceSession session = GetSession();

        int maxLen = 0;
        for (int i = 0; i < count; i++)
        {
            maxLen = Math.Max(maxLen, sequences[offset + i].Length);
        }

        var inputIds = new DenseTensor<long>(new[] { count, maxLen });
        var attentionMask = new DenseTensor<long>(new[] { count, maxLen });
        DenseTensor<long>? tokenTypeIds = _needsTokenTypeIds
            ? new DenseTensor<long>(new[] { count, maxLen })
            : null;

        for (int b = 0; b < count; b++)
        {
            long[] seq = sequences[offset + b];
            for (int s = 0; s < maxLen; s++)
            {
                bool real = s < seq.Length;
                inputIds[b, s] = real ? seq[s] : PadId;
                attentionMask[b, s] = real ? 1 : 0;
                if (tokenTypeIds != null)
                {
                    tokenTypeIds[b, s] = 0;
                }
            }
        }

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input_ids", inputIds),
            NamedOnnxValue.CreateFromTensor("attention_mask", attentionMask),
        };
        if (tokenTypeIds != null)
        {
            inputs.Add(NamedOnnxValue.CreateFromTensor("token_type_ids", tokenTypeIds));
        }

        using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results = session.Run(inputs);

        DisposableNamedOnnxValue hidden =
            results.FirstOrDefault(r => r.Name == "last_hidden_state") ?? results.First();
        Tensor<float> tensor = hidden.AsTensor<float>(); // [count, maxLen, EmbeddingDim]

        for (int b = 0; b < count; b++)
        {
            long[] seq = sequences[offset + b];
            var pooled = new float[EmbeddingDim];
            int valid = seq.Length; // attention_mask=1 のトークン数

            for (int s = 0; s < valid; s++)
            {
                for (int d = 0; d < EmbeddingDim; d++)
                {
                    pooled[d] += tensor[b, s, d];
                }
            }
            float inv = valid > 0 ? 1f / valid : 0f;
            for (int d = 0; d < EmbeddingDim; d++)
            {
                pooled[d] *= inv;
            }
            Normalize(pooled);
            output[offset + b] = pooled;
        }
    }

    private static void Normalize(float[] v)
    {
        double sum = 0;
        for (int i = 0; i < v.Length; i++)
        {
            sum += (double)v[i] * v[i];
        }
        float norm = (float)Math.Sqrt(sum);
        if (norm > 1e-12f)
        {
            for (int i = 0; i < v.Length; i++)
            {
                v[i] /= norm;
            }
        }
    }

    private static float Dot(float[] a, float[] b)
    {
        float sum = 0;
        int len = Math.Min(a.Length, b.Length);
        for (int i = 0; i < len; i++)
        {
            sum += a[i] * b[i];
        }
        return sum;
    }

    // ================================================================
    //  チャンク分割
    // ================================================================

    private sealed record Chunk(string Heading, int Line, string Text);

    /// <summary>
    /// Markdown を共有パイプラインで解析し、見出し（h1〜h3）単位のチャンクへ分割する。
    /// 見出しより前の本文は文書タイトル扱いのチャンク（Heading 空）にまとめる。
    /// 各チャンクの平文には e5 の "passage: " プレフィックスを付与する。
    /// 併せて参照ローカル画像を OCR し、テキストが得られたものを画像チャンクとして追加する。
    /// <paramref name="imageStamps"/> には OCR 対象にした画像の mtime/length を出力する
    /// （画像差し替えの差分検出に使う）。
    /// </summary>
    private static List<Chunk> ChunkFile(string filePath, CancellationToken ct, out List<ImageStamp> imageStamps)
    {
        string text = MarkdownRenderer.ReadFileText(filePath);
        MarkdownDocument document = Markdown.Parse(text, MarkdownRenderer.Pipeline);

        var chunks = new List<Chunk>();
        string currentHeading = string.Empty;
        int currentLine = 1;
        var buffer = new StringBuilder();

        void Flush()
        {
            string body = buffer.ToString().Trim();
            buffer.Clear();
            if (body.Length == 0 && currentHeading.Length == 0)
            {
                return;
            }
            // 見出しテキストも本文の一部として埋め込み対象に含める。
            string combined = currentHeading.Length > 0
                ? currentHeading + "\n" + body
                : body;
            chunks.Add(new Chunk(currentHeading, currentLine, "passage: " + combined));
        }

        foreach (Block block in document)
        {
            if (block is HeadingBlock heading && heading.Level >= 1 && heading.Level <= 3)
            {
                Flush();
                currentHeading = ExtractPlainText(heading);
                currentLine = heading.Line + 1; // Markdig は 0 始まり
                continue;
            }
            string blockText = ExtractPlainText(block);
            if (blockText.Length > 0)
            {
                buffer.Append(blockText);
                buffer.Append('\n');
            }
        }
        Flush();

        // 本文チャンクの上限適用は OCR チャンク追加より前に行う。後に回すと、
        // 本文が上限に達した文書で OCR チャンクだけが全滅する（OCR 側は
        // MaxImagesPerFile で独自に上限管理されるため、本文と枠を分ける）。
        if (chunks.Count > MaxChunksPerFile)
        {
            chunks.RemoveRange(MaxChunksPerFile, chunks.Count - MaxChunksPerFile);
        }

        // 参照ローカル画像を OCR し、テキストが得られたものを画像チャンクとして追加する。
        imageStamps = AppendImageOcrChunks(document, filePath, chunks, ct);

        return chunks;
    }

    /// <summary>
    /// 文書が参照するローカル画像を収集して OCR し、テキストが得られたものを
    /// 画像チャンク（見出し="画像: &lt;ファイル名&gt;"、テキスト="passage: &lt;ファイル名&gt; &lt;OCR&gt;"）
    /// として <paramref name="chunks"/> に追加する。戻り値は OCR 対象にした画像のスタンプ一覧
    /// （差分検出用。OCR テキストの有無に関わらず記録する）。重複パスは 1 回のみ、最大 50 画像。
    /// </summary>
    // 「参照時点でファイルが存在しなかった」ことを表すスタンプ値。
    // 後から画像が置かれたケースを ImagesUnchanged で検出するために記録する。
    private const long MissingImageStamp = -1;
    // 「OCR が一時的に失敗した」ことを表すスタンプ値。このスタンプを含むエントリは
    // 次回の索引更新で常に再埋め込みされ、OCR が再試行される。
    private const long FailedOcrStamp = -2;

    private static List<ImageStamp> AppendImageOcrChunks(
        MarkdownDocument document, string filePath, List<Chunk> chunks, CancellationToken ct)
    {
        var stamps = new List<ImageStamp>();
        string baseDir = Path.GetDirectoryName(Path.GetFullPath(filePath)) ?? string.Empty;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach ((string imagePath, int line) in CollectImageReferences(document, baseDir))
        {
            ct.ThrowIfCancellationRequested();
            if (stamps.Count >= MaxImagesPerFile)
            {
                break;
            }
            if (!seen.Add(imagePath))
            {
                continue; // 重複パスは 1 回だけ
            }

            long mtime;
            long length;
            try
            {
                var info = new FileInfo(imagePath);
                if (!info.Exists)
                {
                    // 未作成の画像も参照として記録する（後から置かれたら再索引される）。
                    stamps.Add(new ImageStamp
                    {
                        Path = imagePath,
                        MtimeUtcTicks = MissingImageStamp,
                        Length = MissingImageStamp,
                    });
                    continue;
                }
                mtime = info.LastWriteTimeUtc.Ticks;
                length = info.Length;
            }
            catch
            {
                continue;
            }

            stamps.Add(new ImageStamp { Path = imagePath, MtimeUtcTicks = mtime, Length = length });

            string? ocr = OcrTextService.ExtractText(imagePath, ct);
            if (ocr == null)
            {
                // OCR の一時的失敗。失敗スタンプに置き換え、次回検索時に再試行させる。
                stamps[^1].MtimeUtcTicks = FailedOcrStamp;
                continue;
            }
            if (string.IsNullOrWhiteSpace(ocr))
            {
                continue;
            }

            string fileName = Path.GetFileName(imagePath);
            chunks.Add(new Chunk(
                "画像: " + fileName,
                line,
                "passage: " + fileName + " " + ocr));
        }

        return stamps;
    }

    /// <summary>
    /// 文書中の画像参照（LinkInline.IsImage）を解決順に列挙する。
    /// http/https・存在しないパス・対象外拡張子はスキップし、行番号は参照を含む
    /// 親ブロックの行（1 始まり）を用いる。
    /// </summary>
    private static IEnumerable<(string Path, int Line)> CollectImageReferences(
        MarkdownDocument document, string baseDir)
    {
        foreach (MarkdownObject item in document.Descendants())
        {
            if (item is not LinkInline { IsImage: true } link)
            {
                continue;
            }
            string? resolved = ResolveLocalImageTarget(link.Url, baseDir);
            if (resolved != null)
            {
                yield return (resolved, GetInlineSourceLine(link));
            }
        }
    }

    private static readonly string[] OcrImageExtensions =
        { ".png", ".jpg", ".jpeg", ".bmp", ".gif" };

    /// <summary>
    /// 画像リンク URL をローカルの実ファイルパスへ解決する。対象外は null。
    /// 相対パスはファイルのフォルダ基準。`file:///C:/...` は表示系（MarkdownRenderer）に
    /// 合わせてローカルパスへ変換する。http(s) 等の外部 URL と UNC パス
    /// （バックグラウンド索引からネットワークへアクセスしない方針）は対象外。
    /// 存在チェックは行わない（未作成画像の参照記録は呼び出し側が担う）。
    /// </summary>
    private static string? ResolveLocalImageTarget(string? url, string baseDir)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        string pathPart;

        // 外部 URL（http: 等のスキーム付き）は対象外。Windows ドライブパスは除外して判定。
        // file: URI は表示系で解決可能なため、ローカルパスに変換して受け入れる。
        bool looksLikeDrivePath = url.Length >= 2 && char.IsLetter(url[0]) && url[1] == ':';
        if (!looksLikeDrivePath && Uri.TryCreate(url, UriKind.Absolute, out Uri? abs))
        {
            if (!abs.IsFile)
            {
                return null;
            }
            try
            {
                pathPart = abs.LocalPath;
            }
            catch
            {
                return null;
            }
        }
        else
        {
            int cut = url.IndexOfAny(new[] { '#', '?' });
            pathPart = cut >= 0 ? url[..cut] : url;
            if (pathPart.Length == 0)
            {
                return null;
            }
            pathPart = Uri.UnescapeDataString(pathPart);
        }

        string ext = Path.GetExtension(pathPart);
        bool supported = false;
        foreach (string e in OcrImageExtensions)
        {
            if (ext.Equals(e, StringComparison.OrdinalIgnoreCase))
            {
                supported = true;
                break;
            }
        }
        if (!supported)
        {
            return null;
        }

        string full;
        try
        {
            full = Path.IsPathRooted(pathPart)
                ? Path.GetFullPath(pathPart)
                : Path.GetFullPath(Path.Combine(baseDir, pathPart));
        }
        catch (ArgumentException)
        {
            return null; // 不正なパス文字
        }

        // UNC（ネットワーク共有）は認証・遅延・情報保存の観点から索引対象にしない。
        if (full.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return null;
        }

        return full;
    }

    /// <summary>
    /// インライン要素を含む親ブロックのソース行（1 始まり）を返す。
    /// 親をたどり最上位のインラインコンテナからブロックを得る。取れなければ 1。
    /// </summary>
    private static int GetInlineSourceLine(Inline inline)
    {
        Inline node = inline;
        while (node.Parent != null)
        {
            node = node.Parent;
        }
        if (node is ContainerInline { ParentBlock: { } block })
        {
            return block.Line + 1; // Markdig は 0 始まり
        }
        return inline.Line + 1;
    }

    /// <summary>
    /// ブロック配下の平文を抽出する（Markdown 記法を除いた地の文）。
    /// ブロックレベルの Descendants はインライン木へ入らないため、
    /// リーフブロックの Inline は明示的に、コンテナブロックは再帰で辿る。
    /// </summary>
    // StructureQueryService（構造クエリビュー）とも共用するため internal。
    internal static string ExtractPlainText(MarkdownObject root)
    {
        var sb = new StringBuilder();
        WalkBlock(root, sb);
        return sb.ToString().Trim();
    }

    private static void WalkBlock(MarkdownObject node, StringBuilder sb)
    {
        if (node is Markdig.Syntax.LeafBlock leaf)
        {
            if (leaf.Inline != null)
            {
                AppendInlines(leaf.Inline, sb);
            }
            else if (leaf.Lines.Count > 0)
            {
                // コードブロック（フェンス/インデント）はインラインを持たないため行から拾う。
                for (int i = 0; i < leaf.Lines.Count; i++)
                {
                    sb.Append(leaf.Lines.Lines[i].Slice.ToString());
                    sb.Append(' ');
                }
            }
        }
        if (node is Markdig.Syntax.ContainerBlock container)
        {
            foreach (Block child in container)
            {
                WalkBlock(child, sb);
            }
        }
    }

    private static void AppendInlines(Inline? inline, StringBuilder sb)
    {
        for (Inline? node = inline; node != null; node = node.NextSibling)
        {
            switch (node)
            {
                case LiteralInline literal:
                    sb.Append(literal.Content.ToString());
                    break;
                case CodeInline code:
                    sb.Append(code.Content);
                    break;
                case LineBreakInline:
                    sb.Append(' ');
                    break;
                case ContainerInline container:
                    AppendInlines(container.FirstChild, sb);
                    break;
            }
        }
    }

    // ================================================================
    //  索引キャッシュ（%LocalAppData%\Hirake\index）
    // ================================================================

    private sealed class ChunkEntry
    {
        public string Heading { get; set; } = string.Empty;
        public int Line { get; set; }

        [JsonIgnore]
        public float[] Vec { get; set; } = Array.Empty<float>();

        // JSON 直列化用（float[] を base64 の生バイトで保持）。
        public string VecBase64
        {
            get => Convert.ToBase64String(MemoryMarshal.AsBytes<float>(Vec).ToArray());
            set
            {
                byte[] bytes = Convert.FromBase64String(value);
                var v = new float[bytes.Length / sizeof(float)];
                Buffer.BlockCopy(bytes, 0, v, 0, v.Length * sizeof(float));
                Vec = v;
            }
        }
    }

    /// <summary>OCR 対象にした画像 1 枚の差分検出用スタンプ。</summary>
    private sealed class ImageStamp
    {
        public string Path { get; set; } = string.Empty;
        public long MtimeUtcTicks { get; set; }
        public long Length { get; set; }
    }

    private sealed class FileEntry
    {
        public string Path { get; set; } = string.Empty;
        public long MtimeUtcTicks { get; set; }
        public long Length { get; set; }
        public List<ChunkEntry> Chunks { get; set; } = new();
        // md 本体が無変化でも、参照画像が差し替わった場合の再埋め込み判定に使う。
        public List<ImageStamp> ImageStamps { get; set; } = new();
    }

    private sealed class IndexCache
    {
        public int Version { get; set; } = IndexFormatVersion;
        public List<FileEntry> Files { get; set; } = new();
    }

    // 索引の構築・更新（読込→差分埋め込み→保存）全体を直列化するゲート。
    // 原子的置換だけでは並行実行の lost update（遅く完了した古いスナップショットが
    // 新しい索引を上書きする）を防げないため、プロセス内で 1 本に絞る。
    private static readonly SemaphoreSlim IndexBuildGate = new(1, 1);

    // BuildOrUpdateIndex が最後に完了した時刻（Environment.TickCount64）。
    // レコメンドなど頻度の高い呼び出し元が、直近に更新済みの索引を再走査なしで
    // 使い回すための鮮度判定に用いる。
    private static long _memoryIndexBuiltAtTick;

    /// <summary>
    /// ルートフォルダの索引を構築・更新し、全チャンクの平坦なリスト（パス・見出し・行・ベクトル）を返す。
    /// mtime/length が一致するファイルは既存ベクトルを再利用し、変化したファイルのみ再埋め込みする。
    /// 消えたファイルは索引から除去する。実行はプロセス内で直列化される。
    /// <paramref name="requireExistingIndex"/> が true のときは、既存索引が読み取れなかった場合
    /// （削除・破損・旧バージョン）に何も構築・保存せず空を返す（レコメンドの副作用ゼロ保証用）。
    /// </summary>
    private static List<(string Path, string Heading, int Line, float[] Vec)> BuildOrUpdateIndex(
        string rootFolder, IProgress<string>? progress, CancellationToken ct,
        bool requireExistingIndex = false)
    {
        IndexBuildGate.Wait(ct);
        try
        {
            return BuildOrUpdateIndexCore(rootFolder, progress, ct, requireExistingIndex);
        }
        finally
        {
            IndexBuildGate.Release();
        }
    }

    private static List<(string Path, string Heading, int Line, float[] Vec)> BuildOrUpdateIndexCore(
        string rootFolder, IProgress<string>? progress, CancellationToken ct,
        bool requireExistingIndex)
    {
        string root = Path.GetFullPath(rootFolder);

        // レコメンド経路（requireExistingIndex）ではメモリキャッシュを使わず、
        // 必ずディスク上の索引を読み直して検証する。メモリ経由だと、構築後に
        // ディスク索引が削除・破損した場合に検証を迂回してしまうため。
        Dictionary<string, FileEntry> previous = LoadIndex(root, bypassMemory: requireExistingIndex);

        // レコメンド経路では「既存索引の差分更新」だけを許可する。
        // 索引が消えた・壊れていた・旧バージョンだった場合 previous は空になるが、
        // そのまま進むと閲覧しただけで全再構築（重い副作用）が起きてしまうため打ち切る。
        // （md が 1 つも無いフォルダの正常な空索引も空になるが、その場合も
        //   レコメンドすべき文書が存在しないため、打ち切りで結果は変わらない。）
        if (requireExistingIndex && previous.Count == 0)
        {
            return new List<(string, string, int, float[])>();
        }

        // 現在のファイル一覧（列挙規則は横断検索と共通）。
        var files = new List<string>();
        foreach (string file in FolderSearchService.EnumerateMarkdownFiles(root, ct))
        {
            if (files.Count >= FolderSearchService.MaxFiles)
            {
                break;
            }
            files.Add(Path.GetFullPath(file));
        }

        var current = new Dictionary<string, FileEntry>(StringComparer.OrdinalIgnoreCase);
        int processed = 0;
        foreach (string file in files)
        {
            ct.ThrowIfCancellationRequested();
            processed++;
            progress?.Report($"索引作成中... {processed}/{files.Count} ファイル");

            FileInfo info;
            try
            {
                info = new FileInfo(file);
                if (info.Length > FolderSearchService.MaxFileBytes)
                {
                    continue;
                }
            }
            catch
            {
                continue;
            }

            long mtime = info.LastWriteTimeUtc.Ticks;
            long length = info.Length;

            // 変化していないファイルは既存ベクトルを再利用する。
            // md 本体が無変化でも、参照画像が差し替わっていれば OCR チャンクの再生成が要る。
            if (previous.TryGetValue(file, out FileEntry? cached)
                && cached.MtimeUtcTicks == mtime
                && cached.Length == length
                && ImagesUnchanged(cached))
            {
                current[file] = cached;
                continue;
            }

            // 変化した / 新規のファイルを再埋め込みする。
            FileEntry? entry = EmbedFile(file, mtime, length, ct);
            if (entry != null)
            {
                current[file] = entry;
            }
        }

        SaveIndex(root, current);

        lock (MemoryLock)
        {
            _memoryRoot = root;
            _memoryIndex = current;
            _memoryIndexBuiltAtTick = Environment.TickCount64;
        }

        // 平坦化して返す。
        var flat = new List<(string, string, int, float[])>();
        foreach (FileEntry entry in current.Values)
        {
            foreach (ChunkEntry chunk in entry.Chunks)
            {
                flat.Add((entry.Path, chunk.Heading, chunk.Line, chunk.Vec));
            }
        }
        return flat;
    }

    private static FileEntry? EmbedFile(string file, long mtime, long length, CancellationToken ct)
    {
        List<Chunk> chunks;
        List<ImageStamp> imageStamps;
        try
        {
            chunks = ChunkFile(file, ct, out imageStamps);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
        if (chunks.Count == 0)
        {
            // OCR テキストが空でも、参照画像のスタンプは差分検出のため保持する。
            return new FileEntry
            {
                Path = file,
                MtimeUtcTicks = mtime,
                Length = length,
                ImageStamps = imageStamps,
            };
        }

        float[][] vectors = EmbedTexts(chunks.Select(c => c.Text).ToList(), ct);

        var entry = new FileEntry
        {
            Path = file,
            MtimeUtcTicks = mtime,
            Length = length,
            ImageStamps = imageStamps,
        };
        for (int i = 0; i < chunks.Count; i++)
        {
            entry.Chunks.Add(new ChunkEntry
            {
                Heading = chunks[i].Heading,
                Line = chunks[i].Line,
                Vec = vectors[i],
            });
        }
        return entry;
    }

    /// <summary>
    /// キャッシュ済みエントリの参照画像がすべて無変化（存在・mtime・length 一致）か判定する。
    /// 1 枚でも消失・変化していれば false（画像だけ差し替えたケースの再埋め込み判定）。
    /// </summary>
    private static bool ImagesUnchanged(FileEntry entry)
    {
        foreach (ImageStamp stamp in entry.ImageStamps)
        {
            try
            {
                var info = new FileInfo(stamp.Path);

                // OCR が一時失敗した画像を含むエントリは常に再埋め込み（OCR 再試行）。
                if (stamp.MtimeUtcTicks == FailedOcrStamp)
                {
                    return false;
                }

                // 索引時点で未作成だった画像は「今も存在しない」場合のみ無変化
                // （後から置かれたら再索引して OCR する）。
                if (stamp.MtimeUtcTicks == MissingImageStamp)
                {
                    if (info.Exists)
                    {
                        return false;
                    }
                    continue;
                }

                if (!info.Exists
                    || info.LastWriteTimeUtc.Ticks != stamp.MtimeUtcTicks
                    || info.Length != stamp.Length)
                {
                    return false;
                }
            }
            catch
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// ルートの索引を（メモリ内キャッシュ優先で）読み込み、path→FileEntry の辞書にする。
    /// <paramref name="bypassMemory"/> が true のときはメモリキャッシュを使わず、
    /// 必ずディスク上の索引ファイルを読み直して検証する。
    /// </summary>
    private static Dictionary<string, FileEntry> LoadIndex(string root, bool bypassMemory = false)
    {
        if (!bypassMemory)
        {
            lock (MemoryLock)
            {
                if (_memoryIndex != null
                    && string.Equals(_memoryRoot, root, StringComparison.OrdinalIgnoreCase))
                {
                    return _memoryIndex;
                }
            }
        }

        var map = new Dictionary<string, FileEntry>(StringComparer.OrdinalIgnoreCase);
        try
        {
            string path = IndexFilePath(root);
            if (!File.Exists(path))
            {
                return map;
            }
            string json = File.ReadAllText(path, Encoding.UTF8);
            IndexCache? cache = JsonSerializer.Deserialize<IndexCache>(json);
            if (cache == null || cache.Version != IndexFormatVersion)
            {
                return map;
            }
            foreach (FileEntry entry in cache.Files)
            {
                // 破損キャッシュ（次元不一致・非有限値など）は該当ファイルごと捨てて再埋め込みさせる。
                if (!string.IsNullOrEmpty(entry.Path) && IsValidEntry(entry))
                {
                    map[entry.Path] = entry;
                }
            }
        }
        catch
        {
            // 破損・読込失敗時は空の索引から作り直す。
            return new Dictionary<string, FileEntry>(StringComparer.OrdinalIgnoreCase);
        }
        return map;
    }

    private static readonly object SaveLock = new();

    private static void SaveIndex(string root, Dictionary<string, FileEntry> map)
    {
        try
        {
            Directory.CreateDirectory(IndexDirectory);
            var cache = new IndexCache
            {
                Version = IndexFormatVersion,
                Files = map.Values.ToList(),
            };
            string json = JsonSerializer.Serialize(cache);
            string dest = IndexFilePath(root);
            string tmp = dest + "." + Guid.NewGuid().ToString("N") + ".tmp";

            // 一意な一時ファイルへ書いてから原子的に置換する（途中状態の読み取り・
            // 書き込み競合による索引破損を防ぐ）。プロセス内はロックで直列化する。
            lock (SaveLock)
            {
                File.WriteAllText(tmp, json, Encoding.UTF8);
                File.Move(tmp, dest, overwrite: true);
            }
        }
        catch
        {
            // 保存失敗は無視する（次回再構築される）。
        }
    }

    /// <summary>
    /// キャッシュから読んだ FileEntry の妥当性検証。ベクトル次元が 384 で全要素が有限値、
    /// 行番号が非負であることを確認する（破損 JSON が検索結果へ混入するのを防ぐ）。
    /// </summary>
    private static bool IsValidEntry(FileEntry entry)
    {
        // 本文チャンク上限 + OCR 画像チャンク上限が正当な最大数。
        if (entry.Chunks.Count > MaxChunksPerFile + MaxImagesPerFile)
        {
            return false;
        }
        foreach (ChunkEntry chunk in entry.Chunks)
        {
            if (chunk.Line < 0 || chunk.Vec.Length != EmbeddingDim)
            {
                return false;
            }
            foreach (float v in chunk.Vec)
            {
                if (!float.IsFinite(v))
                {
                    return false;
                }
            }
        }
        return true;
    }

    /// <summary>ルートの絶対パスを SHA-256 で短縮ハッシュ化して索引ファイル名にする。</summary>
    private static string IndexFilePath(string root)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(root.ToLowerInvariant()));
        var sb = new StringBuilder(16);
        for (int i = 0; i < 8; i++)
        {
            sb.Append(hash[i].ToString("x2"));
        }
        return Path.Combine(IndexDirectory, sb.ToString() + ".json");
    }
}
