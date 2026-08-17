using System;
using Comic.WinUI.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace Comic.WinUI.Controls;

public sealed partial class MangaSelectionPanel : UserControl
{
    private ScrollViewer? _chapterScrollViewer;

    public static readonly DependencyProperty ViewModelProperty =
        DependencyProperty.Register(
            nameof(ViewModel),
            typeof(DownloadPageViewModel),
            typeof(MangaSelectionPanel),
            new PropertyMetadata(null, OnViewModelChanged));

    public DownloadPageViewModel ViewModel
    {
        get => (DownloadPageViewModel)GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }

    public event EventHandler? DownloadStarted;

    public MangaSelectionPanel()
    {
        InitializeComponent();
        ChapterListView.Loaded += OnChapterListLoaded;
    }

    private void OnChapterListLoaded(object sender, RoutedEventArgs e)
    {
        _chapterScrollViewer = FindDescendant<ScrollViewer>(ChapterListView);
    }

    private void OnPanelPointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (ViewModel?.ShowChapterSelection != true)
        {
            return;
        }

        _chapterScrollViewer ??= FindDescendant<ScrollViewer>(ChapterListView);
        if (_chapterScrollViewer is null || _chapterScrollViewer.ScrollableHeight <= 0)
        {
            return;
        }

        var wheelDelta = e.GetCurrentPoint(this).Properties.MouseWheelDelta;
        var targetOffset = Math.Clamp(
            _chapterScrollViewer.VerticalOffset - wheelDelta,
            0,
            _chapterScrollViewer.ScrollableHeight);

        _chapterScrollViewer.ChangeView(null, targetOffset, null, true);
        e.Handled = true;
    }

    private static T? FindDescendant<T>(DependencyObject parent) where T : DependencyObject
    {
        var childCount = VisualTreeHelper.GetChildrenCount(parent);
        for (var index = 0; index < childCount; index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match)
            {
                return match;
            }

            var descendant = FindDescendant<T>(child);
            if (descendant is not null)
            {
                return descendant;
            }
        }

        return null;
    }

    private static void OnViewModelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        ((MangaSelectionPanel)d).Bindings.Update();
    }

    private async void OnStartDownloadClick(object sender, RoutedEventArgs e)
    {
        var previousTaskId = ViewModel.CurrentTaskId;
        await ViewModel.StartDownloadCommand.ExecuteAsync(null);
        if (string.IsNullOrWhiteSpace(ViewModel.PageError)
            && ViewModel.HasCurrentTask
            && !string.Equals(previousTaskId, ViewModel.CurrentTaskId, StringComparison.Ordinal))
        {
            DownloadStarted?.Invoke(this, EventArgs.Empty);
        }
    }
}
