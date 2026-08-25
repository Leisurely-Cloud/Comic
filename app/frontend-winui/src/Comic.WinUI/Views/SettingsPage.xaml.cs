using Comic.WinUI.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace Comic.WinUI.Views;

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
        ViewModel = ((App)Application.Current).Services.GetRequiredService<SettingsPageViewModel>();
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

    private async void OnBrowseFolderClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var window = ((App)Application.Current).MainWindow;
            if (window is null)
            {
                ViewModel.SettingsError = "无法获取当前窗口。";
                return;
            }

            var picker = new FolderPicker
            {
                SuggestedStartLocation = PickerLocationId.Downloads,
            };
            picker.FileTypeFilter.Add("*");
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(window));
            var folder = await picker.PickSingleFolderAsync();
            if (folder is not null)
            {
                ViewModel.StorageRoot = folder.Path;
                ViewModel.SettingsError = string.Empty;
                ViewModel.SaveStatus = "已选择新目录，点击“保存设置”后生效。";
            }
        }
        catch (Exception ex)
        {
            ViewModel.SettingsError = $"选择目录失败: {ex.Message}";
        }
    }

    private void OnSectionNavigationClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string sectionName }) return;

        FrameworkElement? section = sectionName switch
        {
            nameof(GeneralSection) => GeneralSection,
            nameof(AccountSection) => AccountSection,
            nameof(DownloadSection) => DownloadSection,
            nameof(ReaderSection) => ReaderSection,
            nameof(LibrarySection) => LibrarySection,
            nameof(MaintenanceSection) => MaintenanceSection,
            _ => null,
        };
        section?.StartBringIntoView(new BringIntoViewOptions
        {
            AnimationDesired = true,
            VerticalAlignmentRatio = 0,
        });
    }

    private async void OnJmLoginClick(object sender, RoutedEventArgs e)
    {
        var password = JmPasswordBox.Password;
        try
        {
            await ViewModel.LoginJmAsync(password);
        }
        finally
        {
            // Password 不进入 ViewModel，也不在界面中保留。
            JmPasswordBox.Password = string.Empty;
        }
    }

    private async void OnClearSearchHistoryClick(object sender, RoutedEventArgs e)
    {
        if (!await ConfirmClearAsync("清空搜索记录？", "保存的搜索关键词将被删除，此操作无法撤销。")) return;
        ViewModel.ClearSearchHistoryCommand.Execute(null);
    }

    private async void OnClearDownloadHistoryClick(object sender, RoutedEventArgs e)
    {
        if (!await ConfirmClearAsync("清空下载历史？", "只会删除已结束任务的历史记录，不会删除漫画文件。")) return;
        await ViewModel.ClearDownloadHistoryCommand.ExecuteAsync(null);
    }

    private async Task<bool> ConfirmClearAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = message,
            PrimaryButtonText = "清空",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }
}
