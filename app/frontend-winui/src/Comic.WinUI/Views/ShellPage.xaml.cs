using Comic.WinUI.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using PointerUpdateKind = Microsoft.UI.Input.PointerUpdateKind;

namespace Comic.WinUI.Views;

public sealed partial class ShellPage : Page
{
    public ShellViewModel ViewModel { get; private set; } = null!;

    public ShellPage()
    {
        InitializeComponent();
        ContentFrame.Navigated += OnContentFrameNavigated;
        AddHandler(PointerPressedEvent, new PointerEventHandler(OnPointerPressed), true);
        Loaded += OnLoaded;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        if (e.Parameter is ShellViewModel vm)
        {
            ViewModel = vm;
        }
        base.OnNavigatedTo(e);
    }

    private async void OnLoaded(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        try
        {
            await ViewModel.EnsureBackendRunningAsync();
            NavigateToPage("download");
        }
        catch
        {
            // Backend startup failure is already handled by ViewModel
        }
    }

    private void OnNavigationItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        if (args.InvokedItemContainer is NavigationViewItem item && item.Tag is string tag)
        {
            NavigateToPage(tag);
        }
    }

    public void NavigateToPage(string tag)
    {
        var pageType = tag switch
        {
            "download" => typeof(DownloadPage),
            "tasks" => typeof(DownloadPage),
            "ranking" => typeof(RankingPage),
            "weekly" => typeof(WeeklyPicksPage),
            "updates" => typeof(UpdateCenterPage),
            "library" => typeof(LibraryPage),
            "favorites" => typeof(FavoritesPage),
            "settings" => typeof(SettingsPage),
            _ => typeof(DownloadPage),
        };

        var transition = new Microsoft.UI.Xaml.Media.Animation.SlideNavigationTransitionInfo
        {
            Effect = Microsoft.UI.Xaml.Media.Animation.SlideNavigationTransitionEffect.FromRight
        };
        object? parameter = tag == "tasks" ? DownloadPageNavigationTarget.Tasks : null;
        ContentFrame.Navigate(pageType, parameter, transition);
    }

    public void NavigateToPageWithUrl(string tag, string url)
    {
        var pageType = tag switch
        {
            "download" => typeof(DownloadPage),
            "tasks" => typeof(DownloadPage),
            "ranking" => typeof(RankingPage),
            "weekly" => typeof(WeeklyPicksPage),
            "updates" => typeof(UpdateCenterPage),
            "library" => typeof(LibraryPage),
            "favorites" => typeof(FavoritesPage),
            "settings" => typeof(SettingsPage),
            _ => typeof(DownloadPage),
        };

        var transition = new Microsoft.UI.Xaml.Media.Animation.SlideNavigationTransitionInfo
        {
            Effect = Microsoft.UI.Xaml.Media.Animation.SlideNavigationTransitionEffect.FromRight
        };
        ContentFrame.Navigate(pageType, url, transition);

        // Select the corresponding menu item
        foreach (var item in AppNavigationView.MenuItems)
        {
            if (item is NavigationViewItem navItem && navItem.Tag is string navTag && navTag == tag)
            {
                AppNavigationView.SelectedItem = navItem;
                break;
            }
        }
    }

    public void NavigateToDownloadTasks() => NavigateToPage("tasks");

    private void OnContentFrameNavigated(object sender, NavigationEventArgs e)
    {
        AppNavigationView.IsBackButtonVisible = NavigationViewBackButtonVisible.Collapsed;
        var tag = e.SourcePageType == typeof(DownloadPage) && e.Parameter is DownloadPageNavigationTarget.Tasks ? "tasks"
            : e.SourcePageType == typeof(DownloadPage) ? "download"
            : e.SourcePageType == typeof(RankingPage) ? "ranking"
            : e.SourcePageType == typeof(WeeklyPicksPage) ? "weekly"
            : e.SourcePageType == typeof(UpdateCenterPage) ? "updates"
            : e.SourcePageType == typeof(LibraryPage) ? "library"
            : e.SourcePageType == typeof(FavoritesPage) ? "favorites"
            : e.SourcePageType == typeof(SettingsPage) ? "settings"
            : string.Empty;
        if (!string.IsNullOrWhiteSpace(tag)) SelectNavigationItem(tag);
    }

    public bool TryNavigateBack()
    {
        if (!ContentFrame.CanGoBack) return false;
        ContentFrame.GoBack();
        return true;
    }

    public bool TryNavigateForward()
    {
        if (!ContentFrame.CanGoForward) return false;
        ContentFrame.GoForward();
        return true;
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var updateKind = e.GetCurrentPoint(this).Properties.PointerUpdateKind;
        var handled = updateKind switch
        {
            PointerUpdateKind.XButton1Pressed => TryNavigateBack(),
            PointerUpdateKind.XButton2Pressed => TryNavigateForward(),
            _ => false,
        };
        if (handled) e.Handled = true;
    }

    public void SelectNavigationItem(string tag)
    {
        foreach (var item in AppNavigationView.MenuItems)
        {
            if (item is NavigationViewItem navItem && navItem.Tag is string navTag && navTag == tag)
            {
                AppNavigationView.SelectedItem = navItem;
                return;
            }
        }
    }
}
