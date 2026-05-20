using System;
using System.Threading;
using System.Threading.Tasks;
using Comic.WinUI.Models;
using Comic.WinUI.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Comic.WinUI.ViewModels;

public partial class ShellViewModel : ObservableObject
{
    private readonly BackendClient _backendClient;
    private readonly BackendProcessService _backendProcessService;
    private readonly BackendSettingsService _backendSettingsService;

    public ShellViewModel(
        BackendClient backendClient,
        BackendProcessService backendProcessService,
        BackendSettingsService backendSettingsService)
    {
        _backendClient = backendClient;
        _backendProcessService = backendProcessService;
        _backendSettingsService = backendSettingsService;
    }

    [ObservableProperty]
    public partial string BackendStatus { get; set; } = "未连接";

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
            ApplyConfiguredBaseAddress();
            var health = await TryGetHealthAsync(cancellationToken);
            if (health is null)
            {
                BackendStatus = "启动中";
                await _backendProcessService.StartAsync(cancellationToken);
                health = await WaitForHealthAsync(cancellationToken);
            }

            BackendStatus = $"已连接 ({health.Status}, PID {health.Pid})";
            StorageRoot = health.StorageRoot;
            HasShellError = false;
            ShellErrorSummary = string.Empty;
        }
        catch (OperationCanceledException)
        {
            BackendStatus = "已取消";
        }
        catch (BackendApiException ex)
        {
            BackendStatus = "连接失败";
            HasShellError = true;
            ShellErrorSummary = $"后端连接失败: {ex.Error.Message}";
        }
        catch (HttpRequestException)
        {
            BackendStatus = "连接失败";
            HasShellError = true;
            ShellErrorSummary = "无法连接后端服务，请确认后端已启动。";
        }
        catch (Exception ex)
        {
            BackendStatus = "连接失败";
            HasShellError = true;
            ShellErrorSummary = $"后端连接异常: {ex.Message}";
        }
    }

    [RelayCommand]
    public async Task RefreshHealthAsync(CancellationToken cancellationToken = default)
    {
        await EnsureBackendRunningAsync(cancellationToken);
    }

    [RelayCommand]
    public async Task StopBackendAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            ApplyConfiguredBaseAddress();
            var health = await TryGetHealthAsync(cancellationToken);
            await _backendProcessService.StopAsync(health?.Pid, cancellationToken);
            BackendStatus = "已停止";
            HasShellError = false;
            ShellErrorSummary = string.Empty;
        }
        catch (OperationCanceledException)
        {
            // swallowed
        }
        catch (BackendApiException ex)
        {
            HasShellError = true;
            ShellErrorSummary = $"停止后端失败: {ex.Error.Message}";
        }
        catch (HttpRequestException)
        {
            HasShellError = true;
            ShellErrorSummary = "无法连接后端服务，请确认后端已启动。";
        }
        catch (Exception ex)
        {
            HasShellError = true;
            ShellErrorSummary = $"停止后端异常: {ex.Message}";
        }
    }

    private void ApplyConfiguredBaseAddress()
    {
        var settings = _backendSettingsService.GetSettings();
        _backendClient.SetBaseAddress(settings.BackendBaseUrl);
    }

    private async Task<HealthResponse?> TryGetHealthAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _backendClient.GetHealthAsync(cancellationToken);
        }
        catch
        {
            return null;
        }
    }

    private async Task<HealthResponse> WaitForHealthAsync(CancellationToken cancellationToken)
    {
        Exception? lastError = null;
        for (var attempt = 0; attempt < 40; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return await _backendClient.GetHealthAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                lastError = ex;
                await Task.Delay(250, cancellationToken);
            }
        }

        throw lastError ?? new InvalidOperationException("后端未在预期时间内启动。");
    }
}
