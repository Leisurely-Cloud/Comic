using System;
using Comic.WinUI.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Comic.WinUI.Views;

public sealed partial class DownloadPage : Page
{
    public DownloadPageViewModel ViewModel { get; private set; } = null!;
    private string? _pendingUrl;

    public DownloadPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        ViewModel = ((App)Application.Current).Services.GetRequiredService<DownloadPageViewModel>();
        Bindings.Update();

        // Check if we received a URL parameter
        if (e.Parameter is string url && !string.IsNullOrWhiteSpace(url))
        {
            _pendingUrl = url;
        }

        base.OnNavigatedTo(e);
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        // ViewModel 是 transient,每次 OnNavigatedTo 都会新建。离开时必须取消它的
        // 状态轮询,否则被丢弃的实例会继续以 150ms 周期轮询下去。
        ViewModel?.Dispose();
        base.OnNavigatedFrom(e);
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await ViewModel.InitializeCommand.ExecuteAsync(null);

            // If we have a pending URL, set it and resolve
            if (!string.IsNullOrWhiteSpace(_pendingUrl))
            {
                var url = _pendingUrl;
                ViewModel.SearchKeyword = url;
                _pendingUrl = null;
                await ViewModel.ResolveDirectUrlCommand.ExecuteAsync(url);
            }
        }
        catch
        {
            // Initialization failure is already handled by ViewModel
        }
    }

    private void OnWorkspaceTabClick(object sender, RoutedEventArgs e)
    {
        SetWorkspace(ReferenceEquals(sender, TasksWorkspaceTab));
    }

    private void SetWorkspace(bool showTasks)
    {
        DiscoveryWorkspaceTab.IsChecked = !showTasks;
        TasksWorkspaceTab.IsChecked = showTasks;
        DiscoveryWorkspace.Visibility = showTasks ? Visibility.Collapsed : Visibility.Visible;
        TaskWorkspace.Visibility = showTasks ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnDownloadStarted(object? sender, EventArgs e)
    {
        SetWorkspace(showTasks: true);
    }

    private void OnOnlineReadRequested(object? sender, string mangaUrl)
    {
        if (!string.IsNullOrWhiteSpace(mangaUrl))
        {
            Frame.Navigate(typeof(ReaderPage), new ReaderNavigationArgs(null, mangaUrl));
        }
    }
}
