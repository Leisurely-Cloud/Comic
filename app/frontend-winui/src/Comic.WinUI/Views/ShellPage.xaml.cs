using Comic_WinUI.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Comic_WinUI.Views;

public sealed partial class ShellPage : Page
{
    public ShellViewModel ViewModel { get; }

    public ShellPage()
    {
        ViewModel = ((App)Application.Current).Services.GetService(typeof(ShellViewModel)) as ShellViewModel
                    ?? throw new InvalidOperationException("ShellViewModel not registered");
        InitializeComponent();
        ContentFrame.Navigated += OnContentFrameNavigated;
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        await ViewModel.EnsureBackendRunningAsync();
        NavigateToPage("download");
    }

    private void OnNavigationItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        if (args.InvokedItemContainer is NavigationViewItem item && item.Tag is string tag)
        {
            NavigateToPage(tag);
        }
    }

    private void NavigateToPage(string tag)
    {
        var pageType = tag switch
        {
            "download" => typeof(DownloadPage),
            "library" => typeof(LibraryPage),
            "settings" => typeof(SettingsPage),
            _ => typeof(DownloadPage),
        };

        object? parameter = tag switch
        {
            "download" => ((App)Application.Current).Services.GetService(typeof(DownloadPageViewModel)),
            "library" => ((App)Application.Current).Services.GetService(typeof(LibraryPageViewModel)),
            "settings" => ((App)Application.Current).Services.GetService(typeof(SettingsPageViewModel)),
            _ => null,
        };

        ContentFrame.Navigate(pageType, parameter);
    }

    private void OnContentFrameNavigated(object sender, NavigationEventArgs e)
    {
        AppNavigationView.IsBackButtonVisible = NavigationViewBackButtonVisible.Collapsed;
    }
}
