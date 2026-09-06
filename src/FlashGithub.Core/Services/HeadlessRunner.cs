using FlashGithub.Core;

namespace FlashGithub.Core.Services;

/// <summary>
/// 无界面模式（Linux 服务器 / macOS 无头环境）：
/// 直接运行核心加速引擎，控制台输出日志，Ctrl+C 停止加速并还原 hosts。
/// 用法: FlashGithub.App --cli [启动加速]   /   FlashGithub.App --cli --stop [仅还原 hosts]
/// </summary>
public static class HeadlessRunner
{
    public static async Task<int> RunAsync(string[] args)
    {
        await using var engine = new AccelerationEngine();
        Log.OnMessage += m => Console.WriteLine(m);

        Console.WriteLine("""
                FlashGithub 无界面模式
                用法: --cli 启动加速（Ctrl+C 停止并还原 hosts）; --cli --stop 仅还原 hosts
                """);

        if (args.Contains("--stop"))
        {
            try
            {
                await engine.Hosts.RemoveAsync();
                Console.WriteLine("hosts 已还原");
                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"还原失败：{ex.Message}");
                return 1;
            }
        }

        try
        {
            engine.PrepareCertificates();
            if (!engine.IsCertificateTrusted())
            {
                Console.WriteLine("本地根证书未信任，正在安装（服务器需 root）…");
                await engine.InstallCertificateAsync();
            }

            await engine.StartAsync();
            Console.WriteLine("加速运行中，按 Ctrl+C 停止并还原 hosts");
        }
        catch (Exception ex)
        {
            Log.Error(ex.Message);
            return 1;
        }

        var exit = new TaskCompletionSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            exit.TrySetResult();
        };
        await exit.Task;

        Console.WriteLine("正在停止加速并还原 hosts…");
        await engine.StopAsync();
        return 0;
    }
}
