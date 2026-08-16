using System.IO;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;

namespace Hirake;

public partial class App : Application
{
    private SingleInstanceManager? _instanceManager;
    private MainWindow? _mainWindow;

    private static Task<CoreWebView2Environment>? _environmentTask;
    private static readonly object EnvLock = new();

    /// <summary>アプリ全体で 1 つだけ生成・共有する WebView2 環境。</summary>
    public static Task<CoreWebView2Environment> GetEnvironmentAsync()
    {
        lock (EnvLock)
        {
            _environmentTask ??= CreateEnvironmentAsync();
            return _environmentTask;
        }
    }

    private static Task<CoreWebView2Environment> CreateEnvironmentAsync()
    {
        string userDataFolder = AppPaths.WebView2Dir;
        Directory.CreateDirectory(userDataFolder);
        return CoreWebView2Environment.CreateAsync(null, userDataFolder, null);
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;

        var paths = NormalizeArgs(e.Args);

        _instanceManager = new SingleInstanceManager();

        if (!_instanceManager.IsFirstInstance)
        {
            // 2 番目以降: 既存プロセスへパスを送信して終了する。
            SingleInstanceManager.SendToExistingInstance(paths);
            _instanceManager.Dispose();
            Shutdown();
            return;
        }

        _mainWindow = new MainWindow();
        MainWindow = _mainWindow;

        WindowManager.Instance.Register(_mainWindow);

        _instanceManager.PathsReceived = OnPathsReceivedFromOtherInstance;
        _instanceManager.StartServer();

        _mainWindow.Show();

        if (paths.Count > 0)
        {
            // 引数がある場合は前回セッションを復元せず、指定ファイルのみ開く（既存動作）。
            // 起動直後の空ウィンドウは、workspace URI の復元先として再利用してよい。
            _ = OpenAllAsync(paths, _mainWindow);
        }
        else
        {
            // 引数なし起動時のみ前回セッションを復元する（ウィンドウの枚数ごと）。
            WindowManager.Instance.RestoreSession(_mainWindow);
        }
    }

    private void OnPathsReceivedFromOtherInstance(IReadOnlyList<string> paths)
    {
        Dispatcher.BeginInvoke(() =>
        {
            // 送信側（別プロセス）を信用せず、受信側でも必ず再検証する。
            _ = OpenAllAsync(paths);
        });
    }

    /// <summary>
    /// 入力（ファイルパス または hirake:// URI）を順に開き、1 件でも受理したら
    /// 対象ウィンドウを前面化する。1 件も受理しなければ前面化もしない
    /// （不正 URI は完全に無反応にする）。
    /// </summary>
    private static async Task OpenAllAsync(IReadOnlyList<string> values, MainWindow? reusable = null)
    {
        var toFront = new List<MainWindow>();

        foreach (string value in values)
        {
            try
            {
                MainWindow? target = await OpenPathOrUriAsync(value, reusable).ConfigureAwait(true);
                if (target != null && !toFront.Contains(target))
                {
                    toFront.Add(target);
                }
            }
            catch
            {
                // 1 件の失敗で残りを止めない。
            }
        }

        foreach (var window in toFront)
        {
            window.BringToFront();
        }
    }

    /// <summary>
    /// 1 件の入力（ファイルパス または hirake:// URI）を開く。
    /// 受理した場合は前面化すべきウィンドウを返し、受理しなければ null を返す。
    /// URI は <see cref="HirakeUri.TryParse"/> で検証し、落ちたら黙って無視する。
    /// </summary>
    private static async Task<MainWindow?> OpenPathOrUriAsync(string value, MainWindow? reusable = null)
    {
        if (!HirakeUri.IsHirakeUri(value))
        {
            var target = ResolveTargetWindow(value);
            if (target == null)
            {
                return null;
            }
            target.OpenFile(value);
            return target;
        }

        if (!HirakeUri.TryParse(value, out HirakeRequest? request) || request == null)
        {
            return null; // 不正な URI は無反応（ダイアログも出さない）。
        }

        if (request is HirakeWorkspaceRequest workspaceRequest)
        {
            // 同名を開いていれば前面化のみ。無ければ新しいウィンドウで開く。
            // 一致する名前が無ければ黙って無視する（外部からダイアログを出させない）。
            var workspace = WorkspaceStore.Instance.FindByName(workspaceRequest.Name);
            return workspace == null
                ? null
                : WindowManager.Instance.OpenWorkspace(workspace, reusable);
        }

        if (request is not HirakeOpenRequest openRequest)
        {
            return null;
        }

        // heading は line より優先する。解決できなければ line にフォールバックする。
        // 解析は外部から繰り返し叩かれ得るため、UI スレッドを止めないよう別スレッドで行う。
        int? line = openRequest.Line;
        if (openRequest.Heading != null)
        {
            string path = openRequest.Path;
            string heading = openRequest.Heading;
            int? resolved = await Task.Run(() => HirakeUri.FindHeadingLine(path, heading))
                .ConfigureAwait(true);
            line = resolved ?? line;
        }

        var window = ResolveTargetWindow(openRequest.Path);
        if (window == null)
        {
            return null;
        }

        window.OpenFile(openRequest.Path, line);
        return window;
    }

    /// <summary>
    /// ファイルを開く宛先ウィンドウを決める。そのファイルを既に開いているウィンドウが
    /// あればそこ、無ければ最後にアクティブだったウィンドウ。1 枚も無ければ新規に開く。
    /// </summary>
    private static MainWindow? ResolveTargetWindow(string path)
    {
        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch
        {
            fullPath = path;
        }

        return WindowManager.Instance.FindByOpenFile(fullPath)
            ?? WindowManager.Instance.LastActive
            ?? WindowManager.Instance.CreateWindow();
    }

    private static List<string> NormalizeArgs(string[] args)
    {
        var result = new List<string>();
        foreach (var arg in args)
        {
            if (string.IsNullOrWhiteSpace(arg))
            {
                continue;
            }

            // hirake:// URI は Path.GetFullPath に通すと潰れるため、そのまま通す
            // （検証は OpenPathOrUri → HirakeUri.TryParse で行う）。
            if (HirakeUri.IsHirakeUri(arg))
            {
                result.Add(arg.Trim());
                continue;
            }

            try
            {
                result.Add(Path.GetFullPath(arg));
            }
            catch
            {
                // 解決できない引数は無視する。
            }
        }
        return result;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // ウィンドウ構成を確定させてから設定を書き出す（順序が逆だと取りこぼす）。
        WindowManager.Instance.SaveSessionNow();
        WindowManager.Instance.Shutdown();

        // 未保存の設定変更（デバウンス待ち）を確実に書き出す。
        SettingsStore.Instance.SaveNow();

        // キューに残っている診断ログを書き出す（上限つきで待つ）。
        // 異常の直後に閉じられたとき、最も必要な最新の記録が失われないように。
        Diagnostics.Shutdown();

        _instanceManager?.Dispose();
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(
            e.Exception.ToString(),
            "Hirake - 予期しないエラー",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
    }

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        var ex = e.ExceptionObject as Exception;
        MessageBox.Show(
            ex?.ToString() ?? "不明なエラー",
            "Hirake - 致命的なエラー",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }
}
