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

    public ShellViewModel(ApplicationSettingsService applicationSettings)
    {
        _applicationSettings = applicationSettings;
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
        return Task.CompletedTask;
    }

    [RelayCommand]
    public Task RefreshHealthAsync(CancellationToken cancellationToken = default) =>
        EnsureBackendRunningAsync(cancellationToken);
}
