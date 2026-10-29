using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Comic.WinUI.Models;
using Comic.WinUI.Services.Native;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Comic.WinUI.ViewModels;

public partial class LibraryPageViewModel
{
    private CancellationTokenSource? _archiveDropCts;
    private int _archiveDropRequest;

    [ObservableProperty]
    public partial bool IsResolvingArchiveDrop { get; set; }

    public async Task<string?> ResolveDroppedArchiveAsync(
        Func<CancellationToken, Task<IReadOnlyList<ArchiveDropItem>>> getItems,
        CancellationToken cancellationToken = default)
    {
        if (IsResolvingArchiveDrop || cancellationToken.IsCancellationRequested) return null;
        var request = Interlocked.Increment(ref _archiveDropRequest);
        using var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _archiveDropCts = source;
        IsResolvingArchiveDrop = true;
        PageError = string.Empty;
        try
        {
            var items = await getItems(source.Token);
            if (source.IsCancellationRequested || request != _archiveDropRequest) return null;
            if (items is null || items.Count == 0)
            {
                PageError = "没有可读取的文件，请从资源管理器拖入一个 CBZ / ZIP 文件。";
                return null;
            }
            if (items.Count != 1)
            {
                PageError = "每次只能拖入一个 CBZ / ZIP 文件，请重新选择。";
                return null;
            }
            if (items[0] is not { IsFile: true } item)
            {
                PageError = "请拖入 .cbz 或 .zip 文件，暂不支持目录。";
                return null;
            }
            if (string.IsNullOrWhiteSpace(item.FilePath) || !Path.IsPathFullyQualified(item.FilePath))
            {
                PageError = "无法读取拖入文件的路径，请先将文件保存到本地后重试。";
                return null;
            }
            var fullPath = Path.GetFullPath(item.FilePath);
            if (!ArchiveReaderService.IsSupportedArchivePath(fullPath))
            {
                PageError = "仅支持 .cbz 和 .zip 压缩包，暂不支持其他文件。";
                return null;
            }
            return fullPath;
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex)
        {
            if (!source.IsCancellationRequested && request == _archiveDropRequest)
                PageError = $"读取拖入文件失败：{ex.Message}";
            return null;
        }
        finally
        {
            if (request == _archiveDropRequest)
            {
                _archiveDropCts = null;
                IsResolvingArchiveDrop = false;
            }
        }
    }

    public void CancelArchiveDrop()
    {
        Interlocked.Increment(ref _archiveDropRequest);
        var source = _archiveDropCts;
        _archiveDropCts = null;
        source?.Cancel();
        IsResolvingArchiveDrop = false;
    }

    // History is metadata only. Enumerating it must never probe unavailable disks or grant reader access.
    public IReadOnlyList<RecentArchiveItem> GetRecentArchives() => _recentArchives.GetAll()
        .Select(entry =>
        {
            var progress = _readingProgressService.Get(entry.FilePath);
            var chapter = progress?.ChapterDirectoryName;
            var chapterTitle = chapter == "/" ? "根目录" : Path.GetFileName(chapter?.TrimEnd('/', '\\'));
            var text = progress is null ? "尚无阅读进度" : $"{chapterTitle} · 第 {(long)progress.PageIndex + 1} 页";
            return new RecentArchiveItem(entry.FilePath, entry.Title, text, entry.LastOpenedAtUtc);
        }).ToArray();

    public bool RemoveRecentArchive(string filePath)
    {
        PageError = string.Empty;
        if (_recentArchives.Remove(filePath)) return true;
        PageError = "最近记录移除失败，请检查设置目录是否可写后重试。";
        return false;
    }

    public bool ClearRecentArchives()
    {
        PageError = string.Empty;
        if (_recentArchives.Clear()) return true;
        PageError = "最近记录清空失败，请检查设置目录是否可写后重试。";
        return false;
    }
}

public sealed record RecentArchiveItem(string FilePath, string Title, string ProgressText, DateTimeOffset LastOpenedAtUtc);
