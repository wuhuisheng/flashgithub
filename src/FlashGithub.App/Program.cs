using Avalonia;
using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace FlashGithub.App;

sealed class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static int Main(string[] args)
    {
        // 特权后台服务模式（LaunchDaemon 以 root 常驻）：不初始化 GUI，不参与单实例控制
        if (args.Contains("--helper"))
        {
            FlashGithub.Core.Services.HelperDaemon.RunAsync().GetAwaiter().GetResult();
            return 0;
        }

        // 无界面模式（Linux 服务器 / 无头环境）：不初始化 GUI，不需要显示器
        if (args.Contains("--cli"))
        {
            return FlashGithub.Core.Services.HeadlessRunner.RunAsync(args)
                .GetAwaiter().GetResult();
        }

        // 单实例：已有实例时唤起其窗口后退出
        SingleInstance.Initialize();
        if (!SingleInstance.IsFirst)
        {
            Console.WriteLine("FlashGithub 已在运行，已唤起其窗口");
            return 0;
        }

        WaitUntilDisplayAvailableOnMac();
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return 0;
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();

    // macOS：Avalonia 的渲染定时器依赖 CVDisplayLink，系统没有活动显示器时会直接崩溃
    // （native error -6661，参见 AvaloniaUI/Avalonia#18895）。启动前先探测，等显示器接入。
    private static void WaitUntilDisplayAvailableOnMac()
    {
        if (!OperatingSystem.IsMacOS()) return;

        const int kCVReturnSuccess = 0;
        var reported = false;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        while (true)
        {
            var rc = CVDisplayLinkCreateWithActiveCGDisplays(out var link);
            if (rc == kCVReturnSuccess)
            {
                CVDisplayLinkRelease(link);
                return;
            }

            if (!reported)
            {
                Console.WriteLine("未检测到活动显示器（无头运行时 Avalonia 无法启动界面）。");
                Console.WriteLine("接入显示器后将自动继续启动，最长等待 5 分钟…");
                reported = true;
            }

            if (sw.Elapsed.TotalMinutes >= 5)
            {
                Console.Error.WriteLine("等待显示器超时，程序退出。请接入显示器后重新启动。");
                Environment.Exit(1);
            }

            Thread.Sleep(1000);
        }
    }

    [DllImport("/System/Library/Frameworks/CoreVideo.framework/CoreVideo")]
    private static extern int CVDisplayLinkCreateWithActiveCGDisplays(out IntPtr displayLinkOut);

    [DllImport("/System/Library/Frameworks/CoreVideo.framework/CoreVideo")]
    private static extern void CVDisplayLinkRelease(IntPtr displayLink);
}
