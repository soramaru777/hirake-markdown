using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

namespace Hirake;

/// <summary><c>[[wikilink]]</c> の解決結果の種別。</summary>
internal enum WikiLinkVerdict
{
    /// <summary>候補が 1 つに定まった。</summary>
    Resolved,

    /// <summary>同名が複数あり、優先順位でも 1 つに絞れなかった（先頭を採用する）。</summary>
    Ambiguous,

    /// <summary>候補が無い。</summary>
    Unresolved,
}

/// <summary>1 個の <c>[[...]]</c> を解決した結果。</summary>
/// <param name="Verdict">解決の種別。</param>
/// <param name="Path">解決できたファイルのフルパス（<see cref="WikiLinkVerdict.Unresolved"/> なら null）。</param>
/// <param name="Count">候補の総数（曖昧表示に使う）。</param>
/// <param name="IndexComplete">
/// 索引が走査を最後までやり切ったか。やり切っていない場合、
/// 「候補 1 件」は「上限より先に別の同名があるかもしれない」を意味する。
/// </param>
internal readonly record struct WikiLinkResolution(
    WikiLinkVerdict Verdict, string? Path, int Count, bool IndexComplete = true)
{
    public static WikiLinkResolution NotFound { get; } =
        new(WikiLinkVerdict.Unresolved, null, 0);

    /// <summary>選ばれたファイルが「これで確定」と言い切れるか。</summary>
    public bool IsCertain => Verdict == WikiLinkVerdict.Resolved && IndexComplete;
}

/// <summary>
/// <c>[[wikilink]]</c> の名前解決に使う「名前 → ファイルパス」の索引（ISSUE #53）。
///
/// 本文は読まない。ファイルパスの列挙だけで作るため、500 ファイルでも数十 ms で済む
/// （本文まで読むと初回は桁が 2 つ変わる）。列挙規則は横断検索・依存グラフと同じ
/// <see cref="FolderSearchService.EnumerateMarkdownFiles"/> を使い、除外フォルダ・深さ・
/// リンク追従ポリシーを揃える（件数上限だけは本文を読まないぶん多めに取る）。
///
/// キーは「拡張子あり/なし」×「ファイル名だけ / 末尾のフォルダを含む形」を登録する。
/// これで <c>[[note]]</c> <c>[[note.md]]</c> <c>[[sub/note]]</c> のどれでも同じ結果になる。
/// </summary>
internal sealed class WikiLinkIndex
{
    /// <summary>
    /// フォルダ指定つきのキー（"sub/note"）を、末尾から何段まで登録するか。
    /// 深いツリーでキーが増え続けるのを抑える。
    /// </summary>
    private const int MaxSuffixSegments = 4;

    /// <summary>
    /// キャッシュの寿命。依存グラフ（LinkGraphService）と同じ 10 秒。
    /// 描画のたびにフォルダを列挙し直さないためのもの。
    /// </summary>
    private static readonly object CacheLock = new();
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(10);

    /// <summary>
    /// 覚えておくルートの数。1 つだけにすると、別々のフォルダのタブを行き来した
    /// だけで毎回作り直しになる（キャッシュが意味を成さない）。
    /// 追い出しは構築が古い順（FIFO）。参照のたびに寿命が延びると、
    /// 表示中のタブのルートがいつまでも残って更新が反映されなくなる。
    /// </summary>
    private const int MaxCachedRoots = 4;

    /// <summary>
    /// 索引に入れるファイル数の上限。横断検索の 500 件より多く取るのは、
    /// 索引がパス文字列しか持たない（本文を読まない）ぶん安いのと、上限に届いた
    /// 索引では「候補 1 件」を確定として扱えなくなるため。走査自体は描画の前に
    /// バックグラウンドで済ませる。
    /// </summary>
    private const int MaxIndexedFiles = 5000;

    private static readonly Dictionary<string, WikiLinkIndex> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    private DateTime _builtAtUtc;

    /// <summary>
    /// 走査を最後までやり切ったか。上限に達した・途中で例外が出た場合は false。
    /// false のとき「候補 1 件」は「未列挙の範囲に別の同名があるかもしれない」を
    /// 意味するため、確定として扱ってはいけない。
    /// </summary>
    public bool IsComplete { get; private set; } = true;

    /// <summary>正規化キー → フルパス（ordinal 昇順で保持する）。</summary>
    private readonly Dictionary<string, List<string>> _byKey;

    /// <summary>索引の起点フォルダ（フルパス）。</summary>
    public string Root { get; }

    private WikiLinkIndex(string root, Dictionary<string, List<string>> byKey)
    {
        Root = root;
        _byKey = byKey;
    }

    /// <summary>
    /// 指定ルートの索引を返す。同一ルートかつ 10 秒以内ならキャッシュを再利用する。
    /// </summary>
    public static WikiLinkIndex GetOrBuild(string rootFolder, CancellationToken token = default)
    {
        string root;
        try
        {
            root = Path.GetFullPath(rootFolder);
        }
        catch
        {
            root = rootFolder;
        }

        lock (CacheLock)
        {
            if (Cache.TryGetValue(root, out WikiLinkIndex? cached)
                && DateTime.UtcNow - cached._builtAtUtc < CacheTtl)
            {
                return cached;
            }
        }

        WikiLinkIndex index = Build(root, token);
        index._builtAtUtc = DateTime.UtcNow;

        lock (CacheLock)
        {
            Cache[root] = index;

            // 覚えすぎない。古いものから落とす。
            while (Cache.Count > MaxCachedRoots)
            {
                string? oldest = null;
                DateTime oldestAt = DateTime.MaxValue;
                foreach (var pair in Cache)
                {
                    if (pair.Value._builtAtUtc < oldestAt)
                    {
                        oldestAt = pair.Value._builtAtUtc;
                        oldest = pair.Key;
                    }
                }
                if (oldest == null)
                {
                    break;
                }
                Cache.Remove(oldest);
            }
        }
        return index;
    }

    private static WikiLinkIndex Build(string root, CancellationToken token)
    {
        var byKey = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        bool complete = true;

        try
        {
            int count = 0;
            foreach (string file in FolderSearchService.EnumerateMarkdownFiles(root, token))
            {
                // 時間では打ち切らない。途中まで作った索引を「完全な索引」として使うと、
                // 同名候補の一部だけが入った状態を「候補 1 件」と誤判定し、曖昧の印も
                // 付けずに別のファイルを開いてしまう。しかも結果が実行のたびに変わる。
                //
                // 件数上限に当たったときも同じ危険があるので、そのときは
                // 「やり切っていない」ことを索引に持たせ、確定として扱わせない。
                if (count >= MaxIndexedFiles)
                {
                    complete = false;
                    Diagnostics.Record(
                        "wikilink",
                        $"リンク索引が上限（{MaxIndexedFiles} 件）に達しました"
                        + "（超過分は未解決になり、解決できたものも確定として扱いません）",
                        $"root={root}");
                    break;
                }
                count++;
                AddKeys(byKey, root, file);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 列挙の失敗（アクセス拒否など）はそこまでの索引で続ける。
            // ここで落とすと文書そのものが表示できなくなる。ただし途中までなので、
            // 確定として扱わせない。
            complete = false;
            Diagnostics.Record("wikilink", "リンク索引の構築が途中で失敗しました", ex);
        }

        // 同名が複数あるときの「最後の一手」を決定的にするため、常に昇順で持つ。
        foreach (var list in byKey.Values)
        {
            if (list.Count > 1)
            {
                list.Sort(StringComparer.OrdinalIgnoreCase);
            }
        }

        return new WikiLinkIndex(root, byKey) { IsComplete = complete };
    }

    /// <summary>
    /// 文書を描画する前に、その文書が使う索引をバックグラウンドで用意しておく。
    ///
    /// 描画（<see cref="MarkdownRenderer.Render"/>）は UI スレッドで同期に走るため、
    /// そこで初めて索引を作るとフォルダ走査のぶんだけ画面が止まる。走査そのものは
    /// 横断検索・依存グラフと同じ量で、既に別スレッドで行っている処理でもある。
    ///
    /// 本文に "[[" が無ければ索引は要らないので、その場合は何もしない。
    /// 失敗しても描画は続く（パーサ側が必要になった時点で作り直す）。
    /// </summary>
    public static Task PrewarmAsync(string filePath) => Task.Run(() =>
    {
        try
        {
            if (!SettingsStore.Instance.WikiLinksEnabled)
            {
                return;
            }

            var info = new FileInfo(filePath);
            if (!info.Exists || info.Length > FolderSearchService.MaxFileBytes)
            {
                return;
            }

            string text = MarkdownRenderer.ReadFileText(filePath);
            if (!text.Contains("[[", StringComparison.Ordinal))
            {
                return;
            }

            string? directory = Path.GetDirectoryName(Path.GetFullPath(filePath));
            if (string.IsNullOrEmpty(directory))
            {
                return;
            }

            GetOrBuild(LinkGraphService.FindWorkspaceRoot(directory));
        }
        catch
        {
            // 温め損ねただけ。描画は続行し、必要ならパーサ側が同期で作る。
        }
    });

    private static void AddKeys(Dictionary<string, List<string>> byKey, string root, string file)
    {
        string fileName = Path.GetFileName(file);
        string fileNameNoExt = Path.GetFileNameWithoutExtension(file);

        Add(byKey, fileName, file);
        Add(byKey, fileNameNoExt, file);

        string relative;
        try
        {
            relative = Path.GetRelativePath(root, file);
        }
        catch
        {
            return;
        }

        // ルート外（"..\" を含む）はサブパスキーを作らない。
        if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
        {
            return;
        }

        // ルート相対パスの「末尾の並び」をすべて登録する（sub/note, a/sub/note, …）。
        // ルートの推定は FindWorkspaceRoot 任せで、利用者から見える位置とは限らない。
        // 末尾一致にしておけば、ルートがどこに決まっても [[sub/note]] が同じ意味になる。
        var segments = relative.Split(
            new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
        int from = Math.Max(0, segments.Length - 1 - MaxSuffixSegments);
        for (int i = segments.Length - 2; i >= from; i--)
        {
            string suffix = string.Join('/', segments, i, segments.Length - i);
            Add(byKey, suffix, file);

            int dot = suffix.LastIndexOf('.');
            int slash = suffix.LastIndexOf('/');
            if (dot > slash)
            {
                Add(byKey, suffix[..dot], file);
            }
        }
    }

    private static void Add(Dictionary<string, List<string>> byKey, string rawKey, string file)
    {
        string key = NormalizeKey(rawKey);
        if (key.Length == 0)
        {
            return;
        }

        if (!byKey.TryGetValue(key, out var list))
        {
            list = new List<string>(1);
            byKey[key] = list;
        }
        if (!list.Contains(file, StringComparer.OrdinalIgnoreCase))
        {
            list.Add(file);
        }
    }

    /// <summary>
    /// 比較用にキーを正規化する。
    /// 前後の空白を落とし、半角の連続空白を 1 個へ畳み、区切りを "/" へ揃える。
    /// 全角スペースは**潰さない**（ファイル名として半角と区別されるため、
    /// 潰すと実在するファイルを開けなくなる）。
    /// </summary>
    internal static string NormalizeKey(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(value.Length);
        bool pendingSpace = false;

        foreach (char c in value)
        {
            if (c == ' ' || c == '\t')
            {
                pendingSpace = sb.Length > 0;
                continue;
            }
            if (pendingSpace)
            {
                sb.Append(' ');
                pendingSpace = false;
            }
            sb.Append(c == '\\' ? '/' : c);
        }

        // 先頭の "./" は書いても書かなくても同じ意味にする。
        int start = 0;
        while (start + 1 < sb.Length && sb[start] == '.' && sb[start + 1] == '/')
        {
            start += 2;
        }
        return start == 0 ? sb.ToString() : sb.ToString(start, sb.Length - start);
    }

    /// <summary>
    /// 名前をファイルパスへ解決する。
    ///
    /// 優先順位:
    ///   1. サブパス指定（"a/b"）はそのキーで一意に決まる
    ///   2. リンク元と同じフォルダにある候補
    ///   3. ルートからの階層が最も浅い候補
    ///   4. それでも複数なら昇順の先頭を採用し、曖昧として返す
    /// </summary>
    /// <param name="name">`[[...]]` の名前部分（正規化前でよい）。</param>
    /// <param name="fromDirectory">リンク元ファイルのフォルダ。</param>
    public WikiLinkResolution Resolve(string name, string fromDirectory)
    {
        string key = NormalizeKey(name);
        if (key.Length == 0)
        {
            return NotFoundHere();
        }

        // 上位フォルダへ抜ける指定は受け付けない（索引はルート配下しか持たない）。
        if (ContainsParentSegment(key))
        {
            // これは索引の完全性とは無関係に決まる（ルート外は元から対象外）。
            return WikiLinkResolution.NotFound;
        }

        List<string>? candidates = Lookup(key);
        if (candidates == null || candidates.Count == 0)
        {
            return NotFoundHere();
        }

        int total = candidates.Count;
        if (total == 1)
        {
            return new WikiLinkResolution(WikiLinkVerdict.Resolved, candidates[0], 1, IsComplete);
        }

        // 2. リンク元と同じフォルダ。
        string? sameFolder = null;
        int sameFolderCount = 0;
        foreach (string candidate in candidates)
        {
            string? dir = Path.GetDirectoryName(candidate);
            if (dir != null && string.Equals(dir, fromDirectory, StringComparison.OrdinalIgnoreCase))
            {
                sameFolder ??= candidate;
                sameFolderCount++;
            }
        }
        if (sameFolderCount == 1 && sameFolder != null)
        {
            return new WikiLinkResolution(WikiLinkVerdict.Resolved, sameFolder, total, IsComplete);
        }

        // 3. ルートからの階層が最も浅い候補。
        string? shallowest = null;
        int shallowestDepth = int.MaxValue;
        int shallowestCount = 0;
        foreach (string candidate in candidates)
        {
            int depth = DepthFromRoot(candidate);
            if (depth < shallowestDepth)
            {
                shallowestDepth = depth;
                shallowest = candidate;
                shallowestCount = 1;
            }
            else if (depth == shallowestDepth)
            {
                shallowestCount++;
            }
        }
        if (shallowestCount == 1 && shallowest != null)
        {
            return new WikiLinkResolution(WikiLinkVerdict.Resolved, shallowest, total, IsComplete);
        }

        // 4. 決まらない。昇順の先頭を採用し、曖昧であることを呼び出し側へ伝える。
        return new WikiLinkResolution(WikiLinkVerdict.Ambiguous, candidates[0], total, IsComplete);
    }

    /// <summary>
    /// 「見つからなかった」を、索引の完全性つきで返す。索引がやり切れていないなら
    /// 「本当に無い」とは言い切れないため、呼び出し側が言い方を変えられるようにする。
    /// </summary>
    private WikiLinkResolution NotFoundHere()
        => new(WikiLinkVerdict.Unresolved, null, 0, IsComplete);

    private List<string>? Lookup(string key)
    {
        if (_byKey.TryGetValue(key, out var list))
        {
            return list;
        }

        // "[[a.md]]" と書かれたが実体が a.markdown、のような食い違いを救う。
        string ext = Path.GetExtension(key);
        if (ext.Equals(".md", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".markdown", StringComparison.OrdinalIgnoreCase))
        {
            string withoutExt = key[..^ext.Length];
            if (withoutExt.Length > 0 && _byKey.TryGetValue(withoutExt, out var fallback))
            {
                return fallback;
            }
        }
        return null;
    }

    private int DepthFromRoot(string path)
    {
        string relative;
        try
        {
            relative = Path.GetRelativePath(Root, path);
        }
        catch
        {
            return int.MaxValue - 1;
        }

        int depth = 0;
        foreach (char c in relative)
        {
            if (c == '\\' || c == '/')
            {
                depth++;
            }
        }
        return depth;
    }

    private static bool ContainsParentSegment(string key)
    {
        // "..", "../x", "x/../y", "x/.." のいずれも拒否する。
        int index = 0;
        while (index < key.Length)
        {
            int slash = key.IndexOf('/', index);
            int end = slash < 0 ? key.Length : slash;
            if (end - index == 2 && key[index] == '.' && key[index + 1] == '.')
            {
                return true;
            }
            if (slash < 0)
            {
                break;
            }
            index = slash + 1;
        }
        return false;
    }
}
