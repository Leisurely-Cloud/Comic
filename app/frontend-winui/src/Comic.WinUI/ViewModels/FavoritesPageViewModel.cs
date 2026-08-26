using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
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
    private static readonly TimeSpan StatusMessageDisplayDuration = TimeSpan.FromSeconds(3);
    private readonly BackendClient _backendClient;
    private readonly IDispatcher _dispatcher;
    private bool _suppressFolderChange;
    private int _statusMessageVersion;

    public ObservableCollection<FavoriteItemViewModel> Items { get; } = [];
    public ObservableCollection<JmFavoriteFolder> Folders { get; } = [];

    [ObservableProperty] public partial bool IsLoggedIn { get; set; }
    [ObservableProperty] public partial bool IsLoading { get; set; }
    [ObservableProperty] public partial bool IsMutating { get; set; }
    [ObservableProperty] public partial bool IsBatchMode { get; set; }
    [ObservableProperty] public partial string ErrorMessage { get; set; } = string.Empty;
    [ObservableProperty] public partial string StatusMessage { get; set; } = string.Empty;
    [ObservableProperty] public partial string AccountName { get; set; } = string.Empty;
    [ObservableProperty] public partial int CurrentPage { get; set; } = 1;
    [ObservableProperty] public partial int Total { get; set; }
    [ObservableProperty] public partial int PageSize { get; set; } = 20;
    [ObservableProperty] public partial JmFavoriteFolder? SelectedFolder { get; set; }

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);
    public bool HasStatus => !string.IsNullOrWhiteSpace(StatusMessage);
    public bool HasItems => Items.Count > 0;
    public bool ShowLoginPrompt => !IsLoggedIn;
    public bool ShowContent => IsLoggedIn;
    public int TotalPages => Math.Max((int)Math.Ceiling(Total / (double)Math.Max(PageSize, 1)), 1);
    public bool CanGoPrevious => IsLoggedIn && !IsLoading && CurrentPage > 1;
    public bool CanGoNext => IsLoggedIn && !IsLoading && CurrentPage < TotalPages;
    public string PageSummary => $"第 {CurrentPage} / {TotalPages} 页 · 共 {Total} 部";
    public string SelectedFolderName => SelectedFolder?.DisplayName ?? "全部收藏";
    public bool CanManageSelectedFolder => SelectedFolder is { Id: not "0" };
    public int SelectedCount => Items.Count(item => item.IsSelected);
    public bool HasSelection => SelectedCount > 0;
    public string SelectionSummary => HasSelection ? $"已选择 {SelectedCount} 部" : "未选择作品";

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
    partial void OnIsMutatingChanged(bool value)
    {
        NotifyPagingChanged();
        OnPropertyChanged(nameof(HasSelection));
    }
    partial void OnCurrentPageChanged(int value) => NotifyPagingChanged();
    partial void OnTotalChanged(int value) => NotifyPagingChanged();
    partial void OnPageSizeChanged(int value) => NotifyPagingChanged();

    partial void OnErrorMessageChanged(string value) => OnPropertyChanged(nameof(HasError));
    partial void OnStatusMessageChanged(string value)
    {
        OnPropertyChanged(nameof(HasStatus));
        var version = Interlocked.Increment(ref _statusMessageVersion);
        if (!string.IsNullOrWhiteSpace(value))
            _ = ClearStatusMessageAfterDelayAsync(value, version);
    }

    partial void OnSelectedFolderChanged(JmFavoriteFolder? value)
    {
        OnPropertyChanged(nameof(SelectedFolderName));
        OnPropertyChanged(nameof(CanManageSelectedFolder));
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
    private void ToggleBatchMode()
    {
        IsBatchMode = !IsBatchMode;
        foreach (var item in Items)
        {
            item.IsBatchMode = IsBatchMode;
            if (!IsBatchMode) item.IsSelected = false;
        }
        NotifySelectionChanged();
    }

    [RelayCommand]
    private void SelectAllPage()
    {
        foreach (var item in Items) item.IsSelected = true;
        NotifySelectionChanged();
    }

    [RelayCommand]
    private void ClearSelection()
    {
        foreach (var item in Items) item.IsSelected = false;
        NotifySelectionChanged();
    }

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
            StatusMessage = string.Empty;
            await _backendClient.SetJmFavoriteAsync(item.AlbumId, false);
            var targetPage = Items.Count == 1 && CurrentPage > 1 ? CurrentPage - 1 : CurrentPage;
            await RefreshAfterMutationAsync(targetPage, $"已取消收藏《{item.Title}》");
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

    public IReadOnlyList<JmFavoriteFolder> GetMoveTargets() =>
        [new JmFavoriteFolder { Id = "0", Name = "默认收藏夹" },
         .. Folders.Where(folder => folder.Id != "0")];

    public async Task CreateFolderAsync(string name)
    {
        name = (name ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(name)) return;
        await ExecuteFolderMutationAsync(
            () => _backendClient.ManageJmFavoriteFolderAsync(
                JmFavoriteFolderOperation.Add,
                folderName: name),
            $"已新建收藏夹“{name}”");
        if (!HasError)
        {
            var folder = Folders.FirstOrDefault(item => string.Equals(item.Name, name, StringComparison.Ordinal));
            if (folder is not null) SelectedFolder = folder;
        }
    }

    public async Task RenameSelectedFolderAsync(string name)
    {
        var folder = SelectedFolder;
        name = (name ?? string.Empty).Trim();
        if (folder is null || folder.Id == "0" || string.IsNullOrWhiteSpace(name)) return;
        await ExecuteFolderMutationAsync(
            () => _backendClient.ManageJmFavoriteFolderAsync(
                JmFavoriteFolderOperation.Edit,
                folderId: folder.Id,
                folderName: name),
            $"已重命名为“{name}”");
    }

    public async Task DeleteSelectedFolderAsync()
    {
        var folder = SelectedFolder;
        if (folder is null || folder.Id == "0") return;
        IsMutating = true;
        ErrorMessage = string.Empty;
        StatusMessage = string.Empty;
        try
        {
            await _backendClient.ManageJmFavoriteFolderAsync(
                JmFavoriteFolderOperation.Delete,
                folderId: folder.Id);
            _suppressFolderChange = true;
            SelectedFolder = Folders.FirstOrDefault(item => item.Id == "0");
            _suppressFolderChange = false;
            await RefreshAfterMutationAsync(1, $"已删除收藏夹“{folder.DisplayName}”");
        }
        catch (Exception ex)
        {
            ErrorMessage = $"删除收藏夹失败: {ex.Message}";
        }
        finally
        {
            _suppressFolderChange = false;
            IsMutating = false;
        }
    }

    public async Task MoveSelectedAsync(string folderId)
    {
        var selected = Items.Where(item => item.IsSelected).ToList();
        if (selected.Count == 0) return;
        IsMutating = true;
        ErrorMessage = string.Empty;
        StatusMessage = string.Empty;
        var failed = 0;
        foreach (var item in selected)
        {
            try
            {
                item.IsBusy = true;
                await _backendClient.ManageJmFavoriteFolderAsync(
                    JmFavoriteFolderOperation.Move,
                    folderId: string.IsNullOrWhiteSpace(folderId) ? "0" : folderId,
                    albumId: item.AlbumId);
            }
            catch
            {
                failed++;
            }
            finally
            {
                item.IsBusy = false;
            }
        }
        try
        {
            if (failed == 0)
            {
                await RefreshAfterMutationAsync(CurrentPage, $"已移动 {selected.Count} 部作品");
            }
            else
            {
                await LoadPageAsync(CurrentPage);
                StatusMessage = string.Empty;
                ErrorMessage = $"已移动 {selected.Count - failed} 部，{failed} 部移动失败";
            }
        }
        finally
        {
            IsMutating = false;
        }
    }

    public async Task RemoveSelectedAsync()
    {
        var selected = Items.Where(item => item.IsSelected).ToList();
        if (selected.Count == 0) return;
        IsMutating = true;
        ErrorMessage = string.Empty;
        StatusMessage = string.Empty;
        var failed = 0;
        foreach (var item in selected)
        {
            try
            {
                item.IsBusy = true;
                await _backendClient.SetJmFavoriteAsync(item.AlbumId, false);
            }
            catch
            {
                failed++;
            }
            finally
            {
                item.IsBusy = false;
            }
        }
        try
        {
            var targetPage = selected.Count == Items.Count && CurrentPage > 1 ? CurrentPage - 1 : CurrentPage;
            if (failed == 0)
            {
                await RefreshAfterMutationAsync(targetPage, $"已取消收藏 {selected.Count} 部作品");
            }
            else
            {
                await LoadPageAsync(targetPage);
                StatusMessage = string.Empty;
                ErrorMessage = $"已取消 {selected.Count - failed} 部，{failed} 部操作失败";
            }
        }
        finally
        {
            IsMutating = false;
        }
    }

    private async Task ExecuteFolderMutationAsync(Func<Task<JmFavoriteMutationResult>> mutation, string successMessage)
    {
        IsMutating = true;
        ErrorMessage = string.Empty;
        StatusMessage = string.Empty;
        try
        {
            await mutation();
            await RefreshAfterMutationAsync(CurrentPage, successMessage);
        }
        catch (Exception ex)
        {
            ErrorMessage = $"收藏夹操作失败: {ex.Message}";
        }
        finally
        {
            IsMutating = false;
        }
    }

    private async Task<bool> LoadPageAsync(int page, CancellationToken cancellationToken = default)
    {
        if (!IsLoggedIn || IsLoading) return false;
        IsLoading = true;
        ErrorMessage = string.Empty;
        try
        {
            var folderId = SelectedFolder?.Id ?? "0";
            var response = await _backendClient.GetJmFavoritesAsync(Math.Max(page, 1), folderId, cancellationToken);
            if (!_dispatcher.TryEnqueue(() =>
            {
                CurrentPage = response.Page;
                Total = Math.Max(response.Total, response.Items.Count);
                PageSize = response.PageSize > 0 ? response.PageSize : 20;
                UpdateFolders(response.Folders);
                Items.Clear();
                foreach (var item in response.Items)
                {
                    var viewModel = new FavoriteItemViewModel(item, RemoveFavoriteCommand)
                    {
                        IsBatchMode = IsBatchMode,
                    };
                    viewModel.PropertyChanged += (_, args) =>
                    {
                        if (args.PropertyName == nameof(FavoriteItemViewModel.IsSelected)) NotifySelectionChanged();
                    };
                    Items.Add(viewModel);
                }
                NotifySelectionChanged();
            }))
            {
                ErrorMessage = "收藏夹刷新失败，请重试。";
                return false;
            }
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            ErrorMessage = FormatLoadError(ex);
            return false;
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task RefreshAfterMutationAsync(int page, string successMessage)
    {
        var refreshed = await LoadPageAsync(page);
        if (!refreshed)
        {
            // 收藏操作已经由官方接口确认成功，后续列表刷新失败不能再显示成操作失败。
            ErrorMessage = string.Empty;
            StatusMessage = $"{successMessage}；列表暂未刷新，请稍后点击刷新";
            return;
        }

        ErrorMessage = string.Empty;
        StatusMessage = successMessage;
    }

    private static string FormatLoadError(Exception exception)
    {
        var message = exception is BackendApiException apiException
            ? apiException.Error.Message
            : exception.Message;
        if (message.Contains("401", StringComparison.OrdinalIgnoreCase)
            || message.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase))
        {
            return "收藏夹刷新失败：官方接口暂时拒绝访问或登录会话已失效，请稍后刷新；若仍失败请重新登录。";
        }
        if (message.Contains("API 请求失败", StringComparison.OrdinalIgnoreCase)
            || message.Contains("SSL", StringComparison.OrdinalIgnoreCase)
            || message.Contains("invalid start", StringComparison.OrdinalIgnoreCase))
        {
            return "收藏夹刷新失败：官方接口暂时不可用，请稍后点击刷新。";
        }
        return $"加载收藏夹失败：{message}";
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

    private void NotifySelectionChanged()
    {
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(SelectionSummary));
    }

    private async Task ClearStatusMessageAfterDelayAsync(string message, int version)
    {
        await Task.Delay(StatusMessageDisplayDuration);
        if (version != Volatile.Read(ref _statusMessageVersion)
            || !string.Equals(StatusMessage, message, StringComparison.Ordinal))
            return;

        StatusMessage = string.Empty;
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
    [ObservableProperty] public partial bool IsSelected { get; set; }
    [ObservableProperty] public partial bool IsBatchMode { get; set; }
    public ICommand RemoveCommand { get; }

    public FavoriteItemViewModel(SearchResultItem item, ICommand removeCommand)
    {
        _item = item;
        RemoveCommand = removeCommand;
    }
}
