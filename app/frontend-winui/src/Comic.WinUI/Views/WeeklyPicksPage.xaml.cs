using Comic.WinUI.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;

namespace Comic.WinUI.Views;

public sealed partial class WeeklyPicksPage : Page
{
    public WeeklyPicksPageViewModel ViewModel { get; }
    private bool _eventsSubscribed;

    public WeeklyPicksPage()
    {
        ViewModel = ((App)Application.Current).Services.GetRequiredService<WeeklyPicksPageViewModel>();
        InitializeComponent();
        Loaded += OnLoaded;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (_eventsSubscribed) return;
        _eventsSubscribed = true;
        ViewModel.DownloadMangaRequested += OnDownloadMangaRequested;
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        if (_eventsSubscribed)
        {
            _eventsSubscribed = false;
            ViewModel.DownloadMangaRequested -= OnDownloadMangaRequested;
        }
        base.OnNavigatedFrom(e);
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.HasData) await ViewModel.InitializeAsync();
    }

    private void OnRetryClick(object sender, RoutedEventArgs e) =>
        _ = ViewModel.RefreshCommand.ExecuteAsync(null);

    private void OnItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is WeeklyPickItemViewModel item)
        {
            ViewModel.DownloadMangaCommand.Execute(item.Url);
        }
    }

    private void OnDownloadMangaRequested(object? sender, string url)
    {
        var shell = FindParent<ShellPage>(this);
        shell?.NavigateToPageWithUrl("download", url);
    }

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
