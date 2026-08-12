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
        string userDataFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Hirake",
            "WebView2");
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

        _instanceManager.PathsReceived = OnPathsReceivedFromOtherInstance;
        _instanceManager.StartServer();

        _mainWindow.Show();

        if (paths.Count > 0)
        {
            // 引数がある場合は前回セッションを復元せず、指定ファイルのみ開く（既存動作）。
            _ = OpenAllAsync(_mainWindow, paths);
        }
        else
        {
            // 引数なし起動時のみ前回セッションを復元する。
            _mainWindow.RestoreSession();
        }
    }

    private void OnPathsReceivedFromOtherInstance(IReadOnlyList<string> paths)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (_mainWindow == null)
            {
                return;
            }

            // 送信側（別プロセス）を信用せず、受信側でも必ず再検証する。
            _ = OpenAllAsync(_mainWindow, paths);
        });
    }

    /// <summary>
    /// 入力（ファイルパス または hirake:// URI）を順に開き、1 件でも受理したら
    /// ウィンドウを前面化する。1 件も受理しなければ前面化もしない
    /// （不正 URI は完全に無反応にする）。
    /// </summary>
    private static async Task OpenAllAsync(MainWindow window, IReadOnlyList<string> values)
    {
        bool opened = false;
        foreach (string value in values)
        {
            try
            {
                opened |= await OpenPathOrUriAsync(window, value).ConfigureAwait(true);
            }
            catch
            {
                // 1 件の失敗で残りを止めない。
            }
        }

        if (opened)
        {
            window.BringToFront();
        }
    }

    /// <summary>
    /// 1 件の入力（ファイルパス または hirake:// URI）を開く。受理したら true。
    /// URI は <see cref="HirakeUri.TryParse"/> で検証し、落ちたら黙って無視する。
    /// </summary>
    private static async Task<bool> OpenPathOrUriAsync(MainWindow window, string value)
    {
        if (!HirakeUri.IsHirakeUri(value))
        {
            window.OpenFile(value);
            return true;
        }

        if (!HirakeUri.TryParse(value, out HirakeOpenRequest? request) || request == null)
        {
            return false; // 不正な URI は無反応（ダイアログも出さない）。
        }

        // heading は line より優先する。解決できなければ line にフォールバックする。
        // 解析は外部から繰り返し叩かれ得るため、UI スレッドを止めないよう別スレッドで行う。
        int? line = request.Line;
        if (request.Heading != null)
        {
            string path = request.Path;
            string heading = request.Heading;
            int? resolved = await Task.Run(() => HirakeUri.FindHeadingLine(path, heading))
                .ConfigureAwait(true);
            line = resolved ?? line;
        }

        window.OpenFile(request.Path, line);
        return true;
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
        // 未保存の設定変更（デバウンス待ち）を確実に書き出す。
        SettingsStore.Instance.SaveNow();

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
