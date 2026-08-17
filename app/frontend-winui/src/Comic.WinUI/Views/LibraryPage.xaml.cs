using Comic.WinUI.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Comic.WinUI.Views;

public sealed partial class LibraryPage : Page
{
    private const double MediumLayoutThreshold = 1100;
    private const double WideLayoutThreshold = 1400;

    public LibraryPageViewModel ViewModel { get; private set; } = null!;

    public LibraryPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        ViewModel = ((App)Application.Current).Services.GetRequiredService<LibraryPageViewModel>();
        Bindings.Update();
        base.OnNavigatedTo(e);
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await ViewModel.LoadCommand.ExecuteAsync(null);
        }
        catch
        {
            // Load failure is already handled by ViewModel
        }
    }

    private void OnLibraryContentSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var detailWidth = e.NewSize.Width >= WideLayoutThreshold
            ? 420
            : e.NewSize.Width >= MediumLayoutThreshold
                ? 360
                : 320;
        DetailColumn.Width = new GridLength(detailWidth);
    }

    private void OnReadClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedItem is not null && !string.IsNullOrEmpty(ViewModel.SelectedItem.RootDir))
        {
            Frame.Navigate(typeof(ReaderPage), ViewModel.SelectedItem.RootDir);
        }
    }
}
