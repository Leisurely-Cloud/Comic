using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Comic.WinUI.Models;
using Comic.WinUI.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Windows.ApplicationModel.DataTransfer;

namespace Comic.WinUI.ViewModels;

public partial class RankingPageViewModel : ObservableObject
{
    private readonly BackendClient _client;
    private readonly IDispatcher _dispatcher;
    private readonly List<RankingItem> _loadedItems = [];
    private CancellationTokenSource? _rankingCts;
    private int _loadedServerPage;
    private bool _serverHasMore;

    public const int PageSize = 20;

    public ObservableCollection<RankingItemViewModel> RankingItems { get; } = new();

    [ObservableProperty]
    public partial string SelectedSection { get; set; } = "";

    [ObservableProperty]
    public partial ObservableCollection<SectionItem> Sections { get; set; } = new();

    [ObservableProperty]
    public partial SectionItem? SelectedSectionItem { get; set; }

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    public partial bool HasError { get; set; }

    [ObservableProperty]
    public partial string ErrorMessage { get; set; } = "";

    [ObservableProperty]
    public partial bool IsSinglePage { get; set; }

    [ObservableProperty]
    public partial int CurrentPage { get; set; } = 1;

    public bool CanGoPrevious => CurrentPage > 1 && !IsLoading;

    public bool CanGoNext => !IsLoading &&
        (CurrentPage * PageSize < _loadedItems.Count || _serverHasMore);

    public string PageSummary => $"第 {CurrentPage} 页 · 每页 {PageSize} 部";

    public bool HasItems => RankingItems.Count > 0;

    /// <summary>是否已有加载过的榜单数据(用于页面重入时避免重复刷新)。</summary>
    public bool HasData => RankingItems.Count > 0 || Sections.Count > 0;

    public event EventHandler<string>? NavigateToDetailRequested;
    public event EventHandler<string>? DownloadMangaRequested;

    public RankingPageViewModel(BackendClient client, IDispatcher dispatcher)
    {
        _client = client;
        _dispatcher = dispatcher;
        RankingItems.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasItems));
    }

    partial void OnSelectedSectionChanged(string value)
    {
        _ = LoadRankingPageAsync(1, reset: true);
    }

    partial void OnSelectedSectionItemChanged(SectionItem? value)
    {
        if (value != null)
        {
            SelectedSection = value.Key;
        }
    }

    partial void OnIsSinglePageChanged(bool value)
    {
        OnPropertyChanged(nameof(CanGoNext));
    }

    partial void OnCurrentPageChanged(int value)
    {
        OnPropertyChanged(nameof(CanGoPrevious));
        OnPropertyChanged(nameof(CanGoNext));
        OnPropertyChanged(nameof(PageSummary));
    }

    partial void OnIsLoadingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanGoPrevious));
        OnPropertyChanged(nameof(CanGoNext));
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        await LoadRankingPageAsync(1, reset: true);
    }

    [RelayCommand]
    private Task PreviousPageAsync()
    {
        return CanGoPrevious
            ? LoadRankingPageAsync(CurrentPage - 1, reset: false)
            : Task.CompletedTask;
    }

    [RelayCommand]
    private Task NextPageAsync()
    {
        return CanGoNext
            ? LoadRankingPageAsync(CurrentPage + 1, reset: false)
            : Task.CompletedTask;
    }

    [RelayCommand]
    private void NavigateToDetail(string url)
    {
        if (!string.IsNullOrEmpty(url))
        {
            NavigateToDetailRequested?.Invoke(this, url);
        }
    }

    [RelayCommand]
    private void DownloadManga(string url)
    {
        if (!string.IsNullOrEmpty(url))
        {
            DownloadMangaRequested?.Invoke(this, url);
        }
    }

    [RelayCommand]
    private void CopyLink(string url)
    {
        if (string.IsNullOrEmpty(url)) return;
        var package = new DataPackage();
        package.SetText(url);
        Clipboard.SetContent(package);
    }

    public async Task InitializeAsync()
    {
        await LoadSectionsAsync();
    }

    private async Task LoadSectionsAsync()
    {
        try
        {
            HasError = false;
            ErrorMessage = "";

            var result = await _client.GetRankingSectionsAsync(SiteCatalog.Key);
            if (result == null) return;

            var sections = result.Sections ?? new Dictionary<string, string>();

            _dispatcher.TryEnqueue(() =>
            {
                Sections.Clear();
                foreach (var section in sections)
                {
                    Sections.Add(new SectionItem
                    {
                        Key = section.Key,
                        Value = section.Value,
                        DisplayName = section.Key,
                    });
                }

                if (Sections.Any())
                {
                    SelectedSectionItem = Sections.First();
                }
            });
        }
        catch (Exception ex)
        {
            HasError = true;
            ErrorMessage = $"加载分区失败: {ex.Message}";
        }
    }

    private async Task LoadRankingPageAsync(int targetPage, bool reset)
    {
        if (string.IsNullOrEmpty(SelectedSection)) return;

        CancellationTokenSource source;
        if (reset)
        {
            CancelAndDispose(ref _rankingCts);
            source = new CancellationTokenSource();
            _rankingCts = source;
            _loadedItems.Clear();
            _loadedServerPage = 0;
            _serverHasMore = true;
        }
        else
        {
            if (IsLoading) return;
            source = _rankingCts ?? new CancellationTokenSource();
            _rankingCts ??= source;
        }

        try
        {
            IsLoading = true;
            HasError = false;
            ErrorMessage = "";
            var token = source.Token;
            var requiredItemCount = Math.Max(targetPage, 1) * PageSize;
            while (_loadedItems.Count < requiredItemCount && _serverHasMore)
            {
                var serverPage = _loadedServerPage + 1;
                var result = await _client.GetRankingAsync(
                    SiteCatalog.Key,
                    SelectedSection,
                    serverPage,
                    token);
                token.ThrowIfCancellationRequested();
                if (result == null) break;

                var incoming = result.Items ?? [];
                IsSinglePage = result.IsSinglePage;
                _loadedServerPage = serverPage;
                _serverHasMore = !result.IsSinglePage && incoming.Count > 0;
                _loadedItems.AddRange(incoming);
            }

            var pageItems = _loadedItems
                .Skip((Math.Max(targetPage, 1) - 1) * PageSize)
                .Take(PageSize)
                .ToList();

            // 站点没有提供可靠总页数。探测到空页时保留当前页，仅关闭“下一页”，
            // 避免用户被带到一张空白榜单。
            if (targetPage > 1 && pageItems.Count == 0)
            {
                _serverHasMore = false;
                OnPropertyChanged(nameof(CanGoNext));
                return;
            }

            CurrentPage = Math.Max(targetPage, 1);
            _dispatcher.TryEnqueue(() =>
            {
                RankingItems.Clear();
                var index = (CurrentPage - 1) * PageSize;
                foreach (var item in pageItems)
                {
                    RankingItems.Add(new RankingItemViewModel(
                        item,
                        NavigateToDetailCommand,
                        DownloadMangaCommand,
                        ++index));
                }
            });
            OnPropertyChanged(nameof(CanGoNext));
        }
        catch (OperationCanceledException)
        {
            // 切换榜单分类时静默取消旧请求，避免旧分类覆盖新分类。
        }
        catch (Exception ex)
        {
            HasError = true;
            ErrorMessage = $"加载排行榜失败: {ex.Message}";
        }
        finally
        {
            if (ReferenceEquals(_rankingCts, source))
            {
                IsLoading = false;
            }
        }
    }

    private static void CancelAndDispose(ref CancellationTokenSource? source)
    {
        var current = source;
        source = null;
        if (current is null) return;
        try { current.Cancel(); } catch (ObjectDisposedException) { }
        current.Dispose();
    }

}

public class SectionItem
{
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
    public string DisplayName { get; set; } = "";
}

public partial class RankingItemViewModel : ObservableObject
{
    private readonly RankingItem _item;

    public string Title => _item.Title ?? "";
    public string Url => _item.Url ?? "";
    public string CoverUrl => _item.CoverUrl ?? "";
    public string Author => _item.Author ?? "";
    public string LatestChapter => _item.LatestChapter ?? "";
    public string UpdateTime => _item.UpdateTime ?? "";
    public string Section => _item.Section ?? "";
    public string DetailHint => _item.DetailHint ?? "";
    public string DetailSectionLabel => _item.DetailSectionLabel ?? "";

    public int Index { get; }

    public string RankText => $"{Index}";

    public string AuthorDisplay => string.IsNullOrWhiteSpace(Author) ? string.Empty : $"作者：{Author}";

    public ICommand NavigateCommand { get; }
    public ICommand DownloadCommand { get; }

    public RankingItemViewModel(
        RankingItem item,
        ICommand navigateCommand,
        ICommand downloadCommand,
        int index)
    {
        _item = item;
        Index = index;
        NavigateCommand = navigateCommand;
        DownloadCommand = downloadCommand;
    }
}
