using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FlashGithub.Core;
using FlashGithub.Core.Services;

namespace FlashGithub.App.ViewModels;

/// <summary>界面中的域名行。</summary>
public partial class DomainItemViewModel : ObservableObject
{
    private readonly Action<DomainItemViewModel, bool> _setEnabled;
    private readonly Action<DomainItemViewModel> _remove;

    public DomainItemViewModel(DomainConfig config,
        Action<DomainItemViewModel, bool> setEnabled,
        Action<DomainItemViewModel> remove)
    {
        Domain = config.Domain;
        IsBuiltIn = config.IsBuiltIn;
        _setEnabled = setEnabled;
        _remove = remove;
        _isEnabled = config.Enabled;
        UpdateLatency(config.LatencyMs);
    }

    public string Domain { get; }
    public bool IsBuiltIn { get; }
    public bool CanRemove => !IsBuiltIn;

    [ObservableProperty]
    private bool _isEnabled;

    partial void OnIsEnabledChanged(bool value) => _setEnabled(this, value);

    [ObservableProperty]
    private string _latencyText = "…";

    public void UpdateLatency(int? ms) =>
        LatencyText = ms is null ? "不可达" : $"{ms} ms";
}

public partial class MainViewModel : ViewModelBase
{
    private readonly AccelerationEngine _engine;

    public ObservableCollection<DomainItemViewModel> Domains { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActionButtonText))]
    private bool _isAccelerating;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusText = "未加速";

    [ObservableProperty]
    private string _certStatusText = "检测中…";

    [ObservableProperty]
    private string _logText = "";

    [ObservableProperty]
    private string _newDomain = "";

    [ObservableProperty]
    private bool _needsElevation;

    public string ActionButtonText => IsAccelerating ? "关闭加速" : "一键加速";

    public MainViewModel(AccelerationEngine? engine = null)
    {
        _engine = engine ?? new AccelerationEngine();

        Log.OnMessage += line => Dispatcher.UIThread.Post(() =>
        {
            LogText += line + "\n";
        });

        _engine.LatencyUpdated += (domain, ms) => Dispatcher.UIThread.Post(() =>
        {
            Domains.FirstOrDefault(d => d.Domain == domain)?.UpdateLatency(ms);
            if (ms is not null && _engine.Registry.Domains
                    .FirstOrDefault(c => c.Domain == domain) is { } config)
                config.LatencyMs = ms;
        });

        _engine.Registry.Changed += () => Dispatcher.UIThread.Post(ReloadDomains);

        ReloadDomains();
        PrepareAsync();
    }

    private void ReloadDomains()
    {
        Domains.Clear();
        foreach (var config in _engine.Registry.Domains)
        {
            Domains.Add(new DomainItemViewModel(config,
                (i, enabled) => _engine.Registry.SetEnabled(i.Domain, enabled),
                i => _engine.Registry.Remove(i.Domain)));
        }
    }

    private async void PrepareAsync()
    {
        try
        {
            await Task.Run(_engine.PrepareCertificates);
            await UpdateCertStatusAsync();
        }
        catch (Exception ex)
        {
            Log.Error($"初始化证书失败：{ex.Message}");
        }
    }

    private async Task UpdateCertStatusAsync()
    {
        var trusted = await Task.Run(_engine.IsCertificateTrusted);
        CertStatusText = trusted ? "本地根证书已受信任" : "本地根证书尚未安装";
    }

    [RelayCommand]
    private Task ToggleAccelerationAsync() => IsAccelerating ? StopAccelerationAsync() : StartAccelerationAsync();

    private async Task StartAccelerationAsync()
    {
        IsBusy = true;
        StatusText = "正在开启加速…";
        try
        {
            await Task.Run(_engine.StartAsync);
            IsAccelerating = true;
            StatusText = "加速已开启 ✓";
        }
        catch (Exception ex)
        {
            StatusText = "开启失败";
            Log.Error(ex.Message);
            NeedsElevation = _engine.NeedsElevation;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task StopAccelerationAsync()
    {
        IsBusy = true;
        StatusText = "正在关闭加速…";
        try
        {
            await Task.Run(_engine.StopAsync);
        }
        catch (Exception ex)
        {
            Log.Error($"关闭加速出错：{ex.Message}");
        }
        IsAccelerating = false;
        StatusText = "未加速";
        IsBusy = false;
    }

    /// <summary>退出前调用：关闭加速以还原 hosts。</summary>
    public Task ExitAsync() => StopAccelerationAsync();

    [RelayCommand]
    private async Task InstallCertificateAsync()
    {
        IsBusy = true;
        try
        {
            await Task.Run(_engine.InstallCertificateAsync);
        }
        catch (Exception ex)
        {
            Log.Error($"安装证书失败：{ex.Message}");
        }
        await UpdateCertStatusAsync();
        IsBusy = false;
    }

    [RelayCommand]
    private async Task RefreshLatencyAsync()
    {
        IsBusy = true;
        try
        {
            await Task.Run(_engine.RefreshLatencyAsync);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void AddDomain()
    {
        if (string.IsNullOrWhiteSpace(NewDomain)) return;
        var name = NewDomain.Trim();
        if (_engine.Registry.Add(name))
        {
            Log.Info($"已添加自定义域名：{name}");
            NewDomain = "";
            ReloadDomains();
        }
        else
        {
            Log.Warn($"“{name}”不是有效域名或已存在");
        }
    }

    [RelayCommand]
    private void RemoveDomain(DomainItemViewModel? item)
    {
        if (item is null || !item.CanRemove) return;
        _engine.Registry.Remove(item.Domain);
        Log.Info($"已移除自定义域名：{item.Domain}");
        ReloadDomains();
    }

    [RelayCommand]
    private void EnableAllDomains()
    {
        _engine.Registry.SetAllEnabled(true);
        ReloadDomains();
    }

    [RelayCommand]
    private void DisableAllDomains()
    {
        _engine.Registry.SetAllEnabled(false);
        ReloadDomains();
    }

    [RelayCommand]
    private async Task RestartElevatedAsync()
    {
        try
        {
            await PrivilegeService.RelaunchElevatedAsync();
            await ExitAsync();
            Environment.Exit(0);
        }
        catch (Exception ex)
        {
            Log.Error($"以管理员身份重启失败：{ex.Message}");
        }
    }
}
