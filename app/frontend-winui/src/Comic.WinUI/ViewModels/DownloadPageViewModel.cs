using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Comic.WinUI.Models;
using Comic.WinUI.Services;
using Comic.WinUI.Services.Native;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace Comic.WinUI.ViewModels;

public partial class DownloadPageViewModel : ObservableObject, IDisposable
{
    private readonly BackendClient _backendClient;
    private readonly DownloadEventStream _eventStream;
    private readonly ShellViewModel _shellViewModel;
    private readonly SearchHistoryService _searchHistoryService;
    private readonly ApplicationSettingsService _applicationSettings;
    private readonly ILogger<DownloadPageViewModel> _logger;
    private readonly IDispatcher _dispatcherQueue;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = SnakeCaseNamingPolicy.Instance,
    };

    private CancellationTokenSource? _subscriptionCts;
    private CancellationTokenSource? _searchCts;
    private CancellationTokenSource? _resolveCts;
    private CancellationTokenSource? _commentsCts;
    private int _lastEventId;
    private int _searchPage;
    private int _lastSearchPageSize;

    public const int CommentPageSize = 10;

    public DownloadPageViewModel(
        BackendClient backendClient,
        DownloadEventStream eventStream,
        ShellViewModel shellViewModel,
        SearchHistoryService searchHistoryService,
        ApplicationSettingsService applicationSettings,
        ILogger<DownloadPageViewModel> logger,
        IDispatcher dispatcher)
    {
        _backendClient = backendClient;
        _eventStream = eventStream;
        _shellViewModel = shellViewModel;
        _searchHistoryService = searchHistoryService;
        _applicationSettings = applicationSettings;
        _logger = logger;
        _dispatcherQueue = dispatcher;
        HistoryItems.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasHistoryItems));
        Comments.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasComments));
            OnPropertyChanged(nameof(ShowCommentsList));
            OnPropertyChanged(nameof(ShowCommentsEmptyState));
        };
        LoadSearchHistory();
    }

    /// <summary>
    /// 页面导航离开时调用:取消 150ms 状态轮询与在途的搜索/解析请求。
    /// 这个 ViewModel 是 transient,每次导航都会新建一个;不取消的话,被丢弃的实例
    /// 会连同它的轮询循环继续存活,反复切换页面就会叠加多个独立轮询器。
    /// </summary>
    public void Dispose()
    {
        CancelAndDispose(ref _subscriptionCts);
        CancelAndDispose(ref _searchCts);
        CancelAndDispose(ref _resolveCts);
        CancelAndDispose(ref _commentsCts);
    }

    private static void CancelAndDispose(ref CancellationTokenSource? source)
    {
        var current = source;
        source = null;
        if (current is null) return;
        try
        {
            current.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 已释放,无需再取消。
        }

        current.Dispose();
    }

    public ObservableCollection<DownloadTaskItemViewModel> Tasks { get; } = [];

    public ObservableCollection<SearchResultItemViewModel> SearchResults { get; } = [];

    public ObservableCollection<ChapterItemViewModel> AvailableChapters { get; } = [];

    public ObservableCollection<DownloadHistoryItem> HistoryItems { get; } = [];

    public ObservableCollection<SearchHistoryEntry> SearchHistory { get; } = [];

    public ObservableCollection<MangaCommentDto> Comments { get; } = [];

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
    [NotifyPropertyChangedFor(nameof(HasDownloadNotice))]
    public partial string DownloadNotice { get; set; } = string.Empty;

    public bool HasDownloadNotice => !string.IsNullOrWhiteSpace(DownloadNotice);

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

    [ObservableProperty]
    public partial bool IsLoadingComments { get; set; }

    [ObservableProperty]
    public partial string CommentsError { get; set; } = string.Empty;

    [ObservableProperty]
    public partial int CommentTotal { get; set; }

    [ObservableProperty]
    public partial int CommentPage { get; set; } = 1;

    public bool HasComments => Comments.Count > 0;

    public bool HasCommentsError => !string.IsNullOrWhiteSpace(CommentsError);

    public bool ShowCommentsList => HasComments && !IsLoadingComments && !HasCommentsError;

    public bool ShowCommentsEmptyState => HasManga && !IsLoadingComments && !HasCommentsError && !HasComments;

    public bool CanGoPreviousComments => HasManga && !IsLoadingComments && CommentPage > 1;

    public bool CanGoNextComments => HasManga && !IsLoadingComments && CommentPage * CommentPageSize < CommentTotal;

    public string CommentSummary => CommentTotal > 0 ? $"{CommentTotal} 条" : "暂无评论";

    public string CommentPageSummary
    {
        get
        {
            var pageCount = CommentTotal > 0
                ? Math.Max((int)Math.Ceiling(CommentTotal / (double)CommentPageSize), 1)
                : 1;
            return $"第 {CommentPage} / {pageCount} 页";
        }
    }

    public string CurrentMangaTitle => CurrentManga?.Title ?? string.Empty;
    public string CurrentMangaSiteName => CurrentManga?.SiteName ?? string.Empty;
    public string CurrentMangaLatestChapter => CurrentManga?.LatestChapter ?? string.Empty;
    public string CurrentMangaCoverUrl => CurrentManga?.CoverUrl ?? string.Empty;
    public string CurrentMangaDetailHint => CurrentManga?.DetailHint ?? string.Empty;

    public bool HasChapters => AvailableChapters.Count > 0;

    public bool ShowChapterSelection => HasChapters && !IsResolving;

    /// <summary>
    /// 选章面板的空状态是否可见。必须同时排除“解析中”:解析进行中 CurrentManga 还是 null,
    /// 若只看 HasManga,空状态就会和「正在获取漫画详情和章节」遮罩层同时显示、文字互相重叠
    /// (遮罩层用的 CardBackgroundFillColorDefaultBrush 是半透明卡片色,挡不住下面的内容)。
    /// </summary>
    public bool ShowEmptyState => !HasManga && !IsResolving;

    /// <summary>
    /// 漫画详情/选章内容是否可见。同样要排除“解析中”:切换到另一部漫画时 CurrentManga
    /// 仍是上一部(非 null),不排除就会让旧内容和遮罩层文字重叠。
    /// 加上 <see cref="ShowEmptyState"/> 与 IsResolving,三层任意时刻恰好只有一层可见。
    /// </summary>
    public bool ShowMangaContent => HasManga && !IsResolving;

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
        DownloadNotice = string.Empty;
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

        // 输入是 JM 编号或禁漫天堂链接时,直接精确解析,不走站点关键词搜索。
        if (JmComicService.ParseMangaId(keyword).MangaId is not null)
        {
            await SearchByJmIdAsync(keyword, cancellationToken);
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
            _logger.LogInformation("[SearchAsync] cancelled");
        }
        catch (BackendApiException ex)
        {
            _logger.LogWarning("[SearchAsync] BackendApiException: {Message}", ex.Error.Message);
            PageError = $"搜索出错: {ex.Error.Message}";
            SearchStatusText = "搜索失败";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[SearchAsync] Exception: {Type}", ex.GetType().Name);
            PageError = $"搜索异常: {ex.Message}";
            SearchStatusText = "搜索失败";
        }
        finally
        {
            IsSearching = false;
        }
    }

    /// <summary>按 JM 编号或链接精确解析漫画,结果同时进入搜索结果列表与选章面板。</summary>
    private async Task SearchByJmIdAsync(string input, CancellationToken cancellationToken = default)
    {
        _searchCts?.Cancel();
        _searchCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = _searchCts.Token;

        IsSearching = true;
        PageError = string.Empty;
        SearchResults.Clear();
        HasSearchResults = false;
        SearchStatusText = "正在解析 JM 编号...";
        SelectedSearchResult = null;
        CurrentManga = null;

        try
        {
            var detail = await _backendClient.ResolveMangaAsync(
                new MangaResolveRequest
                {
                    Url = input,
                    SiteKey = SiteCatalog.Key,
                },
                token);

            CurrentManga = detail;
            SearchResults.Add(SearchResultItemViewModel.FromResolved(detail));
            HasSearchResults = true;
            SearchStatusText = $"已定位漫画: {detail.Title}";
            _searchHistoryService.Add(input, SiteCatalog.Key, SiteCatalog.DisplayName, 1);
            LoadSearchHistory();
            OnPropertyChanged(nameof(CanLoadMoreSearchResults));
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("[SearchByJmIdAsync] cancelled");
        }
        catch (BackendApiException ex)
        {
            _logger.LogWarning("[SearchByJmIdAsync] BackendApiException: {Message}", ex.Error.Message);
            PageError = $"搜索出错: {ex.Error.Message}";
            SearchStatusText = "搜索失败";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[SearchByJmIdAsync] Exception: {Type}", ex.GetType().Name);
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
        OnPropertyChanged(nameof(ShowEmptyState));
        OnPropertyChanged(nameof(ShowMangaContent));
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

    partial void OnIsLoadingCommentsChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowCommentsList));
        OnPropertyChanged(nameof(ShowCommentsEmptyState));
        OnPropertyChanged(nameof(CanGoPreviousComments));
        OnPropertyChanged(nameof(CanGoNextComments));
    }

    partial void OnCommentsErrorChanged(string value)
    {
        OnPropertyChanged(nameof(HasCommentsError));
        OnPropertyChanged(nameof(ShowCommentsList));
        OnPropertyChanged(nameof(ShowCommentsEmptyState));
    }

    partial void OnCommentTotalChanged(int value)
    {
        OnPropertyChanged(nameof(CommentSummary));
        OnPropertyChanged(nameof(CommentPageSummary));
        OnPropertyChanged(nameof(CanGoNextComments));
    }

    partial void OnCommentPageChanged(int value)
    {
        OnPropertyChanged(nameof(CommentPageSummary));
        OnPropertyChanged(nameof(CanGoPreviousComments));
        OnPropertyChanged(nameof(CanGoNextComments));
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
        CancelAndDispose(ref _commentsCts);
        Comments.Clear();
        CommentsError = string.Empty;
        CommentTotal = 0;
        CommentPage = 1;
        _dispatcherQueue.TryEnqueue(() =>
        {
            OnPropertyChanged(nameof(HasManga));
            OnPropertyChanged(nameof(ShowEmptyState));
            OnPropertyChanged(nameof(ShowMangaContent));
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
            OnPropertyChanged(nameof(ShowCommentsEmptyState));
            OnPropertyChanged(nameof(CanGoPreviousComments));
            OnPropertyChanged(nameof(CanGoNextComments));

            if (value is not null)
            {
                _ = RefreshCommentsAsync();
            }
        });
    }

    [RelayCommand]
    public Task RefreshCommentsAsync(CancellationToken cancellationToken = default) =>
        LoadCommentsPageAsync(CommentPage, cancellationToken);

    [RelayCommand]
    public Task PreviousCommentsPageAsync(CancellationToken cancellationToken = default) =>
        CanGoPreviousComments
            ? LoadCommentsPageAsync(CommentPage - 1, cancellationToken)
            : Task.CompletedTask;

    [RelayCommand]
    public Task NextCommentsPageAsync(CancellationToken cancellationToken = default) =>
        CanGoNextComments
            ? LoadCommentsPageAsync(CommentPage + 1, cancellationToken)
            : Task.CompletedTask;

    private async Task LoadCommentsPageAsync(int targetPage, CancellationToken cancellationToken)
    {
        var mangaUrl = CurrentManga?.MangaUrl;
        if (string.IsNullOrWhiteSpace(mangaUrl)) return;

        CancelAndDispose(ref _commentsCts);
        var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _commentsCts = source;
        var page = Math.Max(targetPage, 1);
        var token = source.Token;
        IsLoadingComments = true;
        CommentsError = string.Empty;
        try
        {
            var response = await _backendClient.GetMangaCommentsAsync(mangaUrl, page, token);
            token.ThrowIfCancellationRequested();
            if (!string.Equals(CurrentManga?.MangaUrl, mangaUrl, StringComparison.OrdinalIgnoreCase)) return;

            if (page > 1 && response.Items.Count == 0)
            {
                CommentsError = "这一页暂时没有评论，请返回上一页。";
                return;
            }

            Comments.Clear();
            foreach (var comment in response.Items) Comments.Add(comment);
            CommentTotal = Math.Max(response.Total, response.Items.Count);
            CommentPage = response.Page > 0 ? response.Page : page;
        }
        catch (OperationCanceledException)
        {
            // 切换漫画或离开页面时静默取消旧评论请求。
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "加载漫画评论失败: {MangaUrl}", mangaUrl);
            if (ReferenceEquals(_commentsCts, source))
            {
                CommentsError = "评论加载失败，请稍后重试。";
            }
        }
        finally
        {
            if (ReferenceEquals(_commentsCts, source))
            {
                IsLoadingComments = false;
            }
        }
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
        DownloadNotice = string.Empty;
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

            ApplyDownloadNotice(task);

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
        => CurrentTask is not null && CurrentTask.Status is "paused" or "pausing" or "stopping" or "stopped";

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
                ApplyDownloadNotice(dto);
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

    internal void ApplyDownloadNotice(DownloadTaskDto dto)
    {
        if (dto.LocalSkippedChapterCount <= 0) return;

        var missingCount = Math.Max(0, dto.RequestedChapterCount - dto.LocalSkippedChapterCount);
        if (missingCount == 0 || string.Equals(dto.StatusText, "本地已下载，已跳过", StringComparison.Ordinal))
        {
            DownloadNotice = $"《{dto.MangaTitle}》所选章节本地已存在，已跳过重复下载。";
        }
        else
        {
            DownloadNotice = $"《{dto.MangaTitle}》本地已有 {dto.LocalSkippedChapterCount} 章，仅补下载缺少的 {missingCount} 章。";
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

