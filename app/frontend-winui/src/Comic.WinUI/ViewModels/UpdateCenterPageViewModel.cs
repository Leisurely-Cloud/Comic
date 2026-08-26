using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Comic.WinUI.Models;
using Comic.WinUI.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Comic.WinUI.ViewModels;

public partial class UpdateCenterPageViewModel : ObservableObject
{
    private readonly BackendClient _backendClient;

    public ObservableCollection<UpdateCenterItemViewModel> Items { get; } = [];

    [ObservableProperty] public partial bool IsChecking { get; set; }
    [ObservableProperty] public partial bool IsDownloading { get; set; }
    [ObservableProperty] public partial bool HasChecked { get; set; }
    [ObservableProperty] public partial string StatusText { get; set; } = "点击“检查更新”扫描本地书库。";
    [ObservableProperty] public partial string ErrorMessage { get; set; } = string.Empty;
    [ObservableProperty] public partial int CheckedCount { get; set; }
    [ObservableProperty] public partial int FailedCount { get; set; }

    public bool HasItems => Items.Count > 0;
    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);
    public bool ShowInitialState => !HasChecked && !IsChecking;
    public bool ShowEmptyState => HasChecked && !IsChecking && !HasItems;
    public int SelectedUpdateCount => Items.Count(item => item.IsSelected && !item.IsQueued);
    public int SelectedChapterCount => Items.Where(item => item.IsSelected && !item.IsQueued).Sum(item => item.MissingCount);
    public bool CanDownloadSelected => !IsChecking && !IsDownloading && SelectedUpdateCount > 0;
    public string SelectionSummary => SelectedUpdateCount == 0
        ? "未选择更新"
        : $"已选择 {SelectedUpdateCount} 部 · 共 {SelectedChapterCount} 章";

    public event EventHandler<string>? OpenMangaRequested;
    public event EventHandler? OpenDownloadsRequested;

    public UpdateCenterPageViewModel(BackendClient backendClient)
    {
        _backendClient = backendClient;
        Items.CollectionChanged += (_, _) => NotifyCollectionStateChanged();
    }

    partial void OnIsCheckingChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowInitialState));
        OnPropertyChanged(nameof(ShowEmptyState));
        NotifySelectionChanged();
    }

    partial void OnIsDownloadingChanged(bool value) => NotifySelectionChanged();
    partial void OnHasCheckedChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowInitialState));
        OnPropertyChanged(nameof(ShowEmptyState));
    }
    partial void OnErrorMessageChanged(string value) => OnPropertyChanged(nameof(HasError));

    [RelayCommand]
    public async Task CheckUpdatesAsync(CancellationToken cancellationToken = default)
    {
        if (IsChecking) return;
        IsChecking = true;
        HasChecked = false;
        ErrorMessage = string.Empty;
        StatusText = "正在检查本地书库，请稍候...";
        Items.Clear();
        try
        {
            var response = await _backendClient.CheckLibraryUpdatesAsync(cancellationToken);
            CheckedCount = response.CheckedCount;
            FailedCount = response.FailedCount;
            foreach (var update in response.Items.Where(item => item.HasUpdate)
                         .OrderByDescending(item => item.MissingChapters.Count)
                         .ThenBy(item => item.Title, StringComparer.CurrentCulture))
            {
                Items.Add(new UpdateCenterItemViewModel(
                    update,
                    QueueDownloadAndOpenDownloadsAsync,
                    item => OpenMangaRequested?.Invoke(this, item.MangaUrl),
                    NotifySelectionChanged));
            }

            var failureText = FailedCount > 0 ? $"，另有 {FailedCount} 部检查失败" : string.Empty;
            StatusText = Items.Count > 0
                ? $"已检查 {CheckedCount} 部，发现 {Items.Count} 部有更新{failureText}。"
                : $"已检查 {CheckedCount} 部，当前没有新章节{failureText}。";
            HasChecked = true;
        }
        catch (OperationCanceledException)
        {
            StatusText = "检查已取消。";
        }
        catch (Exception ex)
        {
            ErrorMessage = $"检查更新失败：{ex.Message}";
            StatusText = "检查未完成。";
            HasChecked = true;
        }
        finally
        {
            IsChecking = false;
        }
    }

    [RelayCommand]
    private void SelectAll()
    {
        foreach (var item in Items.Where(item => !item.IsQueued)) item.IsSelected = true;
    }

    [RelayCommand]
    private void ClearSelection()
    {
        foreach (var item in Items) item.IsSelected = false;
    }

    [RelayCommand(CanExecute = nameof(CanDownloadSelected))]
    private async Task DownloadSelectedAsync()
    {
        if (!CanDownloadSelected) return;
        IsDownloading = true;
        ErrorMessage = string.Empty;
        var queued = 0;
        var failed = 0;
        foreach (var item in Items.Where(item => item.IsSelected && !item.IsQueued).ToList())
        {
            if (await QueueDownloadAsync(item)) queued++;
            else failed++;
        }
        IsDownloading = false;
        StatusText = failed == 0
            ? $"已为 {queued} 部漫画创建补下载任务。"
            : $"已创建 {queued} 个任务，{failed} 部创建失败。";
        if (failed > 0) ErrorMessage = "部分补下载任务创建失败，请查看对应作品的状态提示。";
        if (queued > 0) OpenDownloadsRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void OpenDownloads() => OpenDownloadsRequested?.Invoke(this, EventArgs.Empty);

    private async Task<bool> QueueDownloadAndOpenDownloadsAsync(UpdateCenterItemViewModel item)
    {
        var queued = await QueueDownloadAsync(item);
        if (queued) OpenDownloadsRequested?.Invoke(this, EventArgs.Empty);
        return queued;
    }

    private async Task<bool> QueueDownloadAsync(UpdateCenterItemViewModel item)
    {
        if (item.IsBusy || item.IsQueued || item.MissingCount == 0) return false;
        item.IsBusy = true;
        item.StatusText = "正在创建任务...";
        try
        {
            await _backendClient.CreateDownloadAsync(new DownloadCreateRequest
            {
                Url = item.MangaUrl,
                SiteKey = SiteCatalog.Key,
                Source = "update-center",
                Chapters = item.MissingChapters
                    .Select(chapter => string.IsNullOrWhiteSpace(chapter.Url) ? chapter.Title : chapter.Url)
                    .ToList(),
            });
            item.IsQueued = true;
            item.IsSelected = false;
            item.StatusText = "已加入下载任务";
            return true;
        }
        catch (Exception ex)
        {
            item.StatusText = $"创建失败：{ex.Message}";
            return false;
        }
        finally
        {
            item.IsBusy = false;
            NotifySelectionChanged();
        }
    }

    private void NotifyCollectionStateChanged()
    {
        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(ShowEmptyState));
        NotifySelectionChanged();
    }

    private void NotifySelectionChanged()
    {
        OnPropertyChanged(nameof(SelectedUpdateCount));
        OnPropertyChanged(nameof(SelectedChapterCount));
        OnPropertyChanged(nameof(CanDownloadSelected));
        OnPropertyChanged(nameof(SelectionSummary));
        DownloadSelectedCommand.NotifyCanExecuteChanged();
    }
}

public partial class UpdateCenterItemViewModel : ObservableObject
{
    private readonly LibraryUpdateItem _item;
    private readonly Action _selectionChanged;

    public string Title => _item.Title;
    public string MangaUrl => _item.MangaUrl;
    public string CoverUrl => _item.CoverUrl;
    public string AuthorText => string.IsNullOrWhiteSpace(_item.Author) ? "作者：未知" : $"作者：{_item.Author}";
    public int LocalChapterCount => _item.LocalChapterCount;
    public int RemoteChapterCount => _item.RemoteChapterCount;
    public int MissingCount => _item.MissingChapters.Count;
    public string ChapterCountText => $"本地 {LocalChapterCount} 章 · 官方 {RemoteChapterCount} 章 · 新增 {MissingCount} 章";
    public string MissingChapterText => string.Join("、", _item.MissingChapters
        .TakeLast(3)
        .Select(chapter => chapter.Title));
    public string LatestChapterText => string.IsNullOrWhiteSpace(_item.LatestChapter)
        ? string.Empty
        : $"最新：{_item.LatestChapter}";
    public System.Collections.Generic.IReadOnlyList<MangaChapterDto> MissingChapters => _item.MissingChapters;

    [ObservableProperty] public partial bool IsSelected { get; set; } = true;
    [ObservableProperty] public partial bool IsBusy { get; set; }
    [ObservableProperty] public partial bool IsQueued { get; set; }
    [ObservableProperty] public partial string StatusText { get; set; } = string.Empty;

    public bool CanDownload => !IsBusy && !IsQueued && MissingCount > 0;
    public IAsyncRelayCommand DownloadCommand { get; }
    public ICommand OpenMangaCommand { get; }

    public UpdateCenterItemViewModel(
        LibraryUpdateItem item,
        Func<UpdateCenterItemViewModel, Task<bool>> download,
        Action<UpdateCenterItemViewModel> openManga,
        Action selectionChanged)
    {
        _item = item;
        _selectionChanged = selectionChanged;
        DownloadCommand = new AsyncRelayCommand(async () => await download(this), () => CanDownload);
        OpenMangaCommand = new RelayCommand(() => openManga(this));
    }

    partial void OnIsSelectedChanged(bool value) => _selectionChanged();
    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanDownload));
        DownloadCommand.NotifyCanExecuteChanged();
    }
    partial void OnIsQueuedChanged(bool value)
    {
        OnPropertyChanged(nameof(CanDownload));
        DownloadCommand.NotifyCanExecuteChanged();
    }
}
