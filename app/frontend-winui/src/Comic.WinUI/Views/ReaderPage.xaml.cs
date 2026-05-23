using Comic.WinUI.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;

namespace Comic.WinUI.Views;

public sealed partial class ReaderPage : Page
{
    public ReaderPageViewModel ViewModel { get; private set; } = null!;

    public ReaderPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        ViewModel = ((App)Application.Current).Services.GetRequiredService<ReaderPageViewModel>();
        Bindings.Update();

        if (e.Parameter is string rootDir)
        {
            _rootDir = rootDir;
        }

        base.OnNavigatedTo(e);
    }

    private string _rootDir = string.Empty;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(_rootDir))
        {
            await ViewModel.LoadCommand.ExecuteAsync(_rootDir);
        }
    }

    private void OnBackClick(object sender, RoutedEventArgs e)
    {
        if (Frame.CanGoBack)
        {
            Frame.GoBack();
        }
    }

    private async void OnPreviousImageClick(object sender, RoutedEventArgs e)
    {
        await ViewModel.PreviousImageCommand.ExecuteAsync(null);
    }

    private async void OnNextImageClick(object sender, RoutedEventArgs e)
    {
        await ViewModel.NextImageCommand.ExecuteAsync(null);
    }

    private void OnPreviousChapterClick(object sender, RoutedEventArgs e)
    {
        ViewModel.PreviousChapterCommand.Execute(null);
    }

    private void OnNextChapterClick(object sender, RoutedEventArgs e)
    {
        ViewModel.NextChapterCommand.Execute(null);
    }

    private async void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case Windows.System.VirtualKey.Left:
                await ViewModel.PreviousImageCommand.ExecuteAsync(null);
                e.Handled = true;
                break;
            case Windows.System.VirtualKey.Right:
                await ViewModel.NextImageCommand.ExecuteAsync(null);
                e.Handled = true;
                break;
            case Windows.System.VirtualKey.Escape:
                if (Frame.CanGoBack)
                {
                    Frame.GoBack();
                }
                e.Handled = true;
                break;
        }
    }
}
