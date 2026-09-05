using System.Runtime.InteropServices;

namespace FlashGithub.Core.Services;

/// <summary>
/// 安装 macOS 特权后台服务（LaunchDaemon）：
/// 1) 把当前程序目录发布/复制到 /Library/Application Support/FlashGithub/helper（root 常驻位置）；
/// 2) 写 /Library/LaunchDaemons/com.flashgithub.helper.plist（KeepAlive 常驻）；
/// 3) launchctl 启动。全程只需一次管理员授权，此后加速不再需要 sudo。
/// </summary>
public static class HelperInstaller
{
    public const string Label = "com.flashgithub.helper";
    public static string HelperDir => "/Library/Application Support/FlashGithub/helper";
    public static string PlistPath => $"/Library/LaunchDaemons/{Label}.plist";

    /// <summary>后台服务命令通道是否就绪。</summary>
    public static bool IsInstalled() =>
        OperatingSystem.IsMacOS() && File.Exists(PlistPath);

    /// <summary>后台服务二进制与当前程序是否一致（不一致说明 daemon 落后于代码，需要更新）。</summary>
    public static bool IsOutdated()
    {
        try
        {
            var exePath = Environment.ProcessPath;
            if (exePath is null || !OperatingSystem.IsMacOS()) return false;
            var stagedExe = Path.Combine(HelperDir, Path.GetFileName(exePath));
            if (!File.Exists(stagedExe)) return true;
            return GetSha256(exePath) != GetSha256(stagedExe);
        }
        catch
        {
            return false;
        }
    }

    private static string GetSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream));
    }

    /// <summary>发布 → 提权安装 → 等待命令通道就绪。失败抛异常。</summary>
    public static async Task InstallAsync()
    {
        if (!OperatingSystem.IsMacOS())
            throw new PlatformNotSupportedException("后台服务仅支持 macOS");

        var dataDir = DomainRegistry.AppDataDirectory.Replace("\"", "\\\"");
        var exePath = Environment.ProcessPath!;
        var exeDir = Path.GetDirectoryName(exePath)!;

        // 框架依赖运行（开发模式）：不做任何复制——部分环境会击杀复制出的二进制，
        // 直接让 launchd 以 dotnet 主机运行原始 dll；自包含（安装版）才复制到系统目录。
        var isSelfContained = File.Exists(Path.Combine(exeDir, "libcoreclr.dylib"));
        string programArgs;
        var dotnetRoot = "";
        if (isSelfContained)
        {
            var staging = await PublishToStagingAsync();
            programArgs = $"<string>{HelperDir}/{Path.GetFileName(exePath)}</string>\\n        <string>--helper</string>";
            dotnetRoot = HelperDir;
        }
        else
        {
            var runtimeDir = RuntimeEnvironment.GetRuntimeDirectory(); // .../shared/Microsoft.NETCore.App/<ver>
            dotnetRoot = Path.GetFullPath(Path.Combine(runtimeDir, "..", "..", ".."));
            var dotnetHost = Path.Combine(dotnetRoot, "dotnet");
            var dllPath = Path.ChangeExtension(exePath, ".dll");
            programArgs = $"<string>{dotnetHost}</string>\\n        <string>{dllPath}</string>\\n        <string>--helper</string>";
        }

        var stdoutLog = Path.Combine(dataDir, "helper-stdout.log");
        var stderrLog = Path.Combine(dataDir, "helper-stderr.log");
        var script = $"""
            cat > "{PlistPath}" <<'PLIST'
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
                <key>Label</key><string>{Label}</string>
                <key>ProgramArguments</key>
                <array>
                {programArgs}
                </array>
                <key>EnvironmentVariables</key>
                <dict>
                    <key>FLASHGITHUB_DATA_DIR</key><string>{dataDir}</string>
                    <key>DOTNET_ROOT</key><string>{dotnetRoot}</string>
                </dict>
                <key>RunAtLoad</key><true/>
                <key>KeepAlive</key><true/>
                <key>StandardOutPath</key><string>{stdoutLog}</string>
                <key>StandardErrorPath</key><string>{stderrLog}</string>
            </dict>
            </plist>
            PLIST
            launchctl bootout system/{Label} 2>/dev/null || true
            launchctl bootstrap system "{PlistPath}"
            sleep 1
            echo INSTALLED
            """;

        await PrivilegeService.RunElevatedAsync(script, "flashgithub-install-helper");

        // 等待命令通道就绪（KeepAlive 拉起需要几秒；被系统安全策略击杀则永远不就绪）
        for (var i = 0; i < 20; i++)
        {
            await Task.Delay(500);
            if (HelperClient.Default.IsAvailable())
            {
                Log.Info("后台服务安装成功，加速不再需要管理员权限");
                return;
            }
        }
        throw new InvalidOperationException(
            "后台服务未能启动（可能被系统安全策略拦截）。可继续使用\"以管理员身份重启\"方式加速，" +
            $"详情见 {HelperDir}/stderr.log");
    }

    /// <summary>卸载后台服务。</summary>
    public static async Task UninstallAsync()
    {
        var script = $"""
            launchctl bootout system/{Label} 2>/dev/null || true
            rm -f "{PlistPath}"
            rm -rf "{HelperDir}"
            echo REMOVED
            """;
        await PrivilegeService.RunElevatedAsync(script, "flashgithub-uninstall-helper");
    }

    /// <summary>把当前程序目录（含运行时依赖）复制到用户可写的暂存目录，供提权脚本搬运。</summary>
    private static async Task<string> PublishToStagingAsync()
    {
        var exePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("无法确定程序路径");
        var srcDir = Path.GetDirectoryName(exePath)!;
        var staging = Path.Combine(DomainRegistry.AppDataDirectory, "helper-stage");

        if (Directory.Exists(staging)) Directory.Delete(staging, true);
        Directory.CreateDirectory(staging);

        foreach (var file in Directory.GetFiles(srcDir))
        {
            if (file.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase)) continue;
            File.Copy(file, Path.Combine(staging, Path.GetFileName(file)), true);
        }

        // 自包含发布有本机原生库（libcoreclr.dylib 等）；框架依赖则需要 dotnet 主机启动 dll
        var isSelfContained = File.Exists(Path.Combine(staging, "libcoreclr.dylib"));
        if (!isSelfContained)
        {
            // 记录 dotnet 主机路径，供 plist 包装启动
            var runtimeDir = RuntimeEnvironment.GetRuntimeDirectory(); // .../shared/Microsoft.NETCore.App/10.0.x
            var dotnetHost = Path.GetFullPath(Path.Combine(runtimeDir, "..", "..", "..", "dotnet"));
            await File.WriteAllTextAsync(Path.Combine(staging, "dotnet-host.txt"), dotnetHost);
        }
        await File.WriteAllTextAsync(Path.Combine(staging, "is-selfcontained.txt"),
            isSelfContained ? "1" : "0");
        return staging;
    }

    /// <summary>按暂存内容生成 plist 的 ProgramArguments 数组 XML。</summary>
    private static string BuildProgramArgumentsXml(string staging)
    {
        var exeName = Path.GetFileName(Environment.ProcessPath)!;
        var stagedExe = $"{HelperDir}/{exeName}";

        var selfContained = File.ReadAllText(Path.Combine(staging, "is-selfcontained.txt")) == "1";
        if (selfContained)
        {
            return $"<string>{stagedExe}</string>\n        <string>--helper</string>";
        }

        var dotnetHost = File.ReadAllText(Path.Combine(staging, "dotnet-host.txt")).Trim();
        return $"<string>{dotnetHost}</string>\n        " +
               $"<string>{HelperDir}/FlashGithub.App.dll</string>\n        <string>--helper</string>";
    }
}
