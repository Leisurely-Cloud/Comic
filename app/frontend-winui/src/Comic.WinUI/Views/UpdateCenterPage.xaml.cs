using Comic.WinUI.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;

namespace Comic.WinUI.Views;

public sealed partial class UpdateCenterPage : Page
{
    public UpdateCenterPageViewModel ViewModel { get; }
    private bool _subscribed;

    public UpdateCenterPage()
    {
        ViewModel = ((App)Application.Current).Services.GetRequiredService<UpdateCenterPageViewModel>();
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (_subscribed) return;
        _subscribed = true;
        ViewModel.OpenMangaRequested += OnOpenMangaRequested;
        ViewModel.OpenDownloadsRequested += OnOpenDownloadsRequested;
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        if (_subscribed)
        {
            _subscribed = false;
            ViewModel.OpenMangaRequested -= OnOpenMangaRequested;
            ViewModel.OpenDownloadsRequested -= OnOpenDownloadsRequested;
        }
        base.OnNavigatedFrom(e);
    }

    private void OnOpenMangaRequested(object? sender, string url) =>
        FindParent<ShellPage>(this)?.NavigateToPageWithUrl("download", url);

    private void OnOpenDownloadsRequested(object? sender, System.EventArgs e) =>
        FindParent<ShellPage>(this)?.NavigateToDownloadTasks();

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
