using Comic.WinUI.ViewModels;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Comic.WinUI.Views;

public sealed partial class ShellPage : Page
{
    public ShellViewModel ViewModel { get; }

    public ShellPage(ShellViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        ContentFrame.Navigated += OnContentFrameNavigated;
        Loaded += OnLoaded;
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

    private void NavigateToPage(string tag)
    {
        var pageType = tag switch
        {
            "download" => typeof(DownloadPage),
            "library" => typeof(LibraryPage),
            "settings" => typeof(SettingsPage),
            _ => typeof(DownloadPage),
        };

        ContentFrame.Navigate(pageType);
    }

    private void OnContentFrameNavigated(object sender, NavigationEventArgs e)
    {
        AppNavigationView.IsBackButtonVisible = NavigationViewBackButtonVisible.Collapsed;
    }
}
