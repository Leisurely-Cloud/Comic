using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Comic.WinUI.Services;
using Comic.WinUI.Services.Native;
using Comic.WinUI.Tests.Services;
using Comic.WinUI.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Comic.WinUI.Tests.ViewModels;

/// <summary>
/// 使用延迟调度器和可控解码器验证过期回调、失败恢复与进度提交。
/// 成功替身返回 null，仅测试状态流转；真实 WinRT 位图渲染仍需界面验证。
/// </summary>
[TestClass]
public sealed class ReaderPageViewModelTests
{
    private string _container = null!;
    private string _storageRoot = null!;
    private string _mangaRoot = null!;
    private FakeHttpMessageHandler _handler = null!;
    private DownloadSchedulerService _scheduler = null!;
    private CbzExportService _exporter = null!;
    private ReaderPageViewModel? _viewModel;

    private const string FirstChapterDir = "001_第一话";
    private const string SecondChapterDir = "002_第二话";

    [TestInitialize]
    public void Initialize()
    {
        _container = Path.Combine(Path.GetTempPath(), "Comic.WinUI.Tests", Guid.NewGuid().ToString("N"));
        _storageRoot = Path.Combine(_container, "library");
        _mangaRoot = Path.Combine(_storageRoot, "测试漫画");

        // 第一章图多一些,才能让恢复的页码 5 落在有效范围内。
        CreateChapter(FirstChapterDir, imageCount: 8);
        CreateChapter(SecondChapterDir, imageCount: 3);
        _handler = FakeHttpMessageHandler.AlwaysFails();
    }

    [TestCleanup]
    public async Task Cleanup()
    {
        _viewModel?.Deactivate();
        if (_viewModel is not null)
            await Task.WhenAll(_viewModel.ChapterLoading, _viewModel.PagedImageApplication, _viewModel.ImagePreloading)
                .WaitAsync(TimeSpan.FromSeconds(5));
        _scheduler?.Dispose();
        _exporter?.Dispose();
        _handler?.Dispose();
        if (Directory.Exists(_container)) Directory.Delete(_container, true);
    }

    [TestMethod]
    public async Task StaleImageCallback_DoesNotWriteOldPageIndexIntoNewChapter()
    {
        var dispatcher = new DeferredDispatcher();
        var progressService = new ReadingProgressService(Path.Combine(_container, "progress"));
        // 预置进度:第一话第 6 页。这样第一章会从 index 5 开始,过期回调带的就是 5,
        // 而不是 0 —— 否则写进新章节也看不出区别。
        progressService.Save(_mangaRoot, FirstChapterDir, 5);

        var viewModel = CreateViewModel(dispatcher, progressService);

        await viewModel.LoadAsync(_mangaRoot);
        Assert.AreEqual(FirstChapterDir, viewModel.SelectedChapter?.DirName);

        // 第一章的图片回调已入队但还没执行。
        await dispatcher.WaitForPendingAsync(1, TimeSpan.FromSeconds(15));

        // 切到第二章:这会取消 _imageCts,上面那个已入队的回调就成了过期回调。
        viewModel.SelectedChapter = viewModel.Chapters
            .Single(chapter => chapter.DirName == SecondChapterDir);

        // 等第二章也走到入队,确保此时 TotalImages 已是第二章的值。
        // 否则 SaveReadingProgress 会因为 TotalImages<=0 提前返回,
        // 那样即使没有 token 检查测试也会「通过」,变成假绿。
        await dispatcher.WaitForPendingAsync(2, TimeSpan.FromSeconds(15));

        dispatcher.FlushFirst(); // 执行第一章那个过期回调

        var saved = progressService.Get(_mangaRoot);
        Assert.IsNotNull(saved);
        Assert.AreEqual(
            FirstChapterDir,
            saved.ChapterDirectoryName,
            "过期回调不应把上一章的页码写进新章节的阅读进度。");
        Assert.AreEqual(5, saved.PageIndex);
    }

    [TestMethod]
    public async Task OnlineStripMode_BuildsLazyItemsWithoutDownloadingAllImages()
    {
        _handler.Dispose();
        var imageRequestCount = 0;
        _handler = new FakeHttpMessageHandler(request =>
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            if (path.EndsWith("/album", StringComparison.OrdinalIgnoreCase))
            {
                return EncryptedApiResponse(
                    request,
                    """{"id":"456","name":"在线条漫测试","series":[{"id":"123","name":"第一话"}]}""");
            }

            if (path.EndsWith("/chapter", StringComparison.OrdinalIgnoreCase))
            {
                return EncryptedApiResponse(
                    request,
                    """{"id":"123","name":"第一话","images":["001.jpg","002.jpg","003.jpg"]}""");
            }

            if (path.EndsWith("/chapter_view_template", StringComparison.OrdinalIgnoreCase))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("<script>var scramble_id = 220980;</script>")
                };
            }

            if (path.Contains("/media/photos/", StringComparison.OrdinalIgnoreCase))
            {
                Interlocked.Increment(ref imageRequestCount);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent([1, 2, 3])
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        var dispatcher = new DeferredDispatcher();
        var progressService = new ReadingProgressService(Path.Combine(_container, "progress"));
        var viewModel = CreateViewModel(dispatcher, progressService, useStripMode: true);

        await viewModel.LoadOnlineAsync("https://18comic.vip/album/456");
        await WaitUntilAsync(() => viewModel.StripImages.Count == 3, TimeSpan.FromSeconds(5));

        Assert.IsTrue(viewModel.CanToggleReaderMode);
        Assert.IsTrue(viewModel.IsStripMode);
        Assert.AreEqual("在线条漫测试", viewModel.MangaTitle);
        Assert.AreEqual(3, viewModel.TotalImages);
        Assert.AreEqual(3, viewModel.SelectedChapter?.ImageCount);
        Assert.AreEqual(3, viewModel.StripImages.Count);
        Assert.IsTrue(viewModel.StripImages.All(item => item.Image is null));
        Assert.AreEqual(0, Volatile.Read(ref imageRequestCount));

        // 模拟第一个虚拟化元素进入可视区，只允许请求这一页，不能顺带拉完整章。
        await viewModel.LoadStripImageAsync(viewModel.StripImages[0]);
        Assert.AreEqual(1, Volatile.Read(ref imageRequestCount));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task PagedDecodeFailure_DoesNotEscapeCallbackOrSaveProgressAndCanRetry(bool asynchronous)
    {
        var dispatcher = new DeferredDispatcher();
        var progress = new ReadingProgressService(Path.Combine(_container, "progress"));
        var failure = new TaskCompletionSource<BitmapImage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var viewModel = CreateViewModel(dispatcher, progress, decoder: (_, _) =>
        {
            if (++calls == 2)
            {
                if (asynchronous) return failure.Task;
                throw new IOException("损坏图片");
            }
            return Task.FromResult<BitmapImage>(null!);
        });
        await viewModel.LoadAsync(_mangaRoot);
        await dispatcher.WaitForPendingAsync(1, TimeSpan.FromSeconds(5));
        dispatcher.FlushFirst();
        Assert.AreEqual(0, progress.Get(_mangaRoot)?.PageIndex);

        await viewModel.GoToImageAsync(5);
        dispatcher.FlushFirst();
        if (asynchronous) failure.SetException(new IOException("损坏图片"));
        await WaitUntilAsync(() => viewModel.HasFailedPagedImage && !viewModel.IsLoading, TimeSpan.FromSeconds(5));
        StringAssert.Contains(viewModel.PageError, "损坏图片");
        viewModel.SaveReadingProgress(); // 离开页面时也不能把失败页标成已读。
        Assert.AreEqual(0, progress.Get(_mangaRoot)?.PageIndex, "解码失败不得提交失败页的阅读进度。");
        Assert.AreEqual(5, viewModel.CurrentImageIndex, "保留失败页的位置，允许继续翻页。");

        await viewModel.RetryPagedImageAsync();
        dispatcher.FlushFirst();
        Assert.IsFalse(viewModel.HasFailedPagedImage);
        Assert.AreEqual(string.Empty, viewModel.PageError);
        Assert.AreEqual(5, progress.Get(_mangaRoot)?.PageIndex);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task PagedDecode_StaleCompletionCannotOverwriteNewPage(bool fails)
    {
        var dispatcher = new DeferredDispatcher();
        var progress = new ReadingProgressService(Path.Combine(_container, "progress"));
        var pending = new TaskCompletionSource<BitmapImage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var viewModel = CreateViewModel(dispatcher, progress, decoder: (_, _) =>
            ++calls == 1 ? pending.Task : Task.FromResult<BitmapImage>(null!));
        await viewModel.LoadAsync(_mangaRoot);
        await dispatcher.WaitForPendingAsync(1, TimeSpan.FromSeconds(5));
        dispatcher.FlushFirst();
        var oldApplication = viewModel.PagedImageApplication;
        await viewModel.GoToImageAsync(2);
        dispatcher.FlushFirst();
        if (fails) pending.SetException(new IOException("旧请求失败"));
        else pending.SetResult(null!);
        // 等待旧解码续体真正完成，避免只断言尚未执行回调时的状态。
        await oldApplication.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(2, viewModel.CurrentImageIndex);
        Assert.AreEqual(2, progress.Get(_mangaRoot)?.PageIndex);
        Assert.AreEqual(string.Empty, viewModel.PageError);
        Assert.IsFalse(viewModel.HasFailedPagedImage);
        Assert.IsFalse(viewModel.IsLoading);
    }

    [TestMethod]
    public async Task DoublePage_SecondaryDecodeFailureDoesNotCommitPartialSpread()
    {
        var dispatcher = new DeferredDispatcher();
        var progress = new ReadingProgressService(Path.Combine(_container, "progress"));
        var calls = 0;
        var failSecondary = false;
        var viewModel = CreateViewModel(dispatcher, progress, decoder: (_, _) =>
        {
            if (failSecondary && ++calls == 2) throw new IOException("第二张图片损坏");
            return Task.FromResult<BitmapImage>(null!);
        });
        await viewModel.LoadAsync(_mangaRoot);
        await dispatcher.WaitForPendingAsync(1, TimeSpan.FromSeconds(5));
        dispatcher.FlushFirst();
        viewModel.IsDoublePage = true;
        await dispatcher.WaitForPendingAsync(1, TimeSpan.FromSeconds(5));
        dispatcher.FlushFirst();
        failSecondary = true;
        await viewModel.GoToImageAsync(2);
        dispatcher.FlushFirst();
        Assert.IsTrue(viewModel.HasFailedPagedImage);
        Assert.IsNull(viewModel.CurrentImage);
        Assert.IsNull(viewModel.SecondaryImage);
        Assert.AreEqual(0, progress.Get(_mangaRoot)?.PageIndex);
        StringAssert.Contains(viewModel.PageError, "第二张图片损坏");
    }

    [TestMethod]
    public async Task SwitchingToStrip_IgnoresPendingPagedDecodeFailure()
    {
        var dispatcher = new DeferredDispatcher();
        var progress = new ReadingProgressService(Path.Combine(_container, "progress"));
        var pending = new TaskCompletionSource<BitmapImage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var viewModel = CreateViewModel(dispatcher, progress, decoder: (_, _) => pending.Task);
        await viewModel.LoadAsync(_mangaRoot);
        await dispatcher.WaitForPendingAsync(1, TimeSpan.FromSeconds(5));
        dispatcher.FlushFirst();
        var oldApplication = viewModel.PagedImageApplication;
        viewModel.IsStripMode = true;
        pending.SetException(new IOException("旧分页图片损坏"));
        await oldApplication.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsTrue(viewModel.IsStripMode);
        Assert.IsFalse(viewModel.HasFailedPagedImage);
        Assert.AreEqual(string.Empty, viewModel.PageError);
    }

    [TestMethod]
    public async Task StripPageJump_UpdatesProgressAndRequestsScrollWithoutDecoding()
    {
        var dispatcher = new DeferredDispatcher();
        var progress = new ReadingProgressService(Path.Combine(_container, "progress"));
        var viewModel = CreateViewModel(dispatcher, progress, useStripMode: true);
        await viewModel.LoadAsync(_mangaRoot);
        await viewModel.ChapterLoading;
        var target = -1;
        viewModel.StripPositionRestoreRequested += index => target = index;
        await viewModel.JumpToPageAsync(6);
        Assert.AreEqual(5, target);
        Assert.AreEqual(5, viewModel.CurrentImageIndex);
        Assert.AreEqual(5, progress.Get(_mangaRoot)!.PageIndex);
        Assert.IsNull(viewModel.CurrentImage);
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => viewModel.JumpToPageAsync(0));
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => viewModel.JumpToPageAsync(9));
    }

    [TestMethod]
    public async Task Bookmark_CrossChapterJumpAndNoteEditingPreserveTarget()
    {
        var viewModel = CreateViewModel(new DeferredDispatcher(), new ReadingProgressService(Path.Combine(_container, "progress")), useStripMode: true);
        await viewModel.LoadAsync(_mangaRoot);
        await viewModel.ChapterLoading;
        await viewModel.JumpToPageAsync(6);
        var bookmark = viewModel.SaveBookmark("标注");
        viewModel.UpdateBookmark(bookmark, "修改");
        viewModel.SelectedChapter = viewModel.Chapters.Single(chapter => chapter.DirName == SecondChapterDir);
        await viewModel.ChapterLoading;
        await viewModel.GoToBookmarkAsync(bookmark);
        Assert.AreEqual(FirstChapterDir, viewModel.SelectedChapter!.DirName);
        Assert.AreEqual(5, viewModel.CurrentImageIndex);
        Assert.AreEqual("修改", viewModel.GetBookmarks().Single().Note);
        viewModel.RemoveBookmark(bookmark);
        Assert.AreEqual(0, viewModel.GetBookmarks().Count);
    }

    [TestMethod]
    public async Task Bookmark_MissingChapterOrWrongMangaDoesNotChangeSelection()
    {
        var viewModel = CreateViewModel(new DeferredDispatcher(), new ReadingProgressService(Path.Combine(_container, "progress")), useStripMode: true);
        await viewModel.LoadAsync(_mangaRoot);
        await viewModel.ChapterLoading;
        var bookmark = viewModel.SaveBookmark("");
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => viewModel.GoToBookmarkAsync(bookmark with { Chapter = "missing" }));
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => viewModel.GoToBookmarkAsync(bookmark with { Manga = _mangaRoot + "other" }));
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => viewModel.GoToBookmarkAsync(bookmark with { Chapter = SecondChapterDir, PageIndex = -1 }));
        Assert.AreEqual(FirstChapterDir, viewModel.SelectedChapter!.DirName);
        Assert.AreEqual(0, viewModel.CurrentImageIndex);
    }

    [TestMethod]
    public async Task ThumbnailCreation_UsesPageMetadataOnlyAndRejectsOldChapterItems()
    {
        var viewModel = CreateViewModel(new DeferredDispatcher(), new ReadingProgressService(Path.Combine(_container, "progress")), useStripMode: true);
        await viewModel.LoadAsync(_mangaRoot);
        await viewModel.ChapterLoading;
        var thumbnails = viewModel.CreateThumbnails();
        Assert.AreEqual(8, thumbnails.Count);
        Assert.IsTrue(thumbnails.All(item => item.Image is null && !item.IsLoading));
        viewModel.SelectedChapter = viewModel.Chapters.Single(chapter => chapter.DirName == SecondChapterDir);
        await viewModel.ChapterLoading;
        await viewModel.LoadThumbnailAsync(thumbnails[0], CancellationToken.None);
        Assert.IsFalse(thumbnails[0].IsLoading);
        Assert.IsNull(thumbnails[0].Image);
    }

    [TestMethod]
    public void RecycledThumbnail_OldCancellationCannotUnloadNewRequest()
    {
        var item = new ReaderStripImageItemViewModel(0, "chapter");
        var old = item.BeginLoad(CancellationToken.None)!.Value;
        item.Unload();
        var current = item.BeginLoad(CancellationToken.None)!.Value;
        item.CancelLoad(old);
        Assert.IsTrue(item.IsLoading);
        Assert.IsTrue(item.CanComplete(current));
        item.Unload();
    }

    [TestMethod]
    public async Task OnlineThumbnails_BoundConcurrencyAndCancelQueuedReadsOnClose()
    {
        _handler.Dispose();
        var requests = 0;
        _handler = new FakeHttpMessageHandler(async (request, cancellationToken) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/album")) return EncryptedApiResponse(request,
                """{"id":"456","name":"缩略图测试","series":[{"id":"123","name":"第一话"}]}""");
            if (path.EndsWith("/chapter")) return EncryptedApiResponse(request,
                """{"id":"123","name":"第一话","images":["001.jpg","002.jpg","003.jpg","004.jpg","005.jpg","006.jpg","007.jpg","008.jpg"]}""");
            if (path.EndsWith("/chapter_view_template")) return new(HttpStatusCode.OK)
            { Content = new StringContent("<script>var scramble_id = 220980;</script>") };
            if (path.Contains("/media/photos/"))
            {
                Interlocked.Increment(ref requests);
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            return new(HttpStatusCode.NotFound);
        });
        var viewModel = CreateViewModel(new DeferredDispatcher(), new ReadingProgressService(Path.Combine(_container, "progress")), useStripMode: true);
        await viewModel.LoadOnlineAsync("https://18comic.vip/album/456");
        await viewModel.ChapterLoading;
        var thumbnails = viewModel.CreateThumbnails();
        Assert.AreEqual(8, thumbnails.Count);
        Assert.AreEqual(0, requests);
        using var dialog = new CancellationTokenSource();
        var loading = thumbnails.Select(item => viewModel.LoadThumbnailAsync(item, dialog.Token)).ToArray();
        await WaitUntilAsync(() => Volatile.Read(ref requests) >= 3, TimeSpan.FromSeconds(5));
        Assert.AreEqual(3, Volatile.Read(ref requests));
        dialog.Cancel();
        await Task.WhenAll(loading).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(3, Volatile.Read(ref requests));
        Assert.IsTrue(thumbnails.All(item => !item.IsLoading && item.Image is null));
    }

    [TestMethod]
    public async Task LeavingReader_CancelsPendingOnlineMetadataWithoutRetrying()
    {
        _handler.Dispose();
        _handler = FakeHttpMessageHandler.BlocksUntilCancelled();
        var viewModel = CreateViewModel(new DeferredDispatcher(), new ReadingProgressService(Path.Combine(_container, "progress")));
        var loading = viewModel.LoadOnlineAsync("https://18comic.vip/album/456");
        await _handler.WaitForRequestAsync(TimeSpan.FromSeconds(5));
        viewModel.Deactivate();
        await loading.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(1, _handler.RequestedUris.Count);
        Assert.AreEqual(0, viewModel.Chapters.Count);
        Assert.IsFalse(viewModel.IsLoading);
        Assert.AreEqual(string.Empty, viewModel.PageError);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task LeavingReader_RejectsPendingDecodeAndCanLoadAgain(bool fails)
    {
        var dispatcher = new DeferredDispatcher();
        var progress = new ReadingProgressService(Path.Combine(_container, "progress"));
        var pending = new TaskCompletionSource<BitmapImage>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken decodeToken = default;
        var calls = 0;
        var viewModel = CreateViewModel(dispatcher, progress, decoder: (_, token) =>
        {
            decodeToken = token;
            return ++calls == 1 ? pending.Task : Task.FromResult<BitmapImage>(null!);
        });
        await viewModel.LoadAsync(_mangaRoot);
        await dispatcher.WaitForPendingAsync(1, TimeSpan.FromSeconds(5));
        dispatcher.FlushFirst();
        var oldApplication = viewModel.PagedImageApplication;
        viewModel.Deactivate();
        Assert.IsTrue(decodeToken.IsCancellationRequested);
        if (fails) pending.SetException(new IOException("旧解码失败"));
        else pending.SetResult(null!);
        await oldApplication.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsNull(progress.Get(_mangaRoot));
        Assert.AreEqual(0, viewModel.TotalImages);
        Assert.IsNull(viewModel.SelectedChapter);
        Assert.AreEqual(string.Empty, viewModel.PageError);

        await viewModel.LoadAsync(_mangaRoot);
        await dispatcher.WaitForPendingAsync(1, TimeSpan.FromSeconds(5));
        dispatcher.FlushFirst();
        Assert.AreEqual(8, viewModel.TotalImages);
        Assert.AreEqual(0, progress.Get(_mangaRoot)!.PageIndex);
    }

    [TestMethod]
    public async Task RapidModeSwitches_OnlyFinalSinglePageCallbackDecodesAndCommits()
    {
        var dispatcher = new DeferredDispatcher();
        var progress = new ReadingProgressService(Path.Combine(_container, "progress"));
        var calls = 0;
        var viewModel = CreateViewModel(dispatcher, progress, decoder: (_, _) =>
        {
            calls++;
            return Task.FromResult<BitmapImage>(null!);
        });
        await viewModel.LoadAsync(_mangaRoot);
        await dispatcher.WaitForPendingAsync(1, TimeSpan.FromSeconds(5));
        viewModel.ReaderModeIndex = 1;
        await dispatcher.WaitForPendingAsync(2, TimeSpan.FromSeconds(5));
        viewModel.ReaderModeIndex = 2;
        await viewModel.JumpToPageAsync(6);
        viewModel.ReaderModeIndex = 0;
        await dispatcher.WaitForPendingAsync(3, TimeSpan.FromSeconds(5));
        while (dispatcher.PendingCount > 0) dispatcher.FlushFirst();
        Assert.AreEqual(1, calls, "过期单页、双页回调不能解码或提交进度。");
        Assert.AreEqual(0, viewModel.ReaderModeIndex);
        Assert.AreEqual(5, viewModel.CurrentImageIndex);
        Assert.AreEqual(5, progress.Get(_mangaRoot)!.PageIndex);
        Assert.IsNull(viewModel.SecondaryImage);
        Assert.AreEqual(0, viewModel.StripImages.Count);
        Assert.IsFalse(viewModel.IsLoading);
    }

    [TestMethod]
    public async Task LeavingReader_CancelsActiveStripAndThumbnailReads()
    {
        var requests = 0;
        ConfigureOnlineHandler(8, async token =>
        {
            Interlocked.Increment(ref requests);
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new(HttpStatusCode.OK);
        });
        var viewModel = CreateViewModel(new DeferredDispatcher(), new ReadingProgressService(Path.Combine(_container, "progress")), useStripMode: true);
        await viewModel.LoadOnlineAsync("https://18comic.vip/album/456");
        await viewModel.ChapterLoading;
        var strip = viewModel.StripImages[0];
        var thumbnails = viewModel.CreateThumbnails();
        var reads = thumbnails.Select(item => viewModel.LoadThumbnailAsync(item, CancellationToken.None))
            .Append(viewModel.LoadStripImageAsync(strip)).ToArray();
        await WaitUntilAsync(() => Volatile.Read(ref requests) == 4, TimeSpan.FromSeconds(5));
        viewModel.Deactivate();
        await Task.WhenAll(reads).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(4, requests);
        Assert.IsFalse(strip.IsLoading);
        Assert.IsTrue(thumbnails.All(item => !item.IsLoading && item.Image is null));
    }

    [TestMethod]
    public async Task LeavingReader_CancelsNextPagePrefetchAndDoesNotSaveAnotherPage()
    {
        var requests = 0;
        ConfigureOnlineHandler(3, async token =>
        {
            if (Interlocked.Increment(ref requests) > 1)
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) };
        });
        var dispatcher = new DeferredDispatcher();
        var progress = new ReadingProgressService(Path.Combine(_container, "progress"));
        var viewModel = CreateViewModel(dispatcher, progress, decoder: (_, _) => Task.FromResult<BitmapImage>(null!));
        await viewModel.LoadOnlineAsync("https://18comic.vip/album/456");
        await dispatcher.WaitForPendingAsync(1, TimeSpan.FromSeconds(5));
        dispatcher.FlushFirst();
        await WaitUntilAsync(() => Volatile.Read(ref requests) == 2, TimeSpan.FromSeconds(5));
        var prefetch = viewModel.ImagePreloading;
        viewModel.Deactivate();
        await prefetch.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(2, requests);
        Assert.AreEqual(0, progress.Get("https://18comic.vip/album/456")!.PageIndex);
        Assert.IsFalse(viewModel.IsLoading);
        Assert.AreEqual(string.Empty, viewModel.PageError);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task QueuedThumbnailDecode_RemainsLinkedToReaderAndDialogCancellation(bool closeDialog)
    {
        ConfigureOnlineHandler(3, _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new ByteArrayContent([1, 2, 3]) }));
        var dispatcher = new DeferredDispatcher();
        var viewModel = CreateViewModel(dispatcher, new ReadingProgressService(Path.Combine(_container, "progress")), useStripMode: true);
        await viewModel.LoadOnlineAsync("https://18comic.vip/album/456");
        await viewModel.ChapterLoading;
        var thumbnail = viewModel.CreateThumbnails()[0];
        using var dialog = new CancellationTokenSource();
        await viewModel.LoadThumbnailAsync(thumbnail, dialog.Token);
        Assert.IsTrue(thumbnail.IsLoading);
        Assert.AreEqual(1, dispatcher.PendingCount);
        if (closeDialog) dialog.Cancel();
        else viewModel.Deactivate();
        dispatcher.FlushFirst(); // Must skip WinRT decoding of dummy bytes after cancellation.
        Assert.IsFalse(thumbnail.IsLoading);
        Assert.IsNull(thumbnail.Image);
        Assert.AreEqual(string.Empty, thumbnail.Error);
    }

    [TestMethod]
    public async Task LargeOnlineChapter_CreatesLazyMetadataAndJumpsToLastPageWithoutReadingImages()
    {
        var requests = 0;
        ConfigureOnlineHandler(1200, _ =>
        {
            Interlocked.Increment(ref requests);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });
        var viewModel = CreateViewModel(new DeferredDispatcher(), new ReadingProgressService(Path.Combine(_container, "progress")), useStripMode: true);
        await viewModel.LoadOnlineAsync("https://18comic.vip/album/456");
        await viewModel.ChapterLoading;
        await viewModel.JumpToPageAsync(1200);
        Assert.AreEqual(1200, viewModel.StripImages.Count);
        Assert.AreEqual(1200, viewModel.CreateThumbnails().Count);
        Assert.AreEqual(1199, viewModel.CurrentImageIndex);
        Assert.AreEqual(0, requests);
        Assert.IsTrue(viewModel.StripImages.All(item => item.Image is null && !item.IsLoading));
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public async Task InitialPagedLoad_RemainsBusyUntilDecodeCompletes(bool online, bool fails)
    {
        if (online) ConfigureOnlineHandler(3, _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new ByteArrayContent([1, 2, 3]) }));
        var dispatcher = new DeferredDispatcher();
        var progress = new ReadingProgressService(Path.Combine(_container, "progress"));
        var decoding = new TaskCompletionSource<BitmapImage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var viewModel = CreateViewModel(dispatcher, progress, decoder: (_, token) => decoding.Task.WaitAsync(token));
        var key = online ? "https://18comic.vip/album/456" : _mangaRoot;
        if (online) await viewModel.LoadOnlineAsync(key);
        else await viewModel.LoadAsync(key);
        await dispatcher.WaitForPendingAsync(1, TimeSpan.FromSeconds(5));
        Assert.IsTrue(viewModel.IsLoading, "章节列表完成不能提前结束图片加载状态。");
        viewModel.SaveReadingProgress();
        Assert.IsNull(progress.Get(key));
        Assert.ThrowsExactly<InvalidOperationException>(() => viewModel.SaveBookmark("尚未显示"));
        dispatcher.FlushFirst();
        Assert.IsTrue(viewModel.IsLoading, "异步解码期间仍应显示加载状态。");
        if (fails) decoding.SetException(new IOException("首张图片解码失败"));
        else decoding.SetResult(null!);
        await viewModel.PagedImageApplication.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsFalse(viewModel.IsLoading);
        Assert.AreEqual(fails, viewModel.HasFailedPagedImage);
        if (fails)
        {
            viewModel.SaveReadingProgress();
            Assert.IsNull(progress.Get(key));
        }
        else
        {
            Assert.AreEqual(0, progress.Get(key)!.PageIndex);
            Assert.AreEqual(0, viewModel.SaveBookmark("已显示").PageIndex);
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task InvalidCrossChapterBookmark_PreservesCurrentChapterPageAndProgress(bool strip)
    {
        var dispatcher = new DeferredDispatcher();
        var progress = new ReadingProgressService(Path.Combine(_container, "progress"));
        var viewModel = CreateViewModel(dispatcher, progress, useStripMode: strip,
            decoder: (_, _) => Task.FromResult<BitmapImage>(null!));
        await viewModel.LoadAsync(_mangaRoot);
        await viewModel.ChapterLoading;
        if (!strip) dispatcher.FlushFirst();
        await viewModel.JumpToPageAsync(6);
        if (!strip) dispatcher.FlushFirst();
        var original = viewModel.SelectedChapter;
        var saved = progress.Get(_mangaRoot);
        var bookmark = viewModel.SaveBookmark("原位置");
        // Stale catalog count must not allow a bookmark beyond the actual current file list.
        viewModel.Chapters.Single(chapter => chapter.DirName == SecondChapterDir).ImageCount = 20;
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => viewModel.GoToBookmarkAsync(
            bookmark with { Chapter = SecondChapterDir, PageIndex = 5 }));
        Assert.AreSame(original, viewModel.SelectedChapter);
        Assert.AreEqual(5, viewModel.CurrentImageIndex);
        Assert.AreEqual(8, viewModel.TotalImages);
        Assert.AreEqual(saved!.ChapterDirectoryName, progress.Get(_mangaRoot)!.ChapterDirectoryName);
        Assert.AreEqual(saved.PageIndex, progress.Get(_mangaRoot)!.PageIndex);
        Assert.AreEqual(bookmark, viewModel.GetBookmarks().Single());
    }

    [TestMethod]
    [DataRow("valid")]
    [DataRow("invalid")]
    [DataRow("cancel")]
    public async Task OnlineBookmark_PreflightValidatesBeforeNavigationAndReusesSources(string scenario)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var targetRequests = 0;
        _handler.Dispose();
        _handler = new FakeHttpMessageHandler(async (request, token) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/album")) return EncryptedApiResponse(request,
                """{"id":"456","name":"测试","series":[{"id":"123","name":"第一话"},{"id":"124","name":"第二话"}]}""");
            if (path.EndsWith("/chapter"))
            {
                var target = request.RequestUri.Query.Contains("124");
                if (target)
                {
                    Interlocked.Increment(ref targetRequests);
                    entered.TrySetResult();
                    if (scenario == "cancel") await release.Task.WaitAsync(token);
                }
                return EncryptedApiResponse(request, JsonSerializer.Serialize(new
                {
                    id = target ? "124" : "123",
                    name = target ? "第二话" : "第一话",
                    images = new[] { "001.jpg", "002.jpg", "003.jpg" }
                }));
            }
            if (path.EndsWith("/chapter_view_template")) return new(HttpStatusCode.OK)
            { Content = new StringContent("<script>var scramble_id = 220980;</script>") };
            return new(HttpStatusCode.NotFound);
        });
        var progress = new ReadingProgressService(Path.Combine(_container, "progress"));
        var viewModel = CreateViewModel(new DeferredDispatcher(), progress, useStripMode: true);
        const string key = "https://18comic.vip/album/456";
        await viewModel.LoadOnlineAsync(key);
        await viewModel.ChapterLoading;
        await viewModel.JumpToPageAsync(2);
        var original = viewModel.SelectedChapter;
        var saved = progress.Get(key)!;
        var bookmark = viewModel.SaveBookmark("测试") with
        {
            Chapter = viewModel.Chapters[1].DirName,
            PageIndex = scenario == "invalid" ? 5 : 2
        };
        var jump = viewModel.GoToBookmarkAsync(bookmark);
        if (scenario == "cancel")
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            viewModel.Deactivate();
            await Assert.ThrowsAsync<OperationCanceledException>(() => jump);
        }
        else if (scenario == "invalid") await Assert.ThrowsExactlyAsync<ArgumentException>(() => jump);
        else await jump;
        Assert.AreEqual(1, targetRequests, "预检后的图片列表应直接复用，不重复解析章节。");
        if (scenario == "valid")
        {
            Assert.AreEqual(bookmark.Chapter, viewModel.SelectedChapter!.DirName);
            Assert.AreEqual(2, viewModel.CurrentImageIndex);
            Assert.AreEqual(2, progress.Get(key)!.PageIndex);
        }
        else
        {
            if (scenario == "cancel") Assert.IsNull(viewModel.SelectedChapter, "退出后不得由旧书签请求恢复章节。");
            else Assert.AreSame(original, viewModel.SelectedChapter);
            Assert.AreEqual(saved.ChapterDirectoryName, progress.Get(key)!.ChapterDirectoryName);
            Assert.AreEqual(saved.PageIndex, progress.Get(key)!.PageIndex);
        }
    }

    private void ConfigureOnlineHandler(int imageCount, Func<CancellationToken, Task<HttpResponseMessage>> imageResponse)
    {
        _handler.Dispose();
        _handler = new FakeHttpMessageHandler(async (request, token) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/album")) return EncryptedApiResponse(request,
                """{"id":"456","name":"在线测试","series":[{"id":"123","name":"第一话"}]}""");
            if (path.EndsWith("/chapter")) return EncryptedApiResponse(request, JsonSerializer.Serialize(new
            {
                id = "123",
                name = "第一话",
                images = Enumerable.Range(1, imageCount).Select(index => $"{index:0000}.jpg").ToArray()
            }));
            if (path.EndsWith("/chapter_view_template")) return new(HttpStatusCode.OK)
            { Content = new StringContent("<script>var scramble_id = 220980;</script>") };
            if (path.Contains("/media/photos/")) return await imageResponse(token);
            return new(HttpStatusCode.NotFound);
        });
    }

    private ReaderPageViewModel CreateViewModel(
        IDispatcher dispatcher,
        ReadingProgressService progressService,
        bool useStripMode = false,
        Func<byte[], CancellationToken, Task<BitmapImage>>? decoder = null)
    {
        var settings = TestServiceFactory.CreateSettings(Path.Combine(_container, "settings"));
        if (useStripMode)
        {
            settings.UpdatePreferences(
                ApplicationSettingsService.SystemTheme,
                ApplicationSettingsService.SelectNone,
                true,
                ApplicationSettingsService.ReaderStrip,
                ApplicationSettingsService.DefaultStripZoom,
                20);
        }
        var library = TestServiceFactory.CreateLibrary(_storageRoot);
        var jmComic = TestServiceFactory.CreateOfflineJmComic(_handler);
        _scheduler = TestServiceFactory.CreateScheduler(jmComic, library);
        _exporter = TestServiceFactory.CreateExporter(library);
        var reader = TestServiceFactory.CreateReader(library);
        var backendClient = TestServiceFactory.CreateClient(
            jmComic,
            _scheduler,
            library,
            _exporter,
            reader,
            settings);

        var preferences = new ReaderPreferenceService(Path.Combine(_container, "reader-preferences"));
        _viewModel = decoder is null
            ? new ReaderPageViewModel(backendClient, settings, progressService, preferences, dispatcher)
            : new ReaderPageViewModel(backendClient, settings, progressService, preferences, dispatcher, decoder);
        return _viewModel;
    }

    private static HttpResponseMessage EncryptedApiResponse(HttpRequestMessage request, string json)
    {
        var timestamp = long.Parse(request.Headers.GetValues("tokenparam").Single().Split(',')[0]);
        var plaintext = Encoding.UTF8.GetBytes(json);
        var keyText = Convert.ToHexString(MD5.HashData(
            Encoding.UTF8.GetBytes(timestamp + "185Hcomic3PAPP7R"))).ToLowerInvariant();
        using var aes = Aes.Create();
        aes.Key = Encoding.ASCII.GetBytes(keyText);
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.PKCS7;
        using var encryptor = aes.CreateEncryptor();
        var encrypted = Convert.ToBase64String(
            encryptor.TransformFinalBlock(plaintext, 0, plaintext.Length));
        var envelope = JsonSerializer.Serialize(new { code = 200, data = encrypted });
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(envelope, Encoding.UTF8, "application/json")
        };
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        while (!condition())
        {
            await Task.Delay(10, cancellation.Token);
        }
    }

    private void CreateChapter(string directoryName, int imageCount)
    {
        var chapter = Path.Combine(_mangaRoot, directoryName);
        Directory.CreateDirectory(chapter);
        for (var index = 1; index <= imageCount; index++)
        {
            File.WriteAllBytes(Path.Combine(chapter, $"{index:000}.jpg"), [1, 2, 3, 4]);
        }
    }
}
