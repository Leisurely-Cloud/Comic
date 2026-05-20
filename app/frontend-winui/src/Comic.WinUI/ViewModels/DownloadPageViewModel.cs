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

    public DownloadPageViewModel(BackendClient backendClient, DownloadEventStream eventStream, ShellViewModel shellViewModel)
    {
        _backendClient = backendClient;
        _eventStream = eventStream;
        _shellViewModel = shellViewModel;
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        SiteOptions = new ObservableCollection<string>(SiteCatalog.DownloadSites.Select(s => s.DisplayName));
        SelectedSite = SiteCatalog.DownloadSites.FirstOrDefault(s => s.Key == "baozimh")?.DisplayName ?? SiteOptions.FirstOrDefault();
    }

    public ObservableCollection<DownloadTaskItemViewModel> Tasks { get; } = [];

    public ObservableCollection<string> SiteOptions { get; }

    public ObservableCollection<SearchResultItemViewModel> SearchResults { get; } = [];

    public ObservableCollection<ChapterItemViewModel> AvailableChapters { get; } = [];

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
    public partial string SearchStatusText { get; set; } = string.Empty;

    public string TaskCountSummary => Tasks.Count == 0 ? "暂无任务" : $"共 {Tasks.Count} 个任务";

    public bool HasCurrentTask => CurrentTask is not null;

    public bool HasManga => CurrentManga is not null;

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
                Tasks.Add(DownloadTaskItemViewModel.FromDto(task));
            }

            CurrentTask = SelectPreferredTask();
            CurrentTaskId = CurrentTask?.Id ?? string.Empty;
            OnPropertyChanged(nameof(TaskCountSummary));
        }
        catch (BackendApiException ex)
        {
            PageError = ex.Error.Message;
        }
        catch (HttpRequestException)
        {
            PageError = "无法连接后端服务，请点击「启动后端」。";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    public async Task SearchAsync(CancellationToken cancellationToken = default)
    {
        System.Diagnostics.Debug.WriteLine($"[SearchAsync] called, SearchKeyword='{SearchKeyword}'");

        if (string.IsNullOrWhiteSpace(SearchKeyword))
        {
            PageError = "请输入搜索关键词。";
            return;
        }

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
            System.Diagnostics.Debug.WriteLine($"[SearchAsync] searching '{SearchKeyword.Trim()}' on site '{siteKey}'");

            var result = await _backendClient.SearchAsync(
                SearchKeyword.Trim(), siteKey, 1, token);

            System.Diagnostics.Debug.WriteLine($"[SearchAsync] got {result.Items.Count} results, total={result.Total}");

            foreach (var item in result.Items)
            {
                SearchResults.Add(SearchResultItemViewModel.FromSearch(item));
            }

            HasSearchResults = SearchResults.Count > 0;
            SearchStatusText = HasSearchResults
                ? $"找到 {SearchResults.Count} 部漫画"
                : "未找到相关漫画，请换一个关键词试试";

            System.Diagnostics.Debug.WriteLine($"[SearchAsync] SearchResults.Count={SearchResults.Count}, HasSearchResults={HasSearchResults}");
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

    partial void OnIsResolvingChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowChapterSelection));
    }

    partial void OnSelectedSearchResultChanged(SearchResultItemViewModel? value)
    {
        if (value is not null)
        {
            _ = SelectSearchResultAsync(value);
        }
    }

    partial void OnCurrentMangaChanged(MangaResolveResponse? value)
    {
        OnPropertyChanged(nameof(HasManga));
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
        catch (BackendApiException ex)
        {
            PageError = ex.Error.Message;
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
        }
        catch (BackendApiException ex)
        {
            PageError = ex.Error.Message;
        }
        catch (Exception ex)
        {
            PageError = ex.Message;
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
            Tasks.Insert(0, DownloadTaskItemViewModel.FromDto(dto));
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

