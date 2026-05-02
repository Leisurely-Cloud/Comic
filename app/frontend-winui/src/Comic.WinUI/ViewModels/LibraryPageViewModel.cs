using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Comic.WinUI.Models;
using Comic.WinUI.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Comic_WinUI.ViewModels;

public partial class LibraryPageViewModel : ObservableObject
{
    private readonly BackendClient _backendClient;
    private int _currentPage = 1;
    private int _totalItems;
    private readonly int _pageSize = 20;

    public LibraryPageViewModel(BackendClient backendClient)
    {
        _backendClient = backendClient;
        SiteOptions = new ObservableCollection<SiteOption>(SiteCatalog.LibrarySites);
        SelectedSite = SiteOptions.FirstOrDefault(option => option.Key == string.Empty) ?? SiteOptions.FirstOrDefault();
    }

    public ObservableCollection<LibraryItemViewModel> Items { get; } = [];

    public ObservableCollection<SiteOption> SiteOptions { get; }

    [ObservableProperty]
    public partial SiteOption? SelectedSite { get; set; }

    [ObservableProperty]
    public partial string Keyword { get; set; } = string.Empty;

    [ObservableProperty]
    public partial LibraryItemViewModel? SelectedItem { get; set; }

    [ObservableProperty]
    public partial string PageError { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string UpdateCheckStatus { get; set; } = string.Empty;

    public string PageSummary => $"第 {_currentPage} 页 / 共 {_totalItems} 部";

    public string SelectedTitle => SelectedItem?.Title ?? string.Empty;

    public string SelectedSiteName => SelectedItem?.SiteName ?? string.Empty;

    public string SelectedChapterSummary => SelectedItem is null ? string.Empty : $"{SelectedItem.DownloadedChapterCount} 章";

    public string SelectedLastChapter => SelectedItem?.LastDownloadedChapterTitle ?? string.Empty;

    [RelayCommand]
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        PageError = string.Empty;
        try
        {
            var result = await _backendClient.GetLibraryAsync(SelectedSite?.Key ?? string.Empty, Keyword.Trim(), _currentPage, _pageSize, cancellationToken);
            Items.Clear();
            foreach (var item in result.Items)
            {
                Items.Add(LibraryItemViewModel.FromDto(item));
            }

            _totalItems = result.Total;
            SelectedItem = Items.FirstOrDefault();
            OnPropertyChanged(nameof(PageSummary));
        }
        catch (BackendApiException ex)
        {
            PageError = ex.Error.Message;
        }
    }

    [RelayCommand]
    public Task Search(CancellationToken cancellationToken = default)
    {
        _currentPage = 1;
        return LoadAsync(cancellationToken);
    }

    [RelayCommand]
    public Task RefreshLibrary(CancellationToken cancellationToken = default)
    {
        _currentPage = 1;
        return LoadAsync(cancellationToken);
    }

    [RelayCommand]
    public async Task CheckUpdates(CancellationToken cancellationToken = default)
    {
        UpdateCheckStatus = "检查中...";
        try
        {
            var result = await _backendClient.CheckLibraryUpdatesAsync(cancellationToken);
            var updates = result.Items.Where(i => i.HasUpdate).ToList();
            UpdateCheckStatus = updates.Count > 0
                ? $"发现 {updates.Count} 部漫画有更新"
                : "所有漫画已是最新";
        }
        catch (Exception ex)
        {
            UpdateCheckStatus = $"检查失败: {ex.Message}";
        }
    }

    [RelayCommand]
    public Task PreviousPage(CancellationToken cancellationToken = default)
    {
        if (_currentPage > 1)
        {
            _currentPage--;
            return LoadAsync(cancellationToken);
        }

        return Task.CompletedTask;
    }

    [RelayCommand]
    public Task NextPage(CancellationToken cancellationToken = default)
    {
        if (_currentPage * _pageSize < _totalItems)
        {
            _currentPage++;
            return LoadAsync(cancellationToken);
        }

        return Task.CompletedTask;
    }

    [RelayCommand]
    public void OpenDirectory()
    {
        if (SelectedItem is not null && !string.IsNullOrWhiteSpace(SelectedItem.RootDir))
        {
            System.Diagnostics.Process.Start("explorer.exe", SelectedItem.RootDir);
        }
    }

    [RelayCommand]
    public void OpenSourceLink()
    {
        if (SelectedItem is not null && !string.IsNullOrWhiteSpace(SelectedItem.MangaUrl))
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(SelectedItem.MangaUrl) { UseShellExecute = true });
        }
    }

    partial void OnSelectedItemChanged(LibraryItemViewModel? value)
    {
        OnPropertyChanged(nameof(SelectedTitle));
        OnPropertyChanged(nameof(SelectedSiteName));
        OnPropertyChanged(nameof(SelectedChapterSummary));
        OnPropertyChanged(nameof(SelectedLastChapter));
    }

    [RelayCommand]
    public async Task ExportCbz(CancellationToken cancellationToken = default)
    {
        if (SelectedItem is null || string.IsNullOrWhiteSpace(SelectedItem.RootDir))
        {
            PageError = "请先选择一部漫画。";
            return;
        }

        try
        {
            await _backendClient.ExportCbzAsync(SelectedItem.RootDir, cancellationToken);
            PageError = string.Empty;
        }
        catch (BackendApiException ex)
        {
            PageError = ex.Error.Message;
        }
    }
}

public partial class LibraryItemViewModel : ObservableObject
{
    [ObservableProperty]
    public partial string Title { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string SiteName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string RootDir { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string MangaUrl { get; set; } = string.Empty;

    [ObservableProperty]
    public partial int DownloadedChapterCount { get; set; }

    [ObservableProperty]
    public partial string LastDownloadedChapterTitle { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string UpdateCheckStatus { get; set; } = string.Empty;

    public static LibraryItemViewModel FromDto(LibraryItemDto dto)
    {
        return new LibraryItemViewModel
        {
            Title = dto.Title,
            SiteName = dto.SiteName,
            RootDir = dto.RootDir,
            MangaUrl = dto.MangaUrl,
            DownloadedChapterCount = dto.DownloadedChapterCount,
            LastDownloadedChapterTitle = dto.LastDownloadedChapterTitle,
        };
    }
}
