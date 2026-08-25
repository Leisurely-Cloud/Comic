using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Comic.WinUI.Models;
using Comic.WinUI.Services;
using Comic.WinUI.Services.Native;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Comic.WinUI.ViewModels;

public partial class LibraryPageViewModel : ObservableObject
{
    private readonly BackendClient _backendClient;
    private readonly DownloadEventStream _eventStream;
    private readonly ApplicationSettingsService _applicationSettings;
    private readonly ReadingProgressService _readingProgressService;
    private int _currentPage = 1;
    private int _totalItems;
    private readonly int _pageSize;
    private CancellationTokenSource? _exportCts;
    private string _exportTaskId = string.Empty;

    public LibraryPageViewModel(
        BackendClient backendClient,
        DownloadEventStream eventStream,
        ApplicationSettingsService applicationSettings,
        ReadingProgressService readingProgressService)
    {
        _backendClient = backendClient;
        _eventStream = eventStream;
        _applicationSettings = applicationSettings;
        _readingProgressService = readingProgressService;
        _pageSize = applicationSettings.LibraryPageSize;
        Items.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasItems));
    }

    public ObservableCollection<LibraryItemViewModel> Items { get; } = [];

    [ObservableProperty]
    public partial string Keyword { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDeleteSelectedManga))]
    public partial LibraryItemViewModel? SelectedItem { get; set; }

    [ObservableProperty]
    public partial string PageError { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string UpdateCheckStatus { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    public partial bool IsExporting { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDeleteSelectedManga))]
    [NotifyPropertyChangedFor(nameof(DeleteButtonText))]
    public partial bool IsDeleting { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ImportButtonText))]
    public partial bool IsImporting { get; set; }

    [ObservableProperty]
    public partial string LibraryStatus { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ExportStatusText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial double ExportProgress { get; set; }

    [ObservableProperty]
    public partial bool ShowExportResult { get; set; }

    [ObservableProperty]
    public partial string ExportStatusTitle { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ExportOutputDirectory { get; set; } = string.Empty;

    public bool HasItems => Items.Count > 0;

    public string PageSummary => $"第 {_currentPage} 页 / 共 {_totalItems} 部";

    public string SelectedTitle => SelectedItem?.Title ?? string.Empty;

    public string SelectedAuthor => string.IsNullOrWhiteSpace(SelectedItem?.Author) ? "未识别" : SelectedItem.Author;

    public string SelectedSiteName => SelectedItem?.SiteName ?? string.Empty;

    public string SelectedCoverUrl => SelectedItem?.CoverUrl ?? string.Empty;

    public string SelectedChapterSummary => SelectedItem is null ? string.Empty : $"{SelectedItem.DownloadedChapterCount} 章";

    public string SelectedLastChapter => SelectedItem?.LastDownloadedChapterTitle ?? string.Empty;

    public string SelectedContinueReading => SelectedItem?.ContinueReadingText ?? string.Empty;

    public string SelectedFavoriteButtonText => SelectedItem?.FavoriteButtonText ?? "收藏";

    public bool CanDeleteSelectedManga =>
        SelectedItem is not null &&
        !string.IsNullOrWhiteSpace(SelectedItem.RootDir) &&
        !IsDeleting;

    public string DeleteButtonText => IsDeleting ? "正在处理" : "删除漫画";

    public string ImportButtonText => IsImporting ? "正在导入" : "导入 JM 目录";

    public async Task<JmLibraryImportPreview?> ScanJmImportAsync(
        string sourceRoot,
        CancellationToken cancellationToken = default)
    {
        PageError = string.Empty;
        LibraryStatus = "正在扫描 JM 目录...";
        try
        {
            var preview = await _backendClient.ScanJmLibraryImportAsync(sourceRoot, cancellationToken);
            LibraryStatus = string.Empty;
            return preview;
        }
        catch (OperationCanceledException)
        {
            LibraryStatus = string.Empty;
            return null;
        }
        catch (Exception ex)
        {
            LibraryStatus = string.Empty;
            PageError = $"扫描失败：{ex.Message}";
            return null;
        }
    }

    public async Task<JmLibraryImportResult?> ImportJmAsync(
        string sourceRoot,
        CancellationToken cancellationToken = default)
    {
        if (IsImporting) return null;
        IsImporting = true;
        PageError = string.Empty;
        LibraryStatus = "正在复制缺失章节，源目录不会被修改...";
        try
        {
            var result = await _backendClient.ImportJmLibraryAsync(sourceRoot, cancellationToken);
            _currentPage = 1;
            await LoadAsync(cancellationToken);
            LibraryStatus = BuildImportResultText(result);
            return result;
        }
        catch (OperationCanceledException)
        {
            LibraryStatus = "导入已取消，本轮未完成的内容已回滚。";
            return null;
        }
        catch (Exception ex)
        {
            LibraryStatus = string.Empty;
            PageError = $"导入失败：{ex.Message}";
            return null;
        }
        finally
        {
            IsImporting = false;
        }
    }

    private static string BuildImportResultText(JmLibraryImportResult result)
    {
        var completed = result.ImportedMangaCount + result.UpdatedMangaCount;
        var text = $"导入完成：处理 {completed} 部漫画，新增 {result.ImportedChapterCount} 章";
        if (result.ExistingChapterCount > 0) text += $"，跳过已有 {result.ExistingChapterCount} 章";
        if (result.ConflictChapterCount > 0) text += $"，保留冲突 {result.ConflictChapterCount} 章";
        if (result.SkippedDirectoryCount > 0) text += $"，忽略未识别目录 {result.SkippedDirectoryCount} 个";
        if (result.FailedMangaCount > 0) text += $"，失败 {result.FailedMangaCount} 部";
        return text + "。";
    }

    [RelayCommand]
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        PageError = string.Empty;
        LibraryStatus = string.Empty;
        IsLoading = true;
        try
        {
            var result = await _backendClient.GetLibraryAsync(keyword: Keyword.Trim(), page: _currentPage, pageSize: _pageSize, cancellationToken: cancellationToken);
            Items.Clear();
            foreach (var item in result.Items)
            {
                var viewModel = LibraryItemViewModel.FromDto(item);
                viewModel.ContinueReadingText = BuildContinueReadingText(item.RootDir);
                Items.Add(viewModel);
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
        catch (Exception ex)
        {
            PageError = $"操作异常: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task ToggleFavoriteAsync(CancellationToken cancellationToken = default)
    {
        if (SelectedItem is null || string.IsNullOrWhiteSpace(SelectedItem.RootDir)) return;
        var selectedRoot = SelectedItem.RootDir;
        try
        {
            var isFavorite = await _backendClient.ToggleFavoriteAsync(selectedRoot, cancellationToken);
            SelectedItem.IsFavorite = isFavorite;
            await LoadAsync(cancellationToken);
            SelectedItem = Items.FirstOrDefault(item => item.RootDir == selectedRoot) ?? Items.FirstOrDefault();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            PageError = $"收藏操作失败: {ex.Message}";
        }
    }

    [RelayCommand]
    public async Task DeleteSelectedMangaAsync(CancellationToken cancellationToken = default)
    {
        if (SelectedItem is null || string.IsNullOrWhiteSpace(SelectedItem.RootDir) || IsDeleting) return;

        var selectedRoot = SelectedItem.RootDir;
        var selectedTitle = SelectedItem.Title;
        PageError = string.Empty;
        LibraryStatus = string.Empty;
        IsDeleting = true;
        try
        {
            var deletedDirectoryCount = await _backendClient.DeleteLibraryMangaAsync(selectedRoot, cancellationToken);
            _readingProgressService.Remove(selectedRoot);
            await LoadAsync(cancellationToken);
            LibraryStatus = deletedDirectoryCount > 1
                ? $"《{selectedTitle}》及 {deletedDirectoryCount - 1} 个重复目录已移入回收站。"
                : $"《{selectedTitle}》已移入回收站。";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            PageError = $"删除失败：{ex.Message}";
        }
        finally
        {
            IsDeleting = false;
        }
    }

    private string BuildContinueReadingText(string rootDir)
    {
        var progress = _readingProgressService.Get(rootDir);
        if (progress is null || string.IsNullOrWhiteSpace(progress.ChapterDirectoryName)) return string.Empty;
        var chapterTitle = LibraryStorageService.ChapterTitle(progress.ChapterDirectoryName);
        return $"上次读到 {chapterTitle} · 第 {progress.PageIndex + 1} 页";
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
        OnPropertyChanged(nameof(SelectedAuthor));
        OnPropertyChanged(nameof(SelectedSiteName));
        OnPropertyChanged(nameof(SelectedCoverUrl));
        OnPropertyChanged(nameof(SelectedChapterSummary));
        OnPropertyChanged(nameof(SelectedLastChapter));
        OnPropertyChanged(nameof(SelectedContinueReading));
        OnPropertyChanged(nameof(SelectedFavoriteButtonText));
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

        // 开始新导出前清理上一次的残留。上面的 IsExporting 守卫已保证此刻没有正在运行的
        // 导出,所以这里只是清掉上一个任务号、并取消可能仍在收尾的订阅循环。
        // 用户主动取消走 CancelExportCommand,不是这条路径。
        await RequestServerExportCancelAsync();
        _exportCts?.Cancel();
        _exportCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = _exportCts.Token;

        try
        {
            IsExporting = true;
            ShowExportResult = true;
            ExportStatusTitle = "CBZ 导出中";
            ExportStatusText = "正在启动导出...";
            ExportOutputDirectory = string.Empty;
            ExportProgress = 0;
            PageError = string.Empty;

            var response = await _backendClient.ExportCbzAsync(SelectedItem.RootDir, token);
            if (string.IsNullOrEmpty(response.TaskId))
            {
                ExportStatusTitle = "CBZ 导出失败";
                ExportStatusText = "导出任务创建失败。";
                PageError = ExportStatusText;
                IsExporting = false;
                return;
            }

            // 记下任务号:取消时必须通知服务端真正停止导出线程,
            // 否则只是本地停止轮询,后台仍在继续写文件。
            _exportTaskId = response.TaskId;

            await foreach (var stateEvent in _eventStream.SubscribeExportAsync(response.TaskId, token))
            {
                if (stateEvent.JsonPayload is null) continue;

                var progress = JsonSerializer.Deserialize<ExportCbzProgress>(stateEvent.JsonPayload);
                if (progress is null) continue;

                if (!string.IsNullOrWhiteSpace(progress.ExportDir))
                {
                    ExportOutputDirectory = progress.ExportDir;
                }

                ExportStatusText = progress.TotalChapters > 0
                    ? $"正在导出 {progress.CurrentIndex}/{progress.TotalChapters} 章：{progress.CurrentChapter}"
                    : progress.Status == "completed" ? "导出完成" : progress.Status == "failed" ? $"导出失败: {progress.Error}" : "准备中...";

                ExportProgress = progress.TotalChapters > 0
                    ? (double)progress.CurrentIndex / progress.TotalChapters * 100.0
                    : progress.Status == "completed" ? 100.0 : 0;

                if (progress.Status == "completed")
                {
                    ExportStatusTitle = "CBZ 导出完成";
                    ExportStatusText = progress.SkippedChapters.Count > 0
                        ? $"已生成 {progress.ExportedCount} 个文件，跳过 {progress.SkippedChapters.Count} 个空章节。"
                        : $"已生成 {progress.ExportedCount} 个 CBZ 文件。";
                    ExportProgress = 100;
                    break;
                }

                if (progress.Status == "failed")
                {
                    ExportStatusTitle = "CBZ 导出失败";
                    ExportStatusText = $"导出失败：{progress.Error}";
                    PageError = ExportStatusText;
                    break;
                }

                if (progress.Status == "cancelled")
                {
                    ExportStatusTitle = "CBZ 导出已取消";
                    ExportStatusText = progress.ExportedCount > 0
                        ? $"已取消，保留了已生成的 {progress.ExportedCount} 个 CBZ 文件。"
                        : "导出已取消。";
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            await RequestServerExportCancelAsync();
            ExportStatusTitle = "CBZ 导出已取消";
            ExportStatusText = "导出已取消";
        }
        catch (BackendApiException ex)
        {
            ExportStatusTitle = "CBZ 导出失败";
            ExportStatusText = ex.Error.Message;
            PageError = ExportStatusText;
        }
        catch (Exception ex)
        {
            ExportStatusTitle = "CBZ 导出失败";
            ExportStatusText = $"导出异常：{ex.Message}";
            PageError = ExportStatusText;
        }
        finally
        {
            IsExporting = false;
        }
    }

    /// <summary>取消正在进行的 CBZ 导出。</summary>
    [RelayCommand]
    private async Task CancelExport()
    {
        if (!IsExporting) return;
        ExportStatusText = "正在取消导出...";
        await RequestServerExportCancelAsync();
        _exportCts?.Cancel();
    }

    /// <summary>
    /// 通知服务端停止导出线程。仅停止本地轮询是不够的:导出 worker 会继续写文件。
    /// 取消本身失败不应影响调用方,所以这里吞掉异常。
    /// </summary>
    private async Task RequestServerExportCancelAsync()
    {
        if (string.IsNullOrEmpty(_exportTaskId)) return;
        var taskId = _exportTaskId;
        _exportTaskId = string.Empty;
        try
        {
            await _backendClient.CancelExportAsync(taskId, CancellationToken.None);
        }
        catch (Exception)
        {
            // 取消失败不影响调用方,导出线程最终也会随进程退出。
        }
    }

    [RelayCommand]
    private void DismissExportResult()
    {
        ShowExportResult = false;
        ExportStatusTitle = string.Empty;
        ExportStatusText = string.Empty;
        ExportOutputDirectory = string.Empty;
        ExportProgress = 0;
    }

    [RelayCommand]
    private void OpenExportDirectory()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(ExportOutputDirectory) || !Directory.Exists(ExportOutputDirectory))
            {
                PageError = "导出目录不存在。";
                return;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = ExportOutputDirectory,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            PageError = $"打开导出目录失败：{ex.Message}";
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
    public partial string Author { get; set; } = string.Empty;

    public string AuthorDisplay => string.IsNullOrWhiteSpace(Author) ? string.Empty : $"作者：{Author}";

    [ObservableProperty]
    public partial string RootDir { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string MangaUrl { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string CoverUrl { get; set; } = string.Empty;

    [ObservableProperty]
    public partial int DownloadedChapterCount { get; set; }

    [ObservableProperty]
    public partial string LastDownloadedChapterTitle { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string UpdateCheckStatus { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsFavorite { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDuplicateDirectories))]
    [NotifyPropertyChangedFor(nameof(DuplicateDirectoriesText))]
    public partial int DuplicateDirectoryCount { get; set; }

    public bool HasDuplicateDirectories => DuplicateDirectoryCount > 0;

    public string DuplicateDirectoriesText => DuplicateDirectoryCount > 0
        ? $"检测到 {DuplicateDirectoryCount} 个重复目录，当前显示最佳目录"
        : string.Empty;

    [ObservableProperty]
    public partial string ContinueReadingText { get; set; } = string.Empty;

    public string FavoriteButtonText => IsFavorite ? "取消收藏" : "收藏";

    partial void OnIsFavoriteChanged(bool value) => OnPropertyChanged(nameof(FavoriteButtonText));

    public static LibraryItemViewModel FromDto(LibraryItemDto dto)
    {
        return new LibraryItemViewModel
        {
            Title = dto.Title,
            SiteName = dto.SiteName,
            Author = dto.Author,
            RootDir = dto.RootDir,
            MangaUrl = dto.MangaUrl,
            CoverUrl = dto.CoverUrl,
            DownloadedChapterCount = dto.DownloadedChapterCount,
            LastDownloadedChapterTitle = dto.LastDownloadedChapterTitle,
            IsFavorite = dto.IsFavorite,
            DuplicateDirectoryCount = dto.DuplicateDirectoryCount,
        };
    }
}
