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
            foreach (var path in paths)
            {
                _mainWindow.OpenFile(path);
            }
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

            foreach (var path in paths)
            {
                _mainWindow.OpenFile(path);
            }

            _mainWindow.BringToFront();
        });
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
