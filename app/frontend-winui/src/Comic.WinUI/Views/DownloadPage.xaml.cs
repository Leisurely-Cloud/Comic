using Comic.WinUI.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Comic.WinUI.Views;

public sealed partial class DownloadPage : Page
{
    public DownloadPageViewModel ViewModel { get; private set; } = null!;

    public DownloadPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        ViewModel = ((App)Application.Current).Services.GetRequiredService<DownloadPageViewModel>();
        System.Diagnostics.Debug.WriteLine($"[DownloadPage] OnNavigatedTo: ViewModel={ViewModel.GetHashCode()}, SearchResults={ViewModel.SearchResults.GetHashCode()}");
        Bindings.Update();
        System.Diagnostics.Debug.WriteLine($"[DownloadPage] Bindings.Update done. SearchBarControl.ViewModel={SearchBarControl1?.ViewModel?.GetHashCode()}, SearchResultsList.ViewModel={SearchResultsList1?.ViewModel?.GetHashCode()}");
        base.OnNavigatedTo(e);
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        System.Diagnostics.Debug.WriteLine($"[DownloadPage] OnLoaded: ViewModel={ViewModel.GetHashCode()}");
        try
        {
            await ViewModel.InitializeCommand.ExecuteAsync(null);
        }
        catch
        {
            // Initialization failure is already handled by ViewModel
        }
    }
}
