using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Markdig;
using Markdig.Extensions.TaskLists;
using Markdig.Syntax;

namespace Hirake;

/// <summary>ランキング 1 行分（放置 / ボリューム / 未完了タスク共用）。</summary>
public sealed class StatsRankItem
{
    public string FullPath { get; init; } = string.Empty;
    public string FileName { get; init; } = string.Empty;
    public string RelativePath { get; init; } = string.Empty;

    /// <summary>ランキングの値（放置=経過日数、ボリューム=文字数、タスク=未完了数）。</summary>
    public long Value { get; init; }
}

/// <summary>更新ヒートマップの 1 日分（更新されたファイル数）。</summary>
public sealed class StatsHeatDay
{
    /// <summary>ローカル日付（yyyy-MM-dd）。</summary>
    public string Date { get; init; } = string.Empty;
    public int Count { get; init; }
}

/// <summary>語数推移の 1 点（履歴蓄積型スナップショット）。</summary>
public sealed class StatsHistoryPoint
{
    public string Date { get; set; } = string.Empty;
    public int Files { get; set; }
    public long Chars { get; set; }
    public int OpenTasks { get; set; }
}

/// <summary>フォルダ統計の実行結果。</summary>
public sealed class StatsResult
{
    public string RootFolder { get; init; } = string.Empty;

    public int FileCount { get; init; }
    public long TotalChars { get; init; }
    public int HeadingCount { get; init; }
    public int OpenTasks { get; init; }
    public int DoneTasks { get; init; }
    public int ImageRefs { get; init; }

    public List<StatsHeatDay> Heatmap { get; init; } = new();
    public List<StatsHistoryPoint> History { get; init; } = new();
    public List<StatsRankItem> StaleFiles { get; init; } = new();
    public List<StatsRankItem> LargestFiles { get; init; } = new();
    public List<StatsRankItem> TaskHeavyFiles { get; init; } = new();

    /// <summary>走査上限に到達し、一部が未集計のとき true。</summary>
    public bool Truncated { get; init; }
}

/// <summary>
/// フォルダ配下の Markdown 資産の統計（サマリ・更新ヒートマップ・語数推移・
/// 各種ランキング）を集計する。ファイルシステム情報（mtime）とテキスト集計のみで
/// 動作し、意味索引・モデルには一切依存しない。
///
/// 語数推移は履歴蓄積型: 集計のたびに当日分のスナップショット（集計値のみ）を
/// %LocalAppData%\Hirake\stats\&lt;ルートハッシュ&gt;.jsonl へ保存し（同日は上書き）、
/// 蓄積分を折れ線として返す。文書の内容・ファイル名は保存しない。
/// </summary>
public static class StatsService
{
    // ヒートマップの対象期間（今日を含む過去 365 日）。
    private const int HeatmapDays = 365;

    // ランキング件数。
    private const int StaleTop = 20;
    private const int LargestTop = 20;
    private const int TaskHeavyTop = 10;

    // 履歴の保持上限（日数分の行）。超過分は古い方から捨てる。
    private const int MaxHistoryPoints = 730;

    private static string StatsDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Hirake", "stats");

    /// <summary>rootFolder 配下を走査して統計を集計する（同期・CPU バウンド）。</summary>
    public static StatsResult Build(string rootFolder, CancellationToken token)
    {
        string fullRoot = Path.GetFullPath(rootFolder);

        int fileCount = 0;
        long totalChars = 0;
        int headingCount = 0;
        int openTasks = 0;
        int doneTasks = 0;
        int imageRefs = 0;
        bool truncated = false;

        var mtimes = new List<DateTime>(); // ローカル時刻
        var stale = new List<StatsRankItem>();
        var largest = new List<StatsRankItem>();
        var taskHeavy = new List<StatsRankItem>();

        DateTime today = DateTime.Now.Date;

        foreach (string file in FolderSearchService.EnumerateMarkdownFiles(fullRoot, token))
        {
            token.ThrowIfCancellationRequested();

            // 上限到達後は列挙自体を打ち切る（既存の走査規則と同じ）。
            if (fileCount >= FolderSearchService.MaxFiles)
            {
                truncated = true;
                break;
            }

            FileStat? stat = CollectFile(fullRoot, file, token);
            if (stat == null)
            {
                continue;
            }
            fileCount++;

            totalChars += stat.Chars;
            headingCount += stat.Headings;
            openTasks += stat.OpenTasks;
            doneTasks += stat.DoneTasks;
            imageRefs += stat.ImageRefs;
            mtimes.Add(stat.MtimeLocal);

            stale.Add(new StatsRankItem
            {
                FullPath = stat.FullPath,
                FileName = stat.FileName,
                RelativePath = stat.RelativePath,
                Value = Math.Max(0, (long)(today - stat.MtimeLocal.Date).TotalDays),
            });
            largest.Add(new StatsRankItem
            {
                FullPath = stat.FullPath,
                FileName = stat.FileName,
                RelativePath = stat.RelativePath,
                Value = stat.Chars,
            });
            if (stat.OpenTasks > 0)
            {
                taskHeavy.Add(new StatsRankItem
                {
                    FullPath = stat.FullPath,
                    FileName = stat.FileName,
                    RelativePath = stat.RelativePath,
                    Value = stat.OpenTasks,
                });
            }
        }

        // ランキング（値の降順。同値はパス順で安定させる）。
        stale.Sort(CompareByValueDesc);
        largest.Sort(CompareByValueDesc);
        taskHeavy.Sort(CompareByValueDesc);
        Trim(stale, StaleTop);
        Trim(largest, LargestTop);
        Trim(taskHeavy, TaskHeavyTop);

        // 履歴（当日分を記録してから全体を返す）。
        List<StatsHistoryPoint> history = UpdateHistory(
            fullRoot, today, fileCount, totalChars, openTasks, token);

        return new StatsResult
        {
            RootFolder = fullRoot,
            FileCount = fileCount,
            TotalChars = totalChars,
            HeadingCount = headingCount,
            OpenTasks = openTasks,
            DoneTasks = doneTasks,
            ImageRefs = imageRefs,
            Heatmap = BuildHeatmap(mtimes, today),
            History = history,
            StaleFiles = stale,
            LargestFiles = largest,
            TaskHeavyFiles = taskHeavy,
            Truncated = truncated,
        };
    }

    private static int CompareByValueDesc(StatsRankItem x, StatsRankItem y)
    {
        int byValue = y.Value.CompareTo(x.Value);
        return byValue != 0
            ? byValue
            : string.Compare(x.RelativePath, y.RelativePath, StringComparison.OrdinalIgnoreCase);
    }

    private static void Trim(List<StatsRankItem> list, int top)
    {
        if (list.Count > top)
        {
            list.RemoveRange(top, list.Count - top);
        }
    }

    // ---- 1 ファイル分の集計 -------------------------------------------

    private sealed class FileStat
    {
        public required string FullPath;
        public required string FileName;
        public required string RelativePath;
        public required DateTime MtimeLocal;
        public required long Chars;
        public required int Headings;
        public required int OpenTasks;
        public required int DoneTasks;
        public required int ImageRefs;
    }

    private static FileStat? CollectFile(string root, string file, CancellationToken token)
    {
        try
        {
            var info = new FileInfo(file);
            if (!info.Exists || info.Length > FolderSearchService.MaxFileBytes)
            {
                return null;
            }

            // 表示側と同じ読込規則（UTF-8 厳密 → Shift-JIS フォールバック）。
            string text = MarkdownRenderer.ReadFileText(file);

            // 1 回の AST 解析で見出し・タスク・画像参照を数える。
            MarkdownDocument document = Markdown.Parse(text, MarkdownRenderer.Pipeline);

            int headings = 0;
            int open = 0;
            int done = 0;
            foreach (MarkdownObject node in document.Descendants())
            {
                token.ThrowIfCancellationRequested();
                switch (node)
                {
                    case HeadingBlock:
                        headings++;
                        break;
                    case TaskList task:
                        if (task.Checked)
                        {
                            done++;
                        }
                        else
                        {
                            open++;
                        }
                        break;
                }
            }

            string baseDir = Path.GetDirectoryName(Path.GetFullPath(file)) ?? string.Empty;
            int images = 0;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach ((string imagePath, int _) in
                     SemanticIndexService.CollectImageReferences(document, baseDir))
            {
                if (seen.Add(imagePath))
                {
                    images++;
                }
                if (images >= SemanticIndexService.MaxImagesPerFile)
                {
                    break;
                }
            }

            return new FileStat
            {
                FullPath = file,
                FileName = Path.GetFileName(file),
                RelativePath = MakeRelative(root, file),
                MtimeLocal = info.LastWriteTime,
                Chars = text.Length,
                Headings = headings,
                OpenTasks = open,
                DoneTasks = done,
                ImageRefs = images,
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // 個別ファイルの読込・解析失敗はスキップする（全体を止めない）。
            return null;
        }
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

    // ---- 更新ヒートマップ ---------------------------------------------

    private static List<StatsHeatDay> BuildHeatmap(List<DateTime> mtimes, DateTime today)
    {
        DateTime start = today.AddDays(-(HeatmapDays - 1));
        var counts = new Dictionary<DateTime, int>();
        foreach (DateTime mtime in mtimes)
        {
            DateTime day = mtime.Date;
            if (day < start || day > today)
            {
                continue;
            }
            counts[day] = counts.GetValueOrDefault(day) + 1;
        }

        // 0 の日は送らない（JS 側が期間グリッドを生成して埋める）。
        return counts
            .OrderBy(kv => kv.Key)
            .Select(kv => new StatsHeatDay
            {
                Date = kv.Key.ToString("yyyy-MM-dd"),
                Count = kv.Value,
            })
            .ToList();
    }

    // ---- 語数推移（履歴蓄積） -----------------------------------------

    private static string HistoryFilePath(string fullRoot)
    {
        // 意味索引のキャッシュと同様に、ルートのハッシュでファイル名を決める。
        byte[] hash = SHA256.HashData(
            Encoding.UTF8.GetBytes(fullRoot.ToLowerInvariant()));
        string name = Convert.ToHexString(hash.AsSpan(0, 8)).ToLowerInvariant();
        return Path.Combine(StatsDirectory, name + ".jsonl");
    }

    /// <summary>
    /// 当日分のスナップショットを履歴へ反映（同日は上書き）し、全履歴を返す。
    ///
    /// 蓄積データの保護を最優先する:
    /// - 読込→更新→置換の全体を、ルート単位の名前付き Mutex で排他する
    ///   （publish 版と開発版など複数プロセスの同時集計でも後勝ち消失を防ぐ）
    /// - 既存ファイルの読込に失敗した場合は保存を中止する
    ///   （空の履歴で上書きして蓄積を破壊しない。表示用には当日分のみ返す）
    /// - キャンセル済み・排他取得失敗時も保存しない（表示用の履歴だけ返す）
    /// </summary>
    private static List<StatsHistoryPoint> UpdateHistory(
        string fullRoot, DateTime today, int files, long chars, int openTasks,
        CancellationToken token)
    {
        string path = HistoryFilePath(fullRoot);

        using var mutex = new Mutex(
            initiallyOwned: false, "Hirake.Stats." + Path.GetFileNameWithoutExtension(path));
        bool acquired = false;
        try
        {
            try
            {
                acquired = mutex.WaitOne(TimeSpan.FromSeconds(3));
            }
            catch (AbandonedMutexException)
            {
                acquired = true; // 前保持者の異常終了。所有権は取得できている。
            }

            (List<StatsHistoryPoint> history, bool readOk) = ReadHistory(path);

            string todayKey = today.ToString("yyyy-MM-dd");
            history.RemoveAll(p => p.Date == todayKey);
            history.Add(new StatsHistoryPoint
            {
                Date = todayKey,
                Files = files,
                Chars = chars,
                OpenTasks = openTasks,
            });
            history.Sort((a, b) => string.CompareOrdinal(a.Date, b.Date));
            if (history.Count > MaxHistoryPoints)
            {
                history.RemoveRange(0, history.Count - MaxHistoryPoints);
            }

            if (readOk && acquired && !token.IsCancellationRequested)
            {
                SaveHistory(path, history);
            }

            return history;
        }
        finally
        {
            if (acquired)
            {
                try
                {
                    mutex.ReleaseMutex();
                }
                catch
                {
                    // ignore
                }
            }
        }
    }

    /// <summary>履歴ファイルを読む。ReadOk=false は IO 失敗（保存を中止すべき状態）。</summary>
    private static (List<StatsHistoryPoint> History, bool ReadOk) ReadHistory(string path)
    {
        var history = new List<StatsHistoryPoint>();
        try
        {
            if (!File.Exists(path))
            {
                return (history, true); // 初回。空履歴から始めてよい。
            }
            foreach (string line in File.ReadAllLines(path, Encoding.UTF8))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }
                try
                {
                    StatsHistoryPoint? point =
                        JsonSerializer.Deserialize<StatsHistoryPoint>(line);
                    if (point != null && !string.IsNullOrEmpty(point.Date))
                    {
                        history.Add(point);
                    }
                }
                catch (JsonException)
                {
                    // 壊れた行は読み飛ばす（行単位の破損は許容）。
                }
            }
            return (history, true);
        }
        catch
        {
            // 一時的な読込失敗（共有違反等）。上書きすると蓄積を失うため保存不可とする。
            history.Clear();
            return (history, false);
        }
    }

    private static void SaveHistory(string path, List<StatsHistoryPoint> history)
    {
        string? tmp = null;
        try
        {
            Directory.CreateDirectory(StatsDirectory);
            var sb = new StringBuilder();
            foreach (StatsHistoryPoint point in history)
            {
                sb.AppendLine(JsonSerializer.Serialize(point));
            }
            tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(false));
            File.Move(tmp, path, overwrite: true);
            tmp = null;
        }
        catch
        {
            // 保存失敗は無視（次回また当日分から記録される）。tmp の残骸は掃除する。
            if (tmp != null)
            {
                try
                {
                    File.Delete(tmp);
                }
                catch
                {
                    // ignore
                }
            }
        }
    }

    // ---- JSON ----------------------------------------------------------

    /// <summary>
    /// テンプレートの &lt;script&gt; へ直接埋め込める JSON を生成する
    /// （System.Text.Json の既定エスケープにより &lt; などは \uXXXX 化される）。
    /// </summary>
    public static string ToJson(StatsResult result)
    {
        string folderLabel = Path.GetFileName(
            result.RootFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrEmpty(folderLabel))
        {
            folderLabel = result.RootFolder;
        }

        static object MapRank(StatsRankItem item) => new
        {
            path = item.FullPath,
            name = item.FileName,
            rel = item.RelativePath,
            value = item.Value,
        };

        return JsonSerializer.Serialize(new
        {
            root = result.RootFolder,
            folderLabel,
            truncated = result.Truncated,
            summary = new
            {
                files = result.FileCount,
                chars = result.TotalChars,
                headings = result.HeadingCount,
                openTasks = result.OpenTasks,
                doneTasks = result.DoneTasks,
                imageRefs = result.ImageRefs,
            },
            heatmap = result.Heatmap.Select(d => new { d = d.Date, c = d.Count }).ToList(),
            history = result.History
                .Select(p => new { d = p.Date, files = p.Files, chars = p.Chars, openTasks = p.OpenTasks })
                .ToList(),
            stale = result.StaleFiles.Select(MapRank).ToList(),
            largest = result.LargestFiles.Select(MapRank).ToList(),
            taskHeavy = result.TaskHeavyFiles.Select(MapRank).ToList(),
        });
    }
}
