using System;
using Comic.WinUI.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Comic.WinUI.Views;

public sealed partial class RankingPage : Page
{
    public RankingPageViewModel ViewModel { get; }

    public RankingPage()
    {
        ViewModel = App.GetService<RankingPageViewModel>();
        ViewModel.DownloadMangaRequested += OnDownloadMangaRequested;
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
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

    private void OnSiteSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SiteComboBox.SelectedItem is ComboBoxItem item)
        {
            var siteKey = item.Tag as string ?? "baozimh";
            ViewModel.SelectedSite = siteKey;
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

    private void OnRankingItemSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ListView listView && listView.SelectedItem is RankingItemViewModel item)
        {
            ViewModel.NavigateToDetailCommand.Execute(item.Url);
            listView.SelectedItem = null;
        }
    }

    private void OnCopyLinkClick(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem menuFlyoutItem && menuFlyoutItem.Tag is string url && !string.IsNullOrEmpty(url))
        {
            Windows.ApplicationModel.DataTransfer.DataPackage package = new();
            package.SetText(url);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
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
