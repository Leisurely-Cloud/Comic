using System;
using System.Threading;
using System.Threading.Tasks;
using Comic.WinUI.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Comic.WinUI.ViewModels;

public partial class ShellViewModel : ObservableObject
{
    private readonly BackendClient _backendClient;

    public ShellViewModel(BackendClient backendClient, ApplicationSettingsService applicationSettings)
    {
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
    public async Task EnsureBackendRunningAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var health = await _backendClient.GetHealthAsync(cancellationToken);
            BackendStatus = "内置 C# 服务已就绪";
            StorageRoot = health.StorageRoot;
            HasShellError = false;
            ShellErrorSummary = string.Empty;
        }
        catch (OperationCanceledException)
        {
            BackendStatus = "已取消";
        }
        catch (Exception ex)
        {
            BackendStatus = "初始化失败";
            HasShellError = true;
            ShellErrorSummary = $"内置服务初始化失败: {ex.Message}";
        }
    }

    [RelayCommand]
    public Task RefreshHealthAsync(CancellationToken cancellationToken = default) =>
        EnsureBackendRunningAsync(cancellationToken);
}
