using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;

namespace Hirake;

/// <summary>
/// フォルダを走査してよいか。<b>拒否の理由まで持つ</b>のが要点。
///
/// bool だけだと「ネットワークだから絶対に開けない」と「ローカルのリンクなので
/// 設定を入れれば開ける」を区別できず、利用者に案内が出せない（ISSUE #61）。
/// </summary>
public enum TraversalVerdict
{
    /// <summary>走査してよい。</summary>
    Allowed,

    /// <summary>実在しない・判定できない・リンクの連鎖が長すぎる。</summary>
    NotFound,

    /// <summary>
    /// UNC・ネットワークドライブ・デバイスパス。設定では開けないので、
    /// <b>「設定を有効にすると開けます」と案内してはいけない</b>。
    /// </summary>
    BlockedByNetwork,

    /// <summary>
    /// ローカルのリンクを経由している。<c>FollowDirectoryLinks</c> を
    /// 有効にすれば開けるので、案内を出してよい唯一の理由。
    /// </summary>
    BlockedByLink,
}

/// <summary>走査の可否判定の結果と、許可された場合の実パス。</summary>
public readonly record struct TraversalCheck(TraversalVerdict Verdict, string? ResolvedPath)
{
    /// <summary>走査してよい。</summary>
    public static TraversalCheck Allow(string resolvedPath)
        => new(TraversalVerdict.Allowed, resolvedPath);

    /// <summary>走査しない。理由を添える。</summary>
    public static TraversalCheck Blocked(TraversalVerdict verdict) => new(verdict, null);

    /// <summary>走査してよいか。</summary>
    public bool IsAllowed => Verdict == TraversalVerdict.Allowed;

    /// <summary>「設定を有効にすると開けます」と案内すべきか。</summary>
    public bool NeedsSettingHint => Verdict == TraversalVerdict.BlockedByLink;
}

/// <summary>
/// ファイルツリーサイドバーの 1 ノード（フォルダ or Markdown ファイル）。
/// フォルダは遅延展開し、初回展開時に子を実際に読み込む（深い走査を避ける）。
/// </summary>
public sealed class FileTreeItem : INotifyPropertyChanged
{
    /// <summary>ツリー表示対象の Markdown 拡張子。</summary>
    public static readonly string[] MarkdownExtensions = { ".md", ".markdown" };

    // サブフォルダに Markdown が含まれるか判定するときの再帰上限（深すぎる走査を防ぐ）。
    private const int ContainsScanMaxDepth = 12;

    // リンクを解決するときに辿るホップ数の上限。リンクがリンクを指す連鎖で
    // 止まらなくなるのを防ぐ（上限に達したら「辿らない」に倒す）。
    private const int MaxLinkHops = 8;

    // ContainsMarkdown（深い再帰走査）の結果をディレクトリパスでキャッシュする。
    // タブ切替のたびに同じフォルダを走査し直すのを避ける（プロセス寿命で保持）。
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> ContainsMarkdownCache =
        new(StringComparer.OrdinalIgnoreCase);

    private bool _isExpanded;
    private bool _isSelected;
    private bool _isActive;
    private bool _childrenLoaded;

    private FileTreeItem()
    {
    }

    public string Name { get; private init; } = string.Empty;
    public string FullPath { get; private init; } = string.Empty;
    public bool IsDirectory { get; private init; }

    public ObservableCollection<FileTreeItem> Children { get; } = new();

    /// <summary>Segoe MDL2 Assets のグリフ（フォルダ / ドキュメント）。</summary>
    public string Glyph => IsDirectory ? "" : "";

    /// <summary>TreeViewItem の展開状態にバインドする。展開時に子を遅延ロードする。</summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value)
            {
                return;
            }
            _isExpanded = value;
            OnPropertyChanged();
            if (value)
            {
                EnsureChildrenLoaded();
            }
        }
    }

    /// <summary>TreeViewItem の選択状態にバインドする。</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value)
            {
                return;
            }
            _isSelected = value;
            OnPropertyChanged();
        }
    }

    /// <summary>アクティブタブのファイルかどうか（ハイライト用）。</summary>
    public bool IsActive
    {
        get => _isActive;
        set
        {
            if (_isActive == value)
            {
                return;
            }
            _isActive = value;
            OnPropertyChanged();
        }
    }

    // ---- 生成 ---------------------------------------------------------

    private static FileTreeItem CreateFile(string path) => new()
    {
        Name = Path.GetFileName(path),
        FullPath = path,
        IsDirectory = false,
    };

    private static FileTreeItem CreateDirectory(string path)
    {
        var item = new FileTreeItem
        {
            Name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar)),
            FullPath = path,
            IsDirectory = true,
        };
        // 展開矢印を表示させるためのプレースホルダ（初回展開時に本物へ差し替える）。
        item.Children.Add(new FileTreeItem { Name = string.Empty, FullPath = string.Empty });
        return item;
    }

    // ---- 遅延ロード ---------------------------------------------------

    /// <summary>まだ読み込んでいなければ、このフォルダの子ノードを実ファイルから構築する。</summary>
    public void EnsureChildrenLoaded()
    {
        if (_childrenLoaded || !IsDirectory)
        {
            return;
        }
        _childrenLoaded = true;

        Children.Clear();
        foreach (var child in LoadChildren(FullPath))
        {
            Children.Add(child);
        }
    }

    /// <summary>
    /// 指定フォルダ直下の「Markdown を含むサブフォルダ」と「Markdown ファイル」を
    /// 名前順（フォルダ→ファイル）で返す。
    /// </summary>
    public static List<FileTreeItem> LoadChildren(string directory)
    {
        var dirs = new List<FileTreeItem>();
        var files = new List<FileTreeItem>();

        // 実体でまとめる。同じフォルダを指すリンクが複数あると、実パスに直した
        // 結果として同じ子が並んでしまう（ISSUE #54）。
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 起点そのものも検査する（子だけを検査すると、リンクを直接開いた場合に素通りする）。
        if (!IsSafeTraversalRoot(directory))
        {
            return new List<FileTreeItem>();
        }

        // 検査した実体をそのまま列挙する。元のリンク名で列挙すると、検査後に
        // 差し替えられたリンクを辿ってしまい、検索側（実パスで列挙）とも挙動が食い違う。
        string? root = ResolveRealPath(directory, isDirectory: true);
        if (root == null)
        {
            return new List<FileTreeItem>();
        }

        try
        {
            foreach (var sub in Directory.EnumerateDirectories(root))
            {
                try
                {
                    if (ShouldSkipDirectory(sub))
                    {
                        continue;
                    }
                    // リンク先は実パスで扱う。ツリーの表示・以後の展開・
                    // タブの二重オープン判定をすべて実体で一致させる（ISSUE #54）。
                    string? real = ResolveRealPath(sub, isDirectory: true);
                    if (real != null && seen.Add(real) && ContainsMarkdownCached(real))
                    {
                        dirs.Add(CreateDirectory(real));
                    }
                }
                catch
                {
                    // 個別サブフォルダの判定失敗は無視する。
                }
            }

            foreach (var file in Directory.EnumerateFiles(root))
            {
                if (!IsMarkdownFile(file))
                {
                    continue;
                }

                string? realFile = ResolveRealPath(file, isDirectory: false);
                if (realFile != null && seen.Add(realFile))
                {
                    files.Add(CreateFile(realFile));
                }
            }
        }
        catch
        {
            // フォルダ列挙失敗時は空を返す。
        }

        dirs.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        files.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

        var result = new List<FileTreeItem>(dirs.Count + files.Count);
        result.AddRange(dirs);
        result.AddRange(files);
        return result;
    }

    // ---- 判定ヘルパー -------------------------------------------------

    public static bool IsMarkdownFile(string path)
    {
        string ext = Path.GetExtension(path);
        foreach (var candidate in MarkdownExtensions)
        {
            if (!ext.Equals(candidate, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // 拡張子が合っていても、リンク先が別の場所（UNC 共有など）を指す
            // ファイル symlink は対象にしない。
            return !IsLinkedFile(path);
        }
        return false;
    }

    /// <summary>
    /// 隠しフォルダ・ドット始まり・node_modules・再解析ポイントをスキップ対象とする。
    ///
    /// 再解析ポイント（ジャンクション／シンボリックリンク）を除外するのは 2 つの理由から。
    /// 1 つは、起点フォルダ自体がローカルでも配下のリンクが UNC 共有を指していれば、
    /// そこを走査した時点で SMB 認証情報の漏洩経路になるため（hirake:// の UNC 拒否と
    /// 同じ目的。#42 でワークスペースから任意フォルダを起点にできるようになり、
    /// この経路が外部入力から到達可能になった）。もう 1 つはリンクの循環による
    /// 無限再帰を避けるため。
    ///
    /// 判定は ReparsePoint 属性ではなく <see cref="FileSystemInfo.LinkTarget"/> で行う。
    /// OneDrive などのクラウド同期フォルダも ReparsePoint 属性を持つため、属性で
    /// 判定すると同期フォルダ配下がまるごと検索・ツリーから消える。LinkTarget が
    /// 非 null になるのはシンボリックリンクとジャンクションだけで、狙いどおり
    /// 「別の場所を指しているフォルダ」だけを除外できる。
    ///
    /// 判定できない場合はスキップする（辿ってよいと確認できないものは辿らない）。
    /// </summary>
    public static bool ShouldSkipDirectory(string path)
    {
        string name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar));
        if (string.IsNullOrEmpty(name))
        {
            return false;
        }
        if (name.StartsWith('.'))
        {
            return true;
        }
        if (name.Equals("node_modules", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        try
        {
            var info = new DirectoryInfo(path);
            if ((info.Attributes & FileAttributes.Hidden) == FileAttributes.Hidden)
            {
                return true;
            }
            if (info.LinkTarget != null)
            {
                return !IsFollowableLink(path, isDirectory: true);
            }
        }
        catch
        {
            return true;
        }
        return false;
    }

    /// <summary>
    /// リンク（シンボリックリンク／ジャンクション）を辿ってよいか。
    /// <b>リンクだと分かっているパスにだけ呼ぶこと。</b>
    ///
    /// 既定では常に false を返し、これまでどおり一切辿らない。設定
    /// （<c>FollowDirectoryLinks</c>）で明示的に有効にしたときだけ、
    /// 「解決先がローカルのドライブである」と確認できたリンクを辿る。
    ///
    /// 有効にしても fail-closed の芯は崩さない。解決できない・UNC・
    /// ネットワークドライブ・種別が分からないドライブは辿らない。
    /// これは symlink で束ねた vault を横断するためのもので、SMB 認証情報の
    /// 漏洩経路を開けるためのものではない（ISSUE #54）。
    /// </summary>
    private static bool IsFollowableLink(string path, bool isDirectory)
    {
        bool followLinks = SettingsStore.Instance.FollowDirectoryLinks;
        if (!followLinks)
        {
            return false;
        }

        // 解決できた＝祖先も飛び先もすべてローカルだったということ。
        return CheckTraversalCore(path, isDirectory, followLinks).IsAllowed;
    }

    /// <summary>
    /// パスを<b>ルート側から 1 要素ずつ</b>検査しながら実体へ解決する。
    /// 途中に 1 つでもローカル以外を指すリンクがあれば null。
    /// リンクが 1 つも無ければそのパスの絶対形。
    ///
    /// <para>
    /// ルート側から見るのは、末端から遡ると危険な祖先を判定する前にその祖先を
    /// 経由した問い合わせが起き、そこでネットワークへ出てしまうため
    /// （到達しない UNC 祖先で実測 42 秒 → 0 秒）。
    /// </para>
    /// <para>
    /// リンクを見つけたら、残りの要素を飛び先に継ぎ足して<b>ルートからやり直す</b>。
    /// 飛び先そのものがローカルでも、その祖先が UNC を指していることがあるため
    /// （<c>C:\vault\docs -> C:\safe\redirect\docs</c> かつ
    /// <c>C:\safe\redirect -> \\server\share</c>）、飛び先も同じ検査にかける。
    /// </para>
    /// <para>
    /// <c>returnFinalTarget: true</c> は使わない。UNC を指すリンクの解決結果が
    /// <c>C:\...\UNC\server\share</c> のような**ローカル風のパスに化ける**ため、
    /// それを見て判定するとネットワーク先を辿ってしまう（実際に踏んだ）。
    /// 1 ホップずつ受け取れば <c>\\server\share</c> のまま判定できる。
    /// </para>
    /// </summary>
    private static TraversalCheck CheckTraversalCore(string path, bool isDirectory, bool followLinks)
    {
        char[] separators = { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar };

        try
        {
            string full = Path.GetFullPath(path);
            int hops = 0;

            // 設定が無効でも解決は最後まで行う。途中で打ち切ると、UNC を指す
            // リンクや壊れたリンクまで「設定を有効にすれば開けます」と
            // 案内してしまう（実際には設定を入れても開けない）。
            bool sawLink = false;

            while (true)
            {
                // 起点は「ネットワークでないこと」だけを課す（CD-ROM やボリューム
                // GUID パスを従来どおり開けるように）。リンクの飛び先は許可制。
                bool acceptable = hops == 0 ? !IsNetworkPath(full) : IsLocalDrivePath(full);
                if (!acceptable)
                {
                    return TraversalCheck.Blocked(TraversalVerdict.BlockedByNetwork);
                }

                string? root = Path.GetPathRoot(full);
                if (string.IsNullOrEmpty(root))
                {
                    return TraversalCheck.Blocked(TraversalVerdict.NotFound);
                }

                string[] parts = full[root.Length..].Split(separators, StringSplitOptions.RemoveEmptyEntries);

                string current = root;
                bool followed = false;

                for (int i = 0; i < parts.Length; i++)
                {
                    current = Path.Combine(current, parts[i]);

                    // 末端だけは呼び出し側の種別に従う。途中の要素は必ずフォルダ。
                    bool asDirectory = isDirectory || i < parts.Length - 1;

                    // LinkTarget は実在しないパスでも例外にならないので、
                    // リンクかどうかの判定にはこちらを使う。
                    FileSystemInfo info = asDirectory
                        ? new DirectoryInfo(current)
                        : new FileInfo(current);
                    if (info.LinkTarget == null)
                    {
                        continue;
                    }

                    sawLink = true;

                    if (++hops > MaxLinkHops)
                    {
                        return TraversalCheck.Blocked(TraversalVerdict.NotFound);
                    }

                    FileSystemInfo? target = asDirectory
                        ? Directory.ResolveLinkTarget(current, returnFinalTarget: false)
                        : File.ResolveLinkTarget(current, returnFinalTarget: false);
                    if (target == null)
                    {
                        return TraversalCheck.Blocked(TraversalVerdict.NotFound);
                    }

                    string rest = string.Join(Path.DirectorySeparatorChar, parts[(i + 1)..]);
                    full = rest.Length == 0
                        ? target.FullName
                        : Path.Combine(target.FullName, rest);
                    followed = true;
                    break;
                }

                if (!followed)
                {
                    // ここまで来た＝経路はすべてローカルで、解決も済んでいる。
                    // リンクを 1 つでも通ったなら、設定に従って可否を決める。
                    if (sawLink && !followLinks)
                    {
                        // 「設定を有効にすれば開ける」と言えるのは、実際に開ける
                        // ときだけ。壊れたリンク（飛び先が無い）でこれを返すと、
                        // 設定を入れても開けないのに案内が出てしまう。
                        bool exists = isDirectory ? Directory.Exists(full) : File.Exists(full);
                        return TraversalCheck.Blocked(
                            exists ? TraversalVerdict.BlockedByLink : TraversalVerdict.NotFound);
                    }

                    return TraversalCheck.Allow(full);
                }
            }
        }
        catch
        {
            return TraversalCheck.Blocked(TraversalVerdict.NotFound);
        }
    }

    /// <summary>
    /// ネットワーク上のパスか。UNC（<c>\\server\share</c> と <c>\\?\UNC\...</c>）と
    /// ネットワークドライブだけを拒否する。
    ///
    /// <see cref="IsLocalDrivePath"/> はリンクの飛び先用の許可制で、これはより緩い
    /// 拒否制。起点は従来 CD-ROM・RAM ディスク・種別不明のローカル媒体・
    /// <c>\\?\Volume{...}</c> でも開けたため、その契約（README の
    /// 「UNC・ネットワークドライブを拒否」）を変えないためにこちらを使う。
    ///
    /// <para>
    /// ただしデバイス名前空間は「UNC の別表記でなければ通す」では足りない。
    /// <c>\\?\GLOBALROOT\Device\Mup\server\share</c> のように、UNC を名乗らずに
    /// ネットワークへ出る書き方があるため、<c>\\?\C:\</c> と
    /// <c>\\?\Volume{GUID}\</c> だけを明示的に許可し、それ以外の <c>\\?\</c> と
    /// <c>\\.\</c> はすべて拒否する。
    /// </para>
    /// </summary>
    private static bool IsNetworkPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return true;
        }

        string normalized = path.Replace('/', '\\');

        // \\.\ はデバイスを直接開く書き方で、文書の置き場としては使わない。全拒否。
        if (normalized.StartsWith(@"\\.\", StringComparison.Ordinal))
        {
            return true;
        }

        if (normalized.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            string rest = normalized[4..];

            // \\?\Volume{GUID}\... はローカルボリューム。前方一致だけで通すと
            // \\?\Volume{...}evil のような名前まで通るので、GUID と閉じ括弧、
            // その次が区切りか終端かまで確かめる。
            if (rest.StartsWith("Volume{", StringComparison.OrdinalIgnoreCase))
            {
                int close = rest.IndexOf('}');
                if (close != "Volume{".Length + 36)
                {
                    return true;
                }

                if (!Guid.TryParseExact(rest["Volume{".Length..close], "D", out _))
                {
                    return true;
                }

                bool wellFormed = close == rest.Length - 1 || rest[close + 1] == '\\';
                return !wellFormed;
            }

            // \\?\C:\... はローカルドライブ。ドライブ種別は後段で見る。
            if (rest.Length >= 2 && char.IsAsciiLetter(rest[0]) && rest[1] == ':')
            {
                normalized = rest;
            }
            else
            {
                // GLOBALROOT・Device・UNC など、それ以外はすべて拒否する。
                return true;
            }
        }
        else if (normalized.StartsWith(@"\\", StringComparison.Ordinal))
        {
            // \\server\share。
            return true;
        }

        try
        {
            string full = Path.GetFullPath(normalized);
            if (!Path.IsPathFullyQualified(full))
            {
                return true;
            }

            string? root = Path.GetPathRoot(full);
            if (string.IsNullOrEmpty(root))
            {
                return true;
            }

            // 割り当て済みネットワークドライブ（Z: → \\server\share）を拒否する。
            return new DriveInfo(root).DriveType == DriveType.Network;
        }
        catch
        {
            return true;
        }
    }

    /// <summary>
    /// ローカルの固定／リムーバブルドライブ上のパスか。
    /// UNC・ネットワークドライブ・種別が分からないものは false（fail-closed）。
    ///
    /// 見るのは字句とドライブ種別だけで、途中のリンクは見ない。
    /// <see cref="HirakeUri.IsRemoteOrUncFolder"/> は祖先にリンクがあると拒否するため、
    /// ここで使うと「ローカル → ローカル」のリンクの連鎖まで弾いてしまう。
    /// 連鎖の各ホップは <see cref="CheckTraversalCore"/> が 1 つずつ検査する。
    /// </summary>
    private static bool IsLocalDrivePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        if (path.StartsWith(@"\\", StringComparison.Ordinal)
            || path.StartsWith("//", StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            string full = Path.GetFullPath(path);
            if (full.StartsWith(@"\\", StringComparison.Ordinal) || !Path.IsPathFullyQualified(full))
            {
                return false;
            }

            string? root = Path.GetPathRoot(full);
            if (string.IsNullOrEmpty(root))
            {
                return false;
            }

            // リムーバブル（USB 上の vault）まで許可する。ネットワークと
            // 判定できないものは拒否する。CD-ROM と RAM ディスクは文書の
            // 置き場として想定しない。
            return new DriveInfo(root).DriveType is DriveType.Fixed or DriveType.Removable;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 走査に使う実パス。リンクなら安全に解決できたときだけその解決先を返し、
    /// <b>解決できなければ null を返す（元のパスへは絶対に戻さない）</b>。
    /// リンクでなければそのパスの絶対形。
    ///
    /// 元のパスへフォールバックしてはいけない。判定の直後にリンクの向き先が
    /// UNC へ差し替えられた場合、「解決できなかったリンク名」をそのまま
    /// 走査対象へ戻すことになり、危険と判定したはずの経路へ触れてしまう。
    ///
    /// 走査ではこの実パスで visited を持つ。リンク経由のパスのままだと、
    /// 相互に指し合うリンクで止まらず、同じファイルが二重に出る。
    ///
    /// <para>
    /// 想定している脅威は「あらかじめ置かれた（静的な）リンクを辿って、意図せず
    /// SMB 認証を送ってしまうこと」。検査中にパスを差し替えてくる攻撃者は対象外で、
    /// その競合はここでは閉じ切れない（リンクでないと判定した直後に UNC リンクへ
    /// 差し替えられれば、未検証の元パスを返す）。閉じるにはパスではなく
    /// 再解析ポイントを辿らないハンドルで開き、同じハンドルのまま扱う設計が要る。
    /// </para>
    /// <para>
    /// <b>祖先は検査しない。</b>列挙元が先に <see cref="IsSafeTraversalRoot"/> を
    /// 通っていて、その配下を 1 つずつ見ている場合にだけ使うこと。祖先が未検査の
    /// パスには <see cref="ResolveCheckedPath"/> を使う。
    /// </para>
    /// </summary>
    internal static string? ResolveRealPath(string path, bool isDirectory)
    {
        try
        {
            bool isLink = isDirectory
                ? new DirectoryInfo(path).LinkTarget != null
                : new FileInfo(path).LinkTarget != null;

            if (!isLink)
            {
                return Path.GetFullPath(path);
            }

            return CheckTraversalCore(
                path, isDirectory, SettingsStore.Instance.FollowDirectoryLinks).ResolvedPath;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// <b>祖先まで含めて検査した</b>実パス。安全に解決できなければ null。
    ///
    /// 列挙の途中ではなく、外から来たパス（タブのファイル、ワークスペース、
    /// コマンドライン引数など）を扱うときはこちらを使う。<see cref="ResolveRealPath"/>
    /// は末端しか見ないため、<c>C:\vault\redirect\docs</c>（<c>redirect</c> が
    /// UNC を指すリンク、<c>docs</c> 自体はリンクでない）を素通しし、その後の
    /// <c>Directory.Exists</c> でネットワークへ出てしまう。
    ///
    /// <para>
    /// 設定が無効なとき、リンクを経由するパスは<b>拒否</b>する（ISSUE #61）。
    /// 以前は検査だけ行って元のパスを返していたが、それだと「辿らない」設定なのに
    /// リンク経由のフォルダを走査できてしまい、設定の意味と食い違っていた。
    /// </para>
    /// </summary>
    internal static string? ResolveCheckedPath(string path, bool isDirectory)
        => CheckTraversal(path, isDirectory).ResolvedPath;

    /// <summary>
    /// 設定値を明示的に受け取る版。<b>SettingsStore の初期化中はこちらを使う。</b>
    ///
    /// 初期化中に <see cref="SettingsStore.Instance"/> を引くと、<c>Lazy</c> の
    /// 再帰取得になって例外が飛ぶ。例外は握り潰されて「解決できなかった」ことに
    /// なるため、起動時のセッションだけ黙って正規化されない（ISSUE #60）。
    /// </summary>
    internal static string? ResolveCheckedPath(string path, bool isDirectory, bool followLinks)
        => CheckTraversalCore(path, isDirectory, followLinks).ResolvedPath;

    /// <summary>
    /// 走査してよいかを<b>理由つき</b>で判定し、許可なら実パスも返す。
    /// 案内文の出し分けが要る呼び出し側（UI）はこちらを使う。
    /// </summary>
    internal static TraversalCheck CheckTraversal(string path, bool isDirectory)
        => CheckTraversalCore(path, isDirectory, SettingsStore.Instance.FollowDirectoryLinks);

    /// <summary>
    /// 走査の「起点」として安全なフォルダか。
    ///
    /// 子フォルダ用の <see cref="ShouldSkipDirectory"/> をそのまま起点へ流用しない。
    /// あちらは「再帰中に自動で降りていくべきでない場所」（ドット始まり・
    /// node_modules・隠しフォルダ）まで弾くため、ユーザーが明示的に選んだ
    /// <c>.notes</c> のようなフォルダを起点にできなくなる。
    /// 起点で確認するのは安全性だけ、すなわちリンクでないことと、判定できること。
    ///
    /// 束ねたフォルダの中の 1 つを直接開く使い方があるため、リンクを辿る設定が
    /// 有効なときは起点も同じ規則で許可する。ここだけ塞がっていると片手落ちになる。
    /// </summary>
    public static bool IsSafeTraversalRoot(string path)
    {
        // ファイルシステムへ触る前に字句とドライブ種別で落とす。Exists を先に
        // 呼ぶと、祖先が UNC を指すリンクだった場合にその時点で SMB へ出る。
        //
        // 起点にはリンク用の許可制（固定／リムーバブルのみ）ではなく
        // 「ネットワークでないこと」を課す。従来 CD-ROM・RAM ディスク・
        // 種別不明のローカル媒体・ボリューム GUID パスは開けていたので、
        // 今回の変更でそれらを閉ざしてしまわないようにする。
        if (IsNetworkPath(path))
        {
            return false;
        }

        // 起点自身がリンクでなくても、祖先が \\server\share を指していれば
        // 走査した時点でネットワークへ触れる。ネットワークは設定に関係なく拒否し、
        // ローカルのリンクは設定に従う（設定オフなら祖先リンクでも拒否する。
        // ISSUE #61。以前はここを通していたが、設定の意味と食い違っていた）。
        if (!CheckTraversalCore(
                path, isDirectory: true, SettingsStore.Instance.FollowDirectoryLinks).IsAllowed)
        {
            return false;
        }

        try
        {
            var info = new DirectoryInfo(path);
            if (!info.Exists)
            {
                return false;
            }

            return info.LinkTarget == null || IsFollowableLink(path, isDirectory: true);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// ファイルがリンク（シンボリックリンク／ハードリンク以外の再解析ポイント）かどうか。
    /// フォルダだけを検査しても、ローカルフォルダ直下に <c>\\server\share\x.md</c> を
    /// 指すファイル symlink があれば同じ漏洩経路になるため、ファイルにも同じ判定を行う。
    /// 判定できない場合はリンク扱いにする（fail-closed）。
    ///
    /// リンクを辿る設定が有効で、解決先がローカルだと確認できたものは
    /// 「リンクではない」として扱う（フォルダ側と同じ規則）。
    /// </summary>
    public static bool IsLinkedFile(string path)
    {
        try
        {
            if (new FileInfo(path).LinkTarget == null)
            {
                return false;
            }

            return !IsFollowableLink(path, isDirectory: false);
        }
        catch
        {
            return true;
        }
    }

    /// <summary>
    /// ContainsMarkdown の結果をキャッシュ経由で返す（トップレベル問い合わせ用）。
    ///
    /// キーに設定値を含める。走査結果は <c>FollowDirectoryLinks</c> で変わるため、
    /// 含めないと設定を切り替えたときに古い結果が残る（現状は再起動が要るので
    /// 表面化しないが、設定 UI を付けた時点で壊れる）。
    /// </summary>
    private static bool ContainsMarkdownCached(string directory)
        => ContainsMarkdownCache.GetOrAdd(
            (SettingsStore.Instance.FollowDirectoryLinks ? "1|" : "0|") + directory,
            // 走査するのはキーではなく元のフォルダ。キーには接頭辞が付いている。
            static (_, dir) => ContainsMarkdown(
                dir,
                ContainsScanMaxDepth,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase)),
            directory);

    /// <summary>
    /// フォルダ配下（depth 上限まで）に Markdown が 1 つでも存在するか。最初の 1 件で打ち切る。
    ///
    /// <paramref name="visited"/> には実パスを入れる。同じ実体を指すリンクが
    /// 多数あるフォルダでは、経路ごとに同じ実体へ降りると呼び出し回数が
    /// 分岐数の深さ乗まで膨らむ。1 回の問い合わせの中では同じ実体へ二度降りない。
    /// </summary>
    private static bool ContainsMarkdown(string directory, int depthRemaining, HashSet<string> visited)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(directory))
            {
                if (IsMarkdownFile(file))
                {
                    return true;
                }
            }

            if (depthRemaining <= 0)
            {
                return false;
            }

            foreach (var sub in Directory.EnumerateDirectories(directory))
            {
                if (ShouldSkipDirectory(sub))
                {
                    continue;
                }

                // 実パスで降りる。相互に指し合うリンクがあっても
                // visited と depthRemaining（既定 12）で必ず止まる。
                string? real = ResolveRealPath(sub, isDirectory: true);
                if (real == null || !visited.Add(real))
                {
                    continue;
                }

                if (ContainsMarkdown(real, depthRemaining - 1, visited))
                {
                    return true;
                }
            }
        }
        catch
        {
            // アクセス不能フォルダ等は「含まない」とみなす。
        }
        return false;
    }

    // ---- INotifyPropertyChanged ---------------------------------------

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
