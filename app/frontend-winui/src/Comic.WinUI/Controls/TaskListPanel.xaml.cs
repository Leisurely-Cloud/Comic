using Comic.WinUI.ViewModels;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Comic.WinUI.Controls;

public sealed partial class TaskListPanel : UserControl
{
    public static readonly DependencyProperty ViewModelProperty =
        DependencyProperty.Register(nameof(ViewModel), typeof(DownloadPageViewModel), typeof(TaskListPanel), new PropertyMetadata(null, OnViewModelChanged));

    public DownloadPageViewModel ViewModel
    {
        get => (DownloadPageViewModel)GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }

    public TaskListPanel()
    {
        InitializeComponent();
    }

    private static void OnViewModelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        ((TaskListPanel)d).Bindings.Update();
    }

    private void OnTaskTabClick(object sender, RoutedEventArgs e)
    {
        SetHistoryView(false);
    }

    private void OnHistoryTabClick(object sender, RoutedEventArgs e)
    {
        SetHistoryView(true);
    }

    private void SetHistoryView(bool isHistory)
    {
        CurrentTasksTab.IsChecked = !isHistory;
        HistoryTab.IsChecked = isHistory;
        TaskListView.Visibility = isHistory ? Visibility.Collapsed : Visibility.Visible;
        HistoryView.Visibility = isHistory ? Visibility.Visible : Visibility.Collapsed;
        TaskCountLabel.Visibility = isHistory ? Visibility.Collapsed : Visibility.Visible;
        HistoryCountLabel.Visibility = isHistory ? Visibility.Visible : Visibility.Collapsed;
        ProgressSection.Visibility = isHistory ? Visibility.Collapsed : Visibility.Visible;
        BatchToggleButtons.Visibility = isHistory ? Visibility.Collapsed : Visibility.Visible;
        BatchActionBar.Visibility = isHistory ? Visibility.Collapsed
            : (ViewModel?.IsBatchMode == true ? Visibility.Visible : Visibility.Collapsed);
        HistoryActionBar.Visibility = isHistory ? Visibility.Visible : Visibility.Collapsed;

        if (isHistory)
        {
            _ = ViewModel?.LoadHistoryCommand.ExecuteAsync(null);
        }
    }

    private void OnLocateIncompleteChapterClick(object sender, RoutedEventArgs e)
    {
        var chapter = ViewModel?.CurrentTask?.Chapters.FirstOrDefault(item => !string.Equals(item.Status, "completed", StringComparison.OrdinalIgnoreCase));
        if (chapter is not null)
        {
            ChapterListView.ScrollIntoView(chapter, ScrollIntoViewAlignment.Default);
        }
    }

    private void OnStopCurrentClick(object sender, RoutedEventArgs e)
    {
        _ = ViewModel?.StopCommand.ExecuteAsync(null);
    }

    private void OnToggleBatchModeClick(object sender, RoutedEventArgs e)
    {
        ViewModel?.ToggleBatchModeCommand.Execute(null);
    }

    private void OnSelectAllClick(object sender, RoutedEventArgs e)
    {
        ViewModel?.SelectAllTasksCommand.Execute(null);
    }

    private void OnDeselectAllClick(object sender, RoutedEventArgs e)
    {
        ViewModel?.DeselectAllTasksCommand.Execute(null);
    }

    private void OnBatchStopClick(object sender, RoutedEventArgs e)
    {
        _ = ViewModel?.BatchStopCommand.ExecuteAsync(null);
    }

    private async void OnBatchDeleteClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null || !ViewModel.HasSelectedTasks)
        {
            return;
        }

        var details = ViewModel.SelectedDownloadChapterCount > 0
            ? $"将永久删除 {ViewModel.SelectedDownloadChapterCount} 个章节的本地图片。"
            : "将删除选中的已结束任务记录。";
        if (ViewModel.SelectedTaskCount > 0 && ViewModel.SelectedDownloadChapterCount > 0)
        {
            details += $" 另外还会处理 {ViewModel.SelectedTaskCount} 个整项任务。";
        }

        var dialog = new ContentDialog
        {
            Title = "确认删除所选内容？",
            Content = details + " 此操作无法撤销。",
            PrimaryButtonText = "确认删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            await ViewModel.BatchDeleteCommand.ExecuteAsync(null);
        }
    }

    private void OnHistoryPrevClick(object sender, RoutedEventArgs e)
    {
        _ = ViewModel?.HistoryPrevCommand.ExecuteAsync(null);
    }

    private void OnHistoryNextClick(object sender, RoutedEventArgs e)
    {
        _ = ViewModel?.HistoryNextCommand.ExecuteAsync(null);
    }

    private void OnSelectAllHistoryClick(object sender, RoutedEventArgs e)
    {
        ViewModel?.SelectAllHistoryCommand.Execute(null);
    }

    private void OnDeselectAllHistoryClick(object sender, RoutedEventArgs e)
    {
        ViewModel?.DeselectAllHistoryCommand.Execute(null);
    }

    private async void OnDeleteSelectedHistoryClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        var ids = ViewModel.HistoryItems
            .Where(item => item.IsSelected)
            .Select(item => item.Id)
            .ToList();
        await ConfirmDeleteHistoryAsync(ids);
    }

    private async void OnDeleteHistoryItemClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem { CommandParameter: string id } || string.IsNullOrWhiteSpace(id))
        {
            return;
        }
        await ConfirmDeleteHistoryAsync([id]);
    }

    private async Task ConfirmDeleteHistoryAsync(IReadOnlyCollection<string> ids)
    {
        if (ViewModel is null || ids.Count == 0)
        {
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = ids.Count == 1 ? "删除这条历史记录？" : $"删除所选的 {ids.Count} 条记录？",
            Content = "只会删除下载历史，不会删除已经下载的漫画文件。此操作无法撤销。",
            PrimaryButtonText = "删除记录",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            await ViewModel.DeleteHistoryItemsAsync(ids);
        }
    }

    private async void OnClearHistoryClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel?.HasHistoryItems != true) return;

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "清空下载历史？",
            Content = "只会删除历史记录，不会删除已经下载的漫画文件。此操作无法撤销。",
            PrimaryButtonText = "清空历史",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            await ViewModel.ClearHistoryCommand.ExecuteAsync(null);
        }
    }

    private void OnHistoryOpenDirectoryClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string path }) return;
        try
        {
            if (!Directory.Exists(path))
            {
                if (ViewModel is not null) ViewModel.PageError = "下载目录不存在，可能已经被移动或删除。";
                return;
            }

            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            if (ViewModel is not null) ViewModel.PageError = $"打开下载目录失败：{ex.Message}";
        }
    }

    private async void OnRetryHistoryItemClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null || sender is not Button { Tag: string historyId }) return;
        if (await ViewModel.RetryHistoryItemAsync(historyId))
        {
            SetHistoryView(false);
        }
    }

    private void OnHistoryOpenSourceClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string url } ||
            !Uri.TryCreate(url, UriKind.Absolute, out var sourceUri))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(sourceUri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            if (ViewModel is not null) ViewModel.PageError = $"打开源链接失败：{ex.Message}";
        }
    }
}
