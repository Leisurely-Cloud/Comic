using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Comic.WinUI.Models;
using Comic.WinUI.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Comic.WinUI.ViewModels;

public partial class LibraryPageViewModel : ObservableObject
{
    private readonly BackendClient _backendClient;
    private readonly DownloadEventStream _eventStream;
    private int _currentPage = 1;
    private int _totalItems;
    private readonly int _pageSize = 20;
    private CancellationTokenSource? _exportCts;

    public LibraryPageViewModel(BackendClient backendClient, DownloadEventStream eventStream)
    {
        _backendClient = backendClient;
        _eventStream = eventStream;
    }

    public ObservableCollection<LibraryItemViewModel> Items { get; } = [];

    [ObservableProperty]
    public partial string Keyword { get; set; } = string.Empty;

    [ObservableProperty]
    public partial LibraryItemViewModel? SelectedItem { get; set; }

    [ObservableProperty]
    public partial string PageError { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string UpdateCheckStatus { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsExporting { get; set; }

    [ObservableProperty]
    public partial string ExportStatusText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial double ExportProgress { get; set; }

    [ObservableProperty]
    public partial bool ShowExportResult { get; set; }

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
            var result = await _backendClient.GetLibraryAsync(keyword: Keyword.Trim(), page: _currentPage, pageSize: _pageSize, cancellationToken: cancellationToken);
            Items.Clear();
            foreach (var item in result.Items)
            {
                Items.Add(LibraryItemViewModel.FromDto(item));
            }

            _totalItems = result.Total;
            SelectedItem = Items.FirstOrDefault();
            OnPropertyChanged(nameof(PageSummary));
        }
        catch (OperationCanceledException)
        {
            // swallowed
        }
        catch (BackendApiException ex)
        {
            PageError = ex.Error.Message;
        }
        catch (HttpRequestException)
        {
            PageError = "无法连接后端服务，请确认后端已启动。";
        }
        catch (Exception ex)
        {
            PageError = $"操作异常: {ex.Message}";
        }
    }

    [RelayCommand]
    public Task SearchAsync(CancellationToken cancellationToken = default)
    {
        _currentPage = 1;
        return LoadAsync(cancellationToken);
    }

    [RelayCommand]
    public Task RefreshLibraryAsync(CancellationToken cancellationToken = default)
    {
        _currentPage = 1;
        return LoadAsync(cancellationToken);
    }

    [RelayCommand]
    public async Task CheckUpdatesAsync(CancellationToken cancellationToken = default)
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
        catch (OperationCanceledException)
        {
            UpdateCheckStatus = "检查已取消";
        }
        catch (BackendApiException ex)
        {
            UpdateCheckStatus = $"检查失败: {ex.Error.Message}";
        }
        catch (HttpRequestException)
        {
            UpdateCheckStatus = "无法连接后端服务，请确认后端已启动。";
        }
        catch (Exception ex)
        {
            UpdateCheckStatus = $"检查异常: {ex.Message}";
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

        if (IsExporting)
        {
            return;
        }

        _exportCts?.Cancel();
        _exportCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = _exportCts.Token;

        try
        {
            IsExporting = true;
            ShowExportResult = true;
            ExportStatusText = "正在启动导出...";
            ExportProgress = 0;
            PageError = string.Empty;

            var response = await _backendClient.ExportCbzAsync(SelectedItem.RootDir, token);
            if (string.IsNullOrEmpty(response.TaskId))
            {
                PageError = "导出任务创建失败";
                IsExporting = false;
                return;
            }

            await foreach (var sseEvent in _eventStream.SubscribeExportAsync(response.TaskId, token))
            {
                if (sseEvent.JsonPayload is null) continue;

                var progress = JsonSerializer.Deserialize<ExportCbzProgress>(sseEvent.JsonPayload);
                if (progress is null) continue;

                ExportStatusText = progress.TotalChapters > 0
                    ? $"正在导出 {progress.CurrentIndex}/{progress.TotalChapters} 章: {progress.CurrentChapter}"
                    : progress.Status == "completed" ? "导出完成" : progress.Status == "failed" ? $"导出失败: {progress.Error}" : "准备中...";

                ExportProgress = progress.TotalChapters > 0
                    ? (double)progress.CurrentIndex / progress.TotalChapters * 100.0
                    : progress.Status == "completed" ? 100.0 : 0;

                if (progress.Status == "completed")
                {
                    ExportStatusText = $"已导出 {progress.ExportedCount} 个 CBZ 到 {progress.ExportDir}";
                    ExportProgress = 100;
                    break;
                }

                if (progress.Status == "failed")
                {
                    PageError = $"导出失败: {progress.Error}";
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            ExportStatusText = "导出已取消";
        }
        catch (BackendApiException ex)
        {
            PageError = ex.Error.Message;
        }
        catch (Exception ex)
        {
            PageError = $"导出异常: {ex.Message}";
        }
        finally
        {
            IsExporting = false;
        }
    }

    [RelayCommand]
    private void DismissExportResult()
    {
        ShowExportResult = false;
        ExportStatusText = string.Empty;
        ExportProgress = 0;
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
