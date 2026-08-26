using System;
using System.Linq;
using Comic.WinUI.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;

namespace Comic.WinUI.Views;

public sealed partial class FavoritesPage : Page
{
    public FavoritesPageViewModel ViewModel { get; }
    private bool _subscribed;

    public FavoritesPage()
    {
        ViewModel = ((App)Application.Current).Services.GetRequiredService<FavoritesPageViewModel>();
        InitializeComponent();
        Loaded += OnLoaded;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (_subscribed) return;
        _subscribed = true;
        ViewModel.OpenMangaRequested += OnOpenMangaRequested;
        ViewModel.OpenSettingsRequested += OnOpenSettingsRequested;
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        if (_subscribed)
        {
            _subscribed = false;
            ViewModel.OpenMangaRequested -= OnOpenMangaRequested;
            ViewModel.OpenSettingsRequested -= OnOpenSettingsRequested;
        }
        base.OnNavigatedFrom(e);
    }

    private async void OnLoaded(object sender, RoutedEventArgs e) => await ViewModel.InitializeAsync();

    private void OnItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not FavoriteItemViewModel item) return;
        if (ViewModel.IsBatchMode)
        {
            item.IsSelected = !item.IsSelected;
            return;
        }
        ViewModel.OpenMangaCommand.Execute(item);
    }

    private void OnFolderSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ListView { SelectedItem: not null }) FolderFlyout.Hide();
    }

    private async void OnCreateFolderClick(object sender, RoutedEventArgs e)
    {
        var name = await ShowFolderNameDialogAsync("新建收藏夹", string.Empty);
        if (!string.IsNullOrWhiteSpace(name)) await ViewModel.CreateFolderAsync(name);
    }

    private async void OnRenameFolderClick(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.CanManageSelectedFolder) return;
        var name = await ShowFolderNameDialogAsync("重命名收藏夹", ViewModel.SelectedFolder?.Name ?? string.Empty);
        if (!string.IsNullOrWhiteSpace(name)) await ViewModel.RenameSelectedFolderAsync(name);
    }

    private async void OnDeleteFolderClick(object sender, RoutedEventArgs e)
    {
        var folder = ViewModel.SelectedFolder;
        if (folder is null || folder.Id == "0") return;
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "删除收藏夹",
            Content = $"确定删除“{folder.DisplayName}”吗？该操作会删除官方收藏夹分类，文件夹内作品将按 JM 官方规则处理。",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            await ViewModel.DeleteSelectedFolderAsync();
    }

    private async void OnMoveSelectedClick(object sender, RoutedEventArgs e)
    {
        var targets = ViewModel.GetMoveTargets();
        if (targets.Count == 0) return;
        var picker = new ComboBox
        {
            ItemsSource = targets,
            DisplayMemberPath = "DisplayName",
            SelectedIndex = Math.Max(0, targets.ToList().FindIndex(folder => folder.Id == ViewModel.SelectedFolder?.Id)),
            MinWidth = 280,
        };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"移动 {ViewModel.SelectedCount} 部作品",
            Content = picker,
            PrimaryButtonText = "移动",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary && picker.SelectedItem is Comic.WinUI.Models.JmFavoriteFolder folder)
            await ViewModel.MoveSelectedAsync(folder.Id);
    }

    private async void OnRemoveSelectedClick(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "批量取消收藏",
            Content = $"确定从官方收藏中移除已选择的 {ViewModel.SelectedCount} 部作品吗？",
            PrimaryButtonText = "取消收藏",
            CloseButtonText = "返回",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            await ViewModel.RemoveSelectedAsync();
    }

    private async System.Threading.Tasks.Task<string> ShowFolderNameDialogAsync(string title, string initialValue)
    {
        var input = new TextBox
        {
            Text = initialValue,
            PlaceholderText = "输入收藏夹名称",
            MinWidth = 320,
            MaxLength = 40,
        };
        input.SelectAll();
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = input,
            PrimaryButtonText = "确定",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary ? input.Text.Trim() : string.Empty;
    }

    private void OnOpenMangaRequested(object? sender, string url) =>
        FindParent<ShellPage>(this)?.NavigateToPageWithUrl("download", url);

    private void OnOpenSettingsRequested(object? sender, System.EventArgs e) =>
        FindParent<ShellPage>(this)?.NavigateToPage("settings");

    private static T? FindParent<T>(DependencyObject child) where T : DependencyObject
    {
        var parent = VisualTreeHelper.GetParent(child);
        while (parent is not null)
        {
            if (parent is T typed) return typed;
            parent = VisualTreeHelper.GetParent(parent);
        }
        return null;
    }
}
