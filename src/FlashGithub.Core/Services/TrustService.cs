using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace FlashGithub.Core.Services;

/// <summary>
/// 管理员/用户提权执行脚本，跨平台实现：
/// macOS → osascript with administrator privileges；Windows → PowerShell RunAs；Linux → pkexec。
/// </summary>
public static class PrivilegeService
{
    public static bool IsElevated
    {
        get
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
                var principal = new System.Security.Principal.WindowsPrincipal(identity);
                return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            }

            // Unix: 直接试 id -u
            try
            {
                using var p = Process.Start(new ProcessStartInfo("id", "-u")
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                })!;
                return p.StandardOutput.ReadToEnd().Trim() == "0";
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>弹出系统授权框，以管理员身份执行一段 shell/PowerShell 脚本。用户取消或失败时抛异常。</summary>
    public static async Task RunElevatedAsync(string script, string scriptName)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var path = Path.Combine(Path.GetTempPath(), scriptName + ".ps1");
            await File.WriteAllTextAsync(path, script);
            var psi = new ProcessStartInfo("powershell.exe",
                $"-NoProfile -ExecutionPolicy Bypass -Command \"Start-Process powershell -ArgumentList '-NoProfile','-ExecutionPolicy','Bypass','-File','{path}' -Verb RunAs -Wait\"")
            {
                UseShellExecute = true,
            };
            using var p = Process.Start(psi)!;
            await p.WaitForExitAsync();
            if (p.ExitCode != 0)
                throw new InvalidOperationException("管理员授权被拒绝或执行失败");
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            var path = Path.Combine(Path.GetTempPath(), scriptName + ".sh");
            await File.WriteAllTextAsync(path, script);
            var escaped = path.Replace("\"", "\\\"");
            var psi = new ProcessStartInfo("osascript",
                $"-e \"do shell script \\\"sh '{escaped}'\\\" with administrator privileges\"")
            {
                UseShellExecute = false,
                RedirectStandardError = true,
            };
            using var p = Process.Start(psi)!;
            var err = await p.StandardError.ReadToEndAsync();
            await p.WaitForExitAsync();
            if (p.ExitCode != 0)
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(err)
                    ? "管理员授权被取消"
                    : err.Trim());
        }
        else
        {
            var path = Path.Combine(Path.GetTempPath(), scriptName + ".sh");
            await File.WriteAllTextAsync(path, script);
            using var p = Process.Start(new ProcessStartInfo("pkexec", $"sh {path}") { UseShellExecute = false })!;
            await p.WaitForExitAsync();
            if (p.ExitCode != 0)
                throw new InvalidOperationException("pkexec 授权失败");
        }
    }

    /// <summary>以管理员身份重新启动当前程序（macOS/Linux 用 sudo，Windows 用 RunAs）。</summary>
    public static async Task RelaunchElevatedAsync()
    {
        var exe = Environment.ProcessPath
            ?? throw new InvalidOperationException("无法确定程序路径");
        exe = exe.Replace("'", "'\\''");

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            var script = $"nohup '{exe}' >/dev/null 2>&1 &";
            var path = Path.Combine(Path.GetTempPath(), "flashgithub-relaunch.sh");
            await File.WriteAllTextAsync(path, script);
            var psi = new ProcessStartInfo("osascript",
                $"-e \"do shell script \\\"sh '{path}'\\\" with administrator privileges\"")
            {
                UseShellExecute = false,
                RedirectStandardError = true,
            };
            using var p = Process.Start(psi)!;
            var err = await p.StandardError.ReadToEndAsync();
            await p.WaitForExitAsync();
            if (p.ExitCode != 0)
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(err) ? "管理员授权被取消" : err.Trim());
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            using var p = Process.Start(new ProcessStartInfo("powershell.exe",
                $"-NoProfile -Command \"Start-Process '{exe}' -Verb RunAs\"")
            { UseShellExecute = false })!;
            await p.WaitForExitAsync();
        }
        else
        {
            using var p = Process.Start(new ProcessStartInfo("pkexec", exe) { UseShellExecute = false })!;
            await p.WaitForExitAsync();
        }
    }
}

/// <summary>
/// 将 FlashGithub 本地 CA 安装到系统/用户信任库，并在停止时移除。
/// 优先尝试无需管理员权限的用户级安装（macOS 登录钥匙串、Windows 当前用户 Root 库）。
/// </summary>
public sealed class TrustService
{
    private readonly CertificateAuthority _ca;
    private readonly string _pemPath;

    public TrustService(CertificateAuthority ca, string? configDirectory = null)
    {
        _ca = ca;
        var dir = configDirectory ?? DomainRegistry.AppDataDirectory;
        Directory.CreateDirectory(dir);
        _pemPath = Path.Combine(dir, "flashgithub-ca.crt");
    }

    public string CaPemPath => _pemPath;

    private void EnsurePemExported()
    {
        var pem = _ca.ExportCaPem();
        // 仅在内容变化时写文件
        if (!File.Exists(_pemPath) || File.ReadAllText(_pemPath) != pem)
            File.WriteAllText(_pemPath, pem);
    }

    public bool IsTrusted()
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                EnsurePemExported();
                using var p = Process.Start(new ProcessStartInfo("security", $"verify-cert -c \"{_pemPath}\"")
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                })!;
                p.WaitForExit(5000);
                return p.ExitCode == 0;
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                var thumb = _ca.CaThumbprint;
                if (thumb is null) return false;
                foreach (var location in new[] { StoreLocation.CurrentUser, StoreLocation.LocalMachine })
                {
                    using var store = new X509Store(StoreName.Root, location);
                    store.Open(OpenFlags.ReadOnly);
                    if (store.Certificates.Find(X509FindType.FindByThumbprint, thumb, false).Count > 0)
                        return true;
                }
                return false;
            }

            // Linux: 检查是否安装过（尽力而为）
            return File.Exists("/usr/local/share/ca-certificates/flashgithub-ca.crt");
        }
        catch
        {
            return false;
        }
    }

    /// <summary>安装并信任 CA。先尝试用户级（无需提权），失败则弹出系统授权框做机器级安装。</summary>
    public async Task InstallAsync()
    {
        EnsurePemExported();

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            // 用户级：登录钥匙串，无需管理员密码
            var userKeychain = $"{Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)}/Library/Keychains/login.keychain-db";
            if (await TryRunAsync("security", $"add-trusted-cert -r trustRoot -k \"{userKeychain}\" \"{_pemPath}\""))
            {
                Log.Info("根证书已安装到登录钥匙串（用户级信任）");
                return;
            }

            // 机器级：需要管理员密码
            await PrivilegeService.RunElevatedAsync(
                $"security add-trusted-cert -d -r trustRoot -k /Library/Keychains/System.keychain \"{_pemPath}\"",
                "flashgithub-install-ca");
            Log.Info("根证书已安装到系统钥匙串（管理员信任）");
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            using (var store = new X509Store(StoreName.Root, StoreLocation.CurrentUser))
            {
                store.Open(OpenFlags.ReadWrite);
                var cert = new X509Certificate2(_ca.ExportCaDer());
                if (store.Certificates.Find(X509FindType.FindByThumbprint, cert.Thumbprint, false).Count == 0)
                    store.Add(cert);
            }
            Log.Info("根证书已安装到当前用户受信任的根证书存储");
        }
        else
        {
            await PrivilegeService.RunElevatedAsync(
                $"cp \"{_pemPath}\" /usr/local/share/ca-certificates/flashgithub-ca.crt && update-ca-certificates",
                "flashgithub-install-ca");
            Log.Info("根证书已安装到系统 CA 信任库");
        }
    }

    public async Task UninstallAsync()
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                var userKeychain = $"{Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)}/Library/Keychains/login.keychain-db";
                await TryRunAsync("security", $"delete-certificate -Z \"{_ca.CaThumbprint}\" \"{userKeychain}\"");
                await TryRunAsync("security", $"delete-certificate -Z \"{_ca.CaThumbprint}\" /Library/Keychains/System.keychain");
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                if (_ca.CaThumbprint is not { } thumb) return;
                foreach (var location in new[] { StoreLocation.CurrentUser, StoreLocation.LocalMachine })
                {
                    using var store = new X509Store(StoreName.Root, location);
                    store.Open(OpenFlags.ReadWrite);
                    var found = store.Certificates.Find(X509FindType.FindByThumbprint, thumb, false);
                    foreach (var c in found) store.Remove(c);
                }
            }
            else
            {
                await PrivilegeService.RunElevatedAsync(
                    "rm -f /usr/local/share/ca-certificates/flashgithub-ca.crt && update-ca-certificates",
                    "flashgithub-remove-ca");
            }
            Log.Info("根证书已移除");
        }
        catch (Exception ex)
        {
            Log.Warn($"移除根证书失败：{ex.Message}");
        }
    }

    private static async Task<bool> TryRunAsync(string fileName, string arguments)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(fileName, arguments)
            {
                UseShellExecute = false,
                RedirectStandardError = true,
            })!;
            var err = await p.StandardError.ReadToEndAsync();
            await p.WaitForExitAsync();
            if (p.ExitCode != 0 && !string.IsNullOrWhiteSpace(err))
                Log.Warn($"{fileName} {arguments.Split(' ')[0]} 失败：{err.Trim()}");
            return p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
