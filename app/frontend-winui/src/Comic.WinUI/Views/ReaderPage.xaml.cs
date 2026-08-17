using Comic.WinUI.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;

namespace Comic.WinUI.Views;

public sealed partial class ReaderPage : Page
{
    private const double StripHorizontalGutter = 32;

    public ReaderPageViewModel ViewModel { get; private set; } = null!;
    private readonly Dictionary<UIElement, ReaderStripImageItemViewModel> _stripItemsByElement = [];
    private bool _stripChapterAdvancePending;
    private bool _stripZoomInProgress;

    public ReaderPage()
    {
        InitializeComponent();
        StripScrollViewer.AddHandler(
            UIElement.PointerWheelChangedEvent,
            new PointerEventHandler(OnStripPointerWheelChanged),
            true);
        Loaded += OnLoaded;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        ViewModel = ((App)Application.Current).Services.GetRequiredService<ReaderPageViewModel>();
        ViewModel.StripPositionRestoreRequested += OnStripPositionRestoreRequested;
        Bindings.Update();

        if (e.Parameter is string rootDir)
        {
            _rootDir = rootDir;
        }

        base.OnNavigatedTo(e);
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        SaveCurrentReadingPosition();
        ViewModel.StripPositionRestoreRequested -= OnStripPositionRestoreRequested;
        base.OnNavigatedFrom(e);
    }

    private string _rootDir = string.Empty;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(_rootDir))
        {
            await ViewModel.LoadCommand.ExecuteAsync(_rootDir);
        }
    }

    private void OnBackClick(object sender, RoutedEventArgs e)
    {
        if (Frame.CanGoBack)
        {
            Frame.GoBack();
        }
    }

    private async void OnPreviousImageClick(object sender, RoutedEventArgs e)
    {
        await ViewModel.PreviousImageCommand.ExecuteAsync(null);
    }

    private async void OnNextImageClick(object sender, RoutedEventArgs e)
    {
        await ViewModel.NextImageCommand.ExecuteAsync(null);
    }

    private void OnPreviousChapterClick(object sender, RoutedEventArgs e)
    {
        ViewModel.PreviousChapterCommand.Execute(null);
    }

    private void OnNextChapterClick(object sender, RoutedEventArgs e)
    {
        ViewModel.NextChapterCommand.Execute(null);
    }

    private void OnStripZoomOutClick(object sender, RoutedEventArgs e) =>
        ChangeStripZoom(-10, StripViewportCenter());

    private void OnStripZoomInClick(object sender, RoutedEventArgs e) =>
        ChangeStripZoom(10, StripViewportCenter());

    private void OnStripZoomResetClick(object sender, RoutedEventArgs e) =>
        ChangeStripZoom(100 - ViewModel.StripZoomPercent, StripViewportCenter());

    private void OnStripPointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (!ViewModel.IsStripMode ||
            !e.KeyModifiers.HasFlag(Windows.System.VirtualKeyModifiers.Control))
        {
            return;
        }

        var pointer = e.GetCurrentPoint(StripScrollViewer);
        var wheelDelta = pointer.Properties.MouseWheelDelta;
        if (wheelDelta != 0)
        {
            ChangeStripZoom(wheelDelta > 0 ? 10 : -10, pointer.Position);
        }
        e.Handled = true;
    }

    private Windows.Foundation.Point StripViewportCenter() => new(
        StripScrollViewer.ViewportWidth / 2,
        StripScrollViewer.ViewportHeight / 2);

    private void OnStripViewportSizeChanged(object sender, SizeChangedEventArgs e)
    {
        DispatcherQueue.TryEnqueue(UpdateStripContentWidth);
    }

    private double CalculateStripContentWidth(int zoomPercent)
    {
        var viewportWidth = StripScrollViewer.ViewportWidth > 0
            ? StripScrollViewer.ViewportWidth
            : StripScrollViewer.ActualWidth;
        var fitWidth = Math.Max(1, viewportWidth - StripHorizontalGutter);
        return fitWidth * zoomPercent / 100d;
    }

    private void UpdateStripContentWidth()
    {
        if (ViewModel is null)
        {
            return;
        }

        StripImageRepeater.Width = CalculateStripContentWidth(ViewModel.StripZoomPercent);
    }

    private void ChangeStripZoom(int delta, Windows.Foundation.Point anchor)
    {
        if (delta == 0) return;

        var oldPercent = ViewModel.StripZoomPercent;
        var oldWidth = StripImageRepeater.ActualWidth > 0
            ? StripImageRepeater.ActualWidth
            : CalculateStripContentWidth(oldPercent);
        var viewportWidth = StripScrollViewer.ViewportWidth;
        var oldContentLeft = oldWidth < viewportWidth
            ? (viewportWidth - oldWidth) / 2
            : -StripScrollViewer.HorizontalOffset;
        var relativeHorizontalPosition = oldWidth > 0
            ? Math.Clamp((anchor.X - oldContentLeft) / oldWidth, 0, 1)
            : 0.5;
        var oldVerticalOffset = StripScrollViewer.VerticalOffset;

        ViewModel.ChangeStripZoom(delta);
        if (oldPercent == ViewModel.StripZoomPercent) return;

        var scale = ViewModel.StripZoomPercent / (double)oldPercent;
        UpdateStripContentWidth();
        var newWidth = CalculateStripContentWidth(ViewModel.StripZoomPercent);
        _stripZoomInProgress = true;
        DispatcherQueue.TryEnqueue(() =>
        {
            StripScrollViewer.UpdateLayout();
            var targetHorizontalOffset = newWidth > StripScrollViewer.ViewportWidth
                ? relativeHorizontalPosition * newWidth - anchor.X
                : 0;
            targetHorizontalOffset = Math.Clamp(
                targetHorizontalOffset,
                0,
                StripScrollViewer.ScrollableWidth);
            var targetVerticalOffset = Math.Clamp(
                (oldVerticalOffset + anchor.Y) * scale - anchor.Y,
                0,
                StripScrollViewer.ScrollableHeight);
            StripScrollViewer.ChangeView(
                targetHorizontalOffset,
                targetVerticalOffset,
                null,
                true);
            DispatcherQueue.TryEnqueue(() => _stripZoomInProgress = false);
        });
    }

    private void OnReaderModeToggled(object sender, RoutedEventArgs e)
    {
        _stripChapterAdvancePending = false;
        if (ViewModel?.IsStripMode == true)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                UpdateStripContentWidth();
                StripScrollViewer.ChangeView(null, 0, null, true);
            });
        }
    }

    private void OnChapterSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _stripChapterAdvancePending = false;
        if (ViewModel?.IsStripMode == true)
        {
            StripScrollViewer.ChangeView(null, 0, null, true);
        }
    }

    private void OnStripElementPrepared(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs args)
    {
        if (args.Index < 0 || args.Index >= ViewModel.StripImages.Count) return;

        var item = ViewModel.StripImages[args.Index];
        _stripItemsByElement[args.Element] = item;
        _ = ViewModel.LoadStripImageAsync(item);
    }

    private void OnStripElementClearing(ItemsRepeater sender, ItemsRepeaterElementClearingEventArgs args)
    {
        if (_stripItemsByElement.Remove(args.Element, out var item))
        {
            ViewModel.UnloadStripImage(item);
        }
    }

    private void OnStripViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (sender is not ScrollViewer scrollViewer ||
            !ViewModel.IsStripMode ||
            _stripZoomInProgress ||
            e.IsIntermediate)
        {
            return;
        }

        SaveCurrentStripPosition();

        if (
            _stripChapterAdvancePending ||
            !ViewModel.HasNextChapter ||
            scrollViewer.ScrollableHeight <= 0 ||
            scrollViewer.VerticalOffset < scrollViewer.ScrollableHeight - 4)
        {
            return;
        }

        _stripChapterAdvancePending = true;
        ViewModel.NextChapterCommand.Execute(null);
    }

    private void SaveCurrentReadingPosition()
    {
        if (ViewModel is null) return;

        if (ViewModel.IsStripMode)
        {
            SaveCurrentStripPosition();
        }
        else
        {
            ViewModel.SaveReadingProgress();
        }
    }

    private void SaveCurrentStripPosition()
    {
        if (ViewModel is null || _stripItemsByElement.Count == 0) return;

        var focusY = StripScrollViewer.VerticalOffset + StripScrollViewer.ViewportHeight * 0.3;
        var closestIndex = ViewModel.CurrentImageIndex;
        var closestDistance = double.MaxValue;

        foreach (var pair in _stripItemsByElement)
        {
            if (pair.Key is not FrameworkElement element) continue;

            try
            {
                var top = element.TransformToVisual(StripImageRepeater)
                    .TransformPoint(new Windows.Foundation.Point(0, 0)).Y;
                var bottom = top + Math.Max(1, element.ActualHeight);
                var distance = focusY < top
                    ? top - focusY
                    : focusY > bottom
                        ? focusY - bottom
                        : 0;

                if (distance < closestDistance)
                {
                    closestDistance = distance;
                    closestIndex = pair.Value.Index;
                }
            }
            catch (ArgumentException)
            {
                // The repeater can recycle an element while a scroll event is being handled.
            }
        }

        ViewModel.SaveReadingProgress(closestIndex);
    }

    private void OnStripPositionRestoreRequested(int index)
    {
        if (index <= 0) return;

        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (!ViewModel.IsStripMode || index >= ViewModel.StripImages.Count) return;

            UpdateStripContentWidth();
            StripImageRepeater.UpdateLayout();
            var element = StripImageRepeater.GetOrCreateElement(index);
            element.StartBringIntoView(new BringIntoViewOptions
            {
                AnimationDesired = false,
                VerticalAlignmentRatio = 0,
            });
        });
    }

    private async void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case Windows.System.VirtualKey.Left:
                if (ViewModel.IsStripMode)
                {
                    ViewModel.PreviousChapterCommand.Execute(null);
                }
                else
                {
                    await ViewModel.PreviousImageCommand.ExecuteAsync(null);
                }
                e.Handled = true;
                break;
            case Windows.System.VirtualKey.Right:
                if (ViewModel.IsStripMode)
                {
                    ViewModel.NextChapterCommand.Execute(null);
                }
                else
                {
                    await ViewModel.NextImageCommand.ExecuteAsync(null);
                }
                e.Handled = true;
                break;
            case Windows.System.VirtualKey.Escape:
                if (Frame.CanGoBack)
                {
                    Frame.GoBack();
                }
                e.Handled = true;
                break;
        }
    }
}
