using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Comic.WinUI.Models;
using Comic.WinUI.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Comic.WinUI.ViewModels;

public partial class SettingsPageViewModel : ObservableObject
{
    private readonly BackendClient _backendClient;
    private readonly BackendSettingsService _backendSettingsService;

    public SettingsPageViewModel(BackendClient backendClient, BackendSettingsService backendSettingsService)
    {
        _backendClient = backendClient;
        _backendSettingsService = backendSettingsService;

        var settings = _backendSettingsService.GetSettings();
        BackendBaseUrl = settings.BackendBaseUrl;
        PythonExecutablePath = settings.PythonExecutablePath;
        PythonArguments = settings.PythonArguments;
        WorkingDirectory = settings.WorkingDirectory;
    }

    [ObservableProperty]
    public partial string BackendBaseUrl { get; set; } = "http://127.0.0.1:18765/";

    [ObservableProperty]
    public partial string PythonExecutablePath { get; set; } = "python";

    [ObservableProperty]
    public partial string PythonArguments { get; set; } = "-m backend.api";

    [ObservableProperty]
    public partial string WorkingDirectory { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string StorageRoot { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string LegacyRoot { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool DownloadRunnerConfigured { get; set; }

    public string DownloadRunnerStatus => DownloadRunnerConfigured ? "已配置" : "未配置";

    [ObservableProperty]
    public partial string SupportedSites { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string SettingsError { get; set; } = string.Empty;

    partial void OnDownloadRunnerConfiguredChanged(bool value)
    {
        OnPropertyChanged(nameof(DownloadRunnerStatus));
    }

    [RelayCommand]
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var runtime = _backendSettingsService.GetSettings();
            BackendBaseUrl = runtime.BackendBaseUrl;
            PythonExecutablePath = runtime.PythonExecutablePath;
            PythonArguments = runtime.PythonArguments;
            WorkingDirectory = runtime.WorkingDirectory;

            _backendClient.SetBaseAddress(BackendBaseUrl);
            var settings = await _backendClient.GetSettingsAsync(cancellationToken);
            StorageRoot = settings.StorageRoot;
            LegacyRoot = settings.LegacyRoot;
            DownloadRunnerConfigured = settings.DownloadRunnerConfigured;
            SupportedSites = string.Join(", ", settings.SupportedSites.Select(SiteCatalog.GetDisplayName));
            SettingsError = string.Empty;
        }
        catch (OperationCanceledException)
        {
            // swallowed
        }
        catch (BackendApiException ex)
        {
            SettingsError = $"加载设置失败: {ex.Error.Message}";
        }
        catch (HttpRequestException)
        {
            SettingsError = "无法连接后端服务，请确认后端已启动。";
        }
        catch (Exception ex)
        {
            SettingsError = $"加载设置异常: {ex.Message}";
        }
    }

    [RelayCommand]
    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            _backendClient.SetBaseAddress(BackendBaseUrl);
            var runtime = new BackendRuntimeSettings
            {
                BackendBaseUrl = BackendBaseUrl,
                PythonExecutablePath = PythonExecutablePath,
                PythonArguments = PythonArguments,
                WorkingDirectory = WorkingDirectory,
            };
            _backendSettingsService.SaveSettings(runtime);
            try
            {
                await _backendClient.UpdateSettingsAsync(new
                {
                    pythonExecutablePath = PythonExecutablePath,
                    pythonArguments = PythonArguments,
                    workingDirectory = WorkingDirectory,
                }, cancellationToken);
                SettingsError = string.Empty;
            }
            catch (OperationCanceledException)
            {
                // swallowed
            }
            catch (BackendApiException ex)
            {
                SettingsError = $"本地设置已保存，但后端未同步: {ex.Error.Message}";
                return;
            }
            catch (HttpRequestException)
            {
                SettingsError = "本地设置已保存，但后端未同步: 无法连接后端服务。";
                return;
            }
            catch (Exception ex)
            {
                SettingsError = $"本地设置已保存，但后端未同步: {ex.Message}";
                return;
            }

            SettingsError = string.Empty;
        }
        catch (OperationCanceledException)
        {
            // swallowed
        }
        catch (BackendApiException ex)
        {
            SettingsError = $"保存失败: {ex.Error.Message}";
        }
        catch (HttpRequestException)
        {
            SettingsError = "保存失败: 无法连接后端服务。";
        }
        catch (Exception ex)
        {
            SettingsError = $"保存异常: {ex.Message}";
        }
    }

    [RelayCommand]
    public async Task RefreshBackendInfoAsync(CancellationToken cancellationToken = default)
    {
        await LoadAsync(cancellationToken);
    }
}
