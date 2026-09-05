using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using FlashGithub.App.ViewModels;
using FlashGithub.App.Views;

namespace FlashGithub.App;

public partial class App : Application
{
    private static MainViewModel? _vm;
    private static bool _forceExit;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _vm = new MainViewModel();
            desktop.MainWindow = new MainWindow { DataContext = _vm };

            // 点关闭按钮 → 隐藏到托盘；真正退出走托盘菜单
            desktop.MainWindow.Closing += (_, e) =>
            {
                if (_forceExit) return;
                e.Cancel = true;
                desktop.MainWindow.Hide();
            };

            desktop.ShutdownRequested += async (_, e) =>
            {
                if (!_forceExit)
                {
                    e.Cancel = true;
                    return;
                }
                await _vm.ExitAsync();
            };

            SetupTrayIcon(desktop);
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static void SetupTrayIcon(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var window = desktop.MainWindow!;
        var tray = new TrayIcon
        {
            Icon = window.Icon,
            ToolTipText = "FlashGithub — GitHub 网络加速",
            IsVisible = true,
        };

        var menu = new NativeMenu();

        var show = new NativeMenuItem("显示主窗口");
        show.Click += (_, _) =>
        {
            window.Show();
            window.WindowState = WindowState.Normal;
            window.Activate();
        };
        menu.Add(show);

        var toggle = new NativeMenuItem("开启 / 关闭加速") { Command = _vm!.ToggleAccelerationCommand };
        menu.Add(toggle);

        menu.Add(new NativeMenuItemSeparator());

        var exit = new NativeMenuItem("退出");
        exit.Click += (_, _) =>
        {
            _forceExit = true;
            window.Close();
            desktop.Shutdown();
        };
        menu.Add(exit);

        tray.Menu = menu;
        Application.Current!.SetValue(TrayIcon.IconsProperty, new TrayIcons { tray });
    }
}
