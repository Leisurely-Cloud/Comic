using Comic.WinUI.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Comic.WinUI.Views;

public sealed partial class RankingPage : Page
{
    public RankingPageViewModel ViewModel { get; }
    private bool _eventsSubscribed;

    public RankingPage()
    {
        // 单例 ViewModel:切走再切回时榜单与选择保持,不重新创建。
        ViewModel = ((App)Application.Current).Services.GetRequiredService<RankingPageViewModel>();
        SubscribeEvents();
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void SubscribeEvents()
    {
        if (_eventsSubscribed) return;
        _eventsSubscribed = true;
        ViewModel.DownloadMangaRequested += OnDownloadMangaRequested;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        // 单例 ViewModel 已加载过数据(切走再切回)时直接复用,避免榜单和选择丢失。
        if (ViewModel.HasData)
        {
            return;
        }
        await ViewModel.InitializeAsync();
    }

    private void OnDownloadMangaRequested(object? sender, string url)
    {
        if (!string.IsNullOrEmpty(url))
        {
            var shellPage = FindParent<ShellPage>(this);
            if (shellPage is not null)
            {
                shellPage.NavigateToPageWithUrl("download", url);
            }
        }
    }

    private void OnRetryClick(object sender, RoutedEventArgs e)
    {
        _ = ViewModel.RefreshCommand.ExecuteAsync(null);
    }

    private void OnLoadMoreClick(object sender, RoutedEventArgs e)
    {
        _ = ViewModel.LoadMoreCommand.ExecuteAsync(null);
    }

    private void OnRankingItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is RankingItemViewModel item)
        {
            ViewModel.DownloadMangaCommand.Execute(item.Url);
        }
    }

    private static T? FindParent<T>(DependencyObject child) where T : DependencyObject
    {
        var parent = VisualTreeHelper.GetParent(child);
        while (parent is not null)
        {
            if (parent is T typed)
                return typed;
            parent = VisualTreeHelper.GetParent(parent);
        }
        return null;
    }
}
