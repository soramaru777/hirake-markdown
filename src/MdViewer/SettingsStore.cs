using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace MdViewer;

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
    public List<string> SessionTabs { get; set; } = new();
    public string? SessionActiveTab { get; set; }
    public Dictionary<string, FileState> FileStates { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// アプリ全体の設定を <c>%LocalAppData%\MdViewer\settings.json</c> へ永続化する。
/// スレッドセーフ（lock 保護）で、変更後 1 秒デバウンスで自動保存する。
/// 壊れた JSON は既定値で復旧する。全体で 1 インスタンス（<see cref="Instance"/>）を共有する。
/// </summary>
public sealed class SettingsStore
{
    private const int MaxFileStates = 500;
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
        string directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MdViewer");
        _filePath = Path.Combine(directory, "settings.json");
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

    /// <summary>前回セッションで開いていたタブのパス一覧。</summary>
    public IReadOnlyList<string> SessionTabs
    {
        get { lock (_lock) { return _data.SessionTabs.ToList(); } }
    }

    /// <summary>前回セッションでアクティブだったタブのパス。</summary>
    public string? SessionActiveTab
    {
        get { lock (_lock) { return _data.SessionActiveTab; } }
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

    /// <summary>セッション情報（開いているタブとアクティブタブ）を保存する。</summary>
    public void SetSession(IEnumerable<string> tabs, string? activeTab)
    {
        lock (_lock)
        {
            _data.SessionTabs = tabs.ToList();
            _data.SessionActiveTab = activeTab;
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
        data.SessionTabs ??= new();

        // FileStates は大文字小文字を無視するコンパレータで作り直す。
        var source = data.FileStates ?? new Dictionary<string, FileState>();
        var rebuilt = new Dictionary<string, FileState>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in source)
        {
            rebuilt[pair.Key] = pair.Value ?? new FileState();
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
