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

        // 起点そのものも検査する（子だけを検査すると、リンクを直接開いた場合に素通りする）。
        if (!IsSafeTraversalRoot(directory))
        {
            return new List<FileTreeItem>();
        }

        try
        {
            foreach (var sub in Directory.EnumerateDirectories(directory))
            {
                try
                {
                    if (ShouldSkipDirectory(sub))
                    {
                        continue;
                    }
                    if (ContainsMarkdownCached(sub))
                    {
                        dirs.Add(CreateDirectory(sub));
                    }
                }
                catch
                {
                    // 個別サブフォルダの判定失敗は無視する。
                }
            }

            foreach (var file in Directory.EnumerateFiles(directory))
            {
                if (IsMarkdownFile(file))
                {
                    files.Add(CreateFile(file));
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
                return true;
            }
        }
        catch
        {
            return true;
        }
        return false;
    }

    /// <summary>
    /// 走査の「起点」として安全なフォルダか。
    ///
    /// 子フォルダ用の <see cref="ShouldSkipDirectory"/> をそのまま起点へ流用しない。
    /// あちらは「再帰中に自動で降りていくべきでない場所」（ドット始まり・
    /// node_modules・隠しフォルダ）まで弾くため、ユーザーが明示的に選んだ
    /// <c>.notes</c> のようなフォルダを起点にできなくなる。
    /// 起点で確認するのは安全性だけ、すなわちリンクでないことと、判定できること。
    /// </summary>
    public static bool IsSafeTraversalRoot(string path)
    {
        try
        {
            var info = new DirectoryInfo(path);
            return info.Exists && info.LinkTarget == null;
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
    /// </summary>
    public static bool IsLinkedFile(string path)
    {
        try
        {
            return new FileInfo(path).LinkTarget != null;
        }
        catch
        {
            return true;
        }
    }

    /// <summary>ContainsMarkdown の結果をキャッシュ経由で返す（トップレベル問い合わせ用）。</summary>
    private static bool ContainsMarkdownCached(string directory)
        => ContainsMarkdownCache.GetOrAdd(
            directory, d => ContainsMarkdown(d, ContainsScanMaxDepth));

    /// <summary>フォルダ配下（depth 上限まで）に Markdown が 1 つでも存在するか。最初の 1 件で打ち切る。</summary>
    private static bool ContainsMarkdown(string directory, int depthRemaining)
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
                if (ContainsMarkdown(sub, depthRemaining - 1))
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
