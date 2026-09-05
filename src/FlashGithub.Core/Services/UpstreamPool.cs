using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;

namespace FlashGithub.Core.Services;

/// <summary>
/// 每个域名的上游 IP 池：DoH 解析候选 IP → TCP 测速排序 → 连接失败自动换下一个 IP。
/// 命中过的“优质 IP”会被记住并优先复用。
/// </summary>
public sealed class UpstreamPool : IDisposable
{
    private sealed class DomainState
    {
        public IPAddress[] Candidates = [];
        public int NextIndex;
        public IPAddress? Preferred;   // 最近连接成功的 IP，优先尝试
        public int? LatencyMs;         // 最近一次测速结果，null 表示不可达
        public DateTimeOffset ResolvedAt = DateTimeOffset.MinValue;
    }

    private static readonly TimeSpan ResolveInterval = TimeSpan.FromMinutes(10);

    private readonly DohResolver _resolver;
    private readonly ConcurrentDictionary<string, DomainState> _states = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _resolveLock = new(1, 1);

    /// <summary>(域名, 延迟ms或null) 测速结果更新时触发。</summary>
    public event Action<string, int?>? LatencyUpdated;

    public UpstreamPool(DohResolver resolver) => _resolver = resolver;

    /// <summary>
    /// 为代理提供连接：解析 → 依次尝试候选 IP → 返回已就绪的流（443 端口完成 TLS 握手，SNI 为真实域名，
    /// 因此上游返回的是 GitHub 真实证书，由系统正常校验）。
    /// </summary>
    public async Task<Stream?> ConnectAsync(string host, int port, CancellationToken ct)
    {
        var state = _states.GetOrAdd(host, _ => new DomainState());
        await EnsureCandidatesAsync(host, state, ct);
        if (state.Candidates.Length == 0)
        {
            Log.Error($"无法解析 {host} 的可用 IP");
            return null;
        }

        var ordered = OrderCandidates(state);
        foreach (var ip in ordered.Take(Math.Min(ordered.Length, 6)))
        {
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var tcp = new TcpClient();
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(3));
                await tcp.ConnectAsync(ip, port, timeoutCts.Token);

                Stream stream = tcp.GetStream();
                if (port == 443)
                {
                    var ssl = new SslStream(stream);
                    await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                    {
                        TargetHost = host,
                        // 不设置 RemoteCertificateValidationCallback：使用系统默认校验，确保连到的是真 GitHub
                    }, ct);
                    stream = ssl;
                }

                sw.Stop();
                state.LatencyMs = (int)sw.ElapsedMilliseconds;
                state.Preferred = ip;
                LatencyUpdated?.Invoke(host, state.LatencyMs);
                return stream;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // 单个 IP 超时，继续下一个
            }
            catch
            {
                // 该 IP 不可用（常见于被阻断的 IP），换下一个
            }
        }

        // 候选全部失败：强制重新解析后再试一轮
        state.ResolvedAt = DateTimeOffset.MinValue;
        await EnsureCandidatesAsync(host, state, ct);
        foreach (var ip in OrderCandidates(state).Take(4))
        {
            try
            {
                var tcp = new TcpClient();
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(3));
                await tcp.ConnectAsync(ip, port, timeoutCts.Token);

                Stream stream = tcp.GetStream();
                if (port == 443)
                {
                    var ssl = new SslStream(stream);
                    await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                    {
                        TargetHost = host,
                    }, ct);
                    stream = ssl;
                }
                state.Preferred = ip;
                return stream;
            }
            catch
            {
                // 继续尝试
            }
        }

        Log.Error($"域名 {host} 的所有候选 IP 均连接失败");
        return null;
    }

    /// <summary>对所有域名做 TCP 测速（443 端口），刷新界面延迟显示。</summary>
    public async Task ProbeAllAsync(IEnumerable<string> domains, CancellationToken ct = default)
    {
        foreach (var domain in domains)
        {
            if (ct.IsCancellationRequested) return;
            var state = _states.GetOrAdd(domain, _ => new DomainState());
            await EnsureCandidatesAsync(domain, state, ct);
            if (state.Candidates.Length == 0)
            {
                state.LatencyMs = null;
                LatencyUpdated?.Invoke(domain, null);
                continue;
            }

            // 对前 5 个候选并发 tcping，取最优
            var candidates = OrderCandidates(state).Take(5).ToList();
            var best = await ProbeBestAsync(candidates, domain, ct);
            state.LatencyMs = best;
            LatencyUpdated?.Invoke(domain, best);
        }
    }

    private async Task<int?> ProbeBestAsync(List<IPAddress> candidates, string domain, CancellationToken ct)
    {
        var tasks = candidates.Select(async ip =>
        {
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                using var tcp = new TcpClient();
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(2));
                await tcp.ConnectAsync(ip, 443, timeoutCts.Token);
                sw.Stop();
                return (ip, ms: (int)sw.ElapsedMilliseconds);
            }
            catch
            {
                return (ip, ms: int.MaxValue);
            }
        }).ToList();

        var results = await Task.WhenAll(tasks);
        var winner = results.Where(r => r.ms != int.MaxValue).OrderBy(r => r.ms).FirstOrDefault();
        if (winner.ip is not null)
        {
            var state = _states.GetOrAdd(domain, _ => new DomainState());
            state.Preferred = winner.ip;
            return winner.ms;
        }
        return null;
    }

    private async Task EnsureCandidatesAsync(string host, DomainState state, CancellationToken ct)
    {
        if (state.Candidates.Length > 0 && DateTimeOffset.Now - state.ResolvedAt < ResolveInterval)
            return;

        await _resolveLock.WaitAsync(ct);
        try
        {
            if (state.Candidates.Length > 0 && DateTimeOffset.Now - state.ResolvedAt < ResolveInterval)
                return;

            var addresses = await _resolver.ResolveAsync(host, ct);
            if (addresses.Count > 0)
            {
                state.Candidates = addresses.ToArray();
                state.ResolvedAt = DateTimeOffset.Now;
                Log.Info($"已解析 {host} → {string.Join(", ", state.Candidates.Take(3))}...");
            }
            else
            {
                Log.Warn($"{host} 解析失败（所有 DoH 服务器均不可用）");
            }
        }
        finally
        {
            _resolveLock.Release();
        }
    }

    private static IPAddress[] OrderCandidates(DomainState state)
    {
        var list = new List<IPAddress>(state.Candidates.Length);
        if (state.Preferred is not null && state.Candidates.Contains(state.Preferred))
            list.Add(state.Preferred);
        // 从 NextIndex 开始轮转，避免每次都从第一个 IP 重试
        var start = state.NextIndex % Math.Max(state.Candidates.Length, 1);
        for (var i = 0; i < state.Candidates.Length; i++)
        {
            var ip = state.Candidates[(start + i) % state.Candidates.Length];
            if (!list.Contains(ip)) list.Add(ip);
        }
        state.NextIndex = (state.NextIndex + 1) % Math.Max(state.Candidates.Length, 1);
        return [.. list];
    }

    public void Dispose() => _resolveLock.Dispose();
}
