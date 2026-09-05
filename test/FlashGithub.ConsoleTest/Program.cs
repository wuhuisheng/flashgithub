using System.Net;
using FlashGithub.Core.Services;

// 1) DoH 解析
var resolver = new DohResolver();
var ips = await resolver.ResolveAsync("github.com");
Console.WriteLine($"[1] github.com 解析到 {ips.Count} 个 IP: {string.Join(", ", ips.Take(3))}...");
if (ips.Count == 0) { Console.WriteLine("DoH 解析失败（本机网络无法访问 DoH 服务器）"); return; }

// 2) 证书：生成 CA + 签发叶子证书
var ca = new CertificateAuthority("test-cert-dir");
ca.EnsureCreated();
var leaf = ca.GetCertificate("github.com");
Console.WriteLine($"[2] CA 指纹: {ca.CaThumbprint}");
Console.WriteLine($"    github.com 叶子证书: {leaf?.Subject}, 颁发者: {leaf?.Issuer}");

// 3) 本地反代（临时端口 8443/8080）转发真实请求
var pool = new UpstreamPool(resolver);
var proxy = new ProxyService(ca, pool) { HttpsPort = 8443, HttpPort = 8080 };
await proxy.StartAsync();
Console.WriteLine("[3] 代理已在 127.0.0.1:8443/8080 启动，开始用 curl 验证…");

var psi = new System.Diagnostics.ProcessStartInfo("/usr/bin/curl",
    "-sk -o /dev/null -w %{http_code} --resolve github.com:8443:127.0.0.1 https://github.com:8443/")
{ RedirectStandardOutput = true };
var curl = System.Diagnostics.Process.Start(psi)!;
var code = (await curl.StandardOutput.ReadToEndAsync()).Trim();
await curl.WaitForExitAsync();
Console.WriteLine($"    HTTPS 转发 github.com → HTTP {code}");

var psi2 = new System.Diagnostics.ProcessStartInfo("/usr/bin/curl",
    "-s -o /dev/null -w %{http_code} --resolve github.com:8080:127.0.0.1 http://github.com:8080/")
{ RedirectStandardOutput = true };
var curl2 = System.Diagnostics.Process.Start(psi2)!;
var code2 = (await curl2.StandardOutput.ReadToEndAsync()).Trim();
await curl2.WaitForExitAsync();
Console.WriteLine($"    HTTP  转发 github.com → HTTP {code2}");

await proxy.StopAsync();
Console.WriteLine(code == "200" && code2 is "200" or "301" ? "全部通过 ✓" : "存在失败项，见上方输出");
