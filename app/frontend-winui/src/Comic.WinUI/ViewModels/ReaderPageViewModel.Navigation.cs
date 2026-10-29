using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Comic.WinUI.Services;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Comic.WinUI.ViewModels;

public partial class ReaderPageViewModel
{
    private readonly ReadingBookmarkService _bookmarks;
    private readonly SemaphoreSlim _thumbnailGate = new(3, 3);
    internal Task ChapterLoading { get; private set; } = Task.CompletedTask;

    public IReadOnlyList<ReadingBookmark> GetBookmarks() => _bookmarks.Get(_rootDir);

    public ReadingBookmark SaveBookmark(string note)
    {
        if (IsLoading || SelectedChapter is null || TotalImages == 0 || HasFailedPagedImage)
            throw new InvalidOperationException("请等待当前页加载完成后再添加书签。");
        return _bookmarks.Save(_rootDir, SelectedChapter.DirName, SelectedChapter.Title, CurrentImageIndex, note);
    }

    public void UpdateBookmark(ReadingBookmark bookmark, string note)
    {
        if (!PersonalLibraryIdentity.Equals(bookmark.Manga, _rootDir)) throw new ArgumentException("书签不属于当前漫画。");
        _bookmarks.Save(_rootDir, bookmark.Chapter, bookmark.ChapterTitle, bookmark.PageIndex, note);
    }

    public void RemoveBookmark(ReadingBookmark bookmark) => _bookmarks.Remove(_rootDir, bookmark.Id);

    public async Task GoToBookmarkAsync(ReadingBookmark bookmark)
    {
        if (bookmark.PageIndex < 0 || bookmark.PageIndex == int.MaxValue) throw new ArgumentException("书签页码无效。");
        if (!PersonalLibraryIdentity.Equals(bookmark.Manga, _rootDir)) throw new ArgumentException("书签不属于当前漫画。");
        var chapter = Chapters.FirstOrDefault(item => item.DirName == bookmark.Chapter)
            ?? throw new InvalidOperationException("此书签的章节已不存在，漫画文件未被修改。");
        if (SelectedChapter != chapter)
        {
            // Validate against a fresh list before changing the visible chapter or durable progress.
            // Reuse that exact list for the actual load, avoiding a second request and a count race.
            var origin = SelectedChapter;
            var token = _imageCts?.Token ?? _sessionCts?.Token ?? CancellationToken.None;
            var images = await ReadChapterImagesAsync(chapter, token);
            token.ThrowIfCancellationRequested();
            if (SelectedChapter != origin) throw new InvalidOperationException("章节已切换，请重新选择书签。");
            if (bookmark.PageIndex >= images.Count) throw new ArgumentException("书签页码已超出当前章节范围，阅读位置未改变。");
            _pendingChapterImages = images;
            _pendingImageIndex = bookmark.PageIndex;
            SelectedChapter = chapter;
            await ChapterLoading;
            if (SelectedChapter != chapter) throw new InvalidOperationException("章节已切换，请重新选择书签。");
            if (bookmark.PageIndex < TotalImages) return; // Chapter load already navigated to this page; do not download twice.
        }
        await ChapterLoading;
        if (SelectedChapter != chapter) throw new InvalidOperationException("章节已切换，请重新选择书签。");
        await JumpToPageAsync(bookmark.PageIndex + 1);
    }

    public Task JumpToPageAsync(int pageNumber)
    {
        if (pageNumber < 1 || pageNumber > TotalImages) throw new ArgumentException($"页码应在 1–{TotalImages} 之间。");
        return GoToImageAsync(pageNumber - 1);
    }

    public IReadOnlyList<ReaderStripImageItemViewModel> CreateThumbnails() => Enumerable.Range(0, TotalImages)
        .Select(index => new ReaderStripImageItemViewModel(index, SelectedChapter?.DirName ?? string.Empty)).ToArray();

    public async Task LoadThumbnailAsync(ReaderStripImageItemViewModel item, CancellationToken dialogToken)
    {
        // Reject recycled items from an earlier chapter before touching its image-source arrays.
        if (item.Path != SelectedChapter?.DirName || item.Index < 0 || item.Index >= TotalImages) return;
        // Keep both cancellation links alive until the queued decode has completed, not only until bytes arrive.
        var token = item.BeginLoad(_imageCts?.Token ?? CancellationToken.None, dialogToken);
        if (token is null) return;
        var acquired = false;
        try
        {
            await _thumbnailGate.WaitAsync(token.Value);
            acquired = true;
            token.Value.ThrowIfCancellationRequested();
            var bytes = await GetImageBytesAtAsync(item.Index, token.Value);
            _dispatcherQueue.TryEnqueue(() =>
            {
                if (!item.CanComplete(token.Value))
                {
                    item.CancelLoad(token.Value);
                    return;
                }
                try
                {
                    var bitmap = new BitmapImage { DecodePixelWidth = 160 };
                    using var stream = new MemoryStream(bytes);
                    bitmap.SetSource(stream.AsRandomAccessStream());
                    item.Complete(token.Value, bitmap);
                }
                catch (Exception ex) { item.Fail(token.Value, ex.Message); }
            });
        }
        catch (OperationCanceledException) { item.CancelLoad(token.Value); }
        catch (Exception ex) { _dispatcherQueue.TryEnqueue(() => item.Fail(token.Value, ex.Message)); }
        finally { if (acquired) _thumbnailGate.Release(); }
    }
}
