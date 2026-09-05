using FlashGithub.Core.Services;

namespace FlashGithub.Core;

/// <summary>
/// 加速引擎总控：证书安装 → hosts 写入 → 本地代理启动 → 后台 IP 测速。
/// </summary>
public sealed class AccelerationEngine : IAsyncDisposable
{
    private readonly DomainRegistry _registry;
    private readonly CertificateAuthority _ca;
    private readonly TrustService _trust;
    private readonly HostsService _hosts;
    private readonly DohResolver _resolver;
    private readonly UpstreamPool _pool;
    private readonly ProxyService _proxy;

    private CancellationTokenSource? _probeCts;

    public AccelerationEngine(DomainRegistry? registry = null, string? configDirectory = null)
    {
        _registry = registry ?? new DomainRegistry(configDirectory);
        _ca = new CertificateAuthority(configDirectory);
        _trust = new TrustService(_ca, configDirectory);
        _hosts = new HostsService();
        _resolver = new DohResolver();
        _pool = new UpstreamPool(_resolver);
        _proxy = new ProxyService(_ca, _pool)
        {
            AllowedDomains = () => _registry.EnabledDomains,
        };

        _pool.LatencyUpdated += (domain, ms) => LatencyUpdated?.Invoke(domain, ms);
    }

    public DomainRegistry Registry => _registry;
    public TrustService Trust => _trust;
    public HostsService Hosts => _hosts;
    public CertificateAuthority Ca => _ca;

    public bool IsRunning { get; private set; }

    /// <summary>代理监听 80/443 失败（权限不足等）时为 true，界面据此提示以管理员身份重启。</summary>
    public bool NeedsElevation { get; private set; }

    public string? CaThumbprint => _ca.CaThumbprint;

    public event Action<string, int?>? LatencyUpdated;

    /// <summary>准备证书（生成 CA 文件），检查信任状态。不弹任何提权框。</summary>
    public void PrepareCertificates() => _ca.EnsureCreated();

    public bool IsCertificateTrusted() => _ca.IsReady && _trust.IsTrusted();

    public async Task InstallCertificateAsync()
    {
        _ca.EnsureCreated();
        await _trust.InstallAsync();
    }

    /// <summary>开启加速。按顺序完成：证书信任 → hosts 写入 → 代理监听 → IP 测速。</summary>
    public async Task StartAsync()
    {
        if (IsRunning) return;

        var enabled = _registry.EnabledDomains;
        if (enabled.Count == 0)
            throw new InvalidOperationException("没有启用的域名，请至少勾选一个域名");

        _ca.EnsureCreated();

        if (!_trust.IsTrusted())
        {
            Log.Info("本地根证书尚未信任，开始安装…");
            await _trust.InstallAsync();
        }
        else
        {
            Log.Info("本地根证书已受信任");
        }

        Log.Info("写入 hosts 加速条目（可能弹出管理员授权框）…");
        await _hosts.ApplyAsync(enabled);

        try
        {
            Log.Info("启动本地反向代理…");
            await _proxy.StartAsync();
            NeedsElevation = false;
        }
        catch (System.Net.Sockets.SocketException)
        {
            NeedsElevation = true;
            throw new InvalidOperationException(
                "监听 80/443 端口失败：需要管理员权限。请点击“以管理员身份重启”，或退出程序后用管理员身份运行。");
        }

        _probeCts = new CancellationTokenSource();
        _ = Task.Run(() => _pool.ProbeAllAsync(enabled, _probeCts.Token));

        IsRunning = true;
        Log.Info("加速已开启 ✓");
    }

    /// <summary>关闭加速：停止代理、还原 hosts。证书保留（可在证书页卸载）。</summary>
    public async Task StopAsync()
    {
        if (!IsRunning && !_proxy.IsRunning) return;

        _probeCts?.Cancel();
        await _proxy.StopAsync();

        try
        {
            await _hosts.RemoveAsync();
        }
        catch (Exception ex)
        {
            Log.Warn($"还原 hosts 失败：{ex.Message}");
        }

        IsRunning = false;
        Log.Info("加速已关闭");
    }

    /// <summary>手动触发一轮全量 IP 测速，刷新延迟显示。</summary>
    public Task RefreshLatencyAsync()
    {
        var enabled = _registry.EnabledDomains;
        return Task.Run(() => _pool.ProbeAllAsync(enabled));
    }

    public ValueTask DisposeAsync() => _proxy.DisposeAsync();
}
