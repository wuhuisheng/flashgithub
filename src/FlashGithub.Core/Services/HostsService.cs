using System.Runtime.InteropServices;
using System.Text;

namespace FlashGithub.Core.Services;

/// <summary>
/// hosts 文件管理：以标记块（BEGIN/END FlashGithub）的形式写入/清除 127.0.0.1 域名映射。
/// 修改 hosts 需要管理员权限，通过 PrivilegeService 提权完成；本进程读写内容均为普通权限。
/// </summary>
public sealed class HostsService
{
    public const string BeginMarker = "# BEGIN FlashGithub";
    public const string EndMarker = "# END FlashGithub";

    public static string HostsPath =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                "drivers", "etc", "hosts")
            : "/etc/hosts";

    private static readonly string[] Markers = [BeginMarker, EndMarker];

    /// <summary>读取系统 hosts 中当前生效的 FlashGithub 域名条目。</summary>
    public List<string> GetCurrentEntries()
    {
        try
        {
            if (!File.Exists(HostsPath)) return [];
            var inside = false;
            var entries = new List<string>();
            foreach (var raw in File.ReadAllLines(HostsPath))
            {
                var line = raw.Trim();
                if (line.StartsWith(EndMarker, StringComparison.Ordinal)) break;
                if (inside && !line.StartsWith("#") && line.Length > 0)
                {
                    var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2) entries.Add(parts[1]);
                }
                if (line.StartsWith(BeginMarker, StringComparison.Ordinal)) inside = true;
            }
            return entries;
        }
        catch
        {
            return [];
        }
    }

    /// <summary>判断给定启用域名集合是否与 hosts 中的条目一致。</summary>
    public bool IsApplied(IReadOnlyCollection<string> enabledDomains)
    {
        var current = GetCurrentEntries();
        return current.Count == enabledDomains.Count
            && current.All(d => enabledDomains.Contains(d, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>写入 hosts 加速条目（弹提权授权）。与现有条目一致时不重复写入。</summary>
    public async Task ApplyAsync(IReadOnlyCollection<string> enabledDomains)
    {
        if (IsApplied(enabledDomains))
        {
            Log.Info("hosts 中的加速条目已是最新，无需修改");
            return;
        }

        var newContent = BuildHostsContent(File.Exists(HostsPath) ? await File.ReadAllLinesAsync(HostsPath) : [], enabledDomains);
        var tmp = Path.Combine(Path.GetTempPath(), "flashgithub-hosts");
        await File.WriteAllTextAsync(tmp, newContent, new UTF8Encoding(false));

        await PrivilegeService.RunElevatedAsync(BuildCopyScript(tmp), "flashgithub-write-hosts");
        Log.Info($"已写入 hosts：{enabledDomains.Count} 个域名指向 127.0.0.1");
    }

    /// <summary>清除 hosts 中的加速条目。</summary>
    public async Task RemoveAsync()
    {
        var current = GetCurrentEntries();
        if (current.Count == 0) return;

        var newContent = BuildHostsContent(File.Exists(HostsPath) ? await File.ReadAllLinesAsync(HostsPath) : [], []);
        var tmp = Path.Combine(Path.GetTempPath(), "flashgithub-hosts");
        await File.WriteAllTextAsync(tmp, newContent, new UTF8Encoding(false));

        await PrivilegeService.RunElevatedAsync(BuildCopyScript(tmp), "flashgithub-write-hosts");
        Log.Info("已从 hosts 中移除加速条目");
    }

    private static string BuildHostsContent(string[] originalLines, IReadOnlyCollection<string> domains)
    {
        var sb = new StringBuilder();
        var inside = false;
        foreach (var raw in originalLines)
        {
            var line = raw.TrimEnd();
            if (line.Trim().Equals(BeginMarker, StringComparison.Ordinal)) { inside = true; continue; }
            if (line.Trim().Equals(EndMarker, StringComparison.Ordinal)) { inside = false; continue; }
            if (inside) continue;

            // 块外与受管域名冲突的旧条目一并清除（否则先匹配生效会绕过本地代理）
            if (IsManagedEntry(line, domains)) continue;
            sb.AppendLine(line);
        }
        // 去掉文件尾部多余空行后再追加
        while (sb.Length > 0 && (sb[^1] == '\n' || sb[^1] == '\r'))
            sb.Length--;

        if (domains.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine(BeginMarker);
            // hosts 文件必须保持纯 ASCII：非 ASCII 注释会导致 mDNSResponder 拒绝整个文件
            sb.AppendLine("# Managed by FlashGithub. Do not edit this block.");
            foreach (var domain in domains)
                sb.AppendLine($"127.0.0.1 {domain}");
            sb.Append(EndMarker);
        }
        sb.Append('\n');
        return sb.ToString();
    }

    private static bool IsManagedEntry(string line, IReadOnlyCollection<string> domains)
    {
        var parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 2
            && parts[0] == "127.0.0.1"
            && domains.Contains(parts[1], StringComparer.OrdinalIgnoreCase);
    }

    private static string BuildCopyScript(string tmpPath)
    {
        var hosts = HostsPath;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var psHosts = hosts.Replace("\\", "\\\\");
            return $"""
                    Copy-Item "{hosts}" "{hosts}.flashgithub.bak" -Force
                    Copy-Item "{tmpPath}" "{hosts}" -Force
                    """;
        }
        return $"""
                cp "{hosts}" "{hosts}.flashgithub.bak" 2>/dev/null || true
                cp "{tmpPath}" "{hosts}"
                """;
    }
}
