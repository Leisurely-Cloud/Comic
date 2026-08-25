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
        if (e.ClickedItem is FavoriteItemViewModel item) ViewModel.OpenMangaCommand.Execute(item);
    }

    private void OnFolderSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ListView { SelectedItem: not null }) FolderFlyout.Hide();
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
