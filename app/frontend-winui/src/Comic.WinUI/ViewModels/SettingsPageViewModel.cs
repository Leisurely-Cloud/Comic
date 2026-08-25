using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
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
    private readonly ApplicationSettingsService _applicationSettings;
    private readonly ShellViewModel _shellViewModel;
    private readonly SearchHistoryService _searchHistoryService;

    public SettingsPageViewModel(
        BackendClient backendClient,
        ApplicationSettingsService applicationSettings,
        ShellViewModel shellViewModel,
        SearchHistoryService searchHistoryService)
    {
        _backendClient = backendClient;
        _applicationSettings = applicationSettings;
        _shellViewModel = shellViewModel;
        _searchHistoryService = searchHistoryService;
    }

    public IReadOnlyList<SettingOption> ThemeOptions { get; } =
    [
        new(ApplicationSettingsService.SystemTheme, "跟随系统"),
        new(ApplicationSettingsService.LightTheme, "浅色"),
        new(ApplicationSettingsService.DarkTheme, "深色"),
    ];

    public IReadOnlyList<SettingOption> ChapterSelectionOptions { get; } =
    [
        new(ApplicationSettingsService.SelectNone, "默认不选择"),
        new(ApplicationSettingsService.SelectLatest, "默认选择最新一章"),
        new(ApplicationSettingsService.SelectAll, "默认全选"),
    ];

    public IReadOnlyList<SettingOption> DownloadDirectoryLayoutOptions { get; } =
    [
        new(ApplicationSettingsService.DirectoryLayoutOrganized, "应用整理格式"),
        new(ApplicationSettingsService.DirectoryLayoutJmCompatible, "JM 兼容格式"),
    ];

    public IReadOnlyList<SettingOption> ReaderModeOptions { get; } =
    [
        new(ApplicationSettingsService.ReaderPaged, "单页模式"),
        new(ApplicationSettingsService.ReaderStrip, "条漫模式"),
    ];

    public IReadOnlyList<SettingOption> LibraryPageSizeOptions { get; } =
    [
        new("10", "每页 10 部"),
        new("20", "每页 20 部"),
        new("30", "每页 30 部"),
        new("50", "每页 50 部"),
    ];

    public IReadOnlyList<SettingOption> ConcurrencyOptions { get; } =
    [
        new("1", "1 张"),
        new("2", "2 张"),
        new("3", "3 张"),
        new("4", "4 张"),
        new("5", "5 张"),
        new("6", "6 张"),
        new("8", "8 张"),
    ];

    public IReadOnlyList<SettingOption> ChapterRetryOptions { get; } =
    [
        new("1", "1 次"),
        new("2", "2 次"),
        new("3", "3 次"),
        new("4", "4 次"),
        new("5", "5 次"),
    ];

    [ObservableProperty]
    public partial string StorageRoot { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string BackendType { get; set; } = "C# 进程内服务";

    [ObservableProperty]
    public partial string SupportedSites { get; set; } = SiteCatalog.DisplayName;

    [ObservableProperty]
    public partial string SettingsError { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string SaveStatus { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsSaving { get; set; }

    [ObservableProperty]
    public partial string JmUsername { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsJmLoggedIn { get; set; }

    [ObservableProperty]
    public partial bool IsJmLoggingIn { get; set; }

    [ObservableProperty]
    public partial bool RememberJmLogin { get; set; } = true;

    [ObservableProperty]
    public partial string JmAccountDisplayName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string JmAccountSummary { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string JmLoginStatus { get; set; } = string.Empty;

    public bool ShowJmLoginForm => !IsJmLoggedIn;

    partial void OnIsJmLoggedInChanged(bool value) => OnPropertyChanged(nameof(ShowJmLoginForm));

    [ObservableProperty]
    public partial SettingOption? SelectedTheme { get; set; }

    [ObservableProperty]
    public partial SettingOption? SelectedChapterSelection { get; set; }

    [ObservableProperty]
    public partial bool ExpandNavigationPane { get; set; }

    public string NavigationPaneStateText => ExpandNavigationPane ? "开" : "关";

    partial void OnExpandNavigationPaneChanged(bool value) =>
        OnPropertyChanged(nameof(NavigationPaneStateText));

    [ObservableProperty]
    public partial SettingOption? SelectedReaderMode { get; set; }

    [ObservableProperty]
    public partial double DefaultStripZoomPercent { get; set; } = ApplicationSettingsService.DefaultStripZoom;

    [ObservableProperty]
    public partial SettingOption? SelectedLibraryPageSize { get; set; }

    [ObservableProperty]
    public partial SettingOption? SelectedConcurrency { get; set; }

    [ObservableProperty]
    public partial SettingOption? SelectedChapterRetryCount { get; set; }

    [ObservableProperty]
    public partial SettingOption? SelectedDownloadDirectoryLayout { get; set; }

    public string DefaultStripZoomText => $"{DefaultStripZoomPercent:0}%";

    partial void OnDefaultStripZoomPercentChanged(double value) =>
        OnPropertyChanged(nameof(DefaultStripZoomText));

    [RelayCommand]
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var settings = await _backendClient.GetSettingsAsync(cancellationToken);
            StorageRoot = settings.StorageRoot;
            SupportedSites = SiteCatalog.DisplayName;
            SelectedTheme = ThemeOptions.First(option => option.Key == _applicationSettings.Theme);
            SelectedChapterSelection = ChapterSelectionOptions.First(
                option => option.Key == _applicationSettings.ChapterSelectionMode);
            ExpandNavigationPane = _applicationSettings.ExpandNavigationPane;
            SelectedReaderMode = ReaderModeOptions.First(option => option.Key == _applicationSettings.DefaultReaderMode);
            DefaultStripZoomPercent = _applicationSettings.DefaultStripZoomPercent;
            SelectedLibraryPageSize = LibraryPageSizeOptions.First(
                option => option.Key == _applicationSettings.LibraryPageSize.ToString());
            SelectedConcurrency = ConcurrencyOptions.FirstOrDefault(
                option => option.Key == _applicationSettings.DownloadConcurrency.ToString())
                ?? ConcurrencyOptions.First(option => option.Key == "3");
            SelectedChapterRetryCount = ChapterRetryOptions.FirstOrDefault(
                option => option.Key == _applicationSettings.ChapterRetryCount.ToString())
                ?? ChapterRetryOptions.First(option => option.Key == "3");
            SelectedDownloadDirectoryLayout = DownloadDirectoryLayoutOptions.First(
                option => option.Key == _applicationSettings.DownloadDirectoryLayout);
            SettingsError = string.Empty;
            SaveStatus = string.Empty;
            var accountState = await _backendClient.RestoreJmLoginAsync(cancellationToken);
            ApplyAccountState(accountState);
            RememberJmLogin = _backendClient.HasSavedJmLogin || !accountState.IsLoggedIn;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            SettingsError = $"加载设置失败: {ex.Message}";
        }
    }

    public async Task LoginJmAsync(string password, CancellationToken cancellationToken = default)
    {
        IsJmLoggingIn = true;
        SettingsError = string.Empty;
        JmLoginStatus = string.Empty;
        try
        {
            var account = await _backendClient.LoginJmAsync(
                JmUsername,
                password,
                RememberJmLogin,
                cancellationToken);
            ApplyAccountState(new JmAccountState { IsLoggedIn = true, Account = account });
            JmLoginStatus = RememberJmLogin && _backendClient.HasSavedJmLogin
                ? "登录成功，已通过 Windows 凭据库保持登录。"
                : "登录成功，会话仅在本次运行期间有效。";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            SettingsError = $"JM 登录失败: {ex.Message}";
        }
        finally
        {
            IsJmLoggingIn = false;
        }
    }

    [RelayCommand]
    private void LogoutJm()
    {
        _backendClient.LogoutJm();
        ApplyAccountState(new JmAccountState());
        JmLoginStatus = "已退出 JM 账号。";
    }

    private void ApplyAccountState(JmAccountState state)
    {
        IsJmLoggedIn = state.IsLoggedIn;
        var account = state.Account;
        JmAccountDisplayName = account?.Username ?? string.Empty;
        JmAccountSummary = account is null
            ? string.Empty
            : string.Join(" · ", new[]
            {
                string.IsNullOrWhiteSpace(account.LevelName) ? null : account.LevelName,
                $"收藏 {account.FavoriteCount}",
                $"金币 {account.Coin}",
            }.Where(value => !string.IsNullOrWhiteSpace(value)));
    }

    [RelayCommand]
    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        IsSaving = true;
        SettingsError = string.Empty;
        SaveStatus = string.Empty;
        try
        {
            // 1. 保存所有常规设置(主题/分页/阅读器/下载参数),不受下载任务状态影响。
            _applicationSettings.UpdatePreferences(
                SelectedTheme?.Key ?? ApplicationSettingsService.SystemTheme,
                SelectedChapterSelection?.Key ?? ApplicationSettingsService.SelectNone,
                ExpandNavigationPane,
                SelectedReaderMode?.Key ?? ApplicationSettingsService.ReaderPaged,
                (int)Math.Round(DefaultStripZoomPercent),
                int.TryParse(SelectedLibraryPageSize?.Key, out var pageSize) ? pageSize : 20,
                int.TryParse(SelectedConcurrency?.Key, out var concurrency) ? concurrency : 3,
                int.TryParse(SelectedChapterRetryCount?.Key, out var retryCount) ? retryCount : 3,
                SelectedDownloadDirectoryLayout?.Key ?? ApplicationSettingsService.DirectoryLayoutOrganized);

            // 2. 尝试更新下载目录。存在未结束的下载任务时会被拒绝,
            //    单独提示即可,不能因为目录更新失败而丢失其他设置。
            try
            {
                var settings = await _backendClient.UpdateSettingsAsync(
                    new SettingsUpdateRequest { StorageRoot = StorageRoot },
                    cancellationToken);
                StorageRoot = settings.StorageRoot;
                _shellViewModel.StorageRoot = StorageRoot;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                SettingsError = $"下载目录未更新: {ex.Message}";
            }

            _shellViewModel.IsNavigationPaneOpen = ExpandNavigationPane;
            SaveStatus = "设置已保存并立即生效。";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            SettingsError = $"保存设置失败: {ex.Message}";
        }
        finally
        {
            IsSaving = false;
        }
    }

    [RelayCommand]
    private void Reset()
    {
        StorageRoot = ApplicationSettingsService.ResolveDefaultStorageRoot();
        SelectedTheme = ThemeOptions.First();
        SelectedChapterSelection = ChapterSelectionOptions.First();
        ExpandNavigationPane = true;
        SelectedReaderMode = ReaderModeOptions.First();
        DefaultStripZoomPercent = ApplicationSettingsService.DefaultStripZoom;
        SelectedLibraryPageSize = LibraryPageSizeOptions.First(option => option.Key == "20");
        SelectedConcurrency = ConcurrencyOptions.First(option => option.Key == "3");
        SelectedChapterRetryCount = ChapterRetryOptions.First(option => option.Key == "3");
        SelectedDownloadDirectoryLayout = DownloadDirectoryLayoutOptions.First(
            option => option.Key == ApplicationSettingsService.DirectoryLayoutOrganized);
        SettingsError = string.Empty;
        SaveStatus = "已恢复默认值，点击“保存设置”后生效。";
    }

    [RelayCommand]
    private void OpenStorageRoot()
    {
        try
        {
            Directory.CreateDirectory(StorageRoot);
            Process.Start(new ProcessStartInfo
            {
                FileName = StorageRoot,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            SettingsError = $"打开下载目录失败: {ex.Message}";
        }
    }

    [RelayCommand]
    private void OpenDataDirectory()
    {
        try
        {
            Directory.CreateDirectory(_applicationSettings.SettingsDirectory);
            Process.Start(new ProcessStartInfo
            {
                FileName = _applicationSettings.SettingsDirectory,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            SettingsError = $"打开数据目录失败: {ex.Message}";
        }
    }

    [RelayCommand]
    private void ClearSearchHistory()
    {
        _searchHistoryService.Clear();
        SettingsError = string.Empty;
        SaveStatus = "搜索记录已清空。";
    }

    [RelayCommand]
    private async Task ClearDownloadHistoryAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await _backendClient.ClearDownloadHistoryAsync(cancellationToken);
            SettingsError = string.Empty;
            SaveStatus = "下载历史已清空，正在运行的任务不受影响。";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            SettingsError = $"清空下载历史失败: {ex.Message}";
        }
    }
}
