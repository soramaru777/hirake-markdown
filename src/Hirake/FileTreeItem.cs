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
            if (ext.Equals(candidate, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>隠しフォルダ・ドット始まり・node_modules をスキップ対象とする。</summary>
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
            var attr = File.GetAttributes(path);
            if ((attr & FileAttributes.Hidden) == FileAttributes.Hidden)
            {
                return true;
            }
        }
        catch
        {
            // 属性取得失敗は無視する。
        }
        return false;
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
