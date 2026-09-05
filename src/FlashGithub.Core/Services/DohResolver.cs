using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace FlashGithub.Core.Services;

/// <summary>
/// 基于 DoH(JSON) 的域名解析，多服务器并发竞速，取最先返回的有效结果。
/// 优先使用国内可达的 DoH 服务（阿里、DNSPod），失败时回退到 Cloudflare/Google。
/// </summary>
public sealed class DohResolver : IDisposable
{
    private static readonly string[] Servers =
    [
        "https://dns.alidns.com/resolve?name={0}&type=A",
        "https://doh.pub/dns-query?name={0}&type=A",
        "https://1.1.1.1/dns-query?name={0}&type=A",
        "https://8.8.8.8/resolve?name={0}&type=A",
    ];

    private sealed record CacheEntry(IReadOnlyList<IPAddress> Addresses, DateTimeOffset ExpiresAt);

    private readonly HttpClient _client;
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new();

    public DohResolver()
    {
        _client = new HttpClient(new SocketsHttpHandler
        {
            UseProxy = false,
            ConnectTimeout = TimeSpan.FromSeconds(4),
        })
        {
            Timeout = TimeSpan.FromSeconds(6),
        };
        _client.DefaultRequestHeaders.Accept.ParseAdd("application/dns-json");
    }

    /// <summary>解析域名 A 记录；成功缓存 10 分钟，失败缓存 2 分钟（避免打爆 DoH 服务器）。</summary>
    public async Task<IReadOnlyList<IPAddress>> ResolveAsync(string domain, CancellationToken ct = default)
    {
        if (_cache.TryGetValue(domain, out var entry) && entry.ExpiresAt > DateTimeOffset.Now)
            return entry.Addresses;

        var result = await ResolveUncachedAsync(domain, ct);
        var ttl = result.Count > 0
            ? TimeSpan.FromMinutes(10)
            : TimeSpan.FromMinutes(2);
        _cache[domain] = new CacheEntry(result, DateTimeOffset.Now + ttl);
        return result;
    }

    private async Task<IReadOnlyList<IPAddress>> ResolveUncachedAsync(string domain, CancellationToken ct)
    {
        var tasks = Servers
            .Select(s => QueryOneAsync(string.Format(s, Uri.EscapeDataString(domain)), ct))
            .ToList();

        while (tasks.Count > 0)
        {
            var finished = await Task.WhenAny(tasks);
            tasks.Remove(finished);
            var addresses = await finished;
            if (addresses.Count > 0) return addresses;
        }
        return [];
    }

    private async Task<IReadOnlyList<IPAddress>> QueryOneAsync(string url, CancellationToken ct)
    {
        try
        {
            using var resp = await _client.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode) return [];

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            if (doc.RootElement.TryGetProperty("Status", out var status) && status.GetInt32() != 0)
                return [];

            if (!doc.RootElement.TryGetProperty("Answer", out var answers))
                return [];

            return answers.EnumerateArray()
                .Where(a => a.TryGetProperty("type", out var t) && t.GetInt32() == 1) // A 记录
                .Select(a => a.GetProperty("data").GetString())
                .Where(s => s is not null && IPAddress.TryParse(s, out _))
                .Select(s => IPAddress.Parse(s!))
                .Where(ip => !IsPrivate(ip))
                .Distinct()
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    private static bool IsPrivate(IPAddress ip)
    {
        var bytes = ip.GetAddressBytes();
        return bytes[0] is 10 or 127 or 0
            || (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
            || (bytes[0] == 192 && bytes[1] == 168)
            || (bytes[0] == 169 && bytes[1] == 254);
    }

    public void Dispose() => _client.Dispose();
}
