using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace Hirake;

/// <summary>名前付きワークスペース 1 件。</summary>
public sealed class Workspace
{
    /// <summary>内部 ID。名前を変更しても参照が切れないよう名前とは分離する。</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>表示名（ユーザー入力）。</summary>
    public string Name { get; set; } = string.Empty;

    public DateTime UpdatedUtc { get; set; }

    /// <summary>ウィンドウ 1 枚分の内容。</summary>
    public WindowDescriptor Window { get; set; } = new();

    /// <summary>
    /// このワークスペースでの文書ごとのスクロール位置（キー = フルパスの小文字）。
    /// 復元時はこちらを優先し、無ければ SettingsStore のグローバル値へフォールバックする。
    /// </summary>
    public Dictionary<string, double> Scroll { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>workspaces.json のルート。</summary>
public sealed class WorkspaceData
{
    public int Version { get; set; } = 1;
    public List<Workspace> Workspaces { get; set; } = new();
}

/// <summary>
/// ワークスペースを <c>%LocalAppData%\Hirake\workspaces.json</c> へ永続化する。
/// スレッドセーフ（lock 保護）。壊れた JSON は空リストで復旧してアプリを止めない。
///
/// SettingsStore と違い、書き込みは一時ファイル + <see cref="File.Replace(string,string,string)"/> の
/// 原子的置換で行う。設定は失ってもスクロール位置 1 件で済むが、ワークスペースは
/// 手作業で積み上げたタブ構成が丸ごと消えるため。
/// </summary>
public sealed class WorkspaceStore
{
    /// <summary>保持できるワークスペース数の上限。</summary>
    public const int MaxWorkspaces = 50;

    /// <summary>1 ワークスペースに保存できるタブ数の上限。</summary>
    public const int MaxTabsPerWorkspace = 200;

    /// <summary>名前の長さの上限（文字数）。</summary>
    public const int MaxNameLength = 60;

    /// <summary>1 ワークスペースが保持するスクロール位置の件数上限。</summary>
    public const int MaxScrollEntries = MaxTabsPerWorkspace;

    /// <summary>読み込むファイルサイズの上限（壊れた・肥大化したファイルを丸ごと読まない）。</summary>
    private const long MaxFileBytes = 8L * 1024 * 1024;

    private static readonly Lazy<WorkspaceStore> LazyInstance = new(() => new WorkspaceStore());

    /// <summary>アプリ全体で共有する唯一のインスタンス。</summary>
    public static WorkspaceStore Instance => LazyInstance.Value;

    private readonly object _lock = new();
    private readonly string _filePath;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
    };

    private WorkspaceData _data;

    // 現行ファイルが壊れていて .bak から復旧した状態か。
    // true の間は、壊れた現行ファイルを .bak へ回さない。
    private bool _primaryWasBroken;

    private string BackupPath => _filePath + ".bak";

    private WorkspaceStore()
    {
        string directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Hirake");
        _filePath = Path.Combine(directory, "workspaces.json");
        _data = Load();
    }

    // ---- 参照 ---------------------------------------------------------

    /// <summary>更新が新しい順にワークスペースを返す（防御的コピー）。</summary>
    public IReadOnlyList<Workspace> GetAll()
    {
        lock (_lock)
        {
            return _data.Workspaces
                .OrderByDescending(w => w.UpdatedUtc)
                .ToList();
        }
    }

    /// <summary>ID で 1 件取得する（無ければ null）。</summary>
    public Workspace? FindById(string? id)
    {
        if (string.IsNullOrEmpty(id))
        {
            return null;
        }

        lock (_lock)
        {
            return _data.Workspaces.FirstOrDefault(
                w => string.Equals(w.Id, id, StringComparison.Ordinal));
        }
    }

    /// <summary>
    /// 名前で 1 件取得する（無ければ null）。前後の空白を除いた大文字小文字を無視する
    /// 完全一致で照合する（<c>hirake://workspace?name=…</c> の照合規則と同じ）。
    /// </summary>
    public Workspace? FindByName(string? name)
    {
        string normalized = NormalizeName(name);
        if (normalized.Length == 0)
        {
            return null;
        }

        lock (_lock)
        {
            return _data.Workspaces.FirstOrDefault(
                w => string.Equals(NormalizeName(w.Name), normalized, StringComparison.OrdinalIgnoreCase));
        }
    }

    // ---- 更新 ---------------------------------------------------------

    /// <summary>
    /// 名前を付けて新規保存する。同名が既にあればその内容を置き換える。
    /// 上限に達していて新規作成できない場合は null を返す。
    /// </summary>
    public Workspace? Save(string name, WindowDescriptor window, IReadOnlyDictionary<string, double> scroll)
    {
        string normalized = NormalizeName(name);
        if (normalized.Length == 0)
        {
            return null;
        }

        lock (_lock)
        {
            var existing = _data.Workspaces.FirstOrDefault(
                w => string.Equals(NormalizeName(w.Name), normalized, StringComparison.OrdinalIgnoreCase));

            if (existing == null)
            {
                if (_data.Workspaces.Count >= MaxWorkspaces)
                {
                    return null;
                }

                existing = new Workspace { Id = Guid.NewGuid().ToString("N") };
                _data.Workspaces.Add(existing);
            }

            existing.Name = normalized;
            existing.Window = TrimWindow(window);
            existing.Scroll = BuildScroll(scroll);
            existing.UpdatedUtc = DateTime.UtcNow;

            SaveLocked();
            return Clone(existing);
        }
    }

    /// <summary>
    /// 既存ワークスペースの内容を書き戻す（ウィンドウを閉じたときの自動保存）。
    /// ID が見つからなければ何もしない。
    /// </summary>
    public void Update(string id, WindowDescriptor window, IReadOnlyDictionary<string, double> scroll)
    {
        if (string.IsNullOrEmpty(id))
        {
            return;
        }

        lock (_lock)
        {
            var target = _data.Workspaces.FirstOrDefault(
                w => string.Equals(w.Id, id, StringComparison.Ordinal));
            if (target == null)
            {
                return;
            }

            target.Window = TrimWindow(window);
            target.Scroll = BuildScroll(scroll);
            target.UpdatedUtc = DateTime.UtcNow;

            SaveLocked();
        }
    }

    /// <summary>名前を変更する。成功したら true。</summary>
    public bool Rename(string id, string newName)
    {
        string normalized = NormalizeName(newName);
        if (string.IsNullOrEmpty(id) || normalized.Length == 0)
        {
            return false;
        }

        lock (_lock)
        {
            var target = _data.Workspaces.FirstOrDefault(
                w => string.Equals(w.Id, id, StringComparison.Ordinal));
            if (target == null)
            {
                return false;
            }

            // 他の項目と名前が衝突する場合は拒否する（名前で照合するため）。
            bool conflict = _data.Workspaces.Any(
                w => !string.Equals(w.Id, id, StringComparison.Ordinal) &&
                     string.Equals(NormalizeName(w.Name), normalized, StringComparison.OrdinalIgnoreCase));
            if (conflict)
            {
                return false;
            }

            target.Name = normalized;
            target.UpdatedUtc = DateTime.UtcNow;
            SaveLocked();
            return true;
        }
    }

    /// <summary>削除する。存在すれば true。</summary>
    public bool Delete(string id)
    {
        if (string.IsNullOrEmpty(id))
        {
            return false;
        }

        lock (_lock)
        {
            int removed = _data.Workspaces.RemoveAll(
                w => string.Equals(w.Id, id, StringComparison.Ordinal));
            if (removed == 0)
            {
                return false;
            }

            SaveLocked();
            return true;
        }
    }

    // ---- ヘルパー -----------------------------------------------------

    /// <summary>前後の空白を除き、上限文字数で切り詰める。</summary>
    public static string NormalizeName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        string trimmed = name.Trim();

        // 制御文字を含む名前は受け付けない（表示崩れと不正入力の防止）。
        if (trimmed.Any(char.IsControl))
        {
            return string.Empty;
        }

        return trimmed.Length > MaxNameLength ? trimmed[..MaxNameLength] : trimmed;
    }

    private static WindowDescriptor TrimWindow(WindowDescriptor window)
    {
        var tabs = (window.Tabs ?? new List<TabDescriptor>())
            .Where(t => t != null && TabKinds.IsKnown(t.Kind) && !string.IsNullOrWhiteSpace(t.Path))
            .Take(MaxTabsPerWorkspace)
            .ToList();

        int activeIndex = window.ActiveIndex;
        if (activeIndex < 0 || activeIndex >= tabs.Count)
        {
            activeIndex = tabs.Count > 0 ? 0 : -1;
        }

        return new WindowDescriptor
        {
            WorkspaceId = window.WorkspaceId,
            Tabs = tabs,
            ActiveIndex = activeIndex,
            Bounds = window.Bounds is { IsValid: true } bounds ? bounds : null,
        };
    }

    private static Dictionary<string, double> BuildScroll(IReadOnlyDictionary<string, double>? scroll)
    {
        var result = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        if (scroll == null)
        {
            return result;
        }

        foreach (var pair in scroll)
        {
            if (result.Count >= MaxScrollEntries)
            {
                break;
            }

            // パスとして異常に長いキーは受け付けない（肥大化した JSON への保険）。
            if (!string.IsNullOrEmpty(pair.Key) && pair.Key.Length <= 4096 && double.IsFinite(pair.Value))
            {
                result[pair.Key] = pair.Value;
            }
        }
        return result;
    }

    /// <summary>
    /// ウィンドウ記述子を上限内へ正規化する。セッション（settings.json）側からも
    /// 同じ関数を使い、「ワークスペースには上限があるがセッションには無い」という
    /// 非対称を作らない。
    /// </summary>
    public static WindowDescriptor NormalizeWindow(WindowDescriptor? window) =>
        TrimWindow(window ?? new WindowDescriptor());

    private static Workspace Clone(Workspace source) => new()
    {
        Id = source.Id,
        Name = source.Name,
        UpdatedUtc = source.UpdatedUtc,
        Window = source.Window,
        Scroll = new Dictionary<string, double>(source.Scroll, StringComparer.OrdinalIgnoreCase),
    };

    // ---- 保存・読み込み -----------------------------------------------

    /// <summary>_lock 保持前提。一時ファイルへ書いてから原子的に置き換える。</summary>
    private void SaveLocked()
    {
        try
        {
            string? directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string json = JsonSerializer.Serialize(_data, _jsonOptions);
            string tempPath = _filePath + ".tmp";
            File.WriteAllText(tempPath, json, new UTF8Encoding(false));

            if (!File.Exists(_filePath))
            {
                File.Move(tempPath, _filePath);
                return;
            }

            if (_primaryWasBroken)
            {
                // 壊れた現行ファイルを .bak へ回すと、正常だった .bak を潰してしまう。
                // 復旧の最後の砦なので、この場合だけは .bak を更新しない。
                File.Move(tempPath, _filePath, overwrite: true);
                _primaryWasBroken = false;
                return;
            }

            // 置換前の内容は .bak として残す（置換途中で落ちても復旧できるように）。
            File.Replace(tempPath, _filePath, BackupPath, ignoreMetadataErrors: true);
        }
        catch
        {
            // 保存失敗は握りつぶす（ビューアを止めない）。
        }
    }

    private WorkspaceData Load()
    {
        var data = TryLoadFile(_filePath);
        if (data != null)
        {
            return data;
        }

        // 現行ファイルが壊れていたら .bak から復旧する。
        // .bak を作りっぱなしで読まないと、バックアップは失敗を先送りするだけになる。
        _primaryWasBroken = File.Exists(_filePath);

        var backup = TryLoadFile(BackupPath);
        if (backup != null)
        {
            return backup;
        }

        return new WorkspaceData();
    }

    private WorkspaceData? TryLoadFile(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            // 壊れた（あるいは意図的に肥大化させられた）ファイルを丸ごと読まない。
            var info = new FileInfo(path);
            if (info.Length > MaxFileBytes)
            {
                return null;
            }

            string json = File.ReadAllText(path, Encoding.UTF8);
            var data = JsonSerializer.Deserialize<WorkspaceData>(json, _jsonOptions);
            return data == null ? null : Normalize(data);
        }
        catch
        {
            return null;
        }
    }

    private static WorkspaceData Normalize(WorkspaceData data)
    {
        data.Workspaces ??= new List<Workspace>();

        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        var normalized = new List<Workspace>();

        foreach (var workspace in data.Workspaces)
        {
            if (workspace == null)
            {
                continue;
            }

            // ID が欠けている・重複している項目は復旧のため採番し直す。
            if (string.IsNullOrEmpty(workspace.Id) || !seenIds.Add(workspace.Id))
            {
                workspace.Id = Guid.NewGuid().ToString("N");
                seenIds.Add(workspace.Id);
            }

            workspace.Name = NormalizeName(workspace.Name);
            if (workspace.Name.Length == 0)
            {
                continue; // 名前を失った項目は開けないため捨てる。
            }

            workspace.Window = TrimWindow(workspace.Window ?? new WindowDescriptor());
            workspace.Scroll = BuildScroll(workspace.Scroll);
            normalized.Add(workspace);

            if (normalized.Count >= MaxWorkspaces)
            {
                break;
            }
        }

        data.Workspaces = normalized;
        return data;
    }
}
