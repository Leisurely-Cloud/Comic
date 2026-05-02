using Comic_WinUI.ViewModels;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Comic_WinUI.Views;

public sealed partial class SettingsPage : Page
{
    public SettingsPageViewModel ViewModel { get; private set; } = null!;

    public SettingsPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        if (e.Parameter is SettingsPageViewModel vm)
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
