using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;

namespace Hirake;

/// <summary>
/// 開いているウィンドウを一元管理する。
///
/// セッション保存の責務を個々のウィンドウからここへ移しているのが要点。
/// ウィンドウが自分の分だけ <see cref="SettingsStore.SetSession"/> を呼ぶ形だと、
/// 閉じた順に上書きし合って最後に閉じた 1 枚しか残らない（ISSUE #42 穴 2）。
/// </summary>
public sealed class WindowManager
{
    /// <summary>同時に開けるウィンドウ数の上限。</summary>
    public const int MaxWindows = 10;

    // 「アプリ終了のために順番に閉じている」と「1 枚だけ閉じた」を区別するための猶予。
    // 閉じてからこの時間内に残り全部が閉じた場合は、終了操作とみなして
    // 閉じる前の構成をセッションとして残す（次回起動で同じ枚数が復元される）。
    private static readonly TimeSpan CloseBurstWindow = TimeSpan.FromSeconds(2);

    private static readonly Lazy<WindowManager> LazyInstance = new(() => new WindowManager());

    /// <summary>アプリ全体で共有する唯一のインスタンス。</summary>
    public static WindowManager Instance => LazyInstance.Value;

    // 生成順のウィンドウ一覧。
    private readonly List<MainWindow> _windows = new();

    // 最後にアクティブだったウィンドウ（URI・二重起動転送の既定の宛先）。
    private MainWindow? _lastActive;

    // 連続クローズ（＝アプリ終了操作）の途中で閉じたウィンドウの構成。
    // スナップショットを 1 枚目のクローズ時点で固定すると、その後に起きた
    // 「残ったウィンドウでのタブ操作」や「新しいウィンドウ」を取りこぼすため、
    // 閉じたウィンドウの分だけを貯め、確定時に「開いている分」と合成する。
    private readonly List<WindowDescriptor> _recentlyClosed = new();
    private DispatcherTimer? _sessionTimer;

    private bool _preferenceHooked;

    private WindowManager()
    {
    }

    /// <summary>開いているウィンドウ（生成順）。</summary>
    public IReadOnlyList<MainWindow> Windows => _windows.ToList();

    /// <summary>最後にアクティブだったウィンドウ。1 枚も無ければ null。</summary>
    public MainWindow? LastActive =>
        _lastActive != null && _windows.Contains(_lastActive) ? _lastActive : _windows.LastOrDefault();

    // ---- ウィンドウの生成・登録 ---------------------------------------

    /// <summary>
    /// 新しいウィンドウを作って表示する。上限に達している場合は null を返す。
    /// </summary>
    public MainWindow? CreateWindow(string? workspaceId = null)
    {
        if (_windows.Count >= MaxWindows)
        {
            return null;
        }

        var window = new MainWindow();
        Register(window);
        TryAttachWorkspace(window, workspaceId);
        window.Show();
        return window;
    }

    /// <summary>ウィンドウを管理下に置く（App が最初の 1 枚を作る場合にも使う）。</summary>
    public void Register(MainWindow window)
    {
        if (window == null || _windows.Contains(window))
        {
            return;
        }

        _windows.Add(window);
        _lastActive = window;

        // 新しいウィンドウを作った時点で「終了操作の途中」ではなくなる。
        // 貯めていた分を捨てないと、閉じたはずのウィンドウが次回起動で復活する。
        _recentlyClosed.Clear();
        _sessionTimer?.Stop();

        window.Activated += OnWindowActivated;
        window.Closed += OnWindowClosed;

        HookUserPreference();
    }

    private void OnWindowActivated(object? sender, EventArgs e)
    {
        if (sender is MainWindow window && _windows.Contains(window))
        {
            _lastActive = window;
        }
    }

    // ---- 検索 ---------------------------------------------------------

    /// <summary>指定 ID のワークスペースを開いているウィンドウ（無ければ null）。</summary>
    public MainWindow? FindByWorkspaceId(string? workspaceId)
    {
        if (string.IsNullOrEmpty(workspaceId))
        {
            return null;
        }

        return _windows.FirstOrDefault(
            w => string.Equals(w.WorkspaceId, workspaceId, StringComparison.Ordinal));
    }

    /// <summary>
    /// ワークスペースの所有権をウィンドウへ割り当てる。
    /// 「ストアに存在する」「他のウィンドウが所有していない」の両方を満たす場合だけ成功する。
    ///
    /// 所有権の割り当てはすべてこの入口を通すこと。ウィンドウが直接 WorkspaceId を
    /// 書き換えられると、セッション復元のように UI の排他チェックを経由しない経路から
    /// 重複所有が成立し、閉じた順に古い構成が新しい構成を上書きできてしまう。
    /// </summary>
    public bool TryAttachWorkspace(MainWindow window, string? workspaceId)
    {
        if (window == null)
        {
            return false;
        }

        if (string.IsNullOrEmpty(workspaceId))
        {
            window.SetWorkspaceId(null);
            return true;
        }

        if (WorkspaceStore.Instance.FindById(workspaceId) == null)
        {
            window.SetWorkspaceId(null);
            return false; // 削除済み・存在しない ID は所有させない。
        }

        var owner = FindByWorkspaceId(workspaceId);
        if (owner != null && !ReferenceEquals(owner, window))
        {
            window.SetWorkspaceId(null);
            return false;
        }

        window.SetWorkspaceId(workspaceId);
        return true;
    }

    /// <summary>
    /// 指定 ID のワークスペースの所有権を全ウィンドウから外す（削除時に使う）。
    /// 1 枚だけを対象にすると、万一重複所有が起きたときに削除済み ID が残る。
    /// </summary>
    public void DetachWorkspace(string? workspaceId)
    {
        if (string.IsNullOrEmpty(workspaceId))
        {
            return;
        }

        foreach (var window in _windows)
        {
            if (string.Equals(window.WorkspaceId, workspaceId, StringComparison.Ordinal))
            {
                window.SetWorkspaceId(null);
            }
        }
    }

    /// <summary>指定ファイルを開いているウィンドウ（無ければ null）。</summary>
    public MainWindow? FindByOpenFile(string fullPath)
    {
        if (string.IsNullOrEmpty(fullPath))
        {
            return null;
        }

        return _windows.FirstOrDefault(w => w.HasTabForFile(fullPath));
    }

    // ---- ワークスペース ------------------------------------------------

    /// <summary>
    /// ワークスペースをウィンドウとして開く。既に開いていればそのウィンドウを前面化する
    /// （二重には開かない）。上限に達していて開けない場合は null を返す。
    /// </summary>
    /// <param name="reusable">
    /// 起動直後の空ウィンドウなど、再利用してよいウィンドウ。タブが 1 つも無く
    /// ワークスペースも持たない場合だけ再利用する（<c>hirake://workspace</c> で
    /// 起動したときに空ウィンドウが 1 枚余るのを防ぐ）。
    /// </param>
    public MainWindow? OpenWorkspace(Workspace workspace, MainWindow? reusable = null)
    {
        if (workspace == null)
        {
            return null;
        }

        var existing = FindByWorkspaceId(workspace.Id);
        if (existing != null)
        {
            existing.BringToFront();
            return existing;
        }

        MainWindow? window;
        if (reusable != null && _windows.Contains(reusable) && reusable.IsReusableForWorkspace)
        {
            window = reusable;
            if (!TryAttachWorkspace(window, workspace.Id))
            {
                // 所有権を取れないウィンドウへ内容だけ復元しない
                // （削除済み・stale な Workspace を渡された場合など）。
                return FindByWorkspaceId(workspace.Id);
            }
        }
        else
        {
            window = CreateWindow(workspace.Id);
            if (window != null && !string.Equals(window.WorkspaceId, workspace.Id, StringComparison.Ordinal))
            {
                return FindByWorkspaceId(workspace.Id);
            }
        }

        if (window == null)
        {
            return null;
        }

        window.ApplyBounds(workspace.Window?.Bounds);
        window.SetWorkspaceScroll(workspace.Scroll);
        window.RestoreWindow(workspace.Window ?? new WindowDescriptor());
        return window;
    }

    // ---- テーマの伝播 --------------------------------------------------

    /// <summary>
    /// 全ウィンドウへ実効テーマを適用する（テーマはアプリ全体で 1 つ）。
    /// 1 枚の失敗で他のウィンドウを止めない。
    ///
    /// 失敗を呼び出し元へ返さないのは、次の 3 つが重なっているため。
    ///
    /// 1. 呼び出し元はいずれもイベントハンドラの境界（ボタン・キー入力・
    ///    WebView2 からの転送）で、例外を投げると見た目だけの操作で
    ///    スタックトレース入りの「予期しないエラー」ダイアログが出る。
    /// 2. このアプリには非モーダルな通知面もログ基盤も無く、出せるのが
    ///    モーダルダイアログしかない。
    /// 3. そもそも失敗を完全には観測できない。<see cref="DocumentTab.ApplyTheme"/> は
    ///    ExecuteScriptAsync を待たないため、ユーザーが実際に見る Markdown 本文側の
    ///    失敗はここへ届かない。捕まえられるのは WPF クローム側だけで、
    ///    「失敗を漏れなく扱う」という約束はこの構造では成立しない。
    ///
    /// テーマ適用の失敗をきちんと扱うには、適用経路を非同期化して WebView2 側の
    /// 結果まで集約する必要がある。それは本 ISSUE（伝播漏れの修正）の範囲を超える
    /// ため、別 ISSUE として切り出している。
    /// </summary>
    public void ApplyThemeToAll()
    {
        foreach (var window in _windows.ToList())
        {
            try
            {
                window.ApplyEffectiveThemeFromHost();
            }
            catch
            {
                // 1 枚の失敗で他を止めない。
            }
        }
    }

    /// <summary>
    /// OS のアプリテーマ変更の購読は App 側 1 か所で行う
    /// （ウィンドウごとに購読すると枚数分だけ購読が増え、静的イベントのため
    ///  解除漏れがそのままリークになる）。
    /// </summary>
    private void HookUserPreference()
    {
        if (_preferenceHooked)
        {
            return;
        }

        _preferenceHooked = true;
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category != UserPreferenceCategory.General)
        {
            return;
        }

        try
        {
            var dispatcher = Application.Current?.Dispatcher;
            dispatcher?.BeginInvoke(() =>
            {
                // コールバック全体を包む。ApplyThemeToAll は投げないが、設定の取得や
                // 将来の追加処理で漏れた例外がここから未処理例外になると、
                // ユーザーが Hirake に対して何もしていないタイミングでエラー
                // ダイアログが出てしまう。
                try
                {
                    if (string.Equals(SettingsStore.Instance.Theme, "auto", StringComparison.OrdinalIgnoreCase))
                    {
                        ApplyThemeToAll();
                    }
                }
                catch
                {
                    // OS 由来の追従処理が失敗しても、アプリの動作は止めない。
                }
            });
        }
        catch
        {
            // シャットダウン中などで BeginInvoke 自体が失敗することがある。
            // この呼び出しは SystemEvents のスレッドから来るため、投げ返さない。
        }
    }

    // ---- セッション保存 ------------------------------------------------

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        if (sender is not MainWindow window)
        {
            return;
        }

        window.Activated -= OnWindowActivated;
        window.Closed -= OnWindowClosed;

        // 閉じたウィンドウの「閉じた時点」の構成だけを貯める。
        // 全ウィンドウ分のスナップショットを 1 枚目のクローズ時点で固定すると、
        // その後に残ったウィンドウで起きたタブ操作を取りこぼす。
        try
        {
            _recentlyClosed.Add(window.CaptureWindow());
        }
        catch
        {
            // 取り出せなければその 1 枚だけ諦める。
        }

        _windows.Remove(window);
        if (ReferenceEquals(_lastActive, window))
        {
            _lastActive = _windows.LastOrDefault();
        }

        if (_windows.Count == 0)
        {
            // 最後の 1 枚。連続クローズで閉じた分をまとめてセッションに残す
            // （閉じた順に並ぶので、次回起動時も同じ順序で復元される）。
            _sessionTimer?.Stop();
            CommitSession(_recentlyClosed.ToList());
            _recentlyClosed.Clear();
            return;
        }

        // まだ開いているウィンドウがある。猶予内に残り全部が閉じれば終了操作と
        // みなすため、ここでは確定させずに遅延させる。
        ScheduleSessionCommit();
    }

    private void ScheduleSessionCommit()
    {
        _sessionTimer ??= new DispatcherTimer { Interval = CloseBurstWindow };
        _sessionTimer.Tick -= OnSessionTimerTick;
        _sessionTimer.Tick += OnSessionTimerTick;
        _sessionTimer.Stop();
        _sessionTimer.Start();
    }

    private void OnSessionTimerTick(object? sender, EventArgs e)
    {
        _sessionTimer?.Stop();

        // 猶予内に残りが閉じなかった＝ユーザーが 1 枚だけ閉じた操作。
        // 閉じたウィンドウは復元対象から外し、現在開いている分だけを残す。
        _recentlyClosed.Clear();
        CommitSession(CaptureAll());
    }

    /// <summary>アプリ終了時に、開いているウィンドウの構成を確実に書き出す。</summary>
    public void SaveSessionNow()
    {
        _sessionTimer?.Stop();

        // 最後のウィンドウが閉じた時点で確定済みの場合、ここで CaptureAll() を書くと
        // 「ウィンドウ 0 枚」で上書きしてしまい、直前に保存した構成が消える。
        if (_recentlyClosed.Count == 0 && _windows.Count == 0)
        {
            return;
        }

        // 閉じた分と開いている分を合成する（終了処理の途中で呼ばれても取りこぼさない）。
        var session = new List<WindowDescriptor>(_recentlyClosed);
        session.AddRange(CaptureAll());
        _recentlyClosed.Clear();
        CommitSession(session);
    }

    /// <summary>
    /// アプリ終了時の後片付け。静的イベントの購読とタイマーを解除する
    /// （SystemEvents.UserPreferenceChanged は静的なので対称に外す）。
    /// </summary>
    public void Shutdown()
    {
        if (_sessionTimer != null)
        {
            _sessionTimer.Stop();
            _sessionTimer.Tick -= OnSessionTimerTick;
            _sessionTimer = null;
        }

        if (_preferenceHooked)
        {
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
            _preferenceHooked = false;
        }
    }

    // ---- ズームの伝播 --------------------------------------------------

    /// <summary>
    /// ズーム倍率を全ウィンドウのタブへ反映する。倍率はアプリ全体で 1 つという
    /// 既存の意味を保つため、変更したウィンドウの中だけで閉じてはいけない。
    /// </summary>
    public void ApplyZoomToAll(DocumentTab source, double zoomFactor)
    {
        foreach (var window in _windows.ToList())
        {
            try
            {
                window.ApplyZoomFromHost(source, zoomFactor);
            }
            catch
            {
                // 1 枚の失敗で他を止めない。
            }
        }
    }

    private List<WindowDescriptor> CaptureAll()
    {
        var result = new List<WindowDescriptor>();
        foreach (var window in _windows)
        {
            try
            {
                result.Add(window.CaptureWindow());
            }
            catch
            {
                // 1 枚の失敗で他を落とさない。
            }
        }
        return result;
    }

    private static void CommitSession(IEnumerable<WindowDescriptor>? windows)
    {
        try
        {
            SettingsStore.Instance.SetSession(windows ?? Enumerable.Empty<WindowDescriptor>());
        }
        catch
        {
            // セッション保存失敗は握りつぶす。
        }
    }

    // ---- 復元 ---------------------------------------------------------

    /// <summary>
    /// 前回セッションのウィンドウ構成を復元する。1 枚も復元できなければ
    /// 空のウィンドウを 1 枚だけ開く。
    /// </summary>
    public void RestoreSession(MainWindow firstWindow)
    {
        var session = SettingsStore.Instance.SessionWindows;
        if (session.Count == 0)
        {
            return;
        }

        // 同じワークスペースを指す記述子が複数あっても、所有できるのは 1 枚だけ。
        // 「所有権は取れないが内容だけ復元される」ウィンドウを作らないよう、
        // ウィンドウを用意する前に重複を落とす。
        // 削除済み ID は「無名ウィンドウとして復元」に落とす（内容は残す）。
        foreach (var descriptor in session)
        {
            if (!string.IsNullOrEmpty(descriptor.WorkspaceId)
                && WorkspaceStore.Instance.FindById(descriptor.WorkspaceId) == null)
            {
                descriptor.WorkspaceId = null;
            }
        }

        // 同じ ID が複数あるときは、実際に復元できるタブが最も多い記述子を残す
        // （配列の先頭というだけで、中身が空の記述子に負けないように）。
        // 同数なら元の順序を優先して互換性を保つ。
        var duplicateWinners = session
            .Where(d => !string.IsNullOrEmpty(d.WorkspaceId))
            .GroupBy(d => d.WorkspaceId!, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => g.OrderByDescending(d => d.Tabs?.Count(t => t?.CanRestore() == true) ?? 0).First(),
                StringComparer.Ordinal);

        var restorable = new List<WindowDescriptor>();
        foreach (var descriptor in session)
        {
            string? id = descriptor.WorkspaceId;
            if (!string.IsNullOrEmpty(id)
                && !ReferenceEquals(duplicateWinners[id], descriptor))
            {
                continue; // 同じ ID の代表ではない記述子は復元しない。
            }

            restorable.Add(descriptor);
        }

        if (restorable.Count == 0)
        {
            return;
        }

        // 1 枚目は既に作られているので、それを使い回す。
        RestoreInto(firstWindow, restorable[0]);

        for (int i = 1; i < restorable.Count && _windows.Count < MaxWindows; i++)
        {
            var window = CreateWindow();
            if (window == null)
            {
                break;
            }
            RestoreInto(window, restorable[i]);
        }
    }

    private void RestoreInto(MainWindow window, WindowDescriptor descriptor)
    {
        try
        {
            // 重複は呼び出し側で落としているため、ここで失敗するのは想定外。
            // 失敗した場合は無名のウィンドウとして復元する（タブは開く）。
            bool attached = TryAttachWorkspace(window, descriptor.WorkspaceId);
            window.ApplyBounds(descriptor.Bounds);

            var workspace = attached ? WorkspaceStore.Instance.FindById(window.WorkspaceId) : null;
            if (workspace != null)
            {
                window.SetWorkspaceScroll(workspace.Scroll);
            }

            window.RestoreWindow(descriptor);
        }
        catch
        {
            // 1 枚の復元失敗で他を止めない。
        }
    }
}
