using System;
using System.Collections.ObjectModel;
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

    public DownloadPageViewModel(BackendClient backendClient, DownloadEventStream eventStream, ShellViewModel shellViewModel, SearchHistoryService searchHistoryService)
    {
        _backendClient = backendClient;
        _eventStream = eventStream;
        _shellViewModel = shellViewModel;
        _searchHistoryService = searchHistoryService;
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        SiteOptions = new ObservableCollection<string>(SiteCatalog.DownloadSites.Select(s => s.DisplayName));
        SelectedSite = SiteCatalog.DownloadSites.FirstOrDefault(s => s.Key == "baozimh")?.DisplayName ?? SiteOptions.FirstOrDefault();
        LoadSearchHistory();
    }

    public ObservableCollection<DownloadTaskItemViewModel> Tasks { get; } = [];

    public ObservableCollection<string> SiteOptions { get; }

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

    [ObservableProperty]
    public partial MangaResolveResponse? CurrentManga { get; set; }

    [ObservableProperty]
    public partial DownloadTaskItemViewModel? CurrentTask { get; set; }

    [ObservableProperty]
    public partial string CurrentTaskId { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string SearchKeyword { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string? SelectedSite { get; set; }

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
                vm.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(DownloadTaskItemViewModel.IsSelected))
                    {
                        OnPropertyChanged(nameof(SelectedTaskCount));
                        OnPropertyChanged(nameof(HasSelectedTasks));
                    }
                };
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
            PageError = "无法连接后端服务，请点击「启动后端」。";
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
            var siteKey = SiteCatalog.GetKey(SelectedSite ?? string.Empty);

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
                _searchHistoryService.Add(keyword, siteKey, SelectedSite ?? string.Empty, SearchResults.Count);
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
            PageError = "无法连接后端服务，请确认后端已启动。";
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
            var siteKey = SiteCatalog.GetKey(SelectedSite ?? string.Empty);
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
            PageError = "无法连接后端服务，请确认后端已启动。";
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
                    SiteKey = SiteCatalog.GetKey(SelectedSite ?? string.Empty),
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
            PageError = "无法连接后端服务，请确认后端已启动。";
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
                    SiteKey = SiteCatalog.GetKey(SelectedSite ?? string.Empty),
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
            PageError = "无法连接后端服务，请确认后端已启动。";
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
        SelectedSite = entry.SiteName;
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
                foreach (var ch in value.Chapters)
                {
                    var item = new ChapterItemViewModel { Title = ch.Title, IsSelected = true };
                    item.PropertyChanged += (_, _) =>
                    {
                        OnPropertyChanged(nameof(SelectedChapterCount));
                        OnPropertyChanged(nameof(ChapterSelectionSummary));
                    };
                    AvailableChapters.Add(item);
                }
            }
            OnPropertyChanged(nameof(HasChapters));
            OnPropertyChanged(nameof(ShowChapterSelection));
            OnPropertyChanged(nameof(SelectedChapterCount));
            OnPropertyChanged(nameof(ChapterSelectionSummary));
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

        IsBusy = true;
        PageError = string.Empty;
        try
        {
            var selectedChapters = HasChapters
                ? AvailableChapters.Where(c => c.IsSelected).Select(c => c.Title).ToList()
                : null;

            var task = await _backendClient.CreateDownloadAsync(
                new DownloadCreateRequest
                {
                    Url = downloadUrl,
                    SiteKey = SiteCatalog.GetKey(SelectedSite ?? string.Empty),
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
            PageError = "无法连接后端服务，请确认后端已启动。";
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
                HistoryItems.Clear();
                foreach (var item in result.Items)
                {
                    HistoryItems.Add(item);
                }
                HistoryTotal = result.Total;
                OnPropertyChanged(nameof(HasNextHistoryPage));
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
            PageError = "无法连接后端服务，请确认后端已启动。";
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
                HistoryItems.Clear();
                HistoryTotal = 0;
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
            PageError = "无法连接后端服务，请确认后端已启动。";
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
        }
    }

    public int SelectedTaskCount => Tasks.Count(t => t.IsSelected);

    public bool HasSelectedTasks => SelectedTaskCount > 0;

    partial void OnIsBatchModeChanged(bool value)
    {
        foreach (var task in Tasks)
        {
            task.IsBatchMode = value;
        }
        OnPropertyChanged(nameof(SelectedTaskCount));
        OnPropertyChanged(nameof(HasSelectedTasks));
    }

    [RelayCommand]
    public async Task BatchStopAsync(CancellationToken cancellationToken = default)
    {
        var selectedIds = Tasks.Where(t => t.IsSelected).Select(t => t.Id).ToList();
        if (selectedIds.Count == 0)
        {
            PageError = "请先选择要停止的任务。";
            return;
        }

        IsBusy = true;
        PageError = string.Empty;
        try
        {
            var result = await _backendClient.BatchStopDownloadsAsync(selectedIds, cancellationToken);
            foreach (var taskId in result.Stopped)
            {
                var task = Tasks.FirstOrDefault(t => t.Id == taskId);
                if (task is not null)
                {
                    task.Status = "stopping";
                    task.StatusText = "正在停止";
                }
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
            PageError = "无法连接后端服务，请确认后端已启动。";
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
        var selectedIds = Tasks.Where(t => t.IsSelected).Select(t => t.Id).ToList();
        if (selectedIds.Count == 0)
        {
            PageError = "请先选择要删除的任务。";
            return;
        }

        IsBusy = true;
        PageError = string.Empty;
        try
        {
            var result = await _backendClient.BatchDeleteDownloadsAsync(selectedIds, cancellationToken);
            foreach (var taskId in result.Deleted)
            {
                var task = Tasks.FirstOrDefault(t => t.Id == taskId);
                if (task is not null)
                {
                    Tasks.Remove(task);
                }
            }
            OnPropertyChanged(nameof(TaskCountSummary));
            OnPropertyChanged(nameof(SelectedTaskCount));
            OnPropertyChanged(nameof(HasSelectedTasks));
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
            PageError = $"批量删除异常: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    public Task EnsureBackendRunningAsync(CancellationToken cancellationToken = default)
        => _shellViewModel.EnsureBackendRunningAsync(cancellationToken);

    [RelayCommand]
    public Task RefreshBackendAsync(CancellationToken cancellationToken = default)
        => _shellViewModel.RefreshHealthAsync(cancellationToken);

    [RelayCommand]
    public Task StopBackendAsync(CancellationToken cancellationToken = default)
        => _shellViewModel.StopBackendAsync(cancellationToken);

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
        => CurrentTask is not null && CurrentTask.Status is "running" or "paused" or "pausing" or "dispatched" or "stopping";

    partial void OnCurrentTaskChanged(DownloadTaskItemViewModel? value)
    {
        CurrentTaskId = value?.Id ?? string.Empty;
        PauseCommand.NotifyCanExecuteChanged();
        ResumeCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(HasCurrentTask));
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
            PageError = "无法连接后端服务，请确认后端已启动。";
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
            await foreach (var sseEvent in _eventStream.SubscribeAsync(taskId, _lastEventId, cancellationToken))
            {
                _lastEventId = Math.Max(_lastEventId, sseEvent.EventId);
                ApplyEvent(sseEvent);
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
            PageError = "无法连接后端服务，请确认后端已启动。";
        }
        catch (Exception ex)
        {
            PageError = $"SSE 异常: {ex.Message}";
            var snapshot = await _backendClient.GetDownloadAsync(taskId, CancellationToken.None);
            UpsertTask(snapshot);
        }
    }

    private void ApplyEvent(SseDownloadEvent sseEvent)
    {
        if (string.IsNullOrWhiteSpace(sseEvent.JsonPayload))
        {
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(sseEvent.JsonPayload);
            var root = document.RootElement;

            // Server sends the full task object directly as the SSE data,
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
            // Ignore malformed SSE payloads
        }
    }

    private void UpsertTask(DownloadTaskDto dto)
    {
        var existing = Tasks.FirstOrDefault(item => string.Equals(item.Id, dto.Id, StringComparison.Ordinal));
        if (existing is null)
        {
            var vm = DownloadTaskItemViewModel.FromDto(dto);
            vm.IsBatchMode = IsBatchMode;
            vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(DownloadTaskItemViewModel.IsSelected))
                {
                    OnPropertyChanged(nameof(SelectedTaskCount));
                    OnPropertyChanged(nameof(HasSelectedTasks));
                }
            };
            Tasks.Insert(0, vm);
            OnPropertyChanged(nameof(TaskCountSummary));
            return;
        }

        existing.UpdateFrom(dto);
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

