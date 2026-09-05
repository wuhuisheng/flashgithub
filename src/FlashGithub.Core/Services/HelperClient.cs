using System.Net.Sockets;
using System.Text.Json;

namespace FlashGithub.Core.Services;

/// <summary>
/// UI 侧与特权后台服务通信的客户端：Unix socket 长连接，
/// 命令带 id 做请求-响应匹配，daemon 主动推送的 log/latency 事件转成事件回调。
/// </summary>
public sealed class HelperClient
{
    public static HelperClient Default { get; } = new();

    /// <summary>daemon 转发来的日志。</summary>
    public event Action<string>? Log;

    /// <summary>daemon 侧测速结果（域名, 延迟ms或null）。</summary>
    public event Action<string, int?>? LatencyUpdated;

    private const int CommandTimeoutSeconds = 90;

    private NetworkStream? _stream;
    private readonly SemaphoreSlim _io = new(1, 1);
    private readonly Dictionary<string, TaskCompletionSource<(bool Ok, string? Message)>> _pending = new();
    private long _nextId;

    public bool IsAvailable() =>
        OperatingSystem.IsMacOS() && File.Exists(HelperDaemon.SocketPath);

    private async Task<NetworkStream> ConnectAsync()
    {
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(HelperDaemon.SocketPath));
        _stream = new NetworkStream(socket, ownsSocket: true);
        _ = Task.Run(ReaderLoopAsync);
        return _stream;
    }

    private async Task ReaderLoopAsync()
    {
        try
        {
            using var reader = new StreamReader(_stream!);
            while (await reader.ReadLineAsync() is { } line)
            {
                JsonDocument doc;
                try { doc = JsonDocument.Parse(line); }
                catch { continue; }

                using (doc)
                {
                    if (doc.RootElement.TryGetProperty("id", out var idEl))
                    {
                        var id = idEl.GetString();
                        var ok = doc.RootElement.TryGetProperty("ok", out var okEl) && okEl.GetBoolean();
                        var message = doc.RootElement.TryGetProperty("message", out var msgEl)
                            ? msgEl.GetString() : null;
                        Complete(id, (ok, message));
                    }
                    else if (doc.RootElement.TryGetProperty("event", out var evEl))
                    {
                        var ev = evEl.GetString();
                        if (ev == "log" &&
                            doc.RootElement.TryGetProperty("message", out var logMsg))
                            Log?.Invoke(logMsg.GetString() ?? "");
                        else if (ev == "latency" &&
                                 doc.RootElement.TryGetProperty("domain", out var dEl))
                        {
                            int? ms = doc.RootElement.TryGetProperty("ms", out var msEl)
                                && msEl.ValueKind == JsonValueKind.Number ? msEl.GetInt32() : null;
                            LatencyUpdated?.Invoke(dEl.GetString() ?? "", ms);
                        }
                    }
                }
            }
        }
        catch { /* 断开 */ }

        // 连接断开：让所有在途命令以失败完成
        lock (_pending)
        {
            foreach (var tcs in _pending.Values)
                tcs.TrySetResult((false, "与后台服务的连接已断开"));
            _pending.Clear();
        }
        _stream = null;
    }

    private void Complete(string? id, (bool Ok, string? Message) result)
    {
        if (id is null) return;
        lock (_pending)
        {
            if (_pending.Remove(id, out var tcs))
                tcs.TrySetResult(result);
        }
    }

    /// <summary>发送命令并等待 daemon 确认。失败抛异常。</summary>
    private async Task SendAsync(string cmd)
    {
        ObjectDisposedException.ThrowIf(_io is null, this);

        await _io.WaitAsync();
        try
        {
            var stream = _stream ?? await ConnectAsync();

            var id = Interlocked.Increment(ref _nextId).ToString();
            var tcs = new TaskCompletionSource<(bool, string?)>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_pending) _pending[id] = tcs;

            var line = JsonSerializer.Serialize(new { cmd, id });
            var bytes = System.Text.Encoding.UTF8.GetBytes(line + "\n");
            using var cts = new CancellationTokenSource(
                TimeSpan.FromSeconds(CommandTimeoutSeconds));
            await stream.WriteAsync(bytes, cts.Token);

            var (ok, message) = await tcs.Task.WaitAsync(cts.Token);
            if (!ok)
                throw new InvalidOperationException(message ?? $"命令 {cmd} 执行失败");
        }
        catch
        {
            // 出错时丢弃连接，下次命令重新连接
            try { _stream?.Dispose(); } catch { }
            _stream = null;
            throw;
        }
        finally
        {
            _io.Release();
        }
    }

    /// <summary>让 daemon 启动 80/443 监听（使用 daemon 侧 domains.json 的启用域名）。</summary>
    public Task StartProxyAsync() => SendAsync("start");

    /// <summary>让 daemon 停止监听。</summary>
    public Task StopProxyAsync() => SendAsync("stop");

    /// <summary>让 daemon 触发一轮 IP 测速。</summary>
    public Task ProbeAsync() => SendAsync("probe");
}
