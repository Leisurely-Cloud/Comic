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
using Microsoft.UI.Dispatching;

namespace Comic.WinUI.ViewModels;

public partial class RankingPageViewModel : ObservableObject
{
    private readonly BackendClient _client;
    private readonly DispatcherQueue _dispatcher;

    public ObservableCollection<RankingItemViewModel> RankingItems { get; } = new();

    [ObservableProperty]
    private string _selectedSite = "baozimh";

    [ObservableProperty]
    private string _selectedSection = "";

    [ObservableProperty]
    private ObservableCollection<SectionItem> _sections = new();

    [ObservableProperty]
    private SectionItem? _selectedSectionItem;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private bool _hasError;

    [ObservableProperty]
    private string _errorMessage = "";

    [ObservableProperty]
    private bool _isSinglePage;

    [ObservableProperty]
    private int _currentPage = 1;

    public bool CanLoadMore => !IsSinglePage && !IsLoading;

    public event EventHandler<string>? NavigateToDetailRequested;
    public event EventHandler<string>? DownloadMangaRequested;

    public RankingPageViewModel()
    {
        _client = App.GetService<BackendClient>();
        _dispatcher = DispatcherQueue.GetForCurrentThread();
    }

    partial void OnSelectedSiteChanged(string value)
    {
        _ = LoadSectionsAsync();
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

            var result = await _client.GetRankingAsync(SelectedSite, SelectedSection, CurrentPage);
            if (result == null) return;

            _dispatcher.TryEnqueue(() =>
            {
                foreach (var item in result.Items ?? Enumerable.Empty<RankingItem>())
                {
                    RankingItems.Add(new RankingItemViewModel(item, NavigateToDetailCommand, DownloadMangaCommand));
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

            var result = await _client.GetRankingSectionsAsync(SelectedSite);
            if (result == null) return;

            var siteName = result.SiteName ?? SelectedSite;
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

            var result = await _client.GetRankingAsync(SelectedSite, SelectedSection, CurrentPage);
            if (result == null) return;

            IsSinglePage = result.IsSinglePage;

            _dispatcher.TryEnqueue(() =>
            {
                RankingItems.Clear();
                foreach (var item in result.Items ?? Enumerable.Empty<RankingItem>())
                {
                    RankingItems.Add(new RankingItemViewModel(item, NavigateToDetailCommand, DownloadMangaCommand));
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
    public string LatestChapter => _item.LatestChapter ?? "";
    public string UpdateTime => _item.UpdateTime ?? "";
    public string Section => _item.Section ?? "";
    public string DetailHint => _item.DetailHint ?? "";
    public string DetailSectionLabel => _item.DetailSectionLabel ?? "";

    public ICommand NavigateCommand { get; }
    public ICommand DownloadCommand { get; }

    public RankingItemViewModel(RankingItem item, ICommand navigateCommand, ICommand downloadCommand)
    {
        _item = item;
        NavigateCommand = navigateCommand;
        DownloadCommand = downloadCommand;
    }
}
