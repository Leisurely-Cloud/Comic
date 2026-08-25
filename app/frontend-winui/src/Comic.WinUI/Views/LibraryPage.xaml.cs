using System;
using System.Collections.Generic;
using Comic.WinUI.Models;
using Comic.WinUI.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Windows.Storage.Pickers;
using WinRT.Interop;

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

    private async void OnImportJmClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var window = ((App)Application.Current).MainWindow;
            if (window is null)
            {
                ViewModel.PageError = "无法获取当前窗口。";
                return;
            }

            var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.Downloads };
            picker.FileTypeFilter.Add("*");
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(window));
            var folder = await picker.PickSingleFolderAsync();
            if (folder is null) return;

            var preview = await ViewModel.ScanJmImportAsync(folder.Path);
            if (preview is null) return;

            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = preview.HasImportableContent ? "导入 JM 下载目录？" : "没有需要导入的章节",
                Content = BuildImportPreviewText(preview),
                PrimaryButtonText = preview.HasImportableContent ? "开始导入" : string.Empty,
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close,
            };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                await ViewModel.ImportJmAsync(folder.Path);
            }
        }
        catch (Exception ex)
        {
            ViewModel.PageError = $"选择导入目录失败：{ex.Message}";
        }
    }

    private static string BuildImportPreviewText(JmLibraryImportPreview preview)
    {
        var lines = new List<string>
        {
            $"检测到 {preview.DetectedMangaCount} 部 JM 漫画：新增 {preview.NewMangaCount} 部，补充 {preview.ExistingMangaCount} 部。",
            $"将复制 {preview.ImportableChapterCount} 章、{preview.ImportableImageCount} 张图片（约 {FormatBytes(preview.ImportableBytes)}）。",
        };
        if (preview.ExistingChapterCount > 0) lines.Add($"已存在的 {preview.ExistingChapterCount} 章会自动跳过。");
        if (preview.ConflictChapterCount > 0) lines.Add($"有 {preview.ConflictChapterCount} 章图片数量不同，将保留本地版本且不覆盖。");
        if (preview.SkippedDirectoryCount > 0) lines.Add($"另有 {preview.SkippedDirectoryCount} 个目录无法识别为 JM 漫画，已忽略。");
        lines.Add("导入采用复制方式，源目录不会被移动或删除；失败时会回滚本轮新增章节。");
        return string.Join(Environment.NewLine + Environment.NewLine, lines);
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double display = Math.Max(0, bytes);
        var unit = 0;
        while (display >= 1024 && unit < units.Length - 1)
        {
            display /= 1024;
            unit++;
        }
        return $"{display:0.##} {units[unit]}";
    }
}
