using Comic.WinUI.ViewModels;
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

    private void OnTabSelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (sender.SelectedItem is not SelectorBarItem item) return;

        var index = sender.Items.IndexOf(item);
        var isHistory = index == 1;

        TaskListView.Visibility = isHistory ? Visibility.Collapsed : Visibility.Visible;
        HistoryView.Visibility = isHistory ? Visibility.Visible : Visibility.Collapsed;
        ProgressSection.Visibility = isHistory ? Visibility.Collapsed : Visibility.Visible;
        BatchToggleButtons.Visibility = isHistory ? Visibility.Collapsed : Visibility.Visible;
        BatchActionBar.Visibility = isHistory ? Visibility.Collapsed
            : (ViewModel?.IsBatchMode == true ? Visibility.Visible : Visibility.Collapsed);

        if (isHistory)
        {
            _ = ViewModel?.LoadHistoryCommand.ExecuteAsync(null);
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

    private void OnBatchDeleteClick(object sender, RoutedEventArgs e)
    {
        _ = ViewModel?.BatchDeleteCommand.ExecuteAsync(null);
    }

    private void OnHistoryPrevClick(object sender, RoutedEventArgs e)
    {
        _ = ViewModel?.HistoryPrevCommand.ExecuteAsync(null);
    }

    private void OnHistoryNextClick(object sender, RoutedEventArgs e)
    {
        _ = ViewModel?.HistoryNextCommand.ExecuteAsync(null);
    }

    private void OnClearHistoryClick(object sender, RoutedEventArgs e)
    {
        _ = ViewModel?.ClearHistoryCommand.ExecuteAsync(null);
    }
}
