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

    private async void OnDeleteMangaClick(object sender, RoutedEventArgs e)
    {
        var selected = ViewModel.SelectedItem;
        if (selected is null || string.IsNullOrWhiteSpace(selected.RootDir)) return;

        var duplicateMessage = selected.DuplicateDirectoryCount > 0
            ? $"\n\n另外检测到 {selected.DuplicateDirectoryCount} 个重复目录，也会一并移入回收站。"
            : string.Empty;
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "删除本地漫画？",
            Content = $"《{selected.Title}》及其 {selected.DownloadedChapterCount} 个本地章节将移入 Windows 回收站，可从回收站恢复。{duplicateMessage}",
            PrimaryButtonText = "移入回收站",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            await ViewModel.DeleteSelectedMangaCommand.ExecuteAsync(null);
        }
    }
}
