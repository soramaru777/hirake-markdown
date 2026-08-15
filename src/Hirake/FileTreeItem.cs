using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;

namespace Hirake;

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
        if (!SettingsStore.Instance.FollowDirectoryLinks)
        {
            return false;
        }

        // 解決できた＝祖先も飛び先もすべてローカルだったということ。
        return ResolveSafePath(path, isDirectory) != null;
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
    private static string? ResolveSafePath(string path, bool isDirectory)
    {
        char[] separators = { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar };

        try
        {
            string full = Path.GetFullPath(path);
            int hops = 0;

            while (true)
            {
                // 起点は「ネットワークでないこと」だけを課す（CD-ROM やボリューム
                // GUID パスを従来どおり開けるように）。リンクの飛び先は許可制。
                bool acceptable = hops == 0 ? !IsNetworkPath(full) : IsLocalDrivePath(full);
                if (!acceptable)
                {
                    return null;
                }

                string? root = Path.GetPathRoot(full);
                if (string.IsNullOrEmpty(root))
                {
                    return null;
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

                    if (++hops > MaxLinkHops)
                    {
                        return null;
                    }

                    FileSystemInfo? target = asDirectory
                        ? Directory.ResolveLinkTarget(current, returnFinalTarget: false)
                        : File.ResolveLinkTarget(current, returnFinalTarget: false);
                    if (target == null)
                    {
                        return null;
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
                    return full;
                }
            }
        }
        catch
        {
            return null;
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
    /// 連鎖の各ホップは <see cref="ResolveSafePath"/> が 1 つずつ検査する。
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

            return ResolveSafePath(path, isDirectory);
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
    /// リンクの読み替えを行うのは <c>FollowDirectoryLinks</c> が有効なときだけ。
    /// 無効なときは検査だけ行い、<b>元のパスの絶対形をそのまま返す</b>。
    /// 設定を入れていない利用者のパス表示を勝手に実体へ書き換えないため
    /// （ユーザープロファイル配下のようにジャンクションを含むパスは珍しくない）。
    /// </para>
    /// </summary>
    internal static string? ResolveCheckedPath(string path, bool isDirectory)
    {
        if (ResolveSafePath(path, isDirectory) == null)
        {
            return null;
        }

        if (SettingsStore.Instance.FollowDirectoryLinks)
        {
            return ResolveSafePath(path, isDirectory);
        }

        try
        {
            return Path.GetFullPath(path);
        }
        catch
        {
            return null;
        }
    }

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
        // 走査した時点でネットワークへ触れる。設定に関係なく検査する
        // （オフでも「ローカルの祖先リンク」は従来どおり通す。塞ぐのは
        // ネットワークへ出る経路だけ）。
        if (ResolveSafePath(path, isDirectory: true) == null)
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

    /// <summary>ContainsMarkdown の結果をキャッシュ経由で返す（トップレベル問い合わせ用）。</summary>
    private static bool ContainsMarkdownCached(string directory)
        => ContainsMarkdownCache.GetOrAdd(
            directory,
            d => ContainsMarkdown(
                d,
                ContainsScanMaxDepth,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase)));

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
