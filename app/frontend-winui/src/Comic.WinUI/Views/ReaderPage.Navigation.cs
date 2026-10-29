using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Comic.WinUI.Services;
using Comic.WinUI.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Comic.WinUI.Views;

public sealed partial class ReaderPage
{
    private bool _navigationDialogOpen;

    private async void OnPageNavigatorClick(object sender, RoutedEventArgs e)
    {
        if (_navigationDialogOpen || ViewModel.TotalImages == 0 || ViewModel.IsLoading) return;
        _navigationDialogOpen = true;
        using var scope = new CancellationTokenSource();
        var thumbnails = ViewModel.CreateThumbnails();
        var page = new NumberBox
        {
            Header = $"页码（1–{ViewModel.TotalImages}）",
            Minimum = 1,
            Maximum = ViewModel.TotalImages,
            Value = ViewModel.CurrentImageIndex + 1,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact
        };
        var grid = new GridView
        {
            ItemsSource = thumbnails,
            ItemTemplate = (DataTemplate)Resources["ReaderThumbnailTemplate"],
            Height = 340,
            SelectionMode = ListViewSelectionMode.Single,
            IsItemClickEnabled = true
        };
        var panel = new StackPanel { Spacing = 12, MinWidth = 360 };
        panel.Children.Add(page);
        panel.Children.Add(new TextBlock { Text = "只加载可见缩略图；点击预览可选中页码。", TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(grid);
        var dialog = NavigationDialog("章节页码与缩略图", panel, "跳转");
        grid.ItemClick += (_, args) => page.Value = ((ReaderStripImageItemViewModel)args.ClickedItem).Index + 1;
        grid.ContainerContentChanging += (senderGrid, args) =>
        {
            if (args.Item is not ReaderStripImageItemViewModel item) return;
            if (args.InRecycleQueue) item.Unload();
            else _ = ViewModel.LoadThumbnailAsync(item, scope.Token);
        };
        try
        {
            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                if (!double.IsFinite(page.Value) || page.Value != Math.Truncate(page.Value)) throw new ArgumentException("请输入整数页码。");
                await ViewModel.JumpToPageAsync((int)page.Value);
            }
        }
        catch (Exception ex) { ViewModel.PageError = ex.Message; }
        finally
        {
            scope.Cancel();
            foreach (var item in thumbnails) item.Unload();
            _navigationDialogOpen = false;
        }
    }

    private async void OnBookmarksClick(object sender, RoutedEventArgs e)
    {
        if (_navigationDialogOpen) return;
        SaveCurrentReadingPosition();
        _navigationDialogOpen = true;
        try
        {
            var keepOpen = true;
            while (keepOpen)
            {
                var bookmarks = ViewModel.GetBookmarks();
                var list = new ListView
                {
                    ItemsSource = bookmarks,
                    DisplayMemberPath = nameof(ReadingBookmark.Label),
                    Height = Math.Min(220, Math.Max(44, bookmarks.Count * 44)),
                    SelectionMode = ListViewSelectionMode.Single
                };
                var existing = bookmarks.FirstOrDefault(item => item.Chapter == ViewModel.SelectedChapter?.DirName && item.PageIndex == ViewModel.CurrentImageIndex);
                var note = new TextBox
                {
                    Header = "当前页备注",
                    Text = existing?.Note ?? string.Empty,
                    AcceptsReturn = true,
                    MaxLength = 2000,
                    Height = 110,
                    TextWrapping = TextWrapping.Wrap
                };
                var status = new TextBlock { Text = bookmarks.Count == 0 ? "暂无书签。保存当前页后可随时回到这里。" : "选择书签可编辑备注，或跳转到对应章节。", TextWrapping = TextWrapping.Wrap };
                list.SelectionChanged += (_, _) =>
                {
                    var selected = list.SelectedItem as ReadingBookmark;
                    note.Header = selected?.Label ?? "当前页备注";
                    note.Text = selected?.Note ?? existing?.Note ?? string.Empty;
                };
                var save = new Button { Content = "保存当前页书签" };
                var edit = new Button { Content = "更新选中备注" };
                var delete = new Button { Content = "删除选中书签" };
                var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                buttons.Children.Add(save); buttons.Children.Add(edit); buttons.Children.Add(delete);
                var panel = new StackPanel { Spacing = 12, MinWidth = 360 };
                panel.Children.Add(status); panel.Children.Add(list); panel.Children.Add(note); panel.Children.Add(buttons);
                var dialog = NavigationDialog("书签与备注", panel, "跳转选中书签");
                ReadingBookmark? toDelete = null;
                var refresh = false;
                save.Click += (_, _) =>
                {
                    try { ViewModel.SaveBookmark(note.Text); refresh = true; dialog.Hide(); }
                    catch (Exception ex) { status.Text = ex.Message; }
                };
                edit.Click += (_, _) =>
                {
                    if (list.SelectedItem is not ReadingBookmark selected) { status.Text = "请先选择书签。"; return; }
                    try { ViewModel.UpdateBookmark(selected, note.Text); refresh = true; dialog.Hide(); }
                    catch (Exception ex) { status.Text = ex.Message; }
                };
                delete.Click += (_, _) =>
                {
                    if (list.SelectedItem is not ReadingBookmark selected) { status.Text = "请先选择书签。"; return; }
                    toDelete = selected; dialog.Hide();
                };
                dialog.PrimaryButtonClick += (_, args) =>
                {
                    if (list.SelectedItem is null) { args.Cancel = true; status.Text = "请先选择书签。"; }
                };
                var result = await dialog.ShowAsync();
                if (toDelete is not null)
                {
                    var confirm = NavigationDialog("删除书签？", new TextBlock { Text = $"{toDelete.Label}\n只删除书签与备注，不删除漫画文件。", TextWrapping = TextWrapping.Wrap }, "删除");
                    confirm.DefaultButton = ContentDialogButton.Close;
                    if (await confirm.ShowAsync() == ContentDialogResult.Primary) ViewModel.RemoveBookmark(toDelete);
                    refresh = true;
                }
                else if (result == ContentDialogResult.Primary && list.SelectedItem is ReadingBookmark selected)
                    await ViewModel.GoToBookmarkAsync(selected);
                keepOpen = refresh;
            }
        }
        catch (Exception ex) { ViewModel.PageError = ex.Message; }
        finally { _navigationDialogOpen = false; }
    }

    private ContentDialog NavigationDialog(string title, object content, string primary) => new()
    {
        XamlRoot = XamlRoot,
        RequestedTheme = ActualTheme,
        Title = title,
        Content = content,
        PrimaryButtonText = primary,
        CloseButtonText = "关闭",
        DefaultButton = ContentDialogButton.Primary,
    };
}
