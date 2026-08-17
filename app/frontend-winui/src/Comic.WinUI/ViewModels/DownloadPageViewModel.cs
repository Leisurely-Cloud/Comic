using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Comic.WinUI.Models;
using Comic.WinUI.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;

namespace Comic.WinUI.ViewModels;

public partial class DownloadPageViewModel : ObservableObject
{
    private readonly BackendClient _backendClient;
    private readonly DownloadEventStream _eventStream;
    private readonly ShellViewModel _shellViewModel;
    private readonly SearchHistoryService _searchHistoryService;
    private readonly ApplicationSettingsService _applicationSettings;
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = SnakeCaseNamingPolicy.Instance,
    };

    private CancellationTokenSource? _subscriptionCts;
    private CancellationTokenSource? _searchCts;
    private CancellationTokenSource? _resolveCts;
    private int _lastEventId;
    private int _searchPage;
    private int _lastSearchPageSize;

    public DownloadPageViewModel(
        BackendClient backendClient,
        DownloadEventStream eventStream,
        ShellViewModel shellViewModel,
        SearchHistoryService searchHistoryService,
        ApplicationSettingsService applicationSettings)
    {
        _backendClient = backendClient;
        _eventStream = eventStream;
        _shellViewModel = shellViewModel;
        _searchHistoryService = searchHistoryService;
        _applicationSettings = applicationSettings;
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        HistoryItems.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasHistoryItems));
        LoadSearchHistory();
    }

    public ObservableCollection<DownloadTaskItemViewModel> Tasks { get; } = [];

    public ObservableCollection<SearchResultItemViewModel> SearchResults { get; } = [];

    public ObservableCollection<ChapterItemViewModel> AvailableChapters { get; } = [];

    public ObservableCollection<DownloadHistoryItem> HistoryItems { get; } = [];

    public ObservableCollection<SearchHistoryEntry> SearchHistory { get; } = [];

    public bool HasSearchHistory => SearchHistory.Count > 0;

    [ObservableProperty]
    public partial bool IsBatchMode { get; set; }

    [ObservableProperty]
    public partial int HistoryPage { get; set; } = 1;

    [ObservableProperty]
    public partial int HistoryTotal { get; set; }

    public bool HasNextHistoryPage => HistoryPage * 20 < HistoryTotal;

    public bool HasHistoryItems => HistoryItems.Count > 0;

    public string HistorySummary => HistoryTotal > 0
        ? $"共 {HistoryTotal} 条记录 · 第 {HistoryPage} 页"
        : "暂无下载历史";

    public int SelectedHistoryCount => HistoryItems.Count(item => item.IsSelected);

    public bool HasSelectedHistory => SelectedHistoryCount > 0;

    public string SelectedHistorySummary => HasSelectedHistory
        ? $"已选择 {SelectedHistoryCount} 条"
        : "未选择记录";

    partial void OnHistoryPageChanged(int value)
    {
        OnPropertyChanged(nameof(HasNextHistoryPage));
        OnPropertyChanged(nameof(HistorySummary));
    }

    partial void OnHistoryTotalChanged(int value)
    {
        OnPropertyChanged(nameof(HasNextHistoryPage));
        OnPropertyChanged(nameof(HistorySummary));
    }

    [ObservableProperty]
    public partial MangaResolveResponse? CurrentManga { get; set; }

    [ObservableProperty]
    public partial DownloadTaskItemViewModel? CurrentTask { get; set; }

    [ObservableProperty]
    public partial string CurrentTaskId { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string SearchKeyword { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial bool IsSearching { get; set; }

    [ObservableProperty]
    public partial bool IsResolving { get; set; }

    [ObservableProperty]
    public partial string PageError { get; set; } = string.Empty;

    [ObservableProperty]
    public partial SearchResultItemViewModel? SelectedSearchResult { get; set; }

    [ObservableProperty]
    public partial bool HasSearchResults { get; set; }

    [ObservableProperty]
    public partial bool IsLoadingMore { get; set; }

    [ObservableProperty]
    public partial string SearchStatusText { get; set; } = string.Empty;

    public bool CanLoadMoreSearchResults => HasSearchResults && !IsSearching && !IsLoadingMore && _lastSearchPageSize > 0;

    public string TaskCountSummary => Tasks.Count == 0 ? "暂无任务" : $"共 {Tasks.Count} 个任务";

    public bool HasCurrentTask => CurrentTask is not null;

    public bool HasManga => CurrentManga is not null;

    public string CurrentMangaTitle => CurrentManga?.Title ?? string.Empty;
    public string CurrentMangaSiteName => CurrentManga?.SiteName ?? string.Empty;
    public string CurrentMangaLatestChapter => CurrentManga?.LatestChapter ?? string.Empty;
    public string CurrentMangaCoverUrl => CurrentManga?.CoverUrl ?? string.Empty;
    public string CurrentMangaDetailHint => CurrentManga?.DetailHint ?? string.Empty;

    public bool HasChapters => AvailableChapters.Count > 0;

    public bool ShowChapterSelection => HasChapters && !IsResolving;

    public int SelectedChapterCount => AvailableChapters.Count(c => c.IsSelected);

    public string ChapterSelectionSummary => HasChapters
        ? $"已选 {SelectedChapterCount} / {AvailableChapters.Count} 章"
        : "暂无章节";

    public bool CanStartDownload =>
        HasManga && HasChapters && SelectedChapterCount > 0 && !IsBusy && !IsResolving;

    public string DownloadButtonText => SelectedChapterCount > 0
        ? $"下载所选 {SelectedChapterCount} 章"
        : "请先选择章节";

    [RelayCommand]
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        IsBusy = true;
        PageError = string.Empty;
        try
        {
            var list = await _backendClient.GetDownloadsAsync(cancellationToken);
            Tasks.Clear();
            foreach (var task in list.Items)
            {
                var vm = DownloadTaskItemViewModel.FromDto(task);
                vm.IsBatchMode = IsBatchMode;
                AttachTaskSelectionHandlers(vm);
                Tasks.Add(vm);
            }

            CurrentTask = SelectPreferredTask();
            CurrentTaskId = CurrentTask?.Id ?? string.Empty;
            OnPropertyChanged(nameof(TaskCountSummary));
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
            PageError = "内置服务暂不可用，请重试。";
        }
        catch (Exception ex)
        {
            PageError = $"初始化异常: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    public async Task SearchAsync(CancellationToken cancellationToken = default)
    {
        var keyword = SearchKeyword;
        if (string.IsNullOrWhiteSpace(keyword))
        {
            PageError = "请输入搜索关键词。";
            return;
        }

        keyword = keyword.Trim();

        _searchCts?.Cancel();
        _searchCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = _searchCts.Token;

        IsSearching = true;
        PageError = string.Empty;
        SearchResults.Clear();
        HasSearchResults = false;
        SearchStatusText = "搜索中...";
        SelectedSearchResult = null;

        try
        {
            const string siteKey = SiteCatalog.Key;

            var result = await _backendClient.SearchAsync(
                keyword, siteKey, 1, token);

            _searchPage = 1;
            _lastSearchPageSize = result.Items.Count;

            foreach (var item in result.Items)
            {
                SearchResults.Add(SearchResultItemViewModel.FromSearch(item));
            }

            HasSearchResults = SearchResults.Count > 0;
            SearchStatusText = HasSearchResults
                ? $"找到 {SearchResults.Count} 部漫画"
                : "未找到相关漫画，请换一个关键词试试";
            OnPropertyChanged(nameof(CanLoadMoreSearchResults));

            if (HasSearchResults)
            {
                _searchHistoryService.Add(keyword, siteKey, SiteCatalog.DisplayName, SearchResults.Count);
                LoadSearchHistory();
            }
        }
        catch (OperationCanceledException)
        {
            System.Diagnostics.Debug.WriteLine("[SearchAsync] cancelled");
        }
        catch (BackendApiException ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SearchAsync] BackendApiException: {ex.Error.Message}");
            PageError = $"搜索出错: {ex.Error.Message}";
            SearchStatusText = "搜索失败";
        }
        catch (HttpRequestException ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SearchAsync] HttpRequestException: {ex.Message}");
            PageError = "内置服务调用失败，请重试。";
            SearchStatusText = "搜索失败";
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SearchAsync] Exception: {ex.GetType().Name}: {ex.Message}");
            PageError = $"搜索异常: {ex.Message}";
            SearchStatusText = "搜索失败";
        }
        finally
        {
            IsSearching = false;
        }
    }

    [RelayCommand]
    public async Task LoadMoreSearchResultsAsync(CancellationToken cancellationToken = default)
    {
        if (_searchCts is not null && _searchCts.IsCancellationRequested) return;

        var keyword = SearchKeyword?.Trim();
        if (string.IsNullOrWhiteSpace(keyword)) return;

        IsLoadingMore = true;
        PageError = string.Empty;

        try
        {
            const string siteKey = SiteCatalog.Key;
            var nextPage = _searchPage + 1;
            var result = await _backendClient.SearchAsync(keyword, siteKey, nextPage, cancellationToken);

            _searchPage = nextPage;
            _lastSearchPageSize = result.Items.Count;

            foreach (var item in result.Items)
            {
                SearchResults.Add(SearchResultItemViewModel.FromSearch(item));
            }

            SearchStatusText = $"找到 {SearchResults.Count} 部漫画";
            OnPropertyChanged(nameof(CanLoadMoreSearchResults));
        }
        catch (OperationCanceledException)
        {
        }
        catch (BackendApiException ex)
        {
            PageError = $"加载更多失败: {ex.Error.Message}";
        }
        catch (HttpRequestException)
        {
            PageError = "内置服务调用失败，请重试。";
        }
        catch (Exception ex)
        {
            PageError = $"加载更多异常: {ex.Message}";
        }
        finally
        {
            IsLoadingMore = false;
        }
    }

    [RelayCommand]
    public async Task SelectSearchResultAsync(SearchResultItemViewModel? item, CancellationToken cancellationToken = default)
    {
        if (item is null) return;

        _resolveCts?.Cancel();
        _resolveCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = _resolveCts.Token;

        SelectedSearchResult = item;
        IsResolving = true;
        PageError = string.Empty;

        try
        {
            var detail = await _backendClient.ResolveMangaAsync(
                new MangaResolveRequest
                {
                    Url = item.MangaUrl,
                    SiteKey = SiteCatalog.Key,
                },
                token);
            CurrentManga = detail;
        }
        catch (OperationCanceledException)
        {
        }
        catch (BackendApiException ex)
        {
            PageError = $"获取漫画详情失败: {ex.Error.Message}";
        }
        catch (HttpRequestException)
        {
            PageError = "内置服务调用失败，请重试。";
        }
        catch (Exception ex)
        {
            PageError = $"获取详情异常: {ex.Message}";
        }
        finally
        {
            if (!token.IsCancellationRequested)
            {
                IsResolving = false;
            }
        }
    }

    [RelayCommand]
    public async Task ResolveDirectUrlAsync(string? url, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(url)) return;

        _resolveCts?.Cancel();
        _resolveCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = _resolveCts.Token;

        IsResolving = true;
        PageError = string.Empty;

        try
        {
            var detail = await _backendClient.ResolveMangaAsync(
                new MangaResolveRequest
                {
                    Url = url.Trim(),
                    SiteKey = SiteCatalog.Key,
                },
                token);
            CurrentManga = detail;
        }
        catch (OperationCanceledException)
        {
        }
        catch (BackendApiException ex)
        {
            PageError = $"获取漫画详情失败: {ex.Error.Message}";
        }
        catch (HttpRequestException)
        {
            PageError = "内置服务调用失败，请重试。";
        }
        catch (Exception ex)
        {
            PageError = $"获取详情异常: {ex.Message}";
        }
        finally
        {
            if (!token.IsCancellationRequested)
            {
                IsResolving = false;
            }
        }
    }

    partial void OnIsResolvingChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowChapterSelection));
        OnPropertyChanged(nameof(CanStartDownload));
    }

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanStartDownload));
    }

    partial void OnIsSearchingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanLoadMoreSearchResults));
    }

    partial void OnIsLoadingMoreChanged(bool value)
    {
        OnPropertyChanged(nameof(CanLoadMoreSearchResults));
    }

    partial void OnHasSearchResultsChanged(bool value)
    {
        OnPropertyChanged(nameof(CanLoadMoreSearchResults));
    }

    partial void OnSelectedSearchResultChanged(SearchResultItemViewModel? value)
    {
        if (value is not null)
        {
            _ = SelectSearchResultAsync(value);
        }
    }

    public void LoadSearchHistory()
    {
        SearchHistory.Clear();
        foreach (var entry in _searchHistoryService.GetAll())
            SearchHistory.Add(entry);
        OnPropertyChanged(nameof(HasSearchHistory));
    }

    public void FilterSearchHistory(string keyword)
    {
        SearchHistory.Clear();
        foreach (var entry in _searchHistoryService.Search(keyword))
            SearchHistory.Add(entry);
        OnPropertyChanged(nameof(HasSearchHistory));
    }

    [RelayCommand]
    private void SelectHistoryEntry(SearchHistoryEntry? entry)
    {
        if (entry is null) return;
        SearchKeyword = entry.Keyword;
        _ = SearchCommand.ExecuteAsync(null);
    }

    [RelayCommand]
    private void RemoveHistoryEntry(SearchHistoryEntry? entry)
    {
        if (entry is null) return;
        _searchHistoryService.Remove(entry.Keyword, entry.SiteKey);
        LoadSearchHistory();
    }

    [RelayCommand]
    private void ClearSearchHistory()
    {
        _searchHistoryService.Clear();
        LoadSearchHistory();
    }

    partial void OnCurrentMangaChanged(MangaResolveResponse? value)
    {
        _dispatcherQueue.TryEnqueue(() =>
        {
            OnPropertyChanged(nameof(HasManga));
            OnPropertyChanged(nameof(CurrentMangaTitle));
            OnPropertyChanged(nameof(CurrentMangaSiteName));
            OnPropertyChanged(nameof(CurrentMangaLatestChapter));
            OnPropertyChanged(nameof(CurrentMangaCoverUrl));
            OnPropertyChanged(nameof(CurrentMangaDetailHint));
            AvailableChapters.Clear();
            if (value?.Chapters is { Count: > 0 })
            {
                for (var index = 0; index < value.Chapters.Count; index++)
                {
                    var chapter = value.Chapters[index];
                    var shouldSelect = _applicationSettings.ChapterSelectionMode switch
                    {
                        ApplicationSettingsService.SelectAll => true,
                        ApplicationSettingsService.SelectLatest => index == value.Chapters.Count - 1,
                        _ => false,
                    };
                    var item = new ChapterItemViewModel
                    {
                        Title = chapter.Title,
                        Url = chapter.Url,
                        IsSelected = shouldSelect,
                    };
                    item.PropertyChanged += (_, _) =>
                    {
                        OnPropertyChanged(nameof(SelectedChapterCount));
                        OnPropertyChanged(nameof(ChapterSelectionSummary));
                        OnPropertyChanged(nameof(CanStartDownload));
                        OnPropertyChanged(nameof(DownloadButtonText));
                    };
                    AvailableChapters.Add(item);
                }
            }
            OnPropertyChanged(nameof(HasChapters));
            OnPropertyChanged(nameof(ShowChapterSelection));
            OnPropertyChanged(nameof(SelectedChapterCount));
            OnPropertyChanged(nameof(ChapterSelectionSummary));
            OnPropertyChanged(nameof(CanStartDownload));
            OnPropertyChanged(nameof(DownloadButtonText));
        });
    }

    [RelayCommand]
    private void SelectAllChapters()
    {
        foreach (var ch in AvailableChapters) ch.IsSelected = true;
    }

    [RelayCommand]
    private void DeselectAllChapters()
    {
        foreach (var ch in AvailableChapters) ch.IsSelected = false;
    }

    [RelayCommand]
    public async Task StartDownloadAsync(CancellationToken cancellationToken = default)
    {
        var downloadUrl = CurrentManga?.MangaUrl ?? SelectedSearchResult?.MangaUrl ?? "";
        if (string.IsNullOrWhiteSpace(downloadUrl))
        {
            PageError = "请先搜索并选择一部漫画。";
            return;
        }

        if (HasChapters && SelectedChapterCount == 0)
        {
            PageError = "请至少选择一个需要下载的章节。";
            return;
        }

        IsBusy = true;
        PageError = string.Empty;
        try
        {
            var selectedChapters = HasChapters
                ? AvailableChapters
                    .Where(c => c.IsSelected)
                    .Select(c => string.IsNullOrWhiteSpace(c.Url) ? c.Title : c.Url)
                    .ToList()
                : null;

            var task = await _backendClient.CreateDownloadAsync(
                new DownloadCreateRequest
                {
                    Url = downloadUrl,
                    SiteKey = SiteCatalog.Key,
                    Source = "winui",
                    Chapters = selectedChapters,
                },
                cancellationToken);

            UpsertTask(task);
            CurrentTask = Tasks.FirstOrDefault(item => item.Id == task.Id) ?? SelectPreferredTask();
            CurrentTaskId = task.Id;
            _lastEventId = 0;
            OnPropertyChanged(nameof(TaskCountSummary));

            _subscriptionCts?.Cancel();
            _subscriptionCts = new CancellationTokenSource();
            _ = SubscribeToTaskAsync(CurrentTaskId, _subscriptionCts.Token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (BackendApiException ex)
        {
            PageError = $"创建下载任务失败: {ex.Error.Message}";
        }
        catch (HttpRequestException)
        {
            PageError = "内置服务调用失败，请重试。";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    public async Task LoadHistoryAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await _backendClient.GetDownloadHistoryAsync(HistoryPage, 20, cancellationToken);
            _dispatcherQueue.TryEnqueue(() =>
            {
                foreach (var oldItem in HistoryItems)
                {
                    oldItem.PropertyChanged -= OnHistoryItemPropertyChanged;
                }
                HistoryItems.Clear();
                foreach (var item in result.Items)
                {
                    item.PropertyChanged += OnHistoryItemPropertyChanged;
                    HistoryItems.Add(item);
                }
                HistoryTotal = result.Total;
                OnPropertyChanged(nameof(HasNextHistoryPage));
                NotifyHistorySelectionChanged();
            });
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
            PageError = "内置服务调用失败，请重试。";
        }
        catch (Exception ex)
        {
            PageError = $"加载历史异常: {ex.Message}";
        }
    }

    [RelayCommand]
    public async Task HistoryPrevAsync(CancellationToken cancellationToken = default)
    {
        if (HistoryPage <= 1) return;
        HistoryPage--;
        await LoadHistoryAsync(cancellationToken);
    }

    [RelayCommand]
    public async Task HistoryNextAsync(CancellationToken cancellationToken = default)
    {
        if (!HasNextHistoryPage) return;
        HistoryPage++;
        await LoadHistoryAsync(cancellationToken);
    }

    [RelayCommand]
    public async Task ClearHistoryAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await _backendClient.ClearDownloadHistoryAsync(cancellationToken);
            _dispatcherQueue.TryEnqueue(() =>
            {
                foreach (var item in HistoryItems)
                {
                    item.PropertyChanged -= OnHistoryItemPropertyChanged;
                }
                HistoryItems.Clear();
                HistoryPage = 1;
                HistoryTotal = 0;
                NotifyHistorySelectionChanged();
            });
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
            PageError = "内置服务调用失败，请重试。";
        }
        catch (Exception ex)
        {
            PageError = $"清除历史异常: {ex.Message}";
        }
    }

    [RelayCommand]
    public void ToggleBatchMode()
    {
        IsBatchMode = !IsBatchMode;
        if (!IsBatchMode)
        {
            foreach (var task in Tasks)
            {
                task.IsSelected = false;
                foreach (var chapter in task.Chapters)
                {
                    chapter.IsSelected = false;
                }
            }
        }
    }

    [RelayCommand]
    public void SelectAllTasks()
    {
        foreach (var task in Tasks)
        {
            task.IsSelected = true;
        }
    }

    [RelayCommand]
    public void DeselectAllTasks()
    {
        foreach (var task in Tasks)
        {
            task.IsSelected = false;
            foreach (var chapter in task.Chapters)
            {
                chapter.IsSelected = false;
            }
        }
    }

    [RelayCommand]
    public void SelectAllHistory()
    {
        foreach (var item in HistoryItems)
        {
            item.IsSelected = true;
        }
    }

    [RelayCommand]
    public void DeselectAllHistory()
    {
        foreach (var item in HistoryItems)
        {
            item.IsSelected = false;
        }
    }

    public async Task DeleteHistoryItemsAsync(
        IReadOnlyCollection<string> historyIds,
        CancellationToken cancellationToken = default)
    {
        var ids = historyIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (ids.Count == 0)
        {
            return;
        }

        IsBusy = true;
        PageError = string.Empty;
        try
        {
            var removed = await _backendClient.DeleteDownloadHistoryAsync(ids, cancellationToken);
            if (removed > 0)
            {
                if (HistoryPage > 1 && removed >= HistoryItems.Count)
                {
                    HistoryPage--;
                }
                await LoadHistoryAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (BackendApiException ex)
        {
            PageError = ex.Error.Message;
        }
        catch (HttpRequestException)
        {
            PageError = "内置服务调用失败，请重试。";
        }
        catch (Exception ex)
        {
            PageError = $"删除历史记录异常：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void OnHistoryItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DownloadHistoryItem.IsSelected))
        {
            NotifyHistorySelectionChanged();
        }
    }

    private void NotifyHistorySelectionChanged()
    {
        OnPropertyChanged(nameof(SelectedHistoryCount));
        OnPropertyChanged(nameof(HasSelectedHistory));
        OnPropertyChanged(nameof(SelectedHistorySummary));
    }

    [RelayCommand]
    public void SelectAllCurrentTaskChapters()
    {
        if (CurrentTask is null) return;
        foreach (var chapter in CurrentTask.Chapters)
        {
            chapter.IsSelected = true;
        }
    }

    [RelayCommand]
    public void DeselectAllCurrentTaskChapters()
    {
        if (CurrentTask is null) return;
        foreach (var chapter in CurrentTask.Chapters)
        {
            chapter.IsSelected = false;
        }
    }

    public int SelectedTaskCount => Tasks.Count(t => t.IsSelected);

    public int SelectedDownloadChapterCount => Tasks.Sum(task => task.SelectedChapterCount);

    public int SelectedBatchItemCount => SelectedTaskCount + SelectedDownloadChapterCount;

    public string SelectedBatchSummary =>
        $"已选 {SelectedTaskCount} 个任务 · {SelectedDownloadChapterCount} 个章节";

    public bool HasSelectedTasks => SelectedBatchItemCount > 0;

    partial void OnIsBatchModeChanged(bool value)
    {
        foreach (var task in Tasks)
        {
            task.IsBatchMode = value;
        }
        OnPropertyChanged(nameof(SelectedTaskCount));
        OnPropertyChanged(nameof(SelectedDownloadChapterCount));
        OnPropertyChanged(nameof(SelectedBatchItemCount));
        OnPropertyChanged(nameof(SelectedBatchSummary));
        OnPropertyChanged(nameof(HasSelectedTasks));
    }

    [RelayCommand]
    public async Task BatchStopAsync(CancellationToken cancellationToken = default)
    {
        var selectedTaskIds = Tasks.Where(task => task.IsSelected).Select(task => task.Id).ToHashSet(StringComparer.Ordinal);
        var selectedChapterGroups = Tasks
            .Where(task => !selectedTaskIds.Contains(task.Id) && task.HasSelectedChapters)
            .Select(task => new
            {
                Task = task,
                ChapterIds = task.Chapters.Where(chapter => chapter.IsSelected).Select(chapter => chapter.Id).ToList(),
            })
            .ToList();
        if (selectedTaskIds.Count == 0 && selectedChapterGroups.Count == 0)
        {
            PageError = "请先选择要停止的任务或章节。";
            return;
        }

        IsBusy = true;
        PageError = string.Empty;
        try
        {
            var failedCount = 0;
            if (selectedTaskIds.Count > 0)
            {
                var result = await _backendClient.BatchStopDownloadsAsync(selectedTaskIds.ToList(), cancellationToken);
                failedCount += result.Failed.Count;
                foreach (var taskId in result.Stopped)
                {
                    var task = Tasks.FirstOrDefault(item => item.Id == taskId);
                    if (task is not null)
                    {
                        task.Status = "stopping";
                        task.StatusText = "正在停止";
                    }
                }
            }

            foreach (var group in selectedChapterGroups)
            {
                var result = await _backendClient.BatchStopChaptersAsync(
                    group.Task.Id,
                    group.ChapterIds,
                    cancellationToken);
                failedCount += result.Failed.Count;
                var refreshed = await _backendClient.GetDownloadAsync(group.Task.Id, cancellationToken);
                UpsertTask(refreshed);
            }

            if (failedCount > 0)
            {
                PageError = $"{failedCount} 个已完成或不可控制的项目未能停止。";
            }
            NotifyBatchSelectionChanged();
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
            PageError = "内置服务调用失败，请重试。";
        }
        catch (Exception ex)
        {
            PageError = $"批量停止异常: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    public async Task BatchDeleteAsync(CancellationToken cancellationToken = default)
    {
        var selectedTaskIds = Tasks.Where(task => task.IsSelected).Select(task => task.Id).ToHashSet(StringComparer.Ordinal);
        var selectedChapterGroups = Tasks
            .Where(task => !selectedTaskIds.Contains(task.Id) && task.HasSelectedChapters)
            .Select(task => new
            {
                Task = task,
                ChapterIds = task.Chapters.Where(chapter => chapter.IsSelected).Select(chapter => chapter.Id).ToList(),
            })
            .ToList();
        if (selectedTaskIds.Count == 0 && selectedChapterGroups.Count == 0)
        {
            PageError = "请先选择要删除的任务或章节。";
            return;
        }

        IsBusy = true;
        PageError = string.Empty;
        try
        {
            var failedCount = 0;
            if (selectedTaskIds.Count > 0)
            {
                var result = await _backendClient.BatchDeleteDownloadsAsync(selectedTaskIds.ToList(), cancellationToken);
                failedCount += result.Failed.Count;
                foreach (var taskId in result.Deleted)
                {
                    var task = Tasks.FirstOrDefault(item => item.Id == taskId);
                    if (task is not null)
                    {
                        Tasks.Remove(task);
                    }
                }
            }

            foreach (var group in selectedChapterGroups)
            {
                var result = await _backendClient.BatchDeleteChaptersAsync(
                    group.Task.Id,
                    group.ChapterIds,
                    cancellationToken);
                failedCount += result.Failed.Count;
                var refreshed = await _backendClient.GetDownloadAsync(group.Task.Id, cancellationToken);
                UpsertTask(refreshed);
            }

            if (failedCount > 0)
            {
                PageError = $"{failedCount} 个项目未能删除；正在下载的章节可能仍在释放文件。";
            }
            if (CurrentTask is not null && !Tasks.Contains(CurrentTask))
            {
                CurrentTask = SelectPreferredTask();
            }
            OnPropertyChanged(nameof(TaskCountSummary));
            NotifyBatchSelectionChanged();
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
            PageError = "内置服务调用失败，请重试。";
        }
        catch (Exception ex)
        {
            PageError = $"批量删除异常: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    public Task RefreshBackendAsync(CancellationToken cancellationToken = default)
        => _shellViewModel.RefreshHealthAsync(cancellationToken);

    [RelayCommand(CanExecute = nameof(CanPause))]
    public Task PauseAsync(CancellationToken cancellationToken = default)
        => ExecuteTaskMutationAsync(() => _backendClient.PauseDownloadAsync(CurrentTaskId, cancellationToken));

    [RelayCommand(CanExecute = nameof(CanResume))]
    public Task ResumeAsync(CancellationToken cancellationToken = default)
        => ExecuteTaskMutationAsync(() => _backendClient.ResumeDownloadAsync(CurrentTaskId, cancellationToken));

    [RelayCommand(CanExecute = nameof(CanStop))]
    public Task StopAsync(CancellationToken cancellationToken = default)
        => ExecuteTaskMutationAsync(() => _backendClient.StopDownloadAsync(CurrentTaskId, cancellationToken));

    private bool CanPause()
        => CurrentTask is not null && CurrentTask.Status is "running" or "dispatched";

    private bool CanResume()
        => CurrentTask is not null && CurrentTask.Status is "paused" or "pausing";

    private bool CanStop()
        => CurrentTask is not null && CurrentTask.Status is "pending" or "running" or "paused" or "pausing" or "dispatched";

    partial void OnCurrentTaskChanged(DownloadTaskItemViewModel? value)
    {
        CurrentTaskId = value?.Id ?? string.Empty;
        NotifyTaskControlCanExecuteChanged();
        OnPropertyChanged(nameof(HasCurrentTask));
    }

    private void NotifyTaskControlCanExecuteChanged()
    {
        PauseCommand.NotifyCanExecuteChanged();
        ResumeCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
    }

    private async Task ExecuteTaskMutationAsync(Func<Task> mutation)
    {
        if (string.IsNullOrWhiteSpace(CurrentTaskId))
        {
            return;
        }

        try
        {
            await mutation();
            var refreshed = await _backendClient.GetDownloadAsync(CurrentTaskId);
            UpsertTask(refreshed);
            CurrentTask = Tasks.FirstOrDefault(item => item.Id == refreshed.Id) ?? SelectPreferredTask();
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
            PageError = "内置服务调用失败，请重试。";
        }
        catch (Exception ex)
        {
            PageError = $"操作异常: {ex.Message}";
        }
    }

    private async Task SubscribeToTaskAsync(string taskId, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var stateEvent in _eventStream.SubscribeAsync(taskId, _lastEventId, cancellationToken))
            {
                _lastEventId = Math.Max(_lastEventId, stateEvent.EventId);
                ApplyEvent(stateEvent);
            }
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
            PageError = "内置服务调用失败，请重试。";
        }
        catch (Exception ex)
        {
            PageError = $"任务状态订阅异常: {ex.Message}";
            var snapshot = await _backendClient.GetDownloadAsync(taskId, CancellationToken.None);
            UpsertTask(snapshot);
        }
    }

    private void ApplyEvent(DownloadStateEvent stateEvent)
    {
        if (string.IsNullOrWhiteSpace(stateEvent.JsonPayload))
        {
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(stateEvent.JsonPayload);
            var root = document.RootElement;

            // The in-process event stream sends the full task object directly,
            // or wrapped in a "payload" key for named events.
            var taskElement = root.TryGetProperty("payload", out var payload) ? payload : root;
            var dto = taskElement.Deserialize<DownloadTaskDto>(_jsonOptions);
            if (dto is null)
            {
                return;
            }

            _dispatcherQueue.TryEnqueue(() =>
            {
                UpsertTask(dto);
                if (dto.Id == CurrentTaskId)
                {
                    CurrentTask = Tasks.FirstOrDefault(item => item.Id == dto.Id);
                }
                else if (CurrentTask is null)
                {
                    CurrentTask = SelectPreferredTask();
                }
            });
        }
        catch (JsonException)
        {
            // Ignore malformed task-state payloads.
        }
    }

    private void UpsertTask(DownloadTaskDto dto)
    {
        var existing = Tasks.FirstOrDefault(item => string.Equals(item.Id, dto.Id, StringComparison.Ordinal));
        if (existing is null)
        {
            var vm = DownloadTaskItemViewModel.FromDto(dto);
            vm.IsBatchMode = IsBatchMode;
            AttachTaskSelectionHandlers(vm);
            Tasks.Insert(0, vm);
            OnPropertyChanged(nameof(TaskCountSummary));
            return;
        }

        existing.UpdateFrom(dto);
        if (ReferenceEquals(existing, CurrentTask))
        {
            NotifyTaskControlCanExecuteChanged();
        }
    }

    private void AttachTaskSelectionHandlers(DownloadTaskItemViewModel task)
    {
        task.PropertyChanged += OnTaskSelectionPropertyChanged;
    }

    private void OnTaskSelectionPropertyChanged(
        object? sender,
        System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DownloadTaskItemViewModel.IsSelected)
            or nameof(DownloadTaskItemViewModel.SelectedChapterCount))
        {
            NotifyBatchSelectionChanged();
        }
    }

    private void NotifyBatchSelectionChanged()
    {
        OnPropertyChanged(nameof(SelectedTaskCount));
        OnPropertyChanged(nameof(SelectedDownloadChapterCount));
        OnPropertyChanged(nameof(SelectedBatchItemCount));
        OnPropertyChanged(nameof(SelectedBatchSummary));
        OnPropertyChanged(nameof(HasSelectedTasks));
    }

    private DownloadTaskItemViewModel? SelectPreferredTask()
    {
        static int Rank(DownloadTaskItemViewModel item)
        {
            return item.Status switch
            {
                "running" => 0,
                "paused" => 1,
                "dispatched" => 2,
                "stopping" => 3,
                "failed" => 4,
                "partial" => 5,
                "failed_to_dispatch" => 6,
                _ => 7,
            };
        }

        return Tasks
            .OrderBy(Rank)
            .ThenByDescending(item => item.Id, StringComparer.Ordinal)
            .FirstOrDefault();
    }
}

