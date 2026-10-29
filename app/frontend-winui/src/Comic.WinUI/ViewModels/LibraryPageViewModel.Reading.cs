using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Comic.WinUI.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Comic.WinUI.ViewModels;

public partial class LibraryPageViewModel
{
    private bool _suppressReadingFilterSearch;

    [ObservableProperty]
    public partial int ReadingFilterIndex { get; set; }

    private ReadingStatus? ReadingFilter => ReadingFilterIndex is >= 1 and <= 3
        ? (ReadingStatus)(ReadingFilterIndex - 1)
        : null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ReadingSummary))]
    public partial int ToReadCount { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ReadingSummary))]
    public partial int ReadingCount { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ReadingSummary))]
    public partial int ReadCount { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanContinueReading))]
    public partial string ContinueReadingRoot { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ContinueReadingTitle { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSetReadingStatus))]
    public partial bool IsSettingReadingStatus { get; set; }

    public string ReadingSummary => $"待读 {ToReadCount} · 在读 {ReadingCount} · 已读 {ReadCount}";
    public string SelectedReadingStatusText => SelectedItem?.ReadingStatusText ?? "未选择漫画";
    public bool CanContinueReading => !string.IsNullOrWhiteSpace(ContinueReadingRoot);
    public bool CanSetReadingStatus => SelectedItem is not null && !IsSettingReadingStatus;

    partial void OnReadingFilterIndexChanged(int value)
    {
        if (!_suppressReadingFilterSearch) _ = SearchAsync();
    }

    [RelayCommand]
    private Task ShowReadingAsync(CancellationToken cancellationToken = default)
    {
        _suppressReadingFilterSearch = true;
        ReadingFilterIndex = 2;
        _suppressReadingFilterSearch = false;
        return SearchAsync(cancellationToken);
    }

    [RelayCommand]
    private Task MarkToReadAsync(CancellationToken cancellationToken = default) =>
        SetSelectedReadingStatusAsync(ReadingStatus.ToRead, cancellationToken);

    [RelayCommand]
    private Task MarkReadingAsync(CancellationToken cancellationToken = default) =>
        SetSelectedReadingStatusAsync(ReadingStatus.Reading, cancellationToken);

    [RelayCommand]
    private Task MarkReadAsync(CancellationToken cancellationToken = default) =>
        SetSelectedReadingStatusAsync(ReadingStatus.Read, cancellationToken);

    public async Task SetSelectedReadingStatusAsync(ReadingStatus status, CancellationToken cancellationToken = default)
    {
        if (SelectedItem is null || string.IsNullOrWhiteSpace(SelectedItem.RootDir) || IsSettingReadingStatus) return;
        var selectedRoot = SelectedItem.RootDir;
        var selectedTitle = SelectedItem.Title;
        PageError = string.Empty;
        LibraryStatus = string.Empty;
        IsSettingReadingStatus = true;
        try
        {
            var saved = await Task.Run(() => _readingProgressService.SetReadingStatus(selectedRoot, status), cancellationToken);
            if (!saved)
            {
                PageError = "阅读状态保存失败，请检查设置目录是否可写后重试。";
                return;
            }

            await LoadAsync(cancellationToken);
            SelectedItem = Items.FirstOrDefault(item => string.Equals(item.RootDir, selectedRoot, StringComparison.OrdinalIgnoreCase))
                ?? Items.FirstOrDefault();
            var statusText = status switch { ReadingStatus.Reading => "在读", ReadingStatus.Read => "已读", _ => "待读" };
            LibraryStatus = $"《{selectedTitle}》已标记为{statusText}。";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            PageError = $"设置阅读状态失败：{ex.Message}";
        }
        finally
        {
            IsSettingReadingStatus = false;
        }
    }
}
