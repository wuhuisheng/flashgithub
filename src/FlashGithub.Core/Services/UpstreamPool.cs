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

        /// <summary>连接失败 IP 的冷却期，期间不再优先尝试。</summary>
        public readonly Dictionary<IPAddress, DateTimeOffset> CooldownUntil = new();
    }

    private static readonly TimeSpan FailureCooldown = TimeSpan.FromSeconds(60);

    private static readonly TimeSpan ResolveInterval = TimeSpan.FromMinutes(5);

    // 内置种子 IP：GitHub 在国内"时通时断"，仅靠 DoH 单点结果赌性太大。
    // 种子来自 GitHub 官方地址段的常见可用接入点，与 DoH 结果合并成大候选池。
    private static readonly string[] SeedMain = // github.com 等 Web 层（多地域分散，晚高峰美段常被阻断）
    [
        "140.82.112.3", "140.82.113.3", "140.82.114.3", "140.82.116.3",
        "140.82.121.3", "140.82.112.4", "140.82.113.4", "140.82.114.4",
        "140.82.116.4", "140.82.121.4", "20.205.243.166", "20.27.177.113",
        "20.200.245.247", "4.208.26.197", "20.42.65.92",
    ];
    private static readonly string[] SeedApi = // api.github.com 专用 VIP（.6 段）
    [
        "140.82.112.6", "140.82.113.6", "140.82.114.6", "140.82.116.6",
        "140.82.121.6", "20.205.243.168",
    ];
    private static readonly string[] SeedCodeload = // codeload 专用 VIP（.10 段）
    [
        "140.82.112.10", "140.82.113.10", "140.82.114.10", "140.82.116.10",
        "140.82.121.10", "20.205.243.165",
    ];
    private static readonly string[] SeedPages = // *.githubusercontent.com
        ["185.199.108.133", "185.199.109.133", "185.199.110.133", "185.199.111.133"];
    private static readonly string[] SeedAssets = // githubassets.com
        ["185.199.108.215", "185.199.109.215", "185.199.110.215", "185.199.111.215"];

    private static IEnumerable<IPAddress> GetSeedIps(string domain)
    {
        var d = domain.ToLowerInvariant();
        // 各子域的接入点 VIP 段不同，绝不能混用（API 域名打到 Web VIP 会被 301 跳转）
        string[] seeds = d switch
        {
            "api.github.com" => SeedApi,
            "codeload.github.com" => SeedCodeload,
            _ when d == "github.githubassets.com" || d.EndsWith(".githubassets.com") => SeedAssets,
            _ when d.EndsWith(".githubusercontent.com") || d.EndsWith("githubusercontent.com") => SeedPages,
            _ when d.EndsWith("githubassets.com") => SeedAssets,
            _ when d.EndsWith("github.com") || d.EndsWith("github.io") => SeedMain,
            _ => [],
        };
        return seeds.Select(IPAddress.Parse);
    }

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
        foreach (var ip in ordered.Take(Math.Min(ordered.Length, 10)))
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
                // 单个 IP 超时：进入冷却，换下一个
                state.CooldownUntil[ip] = DateTimeOffset.Now + FailureCooldown;
            }
            catch
            {
                // 该 IP 不可用（常见于被阻断的 IP）：进入冷却，换下一个
                state.CooldownUntil[ip] = DateTimeOffset.Now + FailureCooldown;
            }
        }

        // 候选全部失败：强制重新解析后再试一轮
        state.ResolvedAt = DateTimeOffset.MinValue;
        await EnsureCandidatesAsync(host, state, ct);
        foreach (var ip in OrderCandidates(state).Take(6))
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
                state.CooldownUntil[ip] = DateTimeOffset.Now + FailureCooldown;
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

            // DoH 结果与内置种子合并成大候选池（去重），单个 IP 被阻断时有充足的故障转移余地
            state.CooldownUntil.Clear();
            var candidates = new List<IPAddress>();
            foreach (var ip in GetSeedIps(host))
                if (!candidates.Contains(ip))
                    candidates.Add(ip);
            foreach (var ip in await _resolver.ResolveAsync(host, ct))
                if (!candidates.Contains(ip))
                    candidates.Add(ip);

            if (candidates.Count > 0)
            {
                state.Candidates = [.. candidates];
                state.ResolvedAt = DateTimeOffset.Now;
                Log.Info($"候选 IP 池 {host} 共 {candidates.Count} 个: {string.Join(", ", candidates.Take(3))}...");
            }
            else
            {
                Log.Warn($"{host} 无任何可用候选 IP（DoH 失败且无种子）");
            }
        }
        finally
        {
            _resolveLock.Release();
        }
    }

    private static IPAddress[] OrderCandidates(DomainState state)
    {
        // 冷却中的 IP 排到最后（除非全部都在冷却，那就强制全部重试）
        var now = DateTimeOffset.Now;
        var ready = new List<IPAddress>();
        var cooling = new List<IPAddress>();
        foreach (var ip in state.Candidates)
            (state.CooldownUntil.TryGetValue(ip, out var until) && until > now ? cooling : ready).Add(ip);
        if (ready.Count == 0) ready = cooling;

        var list = new List<IPAddress>(state.Candidates.Length);
        if (state.Preferred is not null && ready.Contains(state.Preferred))
            list.Add(state.Preferred);
        // 从 NextIndex 开始轮转，避免每次都从第一个 IP 重试
        var start = state.NextIndex % Math.Max(ready.Count, 1);
        for (var i = 0; i < ready.Count; i++)
        {
            var ip = ready[(start + i) % ready.Count];
            if (!list.Contains(ip)) list.Add(ip);
        }
        list.AddRange(cooling.Except(list));
        state.NextIndex = (state.NextIndex + 1) % Math.Max(ready.Count, 1);
        return [.. list];
    }

    public void Dispose() => _resolveLock.Dispose();
}
