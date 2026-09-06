using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Text.Json;

namespace FlashGithub.Core.Services;

/// <summary>
/// GitHub 网段自扫描：从官方 meta API 拿全部自有网段（AS36459 等），
/// 并行探测 TCP 443 可达性，再做真实 TLS 握手验证（SNI + 证书主机名校验），
/// 把验证通过的 IP 按服务层（web/api/git/pages）缓存，供连接池优先使用。
/// 从本机网络扫出的结果，比任何远程 IP 列表都贴合本机真实可达性。
/// </summary>
public sealed class GitHubRangeScanner
{
    private sealed record TierResult(IReadOnlyList<IPAddress> Ips, DateTimeOffset At);

    // 服务层 → meta JSON 里的网段键；同一层的域名共享验证结果
    private static readonly Dictionary<string, string[]> TierRanges = new()
    {
        ["web"] = ["web"],
        ["api"] = ["api"],
        ["git"] = ["git"],
        ["pages"] = ["pages"],
    };

    private static readonly Dictionary<string, string> TierPrimaryHost = new()
    {
        ["web"] = "github.com",
        ["api"] = "api.github.com",
        ["git"] = "codeload.github.com",
        ["pages"] = "raw.githubusercontent.com",
    };

    private const int MaxProbeIps = 8192;      // 单层最多展开探测的 IP 数
    private const int TcpConcurrency = 256;    // TCP 探测并发
    private const int TlsConcurrency = 64;     // TLS 验证并发
    private const int MaxTlsValidate = 120;    // 最多 TLS 验证的可达 IP 数

    private static readonly TimeSpan ResultTtl = TimeSpan.FromHours(6);

    private readonly ConcurrentDictionary<string, TierResult> _tiers = new(StringComparer.OrdinalIgnoreCase);
    private readonly HttpClient _metaClient;
    private readonly SemaphoreSlim _scanLock = new(1, 1);
    private readonly string _rangeCachePath;

    public GitHubRangeScanner(string? configDirectory = null)
    {
        _metaClient = new HttpClient(new SocketsHttpHandler { UseProxy = false })
        {
            Timeout = TimeSpan.FromSeconds(15),
        };
        _metaClient.DefaultRequestHeaders.UserAgent.ParseAdd("FlashGithub");
        var dir = configDirectory ?? DomainRegistry.AppDataDirectory;
        Directory.CreateDirectory(dir);
        _rangeCachePath = Path.Combine(dir, "github-ranges-cache.json");
    }

    /// <summary>取某域名所在服务层的已验证 IP（无有效扫描结果时为空）。</summary>
    public IReadOnlyList<IPAddress> GetWorking(string host)
    {
        var tier = TierOf(host);
        if (_tiers.TryGetValue(tier, out var r) && DateTimeOffset.Now - r.At < ResultTtl)
            return r.Ips;
        return [];
    }

    /// <summary>后台循环：立即扫一轮，之后每小时重扫。</summary>
    public async Task RunLoopAsync(Func<IEnumerable<string>> hostsProvider, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var tiers = hostsProvider().Select(TierOf).Distinct().ToList();
                foreach (var tier in tiers)
                {
                    if (ct.IsCancellationRequested) return;
                    try { await ScanTierAsync(tier, ct); }
                    catch (Exception ex) { Log.Warn($"自扫描 {tier} 层失败：{ex.Message}"); }
                }
                await Task.Delay(TimeSpan.FromHours(1), ct);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                Log.Warn($"自扫描循环异常：{ex.Message}");
                try { await Task.Delay(TimeSpan.FromMinutes(5), ct); }
                catch (OperationCanceledException) { return; }
            }
        }
    }

    private readonly DohResolver _apiResolver = new();

    /// <summary>用 DoH 解析结果直连 api.github.com 拉 meta（原始 HTTP/1.0，绕过 hosts 与代理）。</summary>
    private async Task<string?> FetchMetaViaUpstreamAsync(CancellationToken ct)
    {
        var ips = new List<IPAddress>();
        try { ips.AddRange(await _apiResolver.ResolveAsync("api.github.com", ct)); } catch { }
        foreach (var v in new[] { "140.82.112.6", "140.82.121.6" })
            if (IPAddress.TryParse(v, out var ip) && !ips.Contains(ip))
                ips.Add(ip);

        foreach (var ip in ips)
        {
            try
            {
                using var tcp = new TcpClient();
                using var t1 = CancellationTokenSource.CreateLinkedTokenSource(ct);
                t1.CancelAfter(3000);
                await tcp.ConnectAsync(ip, 443, t1.Token);

                using var ssl = new SslStream(tcp.GetStream());
                using var t2 = CancellationTokenSource.CreateLinkedTokenSource(ct);
                t2.CancelAfter(5000);
                await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                {
                    TargetHost = "api.github.com",
                }, t2.Token);

                using var writer = new StreamWriter(ssl, leaveOpen: true) { AutoFlush = true };
                await writer.WriteAsync("GET /meta HTTP/1.0\r\nHost: api.github.com\r\nUser-Agent: FlashGithub\r\n\r\n");

                using var reader = new StreamReader(ssl, leaveOpen: true);
                var raw = await reader.ReadToEndAsync(ct);
                var split = raw.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                var body = split >= 0 ? raw[(split + 4)..] : null;
                if (body is not null && body.Contains('"'))
                {
                    Log.Info($"已通过 {ip} 拉取官方网段（{body.Length} 字节）");
                    return body;
                }
            }
            catch { }
        }
        return null;
    }

    private static string TierOf(string host)
    {
        var d = host.ToLowerInvariant();
        if (d == "api.github.com") return "api";
        if (d == "codeload.github.com") return "git";
        if (d.EndsWith("githubusercontent.com")) return "pages";
        return "web";
    }

    private async Task ScanTierAsync(string tier, CancellationToken ct)
    {
        var ranges = await LoadRangesAsync(tier, ct);
        if (ranges.Count == 0)
        {
            Log.Warn($"自扫描 {tier}：无可用网段（meta 拉取失败且无缓存）");
            return;
        }

        var ips = Expand(ranges);
        var reachable = await TcpProbeAsync(ips, ct);
        if (reachable.Count == 0)
        {
            Log.Warn($"自扫描 {tier}：{ips.Count} 个 IP 中无 TCP 可达");
            return;
        }

        var host = TierPrimaryHost[tier];
        var working = await TlsProbeAsync(reachable, host, tier, ct);
        if (working.Count == 0)
        {
            Log.Warn($"自扫描 {tier}：{reachable.Count} 个可达 IP 的 HTTP 验证均失败");
            return;
        }

        _tiers[tier] = new TierResult(working, DateTimeOffset.Now);
        Log.Info($"自扫描 {tier} 完成：探测 {ips.Count} → 可达 {reachable.Count} → 验证通过 {working.Count}");
    }

    /// <summary>拉取网段列表（在线优先，失败回退本地缓存）。</summary>
    private async Task<List<string>> LoadRangesAsync(string tier, CancellationToken ct)
    {
        Dictionary<string, List<string>>? all = null;

        // 优先：绕开系统 hosts 直连 api.github.com（hosts 可能把它指向 127.0.0.1 造成鸡生蛋）
        var meta = await FetchMetaViaUpstreamAsync(ct);
        if (!string.IsNullOrEmpty(meta))
        {
            all = ParseRanges(meta);
            try { await File.WriteAllTextAsync(_rangeCachePath, JsonSerializer.Serialize(all), ct); }
            catch { }
        }

        if (all is null || all.Count == 0)
        {
            try
            {
                using var resp = await _metaClient.GetAsync("https://api.github.com/meta", ct);
                if (resp.IsSuccessStatusCode)
                {
                    all = ParseRanges(await resp.Content.ReadAsStringAsync(ct));
                    try { await File.WriteAllTextAsync(_rangeCachePath, JsonSerializer.Serialize(all), ct); }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"拉取 GitHub 网段失败：{ex.Message}，尝试本地缓存");
            }
        }

        if ((all is null || all.Count == 0) && File.Exists(_rangeCachePath))
        {
            try
            {
                all = JsonSerializer.Deserialize<Dictionary<string, List<string>>>(
                    await File.ReadAllTextAsync(_rangeCachePath, ct));
            }
            catch { }
        }

        var result = new List<string>();
        if (all is not null && TierRanges.TryGetValue(tier, out var keys))
            foreach (var key in keys)
                if (all.TryGetValue(key, out var list))
                    result.AddRange(list);
        Log.Info($"自扫描 {tier}：命中官方网段 {result.Count} 个");
        return result;
    }

    private static Dictionary<string, List<string>> ParseRanges(string json)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        using var doc = JsonDocument.Parse(json);
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            if (prop.Value.ValueKind != JsonValueKind.Array) continue;
            var list = new List<string>();
            foreach (var item in prop.Value.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String) continue;
                var v = item.GetString();
                // 只要 IPv4 网段
                if (v is not null && v.Contains('/') && !v.Contains(':')) list.Add(v);
            }
            if (list.Count > 0) result[prop.Name] = list;
        }
        return result;
    }

    /// <summary>展开网段为 IP 列表（跳过 /16 及更大的巨段，限制总量）。</summary>
    private static List<IPAddress> Expand(List<string> cidrs)
    {
        var ips = new List<IPAddress>();
        foreach (var cidr in cidrs)
        {
            if (ips.Count >= MaxProbeIps) break;
            try
            {
                var slash = cidr.IndexOf('/');
                if (slash < 0) continue;
                var prefix = int.Parse(cidr[(slash + 1)..]);
                if (prefix < 16 || prefix > 32) continue;
                if (!IPAddress.TryParse(cidr[..slash], out var baseAddr)) continue;
                if (baseAddr.AddressFamily != AddressFamily.InterNetwork) continue;

                // 手动展开 CIDR（base .. base + 2^(32-prefix)）
                var baseBytes = baseAddr.GetAddressBytes();
                uint baseInt = ((uint)baseBytes[0] << 24) | ((uint)baseBytes[1] << 16)
                             | ((uint)baseBytes[2] << 8) | baseBytes[3];
                var count = (long)Math.Pow(2, 32 - prefix);
                for (long i = 0; i < count && ips.Count < MaxProbeIps; i++)
                {
                    var v = baseInt + (uint)i;
                    ips.Add(new IPAddress(new[] {
                        (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v }));
                }
            }
            catch { }
        }
        return ips.Distinct().ToList();
    }

    /// <summary>并行 TCP 探测（1 秒超时）。</summary>
    private static async Task<List<IPAddress>> TcpProbeAsync(List<IPAddress> ips, CancellationToken ct)
    {
        var reachable = new ConcurrentBag<IPAddress>();
        using var sem = new SemaphoreSlim(TcpConcurrency);
        var tasks = ips.Select(async ip =>
        {
            await sem.WaitAsync(ct);
            try
            {
                using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(1000);
                await socket.ConnectAsync(ip, 443, timeout.Token);
                reachable.Add(ip);
            }
            catch { }
            finally { sem.Release(); }
        });
        await Task.WhenAll(tasks);
        return reachable.ToList();
    }

    /// <summary>对可达 IP 做真实 TLS 握手验证（SNI + 系统证书校验 + 主机名匹配）。</summary>
    private static async Task<List<IPAddress>> TlsProbeAsync(List<IPAddress> reachable, string host, string tier, CancellationToken ct)
    {
        // 均匀采样，限制验证数量
        var candidates = reachable;
        if (candidates.Count > MaxTlsValidate)
        {
            var step = (double)candidates.Count / MaxTlsValidate;
            candidates = Enumerable.Range(0, MaxTlsValidate)
                .Select(i => candidates[(int)(i * step)])
                .ToList();
        }

        var working = new ConcurrentBag<IPAddress>();
        using var sem = new SemaphoreSlim(TlsConcurrency);
        var tasks = candidates.Select(async ip =>
        {
            await sem.WaitAsync(ct);
            try
            {
                using var tcp = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(3000);
                await tcp.ConnectAsync(ip, 443, timeout.Token);

                using var ssl = new SslStream(new NetworkStream(tcp, ownsSocket: false));
                using var tlsCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                tlsCts.CancelAfter(3000);
                // 默认系统校验 + 主机名匹配：GitHub 真实入口一定通过，仿冒节点过不了
                await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                {
                    TargetHost = host,
                }, tlsCts.Token);

                // TLS 过了还不够（如 Fastly 对未知域也会完成握手但 HTTP 层报错）：
                // 发一个真实 HTTP 请求，确认服务端真正路由了这个域名
                using var writer = new StreamWriter(ssl, leaveOpen: true) { AutoFlush = true };
                using var reader = new StreamReader(ssl, System.Text.Encoding.UTF8,
                    detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);
                using var httpCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                httpCts.CancelAfter(3000);
                await writer.WriteAsync($"GET / HTTP/1.0\r\nHost: {host}\r\nUser-Agent: FlashGithub\r\n\r\n");
                var head = await reader.ReadToEndAsync(httpCts.Token);

                var ok = head.Contains("HTTP/1.") && !head.Contains("Fastly error");
                if (ok && tier == "api")
                {
                    // api 层更严格：必须是 GitHub API 的 JSON 根响应
                    ok = head.Contains("current_user_url");
                }
                if (ok) working.Add(ip);
            }
            catch { }
            finally { sem.Release(); }
        });
        await Task.WhenAll(tasks);
        return working.ToList();
    }
}
