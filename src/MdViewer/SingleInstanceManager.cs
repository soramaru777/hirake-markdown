using System.IO;
using System.IO.Pipes;
using System.Text;

namespace MdViewer;

/// <summary>
/// 名前付き Mutex による二重起動制御と、名前付きパイプによるプロセス間ファイルパス転送。
/// 初回プロセスがパイプサーバーを常駐させ、2 番目以降のプロセスはパスを送って終了する。
/// </summary>
public sealed class SingleInstanceManager : IDisposable
{
    private const string MutexName = "MdViewer_SingleInstance_Mutex";
    private const string PipeName = "MdViewer_Pipe";

    private readonly Mutex _mutex;
    private CancellationTokenSource? _serverCts;

    /// <summary>受信したファイルパス群を UI スレッドで処理するためのコールバック。</summary>
    public Action<IReadOnlyList<string>>? PathsReceived { get; set; }

    public bool IsFirstInstance { get; }

    public SingleInstanceManager()
    {
        _mutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);
        IsFirstInstance = createdNew;
    }

    /// <summary>初回プロセス用: パイプサーバーを常駐させ、受信ループを開始する。</summary>
    public void StartServer()
    {
        if (!IsFirstInstance)
        {
            return;
        }

        _serverCts = new CancellationTokenSource();
        _ = Task.Run(() => ServerLoopAsync(_serverCts.Token));
    }

    private async Task ServerLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(
                    PipeName,
                    PipeDirection.In,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                await server.WaitForConnectionAsync(token).ConfigureAwait(false);

                using var reader = new StreamReader(server, Encoding.UTF8);
                var paths = new List<string>();
                string? line;
                while ((line = await reader.ReadLineAsync(token).ConfigureAwait(false)) != null)
                {
                    if (!string.IsNullOrWhiteSpace(line))
                    {
                        paths.Add(line.Trim());
                    }
                }

                if (paths.Count > 0)
                {
                    PathsReceived?.Invoke(paths);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // 単一接続の失敗でサーバーを止めない。次の接続を待つ。
            }
        }
    }

    /// <summary>2 番目以降のプロセス用: 既存プロセスへファイルパスを送信する。</summary>
    public static bool SendToExistingInstance(IReadOnlyList<string> paths, int timeoutMs = 3000)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(timeoutMs);

            using var writer = new StreamWriter(client, new UTF8Encoding(false)) { AutoFlush = true };
            foreach (var path in paths)
            {
                writer.WriteLine(path);
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        try
        {
            _serverCts?.Cancel();
            _serverCts?.Dispose();
        }
        catch
        {
            // ignore
        }

        try
        {
            if (IsFirstInstance)
            {
                _mutex.ReleaseMutex();
            }
        }
        catch
        {
            // ignore
        }

        _mutex.Dispose();
    }
}
