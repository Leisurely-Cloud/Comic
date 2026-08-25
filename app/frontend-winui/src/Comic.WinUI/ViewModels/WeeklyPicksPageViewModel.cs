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

namespace Comic.WinUI.ViewModels;

public partial class WeeklyPicksPageViewModel : ObservableObject
{
    private readonly BackendClient _client;
    private readonly IDispatcher _dispatcher;
    private readonly List<WeeklyPickItem> _allItems = [];
    private CancellationTokenSource? _loadCts;
    private bool _suppressSelectionChanges;

    public ObservableCollection<WeeklyPickIssue> Issues { get; } = [];
    public ObservableCollection<WeeklyPickType> Types { get; } = [];
    public ObservableCollection<ContentCategory> Categories { get; } = [];
    public ObservableCollection<WeeklyPickItemViewModel> Items { get; } = [];

    [ObservableProperty]
    public partial WeeklyPickIssue? SelectedIssue { get; set; }

    [ObservableProperty]
    public partial WeeklyPickType? SelectedType { get; set; }

    [ObservableProperty]
    public partial ContentCategory? SelectedCategory { get; set; }

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    public partial bool HasError { get; set; }

    [ObservableProperty]
    public partial string ErrorMessage { get; set; } = string.Empty;

    public bool HasItems => Items.Count > 0;
    public bool HasData => Issues.Count > 0;
    public bool CanShowContent => !IsLoading && !HasError;
    public string ResultSummary => SelectedIssue is null
        ? string.Empty
        : $"官方推荐 {Items.Count} 部";

    public event EventHandler<string>? DownloadMangaRequested;

    public WeeklyPicksPageViewModel(BackendClient client, IDispatcher dispatcher)
    {
        _client = client;
        _dispatcher = dispatcher;
        Items.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasItems));
            OnPropertyChanged(nameof(ResultSummary));
        };
    }

    partial void OnSelectedIssueChanged(WeeklyPickIssue? value)
    {
        if (!_suppressSelectionChanges && value is not null)
        {
            _ = LoadIssueAsync(value.Id);
        }
    }

    partial void OnSelectedTypeChanged(WeeklyPickType? value)
    {
        if (!_suppressSelectionChanges && SelectedIssue is not null)
        {
            _ = LoadIssueAsync(SelectedIssue.Id);
        }
    }

    partial void OnSelectedCategoryChanged(ContentCategory? value)
    {
        if (!_suppressSelectionChanges) ApplyFilters();
    }

    partial void OnIsLoadingChanged(bool value) => OnPropertyChanged(nameof(CanShowContent));

    partial void OnHasErrorChanged(bool value) => OnPropertyChanged(nameof(CanShowContent));

    public Task InitializeAsync() => LoadIndexAndLatestIssueAsync();

    [RelayCommand]
    private Task RefreshAsync() => LoadIndexAndLatestIssueAsync(SelectedIssue?.Id);

    [RelayCommand]
    private void DownloadManga(string url)
    {
        if (!string.IsNullOrWhiteSpace(url))
        {
            DownloadMangaRequested?.Invoke(this, url);
        }
    }

    private async Task LoadIndexAndLatestIssueAsync(string? preferredIssueId = null)
    {
        var source = ReplaceCancellationSource();
        try
        {
            IsLoading = true;
            HasError = false;
            ErrorMessage = string.Empty;
            var index = await _client.GetWeeklyPicksIndexAsync(source.Token);
            source.Token.ThrowIfCancellationRequested();

            WeeklyPickIssue? selected = null;
            await DispatchAsync(() =>
            {
                _suppressSelectionChanges = true;
                try
                {
                    Issues.Clear();
                    foreach (var issue in index.Issues) Issues.Add(issue);

                    Types.Clear();
                    Types.Add(new WeeklyPickType { Id = string.Empty, Title = "全部类型" });
                    foreach (var type in index.Types) Types.Add(type);

                    selected = Issues.FirstOrDefault(issue => issue.Id == preferredIssueId) ?? Issues.FirstOrDefault();
                    SelectedIssue = selected;
                    SelectedType = Types.FirstOrDefault();
                    Categories.Clear();
                    Categories.Add(new ContentCategory { Title = "全部分类" });
                    SelectedCategory = Categories[0];
                }
                finally
                {
                    _suppressSelectionChanges = false;
                }
            });

            if (selected is null)
            {
                await DispatchAsync(() =>
                {
                    _allItems.Clear();
                    Items.Clear();
                });
                return;
            }

            var result = await _client.GetWeeklyPicksAsync(selected.Id, SelectedType?.Id ?? string.Empty, source.Token);
            source.Token.ThrowIfCancellationRequested();
            await SetItemsAsync(result.Items);
        }
        catch (OperationCanceledException)
        {
            // 快速切换期数或刷新时，旧请求静默退出。
        }
        catch (Exception ex)
        {
            HasError = true;
            ErrorMessage = $"加载每周必看失败：{ex.Message}";
        }
        finally
        {
            if (ReferenceEquals(_loadCts, source)) IsLoading = false;
        }
    }

    private async Task LoadIssueAsync(string issueId)
    {
        var source = ReplaceCancellationSource();
        try
        {
            IsLoading = true;
            HasError = false;
            ErrorMessage = string.Empty;
            var result = await _client.GetWeeklyPicksAsync(issueId, SelectedType?.Id ?? string.Empty, source.Token);
            source.Token.ThrowIfCancellationRequested();
            await SetItemsAsync(result.Items);
        }
        catch (OperationCanceledException)
        {
            // 快速切换期数时不显示错误。
        }
        catch (Exception ex)
        {
            HasError = true;
            ErrorMessage = $"加载本期推荐失败：{ex.Message}";
        }
        finally
        {
            if (ReferenceEquals(_loadCts, source)) IsLoading = false;
        }
    }

    private Task SetItemsAsync(IEnumerable<WeeklyPickItem> items) => DispatchAsync(() =>
    {
        _allItems.Clear();
        _allItems.AddRange(items);
        UpdateCategories();
        ApplyFilters();
    });

    private void ApplyFilters()
    {
        var selected = SelectedCategory;
        var filtered = string.IsNullOrWhiteSpace(selected?.Id)
            ? _allItems
            : _allItems.Where(item => item.Categories.Any(category =>
                string.Equals(category.Title, selected.Title, StringComparison.OrdinalIgnoreCase)));

        Items.Clear();
        foreach (var item in filtered)
        {
            var categoryDisplay = string.Join(" · ", item.Categories
                .Select(category => category.Title)
                .Where(title => !string.IsNullOrWhiteSpace(title))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(2));
            Items.Add(new WeeklyPickItemViewModel(item, categoryDisplay, DownloadMangaCommand));
        }
    }

    private void UpdateCategories()
    {
        var previousTitle = SelectedCategory?.Title;
        _suppressSelectionChanges = true;
        try
        {
            Categories.Clear();
            Categories.Add(new ContentCategory { Title = "全部分类" });
            foreach (var title in _allItems
                         .SelectMany(item => item.Categories)
                         .Select(category => category.Title)
                         .Where(title => !string.IsNullOrWhiteSpace(title))
                         .Distinct(StringComparer.OrdinalIgnoreCase)
                         .OrderBy(title => title, StringComparer.CurrentCulture))
            {
                Categories.Add(new ContentCategory { Id = title, Title = title });
            }
            SelectedCategory = Categories.FirstOrDefault(category =>
                string.Equals(category.Title, previousTitle, StringComparison.OrdinalIgnoreCase)) ?? Categories[0];
        }
        finally
        {
            _suppressSelectionChanges = false;
        }
    }

    private CancellationTokenSource ReplaceCancellationSource()
    {
        var previous = _loadCts;
        _loadCts = new CancellationTokenSource();
        if (previous is not null)
        {
            try { previous.Cancel(); } catch (ObjectDisposedException) { }
            previous.Dispose();
        }
        return _loadCts;
    }

    private Task DispatchAsync(Action callback)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_dispatcher.TryEnqueue(() =>
            {
                try
                {
                    callback();
                    completion.SetResult();
                }
                catch (Exception ex)
                {
                    completion.SetException(ex);
                }
            }))
        {
            completion.SetException(new InvalidOperationException("无法将每周必看数据调度到界面线程"));
        }
        return completion.Task;
    }
}

public sealed class WeeklyPickItemViewModel
{
    private readonly WeeklyPickItem _item;

    public string Title => _item.Title;
    public string Url => _item.Url;
    public string CoverUrl => _item.CoverUrl;
    public string AuthorDisplay => string.IsNullOrWhiteSpace(_item.Author) ? string.Empty : $"作者：{_item.Author}";
    public string Description => _item.Description;
    public string UpdateTime => _item.UpdateTime;
    public string CategoryDisplay { get; }
    public bool HasCategory => !string.IsNullOrWhiteSpace(CategoryDisplay);
    public ICommand DownloadCommand { get; }

    public WeeklyPickItemViewModel(WeeklyPickItem item, string categoryDisplay, ICommand downloadCommand)
    {
        _item = item;
        CategoryDisplay = categoryDisplay;
        DownloadCommand = downloadCommand;
    }
}
