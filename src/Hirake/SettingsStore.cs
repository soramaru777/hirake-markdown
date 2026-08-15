using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace Hirake;

/// <summary>1 ファイル分の表示状態（スクロール位置・最終オープン日時）。</summary>
public sealed class FileState
{
    public double ScrollY { get; set; }
    public DateTime LastOpenedUtc { get; set; }
}

/// <summary>settings.json に永続化する設定データ本体。</summary>
public sealed class SettingsData
{
    public string Theme { get; set; } = "auto";
    public double ZoomFactor { get; set; } = 1.0;
    public bool SidebarVisible { get; set; }

    /// <summary>
    /// symlink / ジャンクションで束ねたフォルダを走査するか。既定は false（辿らない）。
    /// true にしても、UNC・ネットワークドライブを指すリンクは辿らない（ISSUE #54）。
    /// </summary>
    public bool FollowDirectoryLinks { get; set; }

    /// <summary>
    /// 前回セッションのウィンドウ構成（1 要素 = ウィンドウ 1 枚）。
    /// ワークスペースと同じ記述子を使い、復元経路を 1 本にまとめている。
    /// </summary>
    public List<WindowDescriptor> SessionWindows { get; set; } = new();

    /// <summary>
    /// 旧形式（1 ウィンドウ分のパス配列）。読み込み時に <see cref="SessionWindows"/> へ
    /// 移行し、以後は書き出さない。次の版で削除する。
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? SessionTabs { get; set; }

    /// <summary>旧形式のアクティブタブ。<see cref="SessionTabs"/> と同じく移行用。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SessionActiveTab { get; set; }

    public Dictionary<string, FileState> FileStates { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// アプリ全体の設定を <c>%LocalAppData%\Hirake\settings.json</c> へ永続化する。
/// スレッドセーフ（lock 保護）で、変更後 1 秒デバウンスで自動保存する。
/// 壊れた JSON は既定値で復旧する。全体で 1 インスタンス（<see cref="Instance"/>）を共有する。
/// </summary>
public sealed class SettingsStore
{
    private const int MaxFileStates = 500;
    private const int MaxFileStateKeyLength = 4096;
    private const int SaveDebounceMs = 1000;

    private static readonly Lazy<SettingsStore> LazyInstance = new(() => new SettingsStore());

    /// <summary>アプリ全体で共有する唯一のインスタンス。</summary>
    public static SettingsStore Instance => LazyInstance.Value;

    private readonly object _lock = new();
    private readonly string _filePath;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
    };

    private SettingsData _data;
    private System.Threading.Timer? _debounceTimer;

    private SettingsStore()
    {
        _filePath = AppPaths.SettingsFile;
        _data = Load();
    }

    // ---- 公開プロパティ -----------------------------------------------

    /// <summary>テーマ設定（"auto" / "light" / "dark"）。</summary>
    public string Theme
    {
        get { lock (_lock) { return _data.Theme; } }
        set
        {
            lock (_lock) { _data.Theme = string.IsNullOrEmpty(value) ? "auto" : value; }
            ScheduleSave();
        }
    }

    /// <summary>ズーム倍率。</summary>
    public double ZoomFactor
    {
        get { lock (_lock) { return _data.ZoomFactor; } }
        set
        {
            lock (_lock) { _data.ZoomFactor = value > 0 ? value : 1.0; }
            ScheduleSave();
        }
    }

    /// <summary>ファイルツリー サイドバーの表示状態。</summary>
    public bool SidebarVisible
    {
        get { lock (_lock) { return _data.SidebarVisible; } }
        set
        {
            lock (_lock) { _data.SidebarVisible = value; }
            ScheduleSave();
        }
    }

    /// <summary>
    /// symlink / ジャンクションで束ねたフォルダを走査するか（ISSUE #54）。
    /// 設定 UI は無く、settings.json を直接編集して切り替える。
    /// </summary>
    public bool FollowDirectoryLinks
    {
        get { lock (_lock) { return _data.FollowDirectoryLinks; } }
        set
        {
            lock (_lock) { _data.FollowDirectoryLinks = value; }
            ScheduleSave();
        }
    }

    /// <summary>前回セッションのウィンドウ構成（1 要素 = ウィンドウ 1 枚）。</summary>
    public IReadOnlyList<WindowDescriptor> SessionWindows
    {
        get { lock (_lock) { return _data.SessionWindows.ToList(); } }
    }

    // ---- スクロール位置 -----------------------------------------------

    /// <summary>指定ファイルの保存済みスクロール位置を返す（無ければ null）。</summary>
    public double? GetScrollPosition(string fullPath)
    {
        string key = ToKey(fullPath);
        lock (_lock)
        {
            if (_data.FileStates.TryGetValue(key, out var state))
            {
                state.LastOpenedUtc = DateTime.UtcNow;
                return state.ScrollY;
            }
            return null;
        }
    }

    /// <summary>指定ファイルのスクロール位置を保存する（LRU 上限 500 件）。</summary>
    public void UpdateScrollPosition(string fullPath, double scrollY)
    {
        string key = ToKey(fullPath);
        lock (_lock)
        {
            if (!_data.FileStates.TryGetValue(key, out var state))
            {
                state = new FileState();
                _data.FileStates[key] = state;
            }
            state.ScrollY = scrollY;
            state.LastOpenedUtc = DateTime.UtcNow;
            TrimFileStates();
        }
        ScheduleSave();
    }

    // ---- セッション ---------------------------------------------------

    /// <summary>
    /// セッション情報（開いているウィンドウ構成）を保存する。
    /// ウィンドウ単位ではなく全ウィンドウ分をまとめて渡すこと。個々のウィンドウが
    /// 自分の分だけ書き込むと、閉じた順に上書きし合って他のウィンドウが失われる。
    /// </summary>
    public void SetSession(IEnumerable<WindowDescriptor> windows)
    {
        lock (_lock)
        {
            _data.SessionWindows = windows.Where(w => w != null).ToList();

            // 旧形式は移行済みとして落とす（次回以降は書き出さない）。
            _data.SessionTabs = null;
            _data.SessionActiveTab = null;
        }
        ScheduleSave();
    }

    // ---- 保存 ---------------------------------------------------------

    /// <summary>デバウンスを待たず即時に保存する。</summary>
    public void SaveNow()
    {
        lock (_lock)
        {
            try
            {
                string? directory = Path.GetDirectoryName(_filePath);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }
                string json = JsonSerializer.Serialize(_data, _jsonOptions);
                File.WriteAllText(_filePath, json, new UTF8Encoding(false));
            }
            catch
            {
                // 保存失敗は握りつぶす（ビューアを止めない）。
            }
        }
    }

    private void ScheduleSave()
    {
        lock (_lock)
        {
            _debounceTimer ??= new System.Threading.Timer(_ => SaveNow());
            _debounceTimer.Change(SaveDebounceMs, Timeout.Infinite);
        }
    }

    // ---- 読み込み -----------------------------------------------------

    private SettingsData Load()
    {
        try
        {
            if (File.Exists(_filePath))
            {
                string json = File.ReadAllText(_filePath, Encoding.UTF8);
                var data = JsonSerializer.Deserialize<SettingsData>(json, _jsonOptions);
                if (data != null)
                {
                    return Normalize(data);
                }
            }
        }
        catch
        {
            // 壊れた JSON は既定値で復旧する。
        }
        return new SettingsData();
    }

    private static SettingsData Normalize(SettingsData data)
    {
        data.Theme = string.IsNullOrEmpty(data.Theme) ? "auto" : data.Theme;
        data.ZoomFactor = data.ZoomFactor > 0 ? data.ZoomFactor : 1.0;
        data.SessionWindows ??= new();

        // ウィンドウ数とタブ数はワークスペースと同じ上限で正規化する
        // （settings.json を書き換えて起動時に大量のタブを開かせられないように）。
        data.SessionWindows = data.SessionWindows
            .Where(w => w != null)
            .Take(WindowManager.MaxWindows)
            // ここは SettingsStore の初期化中。Instance を引くと Lazy の再帰取得で
            // 例外になるため、読み込んだ設定値をそのまま渡す（ISSUE #60）。
            .Select(w => WorkspaceStore.NormalizeWindow(w, data.FollowDirectoryLinks))
            .ToList();

        // 旧形式（パスの配列）からの移行。1 枚のウィンドウの document タブ列とみなす。
        // 仮想タブの FilePath にはフォルダのパスが入っていたが、種別が分からないため捨てる。
        if (data.SessionWindows.Count == 0 && data.SessionTabs is { Count: > 0 })
        {
            var tabs = data.SessionTabs
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Take(WorkspaceStore.MaxTabsPerWorkspace)
                // ここは NormalizeWindow を通らない経路なので、実体への正規化を
                // 個別に行う（ISSUE #60）。
                .Select(p => new TabDescriptor
                {
                    Kind = TabKinds.Document,
                    Path = TabDescriptor.NormalizePath(
                        p, TabKinds.Document, data.FollowDirectoryLinks),
                })
                .ToList();

            int activeIndex = 0;
            if (!string.IsNullOrEmpty(data.SessionActiveTab))
            {
                // タブ側を実体へ直したので、突き合わせる側も同じ形にする。
                // 揃えないと一致せず、アクティブタブが先頭へ戻る。
                string activePath = TabDescriptor.NormalizePath(
                    data.SessionActiveTab, TabKinds.Document, data.FollowDirectoryLinks);

                int found = tabs.FindIndex(
                    t => string.Equals(t.Path, activePath, StringComparison.OrdinalIgnoreCase));
                if (found >= 0)
                {
                    activeIndex = found;
                }
            }

            data.SessionWindows.Add(new WindowDescriptor
            {
                Tabs = tabs,
                ActiveIndex = activeIndex,
            });
        }

        data.SessionTabs = null;
        data.SessionActiveTab = null;

        // FileStates は大文字小文字を無視するコンパレータで作り直す。
        // 件数・キー長・値の妥当性も読み込み時に検査する。更新時のトリムだけだと、
        // 書き換えられた settings.json から大量・巨大なキーをそのまま抱え込める。
        var source = data.FileStates ?? new Dictionary<string, FileState>();
        var rebuilt = new Dictionary<string, FileState>(StringComparer.OrdinalIgnoreCase);

        // 元 JSON は大文字小文字を区別するため、同じファイルを指すキーが複数入り得る。
        // 先に「キーごとに最新の 1 件」へ畳んでから件数を絞らないと、重複だけで
        // 上限枠を食い潰したり、古い状態が新しい状態を上書きしたりする。
        foreach (var group in source
                     .Where(p => !string.IsNullOrEmpty(p.Key) && p.Key.Length <= MaxFileStateKeyLength)
                     .GroupBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
                     .Select(g => g.OrderByDescending(p => p.Value?.LastOpenedUtc ?? DateTime.MinValue).First())
                     .OrderByDescending(p => p.Value?.LastOpenedUtc ?? DateTime.MinValue)
                     .Take(MaxFileStates))
        {
            var state = group.Value ?? new FileState();
            if (!double.IsFinite(state.ScrollY))
            {
                state.ScrollY = 0;
            }
            rebuilt[group.Key] = state;
        }

        data.FileStates = rebuilt;
        return data;
    }

    // ---- ヘルパー -----------------------------------------------------

    private static string ToKey(string fullPath)
    {
        try
        {
            return Path.GetFullPath(fullPath).ToLowerInvariant();
        }
        catch
        {
            return fullPath.ToLowerInvariant();
        }
    }

    /// <summary>_lock 保持前提。件数超過分を最終オープンが古い順に削除する。</summary>
    private void TrimFileStates()
    {
        if (_data.FileStates.Count <= MaxFileStates)
        {
            return;
        }

        var toRemove = _data.FileStates
            .OrderBy(kv => kv.Value.LastOpenedUtc)
            .Take(_data.FileStates.Count - MaxFileStates)
            .Select(kv => kv.Key)
            .ToList();

        foreach (var key in toRemove)
        {
            _data.FileStates.Remove(key);
        }
    }

    // ---- テーマ判定 ---------------------------------------------------

    /// <summary>
    /// テーマ設定から実効テーマ（"light" / "dark"）を求める。
    /// "auto" の場合は Windows のアプリテーマ設定（レジストリ）を参照する。
    /// </summary>
    public static string GetEffectiveTheme(string themeSetting)
    {
        if (string.Equals(themeSetting, "light", StringComparison.OrdinalIgnoreCase))
        {
            return "light";
        }
        if (string.Equals(themeSetting, "dark", StringComparison.OrdinalIgnoreCase))
        {
            return "dark";
        }

        // "auto"（またはそれ以外）は OS のアプリテーマ設定に従う。
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key?.GetValue("AppsUseLightTheme") is int appsUseLightTheme)
            {
                return appsUseLightTheme == 0 ? "dark" : "light";
            }
        }
        catch
        {
            // レジストリ参照失敗時は light にフォールバックする。
        }
        return "light";
    }
}
