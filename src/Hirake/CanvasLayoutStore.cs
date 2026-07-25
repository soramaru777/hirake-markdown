using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Hirake;

/// <summary>キャンバス上の 1 ノード（文書）の配置。キーはルート相対パス。</summary>
public sealed class CanvasNodeLayout
{
    public double X { get; set; }
    public double Y { get; set; }

    /// <summary>ユーザーが手動配置した（力学モデルの対象外にする）とき true。R4。</summary>
    public bool Pinned { get; set; }
}

/// <summary>ビューポート（パン位置とズーム率）。</summary>
public sealed class CanvasViewState
{
    public double X { get; set; }
    public double Y { get; set; }
    public double K { get; set; } = 1;
}

/// <summary>キャンバス配置の永続化モデル（フォルダ単位）。</summary>
public sealed class CanvasLayout
{
    /// <summary>保存形式バージョン（非互換変更時にインクリメント）。</summary>
    public int Version { get; set; } = 1;

    /// <summary>ルート相対パス → 配置。相対パスを安定 ID とする（ルート移動に強い）。</summary>
    public Dictionary<string, CanvasNodeLayout> Files { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    public CanvasViewState? View { get; set; }
}

/// <summary>
/// キャンバス配置とサムネイルの保存場所を管理する。
/// 配置は %LocalAppData%\Hirake\canvas\&lt;ルートハッシュ&gt;.json。
/// 保護則は #27 の統計履歴と同じ: ルート単位の名前付き Mutex で
/// 読込→マージ→置換を排他し、読込失敗時は上書きしない。
///
/// マージ規則: 保存要求に含まれるファイルは新しい値で上書き、含まれない既存
/// エントリは保持する（走査上限や一時的な除外でファイルが消えてもピン位置を
/// 失わない）。保持エントリ数には上限を設け、超過分は要求に含まれない側から捨てる。
/// </summary>
public static class CanvasLayoutStore
{
    // 保存するノード配置エントリの上限（要求分 + 保持分の合計）。
    private const int MaxEntries = 2000;

    private static string CanvasDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Hirake", "canvas");

    /// <summary>サムネイル PNG の置き場（thumbs.hirake 仮想ホストのマップ先）。</summary>
    public static string ThumbsDirectory => Path.Combine(CanvasDirectory, "thumbs");

    /// <summary>文書のサムネイル PNG のフルパス（ファイル名はフルパスのハッシュ）。</summary>
    public static string ThumbnailPathFor(string documentFullPath)
        => Path.Combine(ThumbsDirectory, HashName(documentFullPath) + ".png");

    /// <summary>サムネイルのファイル名部分のみ（仮想ホスト URL 組み立て用）。</summary>
    public static string ThumbnailNameFor(string documentFullPath)
        => HashName(documentFullPath) + ".png";

    private static string LayoutFilePath(string rootFolder)
        => Path.Combine(CanvasDirectory, HashName(rootFolder) + ".json");

    private static string HashName(string path)
    {
        byte[] hash = SHA256.HashData(
            Encoding.UTF8.GetBytes(Path.GetFullPath(path).ToLowerInvariant()));
        return Convert.ToHexString(hash.AsSpan(0, 8)).ToLowerInvariant();
    }

    private static Mutex CreateMutex(string rootFolder)
        => new(initiallyOwned: false, "Hirake.Canvas." + HashName(rootFolder));

    /// <summary>配置を読み込む。無ければ空の配置（保存可）。読込失敗時も空を返す（表示は可能）。</summary>
    public static CanvasLayout Load(string rootFolder)
    {
        using Mutex mutex = CreateMutex(rootFolder);
        bool acquired = Acquire(mutex);
        try
        {
            (CanvasLayout layout, _) = ReadLayout(LayoutFilePath(rootFolder));
            return layout;
        }
        finally
        {
            Release(mutex, acquired);
        }
    }

    /// <summary>
    /// 配置を保存する（マージあり）。読込失敗・排他取得失敗時は上書きせずスキップする。
    /// </summary>
    public static void Save(string rootFolder, CanvasLayout incoming)
    {
        string path = LayoutFilePath(rootFolder);

        using Mutex mutex = CreateMutex(rootFolder);
        bool acquired = Acquire(mutex);
        try
        {
            if (!acquired)
            {
                return; // 排他を取れないときは安全側（保存しない）。
            }

            (CanvasLayout current, bool readOk) = ReadLayout(path);
            if (!readOk)
            {
                return; // 一時的な読込失敗。上書きすると既存配置を失うため保存しない。
            }

            // マージ: 要求分を上書きし、要求に含まれない既存エントリは保持する。
            var merged = new CanvasLayout
            {
                Version = 1,
                View = incoming.View ?? current.View,
            };
            foreach ((string rel, CanvasNodeLayout node) in incoming.Files)
            {
                if (merged.Files.Count >= MaxEntries)
                {
                    break;
                }
                merged.Files[rel] = node;
            }
            foreach ((string rel, CanvasNodeLayout node) in current.Files)
            {
                if (merged.Files.Count >= MaxEntries)
                {
                    break;
                }
                if (!merged.Files.ContainsKey(rel))
                {
                    merged.Files[rel] = node;
                }
            }

            WriteLayout(path, merged);
        }
        finally
        {
            Release(mutex, acquired);
        }
    }

    private static bool Acquire(Mutex mutex)
    {
        try
        {
            return mutex.WaitOne(TimeSpan.FromSeconds(3));
        }
        catch (AbandonedMutexException)
        {
            return true; // 前保持者の異常終了。所有権は取得できている。
        }
        catch
        {
            return false;
        }
    }

    private static void Release(Mutex mutex, bool acquired)
    {
        if (!acquired)
        {
            return;
        }
        try
        {
            mutex.ReleaseMutex();
        }
        catch
        {
            // ignore
        }
    }

    // サムネイル置場の容量バジェット（超過分は古い順に削除）。
    private const long MaxThumbsBytes = 200L * 1024 * 1024;

    /// <summary>
    /// サムネイル置場の掃除: .tmp 残骸を削除し、合計サイズが予算を超える分を
    /// 最終更新の古い順に削除する。失敗は無視（次回また試みる）。
    /// キャンバスを開いたときにバックグラウンドで 1 回呼ばれる。
    /// </summary>
    public static void CleanupThumbnails()
    {
        try
        {
            if (!Directory.Exists(ThumbsDirectory))
            {
                return;
            }

            var files = new List<FileInfo>();
            foreach (string file in Directory.EnumerateFiles(ThumbsDirectory))
            {
                try
                {
                    var info = new FileInfo(file);
                    if (file.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
                    {
                        // 撮影の中断で残った一時ファイル（1 時間以上前のもの）を掃除する。
                        if (info.LastWriteTimeUtc < DateTime.UtcNow.AddHours(-1))
                        {
                            info.Delete();
                        }
                        continue;
                    }
                    files.Add(info);
                }
                catch
                {
                    // 個別ファイルの失敗は無視する。
                }
            }

            long total = files.Sum(f => f.Length);
            if (total <= MaxThumbsBytes)
            {
                return;
            }

            foreach (FileInfo info in files.OrderBy(f => f.LastWriteTimeUtc))
            {
                if (total <= MaxThumbsBytes)
                {
                    break;
                }
                try
                {
                    total -= info.Length;
                    info.Delete();
                }
                catch
                {
                    // 使用中などの削除失敗は無視する。
                }
            }
        }
        catch
        {
            // 掃除は補助機能。失敗しても本体へ影響させない。
        }
    }

    private static (CanvasLayout Layout, bool ReadOk) ReadLayout(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return (new CanvasLayout(), true);
            }
            string json = File.ReadAllText(path, Encoding.UTF8);
            CanvasLayout? loaded = JsonSerializer.Deserialize<CanvasLayout>(json);
            if (loaded == null)
            {
                return (new CanvasLayout(), true);
            }
            // System.Text.Json はコンパレータを復元しないため、大文字小文字非依存で包み直す。
            loaded.Files = loaded.Files == null
                ? new Dictionary<string, CanvasNodeLayout>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, CanvasNodeLayout>(loaded.Files, StringComparer.OrdinalIgnoreCase);
            return (loaded, true);
        }
        catch (JsonException)
        {
            // 壊れた JSON はそのまま上書きせず .corrupt へ退避してから作り直す
            // （ユーザーが手動復旧できる余地を残しつつ、保存経路は生かす）。
            try
            {
                File.Move(path, path + ".corrupt", overwrite: true);
                return (new CanvasLayout(), true);
            }
            catch
            {
                // 退避もできない状態では上書きしない（既存データ保護を優先）。
                return (new CanvasLayout(), false);
            }
        }
        catch
        {
            // IO 失敗（共有違反等）。上書き禁止のシグナルとして ReadOk=false。
            return (new CanvasLayout(), false);
        }
    }

    private static void WriteLayout(string path, CanvasLayout layout)
    {
        string? tmp = null;
        try
        {
            Directory.CreateDirectory(CanvasDirectory);
            string json = JsonSerializer.Serialize(layout);
            tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(tmp, json, new UTF8Encoding(false));
            File.Move(tmp, path, overwrite: true);
            tmp = null;
        }
        catch
        {
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
}
