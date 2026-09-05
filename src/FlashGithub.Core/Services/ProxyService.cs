using System.Net;
using System.Net.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Yarp.ReverseProxy.Forwarder;

namespace FlashGithub.Core.Services;

/// <summary>
/// 本地反向代理（MITM）：
///  - 监听 127.0.0.1:443 —— hosts 已把白名单域名指向 127.0.0.1，Kestrel 按 SNI 用本地 CA 签发的证书完成握手；
///  - 监听 127.0.0.1:80  —— 转发明文 HTTP（GitHub 会 301 到 https）；
///  - 请求经 YARP 转发到真实域名，TCP 连接由 UpstreamPool 完成（DoH 动态解析 + 多 IP 故障转移）。
/// 上游 TLS 握手使用真实域名校验：浏览器/git 看到的是本地签发的证书，本地看到的是 GitHub 真实证书。
/// </summary>
public sealed class ProxyService : IAsyncDisposable
{
    private readonly CertificateAuthority _ca;
    private readonly UpstreamPool _pool;
    private WebApplication? _app;

    public int HttpsPort { get; set; } = 443;
    public int HttpPort { get; set; } = 80;

    /// <summary>是否只转发白名单内域名（收到未知 SNI 时直接断开）。</summary>
    public Func<IReadOnlyCollection<string>>? AllowedDomains { get; set; }

    public ProxyService(CertificateAuthority ca, UpstreamPool pool)
    {
        _ca = ca;
        _pool = pool;
    }

    public bool IsRunning => _app is not null;

    public async Task StartAsync(CancellationToken ct = default)
    {
        if (_app is not null) return;

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = AppContext.BaseDirectory,
        });
        builder.Logging.ClearProviders();
        builder.Services.AddHttpForwarder();
        builder.Services.Configure<KestrelServerOptions>(options =>
        {
            options.AddServerHeader = false;
            options.Listen(IPAddress.Loopback, HttpsPort, listenOptions =>
            {
                listenOptions.UseHttps(httpsOptions =>
                {
                    httpsOptions.ServerCertificateSelector = (_, sni) =>
                    {
                        if (sni is null) return null;
                        var allowed = AllowedDomains?.Invoke();
                        if (allowed is not null
                            && !allowed.Contains(sni, StringComparer.OrdinalIgnoreCase))
                        {
                            Log.Warn($"收到白名单之外的 SNI 请求：{sni}，已拒绝");
                            return null;
                        }
                        return _ca.GetCertificate(sni);
                    };
                });
            });
            options.Listen(IPAddress.Loopback, HttpPort);
        });
        var app = builder.Build();

        var forwarder = app.Services.GetRequiredService<IHttpForwarder>();
        var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(5),
            ConnectCallback = async (context, token) =>
            {
                var stream = await _pool.ConnectAsync(
                    context.DnsEndPoint.Host, context.DnsEndPoint.Port, token);
                return stream ?? throw new HttpRequestException(
                    $"无法连接上游 {context.DnsEndPoint.Host}:{context.DnsEndPoint.Port}");
            },
        };
        var httpClient = new HttpMessageInvoker(handler);

        app.Run(async context =>
        {
            var host = context.Request.Host.Host;
            var scheme = context.Request.IsHttps ? "https" : "http";
            var error = await forwarder.SendAsync(context, $"{scheme}://{host}", httpClient);

            if (error != ForwarderError.None
                && context.Features.Get<IForwarderErrorFeature>() is { } errorFeature)
            {
                var detail = errorFeature.Exception?.Message;
                Log.Error($"转发 {host}{context.Request.Path} 失败：{error}{(detail is null ? "" : $"（{detail}）")}");
            }
        });

        _app = app;
        await app.StartAsync(ct);
        Log.Info($"本地反向代理已启动：127.0.0.1:{HttpsPort}(HTTPS) / 127.0.0.1:{HttpPort}(HTTP)");
    }

    public async Task StopAsync()
    {
        if (_app is null) return;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await _app.StopAsync(cts.Token);
            Log.Info("本地反向代理已停止");
        }
        finally
        {
            await _app.DisposeAsync();
            _app = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
            await StopAsync();
    }
}
