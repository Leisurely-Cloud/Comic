using System;
using System.Threading;
using System.Threading.Tasks;
using Comic.WinUI.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Comic.WinUI.ViewModels;

public partial class ShellViewModel : ObservableObject
{
    private readonly ApplicationSettingsService _applicationSettings;
    private readonly BackendClient? _backendClient;

    public ShellViewModel(ApplicationSettingsService applicationSettings, BackendClient? backendClient = null)
    {
        _applicationSettings = applicationSettings;
        _backendClient = backendClient;
        IsNavigationPaneOpen = applicationSettings.ExpandNavigationPane;
    }

    [ObservableProperty]
    public partial bool IsNavigationPaneOpen { get; set; }

    [ObservableProperty]
    public partial string BackendStatus { get; set; } = "内置服务准备中";

    [ObservableProperty]
    public partial string StorageRoot { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool HasShellError { get; set; }

    [ObservableProperty]
    public partial string ShellErrorSummary { get; set; } = string.Empty;

    [RelayCommand]
    public Task EnsureBackendRunningAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        BackendStatus = "内置 C# 服务已就绪";
        StorageRoot = _applicationSettings.StorageRoot;
        HasShellError = false;
        ShellErrorSummary = string.Empty;
        if (_backendClient?.HasSavedJmLogin == true) _ = RestoreJmLoginInBackgroundAsync();
        return Task.CompletedTask;
    }

    private async Task RestoreJmLoginInBackgroundAsync()
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await _backendClient!.RestoreJmLoginAsync(timeout.Token);
        }
        catch
        {
            // 自动登录失败不阻塞主页面；进入收藏夹或设置时可再次重试并显示错误。
        }
    }

    [RelayCommand]
    public Task RefreshHealthAsync(CancellationToken cancellationToken = default) =>
        EnsureBackendRunningAsync(cancellationToken);
}
