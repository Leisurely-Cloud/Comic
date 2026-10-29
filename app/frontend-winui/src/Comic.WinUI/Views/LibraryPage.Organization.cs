using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Comic.WinUI.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Comic.WinUI.Views;

public sealed partial class LibraryPage
{
    private bool _organizationDialogOpen;

    private async void OnOrganizeSelectedClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedItem is { } item) await OrganizeAsync([item.RootDir]);
    }

    private async void OnOrganizeMarkedClick(object sender, RoutedEventArgs e)
    {
        var roots = ViewModel.Items.Where(item => item.IsMarked).Select(item => item.RootDir).ToArray();
        if (roots.Length == 0) { ViewModel.PageError = "请先勾选当前页中需要整理的漫画。"; return; }
        await OrganizeAsync(roots);
    }

    private async Task OrganizeAsync(string[] roots)
    {
        if (_organizationDialogOpen) return;
        using var archiveBlock = BlockArchiveInput();
        _organizationDialogOpen = true;
        try
        {
            var current = ViewModel.Organization.Get(roots[0]);
            var panel = new StackPanel { Spacing = 12, MinWidth = 320 };
            panel.Children.Add(new TextBlock
            {
                Text = roots.Length > 1
                ? $"将替换 {roots.Length} 部勾选漫画的书架与标签。初始值取自第一部；不会移动文件或修改官方收藏。"
                : "可以同时加入多个本地书架；不会移动文件或修改官方收藏。",
                TextWrapping = TextWrapping.Wrap
            });
            var checks = new List<CheckBox>();
            foreach (var shelf in ViewModel.Organization.GetShelves())
            {
                var check = new CheckBox { Content = shelf.Name, Tag = shelf.Id, IsChecked = current.ShelfIds.Contains(shelf.Id) };
                checks.Add(check); panel.Children.Add(check);
            }
            if (checks.Count == 0) panel.Children.Add(new TextBlock { Text = "暂无书架，请在“管理书架”中创建。标签仍可单独保存。", TextWrapping = TextWrapping.Wrap });
            var tags = new TextBox { Header = "标签（逗号分隔）", Text = string.Join("，", current.Tags), PlaceholderText = "例如：长篇，待看", TextWrapping = TextWrapping.Wrap };
            panel.Children.Add(tags);
            var error = new TextBlock { TextWrapping = TextWrapping.Wrap };
            panel.Children.Add(error);
            var dialog = OrganizationDialog(roots.Length > 1 ? "批量整理漫画" : "书架与标签", new ScrollViewer { Content = panel, MaxHeight = 420 }, "保存");
            dialog.PrimaryButtonClick += (_, args) =>
            {
                try
                {
                    ViewModel.OrganizeBooks(roots, checks.Where(check => check.IsChecked == true).Select(check => (string)check.Tag),
                        tags.Text.Split([',', '，'], StringSplitOptions.RemoveEmptyEntries));
                }
                catch (Exception ex) { args.Cancel = true; error.Text = ex.Message; }
            };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary) await ViewModel.SearchAsync();
        }
        catch (Exception ex) { ViewModel.PageError = ex.Message; }
        finally { _organizationDialogOpen = false; }
    }

    private async void OnManageShelvesClick(object sender, RoutedEventArgs e)
    {
        if (_organizationDialogOpen) return;
        using var archiveBlock = BlockArchiveInput();
        _organizationDialogOpen = true;
        try
        {
            var refresh = true;
            while (refresh)
            {
                refresh = false;
                var shelves = ViewModel.Organization.GetShelves();
                var list = new ListView { ItemsSource = shelves, DisplayMemberPath = nameof(LocalBookshelf.Name), Height = Math.Min(200, Math.Max(44, shelves.Count * 44)) };
                var name = new TextBox { Header = "书架名称（最多 40 字）", MaxLength = 40 };
                list.SelectionChanged += (_, _) => name.Text = (list.SelectedItem as LocalBookshelf)?.Name ?? string.Empty;
                var error = new TextBlock { Text = shelves.Count == 0 ? "暂无本地书架，输入名称后点击新增。" : "新增时输入名称；重命名或删除时先选择书架。", TextWrapping = TextWrapping.Wrap };
                var add = new Button { Content = "新增" };
                var rename = new Button { Content = "重命名选中" };
                var delete = new Button { Content = "删除选中" };
                var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                buttons.Children.Add(add); buttons.Children.Add(rename); buttons.Children.Add(delete);
                var panel = new StackPanel { Spacing = 12, MinWidth = 320 };
                panel.Children.Add(error); panel.Children.Add(list); panel.Children.Add(name); panel.Children.Add(buttons);
                var dialog = OrganizationDialog("管理本地书架", panel, "");
                LocalBookshelf? toDelete = null;
                void SaveShelf(string? id)
                {
                    try { ViewModel.Organization.SaveShelf(id, name.Text); refresh = true; dialog.Hide(); }
                    catch (Exception ex) { error.Text = ex.Message; }
                }
                add.Click += (_, _) => SaveShelf(null);
                rename.Click += (_, _) =>
                {
                    if (list.SelectedItem is LocalBookshelf selected) SaveShelf(selected.Id);
                    else error.Text = "请先选择书架。";
                };
                delete.Click += (_, _) =>
                {
                    if (list.SelectedItem is LocalBookshelf selected) { toDelete = selected; dialog.Hide(); }
                    else error.Text = "请先选择书架。";
                };
                await dialog.ShowAsync();
                if (toDelete is not null)
                {
                    var confirm = OrganizationDialog("删除本地书架？", new TextBlock
                    {
                        Text = $"删除“{toDelete.Name}”及其分类关系；保留漫画文件、标签和收藏。",
                        TextWrapping = TextWrapping.Wrap,
                    }, "删除");
                    confirm.DefaultButton = ContentDialogButton.Close;
                    if (await confirm.ShowAsync() == ContentDialogResult.Primary) ViewModel.Organization.RemoveShelf(toDelete.Id);
                    refresh = true;
                }
                ViewModel.RefreshShelves();
            }
            await ViewModel.SearchAsync();
        }
        catch (Exception ex) { ViewModel.PageError = ex.Message; }
        finally { _organizationDialogOpen = false; }
    }

    private ContentDialog OrganizationDialog(string title, object content, string primary) => new()
    {
        XamlRoot = XamlRoot,
        RequestedTheme = ActualTheme,
        Title = title,
        Content = content,
        PrimaryButtonText = primary,
        CloseButtonText = "关闭",
        DefaultButton = primary.Length == 0 ? ContentDialogButton.Close : ContentDialogButton.Primary,
    };
}
