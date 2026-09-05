using System.Text.Json;
using System.Text.Json.Serialization;

namespace FlashGithub.Core;

/// <summary>可加速的域名条目。</summary>
public class DomainConfig
{
    public string Domain { get; set; } = "";

    /// <summary>是否参与加速（写入 hosts 并由本地代理转发）。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>是否为内置域名（内置域名不允许删除，只能停用）。</summary>
    public bool IsBuiltIn { get; set; }

    [JsonIgnore]
    public int? LatencyMs { get; set; }
}

/// <summary>域名清单：内置 GitHub 常用域名 + 用户自定义域名（如 huggingface.co），持久化到配置文件。</summary>
public sealed class DomainRegistry
{
    private readonly string _configPath;
    private readonly List<DomainConfig> _domains = new();
    private readonly object _lock = new();

    public static readonly string[] BuiltInDomains =
    [
        "github.com",
        "api.github.com",
        "codeload.github.com",
        "raw.githubusercontent.com",
        "gist.github.com",
        "gist.githubusercontent.com",
        "cloud.githubusercontent.com",
        "camo.githubusercontent.com",
        "avatars.githubusercontent.com",
        "objects.githubusercontent.com",
        "release-assets.githubusercontent.com",
        "media.githubusercontent.com",
        "user-images.githubusercontent.com",
        "github.githubassets.com",
        "live.github.com",
        "collector.github.com",
    ];

    /// <summary>清单发生变化（增删、启用状态切换）时触发。</summary>
    public event Action? Changed;

    public DomainRegistry(string? configDirectory = null)
    {
        var dir = configDirectory ?? AppDataDirectory;
        Directory.CreateDirectory(dir);
        _configPath = Path.Combine(dir, "domains.json");
        Load();
    }

    public static string AppDataDirectory =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "FlashGithub");

    public IReadOnlyList<DomainConfig> Domains
    {
        get { lock (_lock) return _domains.ToList(); }
    }

    public List<string> EnabledDomains =>
        Domains.Where(d => d.Enabled).Select(d => d.Domain).ToList();

    public bool Add(string domain)
    {
        domain = domain.Trim().TrimEnd('.').ToLowerInvariant();
        if (domain.Length == 0 || !Uri.CheckHostName(domain).Equals(UriHostNameType.Dns))
            return false;

        lock (_lock)
        {
            if (_domains.Any(d => d.Domain.Equals(domain, StringComparison.OrdinalIgnoreCase)))
                return false;
            _domains.Add(new DomainConfig { Domain = domain, Enabled = true, IsBuiltIn = false });
        }
        Save();
        Changed?.Invoke();
        return true;
    }

    public bool Remove(string domain)
    {
        bool removed;
        lock (_lock)
        {
            var item = _domains.FirstOrDefault(d =>
                d.Domain.Equals(domain, StringComparison.OrdinalIgnoreCase) && !d.IsBuiltIn);
            if (item is null) return false;
            removed = _domains.Remove(item);
        }
        if (removed)
        {
            Save();
            Changed?.Invoke();
        }
        return removed;
    }

    public void SetEnabled(string domain, bool enabled)
    {
        lock (_lock)
        {
            var item = _domains.FirstOrDefault(d =>
                d.Domain.Equals(domain, StringComparison.OrdinalIgnoreCase));
            if (item is null || item.Enabled == enabled) return;
            item.Enabled = enabled;
        }
        Save();
        Changed?.Invoke();
    }

    public void SetAllEnabled(bool enabled)
    {
        lock (_lock)
        {
            foreach (var d in _domains) d.Enabled = enabled;
        }
        Save();
        Changed?.Invoke();
    }

    private void Load()
    {
        try
        {
            if (File.Exists(_configPath))
            {
                var saved = JsonSerializer.Deserialize<List<DomainConfig>>(File.ReadAllText(_configPath));
                if (saved is not null)
                {
                    lock (_lock)
                    {
                        // 内置域名始终存在（保证升级后新增的内置域名会出现）
                        foreach (var name in BuiltInDomains)
                            _domains.Add(new DomainConfig
                            {
                                Domain = name,
                                Enabled = saved.FirstOrDefault(s =>
                                    s.Domain.Equals(name, StringComparison.OrdinalIgnoreCase))?.Enabled ?? true,
                                IsBuiltIn = true,
                            });
                        foreach (var s in saved.Where(s =>
                                     !BuiltInDomains.Contains(s.Domain, StringComparer.OrdinalIgnoreCase)))
                            _domains.Add(s);
                    }
                    return;
                }
            }
        }
        catch
        {
            // 配置损坏时回退到默认清单
        }

        lock (_lock)
        {
            foreach (var name in BuiltInDomains)
                _domains.Add(new DomainConfig { Domain = name, Enabled = true, IsBuiltIn = true });
        }
        Save();
    }

    private void Save()
    {
        List<DomainConfig> snapshot;
        lock (_lock) snapshot = _domains.ToList();
        File.WriteAllText(_configPath,
            JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true }));
    }
}
