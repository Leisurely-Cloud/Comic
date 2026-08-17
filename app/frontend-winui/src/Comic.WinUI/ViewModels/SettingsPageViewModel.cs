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
    public partial double DefaultStripZoomPercent { get; set; } = 100;

    [ObservableProperty]
    public partial SettingOption? SelectedLibraryPageSize { get; set; }

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
            SettingsError = string.Empty;
            SaveStatus = string.Empty;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            SettingsError = $"加载设置失败: {ex.Message}";
        }
    }

    [RelayCommand]
    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        IsSaving = true;
        SettingsError = string.Empty;
        SaveStatus = string.Empty;
        try
        {
            var settings = await _backendClient.UpdateSettingsAsync(
                new SettingsUpdateRequest { StorageRoot = StorageRoot },
                cancellationToken);
            StorageRoot = settings.StorageRoot;
            _applicationSettings.UpdatePreferences(
                SelectedTheme?.Key ?? ApplicationSettingsService.SystemTheme,
                SelectedChapterSelection?.Key ?? ApplicationSettingsService.SelectNone,
                ExpandNavigationPane,
                SelectedReaderMode?.Key ?? ApplicationSettingsService.ReaderPaged,
                (int)Math.Round(DefaultStripZoomPercent),
                int.TryParse(SelectedLibraryPageSize?.Key, out var pageSize) ? pageSize : 20);
            _shellViewModel.StorageRoot = StorageRoot;
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
        DefaultStripZoomPercent = 100;
        SelectedLibraryPageSize = LibraryPageSizeOptions.First(option => option.Key == "20");
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
