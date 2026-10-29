using System;
using System.IO;
using System.Linq;
using Comic.WinUI.Models;
using Comic.WinUI.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace Comic.WinUI.Views;

public sealed partial class LibraryPage
{
    private bool _isChoosingArchive;
    private bool _archiveHistoryOpen;
    private bool _archivePageActive;
    private int _archiveInputBlocks;

    private bool CanAcceptArchiveDrop => _archivePageActive && !_isChoosingArchive && !_archiveHistoryOpen &&
        !_organizationDialogOpen && _archiveInputBlocks == 0 && !ViewModel.IsResolvingArchiveDrop;

    private IDisposable BlockArchiveInput()
    {
        ViewModel.CancelArchiveDrop();
        _archiveInputBlocks++;
        return new ArchiveInputBlock(this);
    }

    private sealed class ArchiveInputBlock(LibraryPage page) : IDisposable
    {
        private LibraryPage? _page = page;

        public void Dispose()
        {
            if (_page is not { } owner) return;
            _page = null;
            owner._archiveInputBlocks--;
        }
    }

    private void OnArchiveDragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        e.AcceptedOperation = DataPackageOperation.None;
        if (!CanAcceptArchiveDrop || !e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        e.AcceptedOperation = DataPackageOperation.Copy;
        e.DragUIOverride.Caption = "打开压缩包阅读（每次一个 CBZ / ZIP）";
        e.DragUIOverride.IsCaptionVisible = true;
    }

    private async void OnArchiveDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        e.AcceptedOperation = DataPackageOperation.None;
        if (!CanAcceptArchiveDrop || !e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        var deferral = e.GetDeferral();
        var viewModel = ViewModel;
        try
        {
            RecentArchiveMenu.Hide();
            var filePath = await viewModel.ResolveDroppedArchiveAsync(async token =>
            {
                var items = await e.DataView.GetStorageItemsAsync().AsTask(token);
                return items.Take(2).Select(item => new ArchiveDropItem(item.Path, item is StorageFile)).ToArray();
            });
            // The drag source may ignore cancellation. Never navigate after leaving the page or opening a dialog.
            if (filePath is null || !_archivePageActive || !ReferenceEquals(ViewModel, viewModel) ||
                _isChoosingArchive || _archiveHistoryOpen || _organizationDialogOpen || _archiveInputBlocks > 0) return;
            OpenArchive(filePath);
            e.AcceptedOperation = DataPackageOperation.Copy;
        }
        catch (Exception ex)
        {
            if (_archivePageActive && ReferenceEquals(ViewModel, viewModel))
                viewModel.PageError = $"打开拖入文件失败：{ex.Message}";
        }
        finally { deferral.Complete(); }
    }

    private async void OnOpenArchiveClick(SplitButton sender, SplitButtonClickEventArgs e)
    {
        if (!_archivePageActive || _isChoosingArchive || _archiveHistoryOpen || ViewModel.IsResolvingArchiveDrop) return;
        _isChoosingArchive = true;
        try
        {
            var window = ((App)Application.Current).MainWindow;
            if (window is null)
            {
                ViewModel.PageError = "无法获取当前窗口。";
                return;
            }

            var picker = new FileOpenPicker
            {
                SuggestedStartLocation = PickerLocationId.Downloads,
                ViewMode = PickerViewMode.List,
                CommitButtonText = "打开阅读",
            };
            picker.FileTypeFilter.Add(".cbz");
            picker.FileTypeFilter.Add(".zip");
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(window));
            var file = await picker.PickSingleFileAsync();
            if (file is null) return;

            OpenArchive(Path.GetFullPath(file.Path));
        }
        catch (Exception ex)
        {
            ViewModel.PageError = $"打开压缩包失败：{ex.Message}";
        }
        finally
        {
            _isChoosingArchive = false;
        }
    }

    private void OpenArchive(string filePath)
    {
        if (!_archivePageActive) return;
        ViewModel.PageError = string.Empty;
        // A history click is a new, explicit open. The reader revalidates the file and creates a fresh session.
        if (!Frame.Navigate(typeof(ReaderPage), new ReaderNavigationArgs(null, null, filePath)))
            ViewModel.PageError = "无法打开阅读器，请重试。";
    }

    private void OnRecentArchiveMenuOpening(object sender, object e)
    {
        RecentArchiveMenu.Items.Clear();
        var entries = ViewModel.GetRecentArchives();
        if (entries.Count == 0)
        {
            RecentArchiveMenu.Items.Add(new MenuFlyoutItem { Text = "暂无压缩包阅读记录", IsEnabled = false });
        }
        foreach (var entry in entries.Take(5))
        {
            var item = new MenuFlyoutItem { Text = $"{entry.Title} · {entry.ProgressText}", MaxWidth = 520 };
            ToolTipService.SetToolTip(item, entry.FilePath);
            item.Click += (_, _) => OpenArchive(entry.FilePath);
            RecentArchiveMenu.Items.Add(item);
        }
        RecentArchiveMenu.Items.Add(new MenuFlyoutSeparator());
        var manage = new MenuFlyoutItem { Text = $"管理最近记录（{entries.Count}）" };
        manage.Click += OnManageRecentArchivesClick;
        RecentArchiveMenu.Items.Add(manage);
    }

    private async void OnManageRecentArchivesClick(object sender, RoutedEventArgs e)
    {
        if (!_archivePageActive || _archiveHistoryOpen || _isChoosingArchive || ViewModel.IsResolvingArchiveDrop) return;
        _archiveHistoryOpen = true;
        try
        {
            var list = new ListView { SelectionMode = ListViewSelectionMode.Single, MaxHeight = 280 };
            AutomationProperties.SetName(list, "最近打开的压缩包");
            var remove = new Button { Content = "移除所选记录" };
            var clear = new Button { Content = "清空记录" };
            var empty = new TextBlock { Text = "暂无压缩包阅读记录。选择文件并成功阅读后会显示在这里。", TextWrapping = TextWrapping.Wrap };
            var error = new TextBlock { TextWrapping = TextWrapping.Wrap };
            if (Application.Current.Resources.TryGetValue("SystemFillColorCriticalBrush", out var brush) && brush is Microsoft.UI.Xaml.Media.Brush errorBrush)
                error.Foreground = errorBrush;
            AutomationProperties.SetLiveSetting(error, AutomationLiveSetting.Assertive);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            buttons.Children.Add(remove);
            buttons.Children.Add(clear);
            var panel = new StackPanel { Spacing = 12, MinWidth = 340, MaxWidth = 520 };
            panel.Children.Add(new TextBlock { Text = "保留最近 20 个成功阅读的压缩包。移除记录会保留源文件、阅读进度和书签。", TextWrapping = TextWrapping.Wrap });
            panel.Children.Add(empty);
            panel.Children.Add(list);
            panel.Children.Add(buttons);
            panel.Children.Add(error);
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                RequestedTheme = ActualTheme,
                Title = "最近打开的压缩包",
                Content = panel,
                PrimaryButtonText = "继续阅读",
                CloseButtonText = "关闭",
                DefaultButton = ContentDialogButton.Primary,
            };
            void UpdateButtons()
            {
                var hasSelection = list.SelectedItem is ListViewItem;
                dialog.IsPrimaryButtonEnabled = hasSelection;
                remove.IsEnabled = hasSelection;
                clear.IsEnabled = list.Items.Count > 0;
                empty.Visibility = list.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            }
            void Reload()
            {
                var selectedIndex = list.SelectedIndex;
                list.Items.Clear();
                foreach (var entry in ViewModel.GetRecentArchives())
                {
                    var row = new StackPanel { Spacing = 3, Margin = new Thickness(0, 6, 0, 6) };
                    row.Children.Add(new TextBlock { Text = entry.Title, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
                    row.Children.Add(new TextBlock { Text = entry.ProgressText, FontSize = 12 });
                    row.Children.Add(new TextBlock { Text = entry.FilePath, FontSize = 12, Opacity = 0.7, TextWrapping = TextWrapping.Wrap });
                    list.Items.Add(new ListViewItem { Content = row, Tag = entry, HorizontalContentAlignment = HorizontalAlignment.Stretch });
                }
                if (list.Items.Count > 0) list.SelectedIndex = Math.Clamp(selectedIndex, 0, list.Items.Count - 1);
                UpdateButtons();
            }
            list.SelectionChanged += (_, _) => UpdateButtons();
            remove.Click += (_, _) =>
            {
                if (list.SelectedItem is not ListViewItem { Tag: RecentArchiveItem entry }) return;
                if (ViewModel.RemoveRecentArchive(entry.FilePath)) { error.Text = string.Empty; Reload(); }
                else error.Text = ViewModel.PageError;
            };
            clear.Click += (_, _) =>
            {
                if (ViewModel.ClearRecentArchives()) { error.Text = string.Empty; Reload(); }
                else error.Text = ViewModel.PageError;
            };
            Reload();
            if (await dialog.ShowAsync() == ContentDialogResult.Primary &&
                list.SelectedItem is ListViewItem { Tag: RecentArchiveItem selected })
                OpenArchive(selected.FilePath);
        }
        catch (Exception ex) { ViewModel.PageError = $"打开最近记录失败：{ex.Message}"; }
        finally { _archiveHistoryOpen = false; }
    }
}
