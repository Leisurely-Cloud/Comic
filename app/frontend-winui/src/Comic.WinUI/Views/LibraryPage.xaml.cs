using Comic.WinUI.ViewModels;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Comic.WinUI.Views;

public sealed partial class LibraryPage : Page
{
    public LibraryPageViewModel ViewModel { get; private set; } = null!;

    public LibraryPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        if (e.Parameter is LibraryPageViewModel vm)
        {
            ViewModel = vm;
        }

        base.OnNavigatedTo(e);
    }

    private async void OnLoaded(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (ViewModel is not null)
        {
            await ViewModel.LoadCommand.ExecuteAsync(null);
        }
    }
}
