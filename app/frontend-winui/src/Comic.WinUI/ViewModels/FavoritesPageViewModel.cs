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

public partial class FavoritesPageViewModel : ObservableObject
{
    private readonly BackendClient _backendClient;
    private readonly IDispatcher _dispatcher;
    private bool _suppressFolderChange;

    public ObservableCollection<FavoriteItemViewModel> Items { get; } = [];
    public ObservableCollection<JmFavoriteFolder> Folders { get; } = [];

    [ObservableProperty] public partial bool IsLoggedIn { get; set; }
    [ObservableProperty] public partial bool IsLoading { get; set; }
    [ObservableProperty] public partial string ErrorMessage { get; set; } = string.Empty;
    [ObservableProperty] public partial string AccountName { get; set; } = string.Empty;
    [ObservableProperty] public partial int CurrentPage { get; set; } = 1;
    [ObservableProperty] public partial int Total { get; set; }
    [ObservableProperty] public partial int PageSize { get; set; } = 20;
    [ObservableProperty] public partial JmFavoriteFolder? SelectedFolder { get; set; }

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);
    public bool HasItems => Items.Count > 0;
    public bool ShowLoginPrompt => !IsLoggedIn;
    public bool ShowContent => IsLoggedIn;
    public int TotalPages => Math.Max((int)Math.Ceiling(Total / (double)Math.Max(PageSize, 1)), 1);
    public bool CanGoPrevious => IsLoggedIn && !IsLoading && CurrentPage > 1;
    public bool CanGoNext => IsLoggedIn && !IsLoading && CurrentPage < TotalPages;
    public string PageSummary => $"第 {CurrentPage} / {TotalPages} 页 · 共 {Total} 部";
    public string SelectedFolderName => SelectedFolder?.DisplayName ?? "全部收藏";

    public event EventHandler<string>? OpenMangaRequested;
    public event EventHandler? OpenSettingsRequested;

    public FavoritesPageViewModel(BackendClient backendClient, IDispatcher dispatcher)
    {
        _backendClient = backendClient;
        _dispatcher = dispatcher;
        Items.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasItems));
    }

    partial void OnIsLoggedInChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowLoginPrompt));
        OnPropertyChanged(nameof(ShowContent));
        NotifyPagingChanged();
    }

    partial void OnIsLoadingChanged(bool value) => NotifyPagingChanged();
    partial void OnCurrentPageChanged(int value) => NotifyPagingChanged();
    partial void OnTotalChanged(int value) => NotifyPagingChanged();
    partial void OnPageSizeChanged(int value) => NotifyPagingChanged();

    partial void OnErrorMessageChanged(string value) => OnPropertyChanged(nameof(HasError));

    partial void OnSelectedFolderChanged(JmFavoriteFolder? value)
    {
        OnPropertyChanged(nameof(SelectedFolderName));
        if (!_suppressFolderChange && IsLoggedIn) _ = LoadPageAsync(1);
    }

    [RelayCommand]
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        JmAccountState state;
        try
        {
            state = await _backendClient.RestoreJmLoginAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            state = _backendClient.GetJmAccountState();
            ErrorMessage = $"自动登录失败: {ex.Message}";
        }
        IsLoggedIn = state.IsLoggedIn;
        AccountName = state.Account?.Username ?? string.Empty;
        if (!IsLoggedIn)
        {
            Items.Clear();
            return;
        }
        await LoadPageAsync(Math.Max(CurrentPage, 1), cancellationToken);
    }

    [RelayCommand] private Task RefreshAsync() => InitializeAsync();
    [RelayCommand] private Task PreviousPageAsync() => CanGoPrevious ? LoadPageAsync(CurrentPage - 1) : Task.CompletedTask;
    [RelayCommand] private Task NextPageAsync() => CanGoNext ? LoadPageAsync(CurrentPage + 1) : Task.CompletedTask;
    [RelayCommand] private void OpenSettings() => OpenSettingsRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void OpenManga(FavoriteItemViewModel? item)
    {
        if (item is not null && !string.IsNullOrWhiteSpace(item.Url))
            OpenMangaRequested?.Invoke(this, item.Url);
    }

    [RelayCommand]
    private async Task RemoveFavoriteAsync(FavoriteItemViewModel? item)
    {
        if (item is null || string.IsNullOrWhiteSpace(item.AlbumId)) return;
        try
        {
            item.IsBusy = true;
            ErrorMessage = string.Empty;
            await _backendClient.SetJmFavoriteAsync(item.AlbumId, false);
            var targetPage = Items.Count == 1 && CurrentPage > 1 ? CurrentPage - 1 : CurrentPage;
            await LoadPageAsync(targetPage);
        }
        catch (Exception ex)
        {
            ErrorMessage = $"取消收藏失败: {ex.Message}";
        }
        finally
        {
            item.IsBusy = false;
        }
    }

    private async Task LoadPageAsync(int page, CancellationToken cancellationToken = default)
    {
        if (!IsLoggedIn || IsLoading) return;
        IsLoading = true;
        ErrorMessage = string.Empty;
        try
        {
            var folderId = SelectedFolder?.Id ?? "0";
            var response = await _backendClient.GetJmFavoritesAsync(Math.Max(page, 1), folderId, cancellationToken);
            _dispatcher.TryEnqueue(() =>
            {
                CurrentPage = response.Page;
                Total = Math.Max(response.Total, response.Items.Count);
                PageSize = response.PageSize > 0 ? response.PageSize : 20;
                UpdateFolders(response.Folders);
                Items.Clear();
                foreach (var item in response.Items)
                    Items.Add(new FavoriteItemViewModel(item, RemoveFavoriteCommand));
            });
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ErrorMessage = $"加载收藏夹失败: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void UpdateFolders(System.Collections.Generic.IReadOnlyCollection<JmFavoriteFolder> folders)
    {
        var selectedId = SelectedFolder?.Id ?? "0";
        _suppressFolderChange = true;
        try
        {
            Folders.Clear();
            Folders.Add(new JmFavoriteFolder { Id = "0", Name = "全部收藏" });
            foreach (var folder in folders.Where(folder => folder.Id != "0")) Folders.Add(folder);
            SelectedFolder = Folders.FirstOrDefault(folder => folder.Id == selectedId) ?? Folders[0];
        }
        finally
        {
            _suppressFolderChange = false;
        }
    }

    private void NotifyPagingChanged()
    {
        OnPropertyChanged(nameof(TotalPages));
        OnPropertyChanged(nameof(CanGoPrevious));
        OnPropertyChanged(nameof(CanGoNext));
        OnPropertyChanged(nameof(PageSummary));
    }
}

public partial class FavoriteItemViewModel : ObservableObject
{
    private readonly SearchResultItem _item;
    public string Title => _item.Title;
    public string Url => _item.Url;
    public string CoverUrl => _item.CoverUrl;
    public string AuthorText => string.IsNullOrWhiteSpace(_item.Author) ? "作者：未知" : $"作者：{_item.Author}";
    public string UpdateTime => _item.UpdateTime;
    public string AlbumId => Uri.TryCreate(_item.Url, UriKind.Absolute, out var uri)
        ? uri.Segments.LastOrDefault()?.Trim('/') ?? string.Empty
        : string.Empty;

    [ObservableProperty] public partial bool IsBusy { get; set; }
    public ICommand RemoveCommand { get; }

    public FavoriteItemViewModel(SearchResultItem item, ICommand removeCommand)
    {
        _item = item;
        RemoveCommand = removeCommand;
    }
}
