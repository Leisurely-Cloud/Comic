using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
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

    public bool CanLoadMore => !IsSinglePage && !IsLoading;

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
        _ = LoadRankingAsync();
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
        OnPropertyChanged(nameof(CanLoadMore));
    }

    partial void OnCurrentPageChanged(int value)
    {
        OnPropertyChanged(nameof(CanLoadMore));
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        await LoadRankingAsync();
    }

    [RelayCommand]
    private async Task LoadMoreAsync()
    {
        if (IsSinglePage || IsLoading) return;

        try
        {
            IsLoading = true;
            CurrentPage++;

            var result = await _client.GetRankingAsync(SiteCatalog.Key, SelectedSection, CurrentPage);
            if (result == null) return;

            _dispatcher.TryEnqueue(() =>
            {
                foreach (var item in result.Items ?? Enumerable.Empty<RankingItem>())
                {
                    RankingItems.Add(new RankingItemViewModel(
                        item,
                        NavigateToDetailCommand,
                        DownloadMangaCommand,
                        RankingItems.Count + 1));
                }
            });
        }
        catch (Exception ex)
        {
            CurrentPage--;
            ErrorMessage = $"加载更多失败: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
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

    private async Task LoadRankingAsync()
    {
        if (string.IsNullOrEmpty(SelectedSection)) return;

        try
        {
            IsLoading = true;
            HasError = false;
            ErrorMessage = "";
            CurrentPage = 1;

            var result = await _client.GetRankingAsync(SiteCatalog.Key, SelectedSection, CurrentPage);
            if (result == null) return;

            IsSinglePage = result.IsSinglePage;

            _dispatcher.TryEnqueue(() =>
            {
                RankingItems.Clear();
                var index = 0;
                foreach (var item in result.Items ?? Enumerable.Empty<RankingItem>())
                {
                    RankingItems.Add(new RankingItemViewModel(
                        item,
                        NavigateToDetailCommand,
                        DownloadMangaCommand,
                        ++index));
                }
            });
        }
        catch (Exception ex)
        {
            HasError = true;
            ErrorMessage = $"加载排行榜失败: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
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
