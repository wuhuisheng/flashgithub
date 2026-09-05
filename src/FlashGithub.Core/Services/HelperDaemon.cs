using System.Net.Sockets;
using System.Text.Json;

namespace FlashGithub.Core.Services;

/// <summary>
/// 特权后台服务（macOS LaunchDaemon，以 root 常驻）：
/// 通过 /private/var/run/com.flashgithub.helper.sock 接收 UI 的 JSON 命令，
/// 代为承载 80/443 监听与上游连接池，使用 `--helper` 参数运行，不启动 GUI。
/// 协议：每行一个 JSON。请求 {"cmd":"start|stop|probe|status","id":n}；
/// 响应 {"id":n,"ok":true}；事件 {"event":"log|latency",...}。
/// </summary>
public static class HelperDaemon
{
    public const string SocketPath = "/private/var/run/com.flashgithub.helper.sock";

    private static readonly List<StreamWriter> _clients = [];
    private static readonly object _clientsLock = new();
    private static ProxyService? _proxy;
    private static UpstreamPool? _pool;
    private static DomainRegistry? _registry;

    public static async Task RunAsync()
    {
        var dataDir = DomainRegistry.AppDataDirectory;
        Directory.CreateDirectory(dataDir);

        var ca = new CertificateAuthority(dataDir);
        ca.EnsureCreated();
        _pool = new UpstreamPool(new DohResolver());
        _registry = new DomainRegistry(dataDir);
        _proxy = new ProxyService(ca, _pool) { AllowedDomains = () => _registry.EnabledDomains };

        _pool.LatencyUpdated += (d, ms) =>
            Broadcast(JsonSerializer.Serialize(new { @event = "latency", domain = d, ms }));
        Log.OnMessage += m =>
        {
            try { File.AppendAllText(Path.Combine(dataDir, "helper.log"), m + Environment.NewLine); }
            catch { }
            Broadcast(JsonSerializer.Serialize(new { @event = "log", message = m }));
        };

        Log.Info($"后台服务已启动（数据目录 {dataDir}）");

        try { File.Delete(SocketPath); } catch { }

        var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(SocketPath));
        // 所有用户可连接（UI 以普通用户身份运行）
        File.SetUnixFileMode(SocketPath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite |
            UnixFileMode.GroupRead | UnixFileMode.GroupWrite |
            UnixFileMode.OtherRead | UnixFileMode.OtherWrite);
        listener.Listen(10);
        Log.Info($"命令通道就绪：{SocketPath}");

        while (true)
        {
            var conn = await listener.AcceptAsync();
            _ = HandleConnectionAsync(conn);
        }
    }

    private static void Broadcast(string line)
    {
        lock (_clientsLock)
        {
            for (var i = _clients.Count - 1; i >= 0; i--)
            {
                try
                {
                    _clients[i].WriteLine(line);
                }
                catch
                {
                    _clients.RemoveAt(i);
                }
            }
        }
    }

    private static async Task HandleConnectionAsync(Socket conn)
    {
        var stream = new NetworkStream(conn, ownsSocket: true);
        using var reader = new StreamReader(stream);
        var writer = new StreamWriter(stream) { AutoFlush = true };
        lock (_clientsLock) _clients.Add(writer);

        try
        {
            while (reader.Peek() >= 0)
            {
                var line = await reader.ReadLineAsync();
                if (line is null) break;
                await HandleCommandAsync(line, writer);
            }
        }
        catch { /* 客户端断开 */ }
        finally
        {
            lock (_clientsLock) _clients.Remove(writer);
        }
    }

    private static async Task HandleCommandAsync(string line, StreamWriter writer)
    {
        string? id = null;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            id = root.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
            var cmd = root.GetProperty("cmd").GetString();

            switch (cmd)
            {
                case "start":
                    if (_proxy is { IsRunning: false })
                    {
                        await _proxy.StartAsync();
                        StartProbeLoop();
                    }
                    await ReplyAsync(writer, id, true, null);
                    break;

                case "stop":
                    if (_proxy is { IsRunning: true })
                        await _proxy.StopAsync();
                    _probeCts?.Cancel();
                    await ReplyAsync(writer, id, true, null);
                    break;

                case "probe":
                    var probeDomains = _registry!.EnabledDomains;
                    _ = Task.Run(() => _pool!.ProbeAllAsync(probeDomains));
                    await ReplyAsync(writer, id, true, null);
                    break;

                case "status":
                    await writer.WriteLineAsync(JsonSerializer.Serialize(new
                    {
                        id,
                        ok = true,
                        running = _proxy is { IsRunning: true },
                    }));
                    break;

                default:
                    await ReplyAsync(writer, id, false, $"未知命令 {cmd}");
                    break;
            }
        }
        catch (Exception ex)
        {
            try { await ReplyAsync(writer, id, false, ex.Message); } catch { }
        }
    }

    private static async Task ReplyAsync(StreamWriter writer, string? id, bool ok, string? message)
    {
        await writer.WriteLineAsync(JsonSerializer.Serialize(new { id, ok, message }));
    }

    private static CancellationTokenSource? _probeCts;

    /// <summary>后台持续测速：每 3 分钟一轮，与 UI 侧引擎逻辑一致。</summary>
    private static void StartProbeLoop()
    {
        _probeCts?.Cancel();
        _probeCts = new CancellationTokenSource();
        var ct = _probeCts.Token;
        _ = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await _pool!.ProbeAllAsync(_registry!.EnabledDomains, ct);
                    await Task.Delay(TimeSpan.FromMinutes(3), ct);
                }
                catch (OperationCanceledException) { return; }
                catch { try { await Task.Delay(TimeSpan.FromSeconds(30), ct); } catch (OperationCanceledException) { return; } }
            }
        });
    }
}
