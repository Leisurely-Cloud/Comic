using Comic.WinUI.ViewModels;
using Comic.WinUI.Services;
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
            var app = (App)Application.Current;
            var maintenance = app.Services.GetRequiredService<DataMaintenanceService>();
            ViewModel.DataMaintenanceStatus = maintenance.HasPendingRestore
                ? "已准备恢复数据，请关闭并重新打开软件。可在重启前取消。"
                : app.DataRestoreStatus ?? "尚未检查；备份适用于当前书库目录。";
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
            RequestedTheme = ActualTheme,
            PrimaryButtonText = "清空",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private async Task RunMaintenanceAsync(Func<DataMaintenanceService, Task> action)
    {
        if (ViewModel.IsDataMaintenanceBusy) return;
        ViewModel.IsDataMaintenanceBusy = true;
        ViewModel.DataMaintenanceStatus = "正在处理…";
        try { await action(((App)Application.Current).Services.GetRequiredService<DataMaintenanceService>()); }
        catch (Exception ex) { ViewModel.DataMaintenanceStatus = $"操作未完成：{ex.Message}"; }
        finally { ViewModel.IsDataMaintenanceBusy = false; }
    }

    private nint PickerWindowHandle => WindowNative.GetWindowHandle(((App)Application.Current).MainWindow!);

    private async void OnExportDataBackupClick(object sender, RoutedEventArgs e)
    {
        await RunMaintenanceAsync(async service =>
        {
            var picker = new FileSavePicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary, SuggestedFileName = $"Comic-Data-{DateTime.Now:yyyyMMdd-HHmmss}" };
            picker.FileTypeChoices.Add("漫画下载器数据备份", new List<string> { ".zip" });
            InitializeWithWindow.Initialize(picker, PickerWindowHandle);
            var file = await picker.PickSaveFileAsync();
            if (file is null) { ViewModel.DataMaintenanceStatus = "已取消导出。"; return; }
            await Task.Run(() => service.ExportBackup(file.Path));
            ViewModel.DataMaintenanceStatus = $"备份已导出：{file.Path}";
        });
    }

    private async void OnRestoreDataBackupClick(object sender, RoutedEventArgs e)
    {
        await RunMaintenanceAsync(async service =>
        {
            var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
            picker.FileTypeFilter.Add(".zip");
            InitializeWithWindow.Initialize(picker, PickerWindowHandle);
            var file = await picker.PickSingleFileAsync();
            if (file is null) { ViewModel.DataMaintenanceStatus = "已取消恢复。"; return; }
            var preview = await Task.Run(() => service.PreviewBackup(file.Path));
            var message = $"备份时间：{preview.CreatedAt.LocalDateTime:yyyy-MM-dd HH:mm}\n应用数据：{preview.AppFileCount} 份\n下载记录：{preview.HistoryFileCount} 份\n漫画元数据与收藏：{preview.MangaCount} 部\n数据大小：{preview.Bytes / 1024d:N1} KB\n\n将覆盖对应数据，关闭并重新打开软件后生效。恢复前会自动保存一份备份。";
            if (!await ConfirmMaintenanceAsync("恢复这份备份？", message, "准备恢复")) { ViewModel.DataMaintenanceStatus = "已取消恢复。"; return; }
            await Task.Run(() => service.StageRestore(file.Path));
            ViewModel.DataMaintenanceStatus = "已准备恢复数据，请关闭并重新打开软件。可在重启前取消。";
        });
    }

    private async void OnCheckDataClick(object sender, RoutedEventArgs e)
    {
        await RunMaintenanceAsync(async service =>
        {
            var issues = await Task.Run(() => service.PreviewRepairs());
            if (issues.Count == 0) { ViewModel.DataMaintenanceStatus = "检查完成，未发现损坏数据、缺图记录或未完成文件。"; return; }
            var repairable = issues.Count(issue => issue.CanRepair);
            var content = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true,
                Text = $"发现 {issues.Count} 项，其中 {repairable} 项可从备份修复。\n\n" + string.Join("\n\n", issues.Take(200).Select(issue => $"{issue.Description}\n{issue.Path}")) +
                    (issues.Count > 200 ? "\n\n仅显示前 200 项。" : string.Empty),
            };
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "数据检查结果",
                RequestedTheme = ActualTheme,
                Content = new ScrollViewer { Content = content, MaxHeight = 420, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled },
                PrimaryButtonText = repairable > 0 ? "准备修复" : string.Empty,
                CloseButtonText = "关闭",
                DefaultButton = ContentDialogButton.Close,
            };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                var count = await Task.Run(() => service.StageRepairs(issues));
                ViewModel.DataMaintenanceStatus = $"已准备修复 {count} 份数据，请关闭并重新打开软件；其他问题请按检查结果处理。";
            }
            else ViewModel.DataMaintenanceStatus = $"检查完成：发现 {issues.Count} 项问题，未修改文件。";
        });
    }

    private async void OnCancelDataRestoreClick(object sender, RoutedEventArgs e)
    {
        await RunMaintenanceAsync(async service =>
        {
            if (!service.HasPendingRestore) { ViewModel.DataMaintenanceStatus = "没有待恢复的数据。"; return; }
            if (!await ConfirmMaintenanceAsync("取消待恢复的数据？", "取消后将继续使用当前设置和记录。", "取消恢复")) return;
            service.CancelRestore();
            ViewModel.DataMaintenanceStatus = "已取消待恢复的数据。";
        });
    }

    private async Task<bool> ConfirmMaintenanceAsync(string title, string message, string button)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            RequestedTheme = ActualTheme,
            PrimaryButtonText = button,
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }
}
