using Comic.WinUI.ViewModels;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Comic.WinUI.Views;

public sealed partial class ShellPage : Page
{
    public ShellViewModel ViewModel { get; private set; } = null!;

    public ShellPage()
    {
        InitializeComponent();
        ContentFrame.Navigated += OnContentFrameNavigated;
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
            "ranking" => typeof(RankingPage),
            "weekly" => typeof(WeeklyPicksPage),
            "library" => typeof(LibraryPage),
            "settings" => typeof(SettingsPage),
            _ => typeof(DownloadPage),
        };

        var transition = new Microsoft.UI.Xaml.Media.Animation.SlideNavigationTransitionInfo
        {
            Effect = Microsoft.UI.Xaml.Media.Animation.SlideNavigationTransitionEffect.FromRight
        };
        ContentFrame.Navigate(pageType, null, transition);
    }

    public void NavigateToPageWithUrl(string tag, string url)
    {
        var pageType = tag switch
        {
            "download" => typeof(DownloadPage),
            "ranking" => typeof(RankingPage),
            "weekly" => typeof(WeeklyPicksPage),
            "library" => typeof(LibraryPage),
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

    private void OnContentFrameNavigated(object sender, NavigationEventArgs e)
    {
        AppNavigationView.IsBackButtonVisible = NavigationViewBackButtonVisible.Collapsed;
    }
}
