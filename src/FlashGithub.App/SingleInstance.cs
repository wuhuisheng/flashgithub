using System.IO.Pipes;

namespace FlashGithub.App;

/// <summary>
/// 单实例控制：第二个实例启动时通过命名管道通知第一个实例唤起主窗口，然后自己退出。
/// </summary>
public static class SingleInstance
{
    private const string MutexName = "FlashGithub.SingleInstance";
    private const string PipeName = "FlashGithub.Show";

    private static Mutex? _mutex;

    /// <summary>当前实例是否为第一个实例。</summary>
    public static bool IsFirst { get; private set; }

    /// <summary>第二个实例请求唤起窗口时触发（UI 线程订阅）。</summary>
    public static event Action? ShowRequested;

    public static void Initialize()
    {
        _mutex = new Mutex(true, MutexName, out var first);
        IsFirst = first;
        if (first)
        {
            // 持有互斥体的同时启动管道服务，等待后续实例的唤起请求
            _ = Task.Run(ServerLoopAsync);
            return;
        }

        // 已有实例在跑：通知它唤起窗口，然后让当前进程退出
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(1500);
            using var writer = new StreamWriter(client);
            writer.WriteLine("SHOW");
        }
        catch
        {
            // 通知失败（如旧实例刚退出），按首个实例继续也无妨
            IsFirst = true;
        }
    }

    private static async Task ServerLoopAsync()
    {
        while (true)
        {
            NamedPipeServerStream server;
            try
            {
                server = new NamedPipeServerStream(PipeName, PipeDirection.In);
                await server.WaitForConnectionAsync();
            }
            catch
            {
                return; // 进程退出时管道被释放
            }

            string? line = null;
            try
            {
                using var reader = new StreamReader(server);
                line = await reader.ReadLineAsync();
            }
            finally
            {
                server.Dispose();
            }

            if (line == "SHOW")
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() => ShowRequested?.Invoke());
            }
        }
    }
}
