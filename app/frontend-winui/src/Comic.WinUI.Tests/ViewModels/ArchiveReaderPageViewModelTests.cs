using System.IO.Compression;
using Comic.WinUI.Models;
using Comic.WinUI.Services;
using Comic.WinUI.Services.Native;
using Comic.WinUI.Tests.Services;
using Comic.WinUI.ViewModels;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Comic.WinUI.Tests.ViewModels;

/// <summary>Use actual ZIP fixtures and controlled image decoding; native rendering is verified separately in the reader window.</summary>
[TestClass]
public sealed class ArchiveReaderPageViewModelTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    private const string FirstChapter = "Book/Chapter 2";
    private const string SecondChapter = "Book/Chapter 10";
    private string _container = null!;
    private ApplicationSettingsService _settings = null!;
    private LibraryStorageService _library = null!;
    private JmComicService _jmComic = null!;
    private FakeHttpMessageHandler _handler = null!;
    private DownloadSchedulerService _scheduler = null!;
    private CbzExportService _exporter = null!;
    private BackendClient _backend = null!;
    private RecentArchiveService _recentArchives = null!;
    private readonly List<ReaderPageViewModel> _readers = [];

    [TestInitialize]
    public void Initialize()
    {
        _container = Path.Combine(Path.GetTempPath(), "Comic.WinUI.ArchiveReaderTests", Guid.NewGuid().ToString("N"));
        _settings = TestServiceFactory.CreateSettings(Path.Combine(_container, "settings"));
        _recentArchives = new(_settings);
        _library = TestServiceFactory.CreateLibrary(Path.Combine(_container, "library"));
        _handler = FakeHttpMessageHandler.AlwaysFails();
        _jmComic = TestServiceFactory.CreateOfflineJmComic(_handler);
        _scheduler = TestServiceFactory.CreateScheduler(_jmComic, _library, _settings);
        _exporter = TestServiceFactory.CreateExporter(_library);
        _backend = TestServiceFactory.CreateClient(_jmComic, _scheduler, _library, _exporter,
            TestServiceFactory.CreateReader(_library), _settings);
    }

    [TestCleanup]
    public async Task Cleanup()
    {
        foreach (var reader in _readers)
        {
            reader.Deactivate();
            await Task.WhenAll(reader.ChapterLoading, reader.PagedImageApplication, reader.ImagePreloading).WaitAsync(Timeout);
        }
        _scheduler.Dispose();
        _exporter.Dispose();
        _library.Dispose();
        _jmComic.Dispose();
        _handler.Dispose();
        if (Directory.Exists(_container)) Directory.Delete(_container, true);
    }

    [TestMethod]
    [DataRow(".cbz", false)]
    [DataRow(".zip", true)]
    public async Task ArchiveReader_RestoresNestedChapterPageBookmarksPreferencesAndReadingStatus(string extension, bool strip)
    {
        var path = CreateArchive(extension);
        var progress = new ReadingProgressService(_settings);
        Assert.IsTrue(progress.SetReadingStatus(path, ReadingStatus.ToRead));
        new ReaderPreferenceService(_settings).Save(path, new ReaderPreference(strip ? "strip" : "paged", false, 100, 100));
        var dispatcher = new DeferredDispatcher();
        var reader = CreateReader(dispatcher, progress);

        await reader.LoadArchiveAsync(path);
        await FinishChapterAsync(reader, dispatcher);
        CollectionAssert.AreEqual(new[] { FirstChapter, SecondChapter }, reader.Chapters.Select(chapter => chapter.DirName).ToArray());
        Assert.AreEqual(3, reader.TotalImages);
        Assert.AreEqual(ReadingStatus.Reading, progress.GetReadingStatus(path));
        await reader.JumpToPageAsync(2);
        if (!strip) await ApplyPagedAsync(reader, dispatcher);
        var firstBookmark = reader.SaveBookmark("第一页夹之后");

        reader.NextChapter();
        await FinishChapterAsync(reader, dispatcher);
        Assert.AreEqual(SecondChapter, reader.SelectedChapter!.DirName);
        reader.SaveBookmark("第二章");
        await reader.GoToBookmarkAsync(firstBookmark);
        if (!strip) await ApplyPagedAsync(reader, dispatcher);
        Assert.AreEqual(FirstChapter, reader.SelectedChapter!.DirName);
        Assert.AreEqual(1, reader.CurrentImageIndex);

        Assert.IsTrue(progress.SetReadingStatus(path, ReadingStatus.Read));
        await reader.JumpToPageAsync(3);
        if (!strip) await ApplyPagedAsync(reader, dispatcher);
        Assert.AreEqual(ReadingStatus.Read, progress.GetReadingStatus(path), "继续翻页应保留手动已读状态。");
        reader.IsRightToLeft = true;
        reader.StripZoomPercent = 170;
        reader.PagedZoomPercent = 140;
        reader.Deactivate();

        var freshProgress = new ReadingProgressService(_settings);
        var restoredDispatcher = new DeferredDispatcher();
        var restored = CreateReader(restoredDispatcher, freshProgress);
        await restored.LoadArchiveAsync(path);
        await FinishChapterAsync(restored, restoredDispatcher);
        Assert.AreEqual(FirstChapter, restored.SelectedChapter!.DirName);
        Assert.AreEqual(2, restored.CurrentImageIndex);
        Assert.AreEqual(strip, restored.IsStripMode);
        Assert.IsTrue(restored.IsRightToLeft);
        Assert.AreEqual(170, restored.StripZoomPercent);
        Assert.AreEqual(140, restored.PagedZoomPercent);
        Assert.AreEqual(2, restored.GetBookmarks().Count);
        Assert.AreEqual(ReadingStatus.Read, freshProgress.GetReadingStatus(path));
        Assert.AreEqual(FirstChapter, freshProgress.Get(path)!.ChapterDirectoryName);
    }

    [TestMethod]
    public async Task ArchiveReader_RootChapterRestoresProgressAndBookmarksAndCancelsArchiveThumbnails()
    {
        var path = Path.Combine(_container, "Root pages.cbz");
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            AddEntry(archive, "2.png", [2]);
            AddEntry(archive, "1.png", [1]);
        }
        var progress = new ReadingProgressService(_settings);
        progress.Save(path, "/", 1);
        var dispatcher = new DeferredDispatcher();
        var reader = CreateReader(dispatcher, progress);

        await reader.LoadArchiveAsync(path);
        await FinishChapterAsync(reader, dispatcher);
        Assert.AreEqual("/", reader.SelectedChapter!.DirName);
        Assert.AreEqual(1, reader.CurrentImageIndex);
        var bookmark = reader.SaveBookmark("根目录第二页");
        Assert.AreEqual("/", bookmark.Chapter);

        var thumbnails = reader.CreateThumbnails();
        using (var dialog = new CancellationTokenSource())
        {
            await Task.WhenAll(thumbnails.Select(item => reader.LoadThumbnailAsync(item, dialog.Token))).WaitAsync(Timeout);
            dialog.Cancel();
            while (dispatcher.PendingCount > 0) dispatcher.FlushFirst();
        }
        Assert.IsTrue(thumbnails.All(item => item.Image is null && !item.IsLoading && !item.HasError));

        await reader.JumpToPageAsync(1);
        await ApplyPagedAsync(reader, dispatcher);
        await reader.GoToBookmarkAsync(bookmark);
        await ApplyPagedAsync(reader, dispatcher);
        Assert.AreEqual(1, reader.CurrentImageIndex);
        reader.Deactivate();

        var freshProgress = new ReadingProgressService(_settings);
        var restoredDispatcher = new DeferredDispatcher();
        var restored = CreateReader(restoredDispatcher, freshProgress);
        await restored.LoadArchiveAsync(path);
        await FinishChapterAsync(restored, restoredDispatcher);
        Assert.AreEqual("/", freshProgress.Get(path)!.ChapterDirectoryName);
        Assert.AreEqual(1, restored.CurrentImageIndex);
        Assert.AreEqual(bookmark, restored.GetBookmarks().Single());
    }

    [TestMethod]
    public async Task ArchiveReader_DoublePageUsesArchiveImagesAndKeepsReadingPositionUntilBothDecode()
    {
        var path = CreateArchive(".cbz");
        var progress = new ReadingProgressService(_settings);
        new ReaderPreferenceService(_settings).Save(path, new ReaderPreference("spread", true, 100, 100));
        var bytesSeen = new List<byte[]>();
        var secondDecode = new TaskCompletionSource<BitmapImage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatcher = new DeferredDispatcher();
        var reader = CreateReader(dispatcher, progress, (bytes, _) =>
        {
            bytesSeen.Add(bytes);
            return bytesSeen.Count == 2 ? secondDecode.Task : Task.FromResult<BitmapImage>(null!);
        });

        await reader.LoadArchiveAsync(path);
        await reader.ChapterLoading.WaitAsync(Timeout);
        await dispatcher.WaitForPendingAsync(1, Timeout);
        Assert.IsTrue(reader.IsDoublePage);
        Assert.IsNull(progress.Get(path));
        Assert.AreEqual(0, _recentArchives.GetAll().Count);
        dispatcher.FlushFirst();
        Assert.AreEqual(2, bytesSeen.Count);
        CollectionAssert.AreEqual(new byte[] { 1 }, bytesSeen[0]);
        CollectionAssert.AreEqual(new byte[] { 2 }, bytesSeen[1]);
        Assert.IsTrue(reader.IsLoading);
        Assert.IsNull(progress.Get(path));
        Assert.AreEqual(0, _recentArchives.GetAll().Count, "双页尚有一页未解码时不能加入最近记录。");
        secondDecode.SetResult(null!);
        await reader.PagedImageApplication.WaitAsync(Timeout);
        Assert.IsFalse(reader.IsLoading);
        Assert.AreEqual("1-2 / 3", reader.PageIndicator);
        Assert.AreEqual(0, progress.Get(path)!.PageIndex);
        Assert.AreEqual(Path.GetFullPath(path), _recentArchives.GetAll().Single().FilePath);
    }

    [TestMethod]
    public async Task ArchiveReader_DecodeFailureDoesNotSaveProgressOrAllowBookmarks()
    {
        var path = CreateArchive(".zip");
        var progress = new ReadingProgressService(_settings);
        var dispatcher = new DeferredDispatcher();
        var reader = CreateReader(dispatcher, progress, (_, _) => Task.FromException<BitmapImage>(new IOException("图片已损坏")));

        await reader.LoadArchiveAsync(path);
        await FinishChapterAsync(reader, dispatcher);
        Assert.IsFalse(reader.IsLoading);
        Assert.IsTrue(reader.HasFailedPagedImage);
        StringAssert.Contains(reader.PageError, "图片已损坏");
        reader.SaveReadingProgress();
        Assert.IsNull(progress.Get(path));
        Assert.AreEqual(ReadingStatus.ToRead, progress.GetReadingStatus(path));
        Assert.AreEqual(0, _recentArchives.GetAll().Count);
        Assert.ThrowsExactly<InvalidOperationException>(() => reader.SaveBookmark("不能保存失败页"));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ArchiveReader_CancelOrDeactivateRejectsQueuedPageAndPreservesProgress(bool deactivate)
    {
        var path = CreateArchive(".cbz");
        var progress = new ReadingProgressService(_settings);
        progress.Save(path, FirstChapter, 1);
        var before = progress.Get(path);
        var dispatcher = new DeferredDispatcher();
        var reader = CreateReader(dispatcher, progress);

        await reader.LoadArchiveAsync(path);
        await reader.ChapterLoading.WaitAsync(Timeout);
        await dispatcher.WaitForPendingAsync(1, Timeout);
        Assert.IsTrue(reader.IsLoading);
        if (deactivate) reader.Deactivate();
        else reader.CancelLoading();
        dispatcher.FlushFirst();
        await reader.PagedImageApplication.WaitAsync(Timeout);
        Assert.IsFalse(reader.IsLoading);
        Assert.IsNull(reader.SelectedChapter);
        Assert.IsNull(reader.CurrentImage);
        Assert.AreEqual(0, reader.TotalImages);
        Assert.AreEqual(before, progress.Get(path));
        Assert.AreEqual(0, _recentArchives.GetAll().Count);
        if (!deactivate) StringAssert.Contains(reader.PageError, "已取消加载");
    }

    [TestMethod]
    public async Task ArchiveReader_CancelDuringDecodeRejectsUncooperativeLateCompletion()
    {
        var path = CreateArchive(".zip");
        var progress = new ReadingProgressService(_settings);
        var decoding = new TaskCompletionSource<BitmapImage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatcher = new DeferredDispatcher();
        var reader = CreateReader(dispatcher, progress, (_, _) => decoding.Task);

        await reader.LoadArchiveAsync(path);
        await reader.ChapterLoading.WaitAsync(Timeout);
        await dispatcher.WaitForPendingAsync(1, Timeout);
        dispatcher.FlushFirst();
        reader.CancelLoading();
        decoding.SetResult(null!);
        await reader.PagedImageApplication.WaitAsync(Timeout);
        Assert.IsFalse(reader.IsLoading);
        Assert.AreEqual(0, reader.Chapters.Count);
        Assert.IsNull(reader.CurrentImage);
        Assert.IsNull(progress.Get(path));
        StringAssert.Contains(reader.PageError, "已取消加载");
        Assert.AreEqual(0, _recentArchives.GetAll().Count);
    }

    [TestMethod]
    public async Task ArchiveReader_PreCanceledOpenDoesNotRemainBusyOrPublishChapters()
    {
        var path = CreateArchive(".cbz");
        var progress = new ReadingProgressService(_settings);
        var reader = CreateReader(new DeferredDispatcher(), progress);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await reader.LoadArchiveAsync(path, cancellation.Token);
        Assert.IsFalse(reader.IsLoading);
        Assert.AreEqual(0, reader.Chapters.Count);
        Assert.IsNull(progress.Get(path));
        Assert.AreEqual(0, _recentArchives.GetAll().Count);
    }

    [TestMethod]
    [DataRow(".zip", "破损内容")]
    [DataRow(".cbz", "")]
    public async Task ArchiveReader_InvalidArchiveShowsErrorAndDoesNotSaveProgress(string extension, string contents)
    {
        var path = Path.Combine(_container, "invalid" + extension);
        File.WriteAllText(path, contents);
        var progress = new ReadingProgressService(_settings);
        var reader = CreateReader(new DeferredDispatcher(), progress);

        await reader.LoadArchiveAsync(path);
        Assert.IsFalse(reader.IsLoading);
        Assert.IsFalse(string.IsNullOrWhiteSpace(reader.PageError));
        Assert.AreEqual(0, reader.Chapters.Count);
        Assert.IsNull(progress.Get(path));
        Assert.AreEqual(0, _recentArchives.GetAll().Count);
    }

    [TestMethod]
    public async Task RecentArchives_PageDisplayRecordsOncePerSessionAndReopeningMovesItToFront()
    {
        var path = CreateArchive(".cbz");
        var otherPath = Path.Combine(_container, "Other book.zip");
        var dispatcher = new DeferredDispatcher();
        var reader = CreateReader(dispatcher, new ReadingProgressService(_settings));

        await reader.LoadArchiveAsync(path);
        await reader.ChapterLoading.WaitAsync(Timeout);
        await dispatcher.WaitForPendingAsync(1, Timeout);
        Assert.AreEqual(0, _recentArchives.GetAll().Count, "章节索引和待执行的页面回调不能算作成功阅读。");
        await ApplyPagedAsync(reader, dispatcher);
        await reader.ImagePreloading.WaitAsync(Timeout);
        var firstOpen = _recentArchives.GetAll().Single();
        Assert.AreEqual(Path.GetFullPath(path), firstOpen.FilePath);
        Assert.IsTrue(_recentArchives.RecordOpened(otherPath));

        await reader.JumpToPageAsync(2);
        await ApplyPagedAsync(reader, dispatcher);
        CollectionAssert.AreEqual(new[] { otherPath, path }, _recentArchives.GetAll().Select(entry => entry.FilePath).ToArray());
        Assert.AreEqual(firstOpen, _recentArchives.GetAll()[1], "同一会话翻页不应反复改写最近打开时间或顺序。");

        reader.Deactivate();
        await reader.LoadArchiveAsync(path);
        await FinishChapterAsync(reader, dispatcher);
        CollectionAssert.AreEqual(new[] { path, otherPath }, _recentArchives.GetAll().Select(entry => entry.FilePath).ToArray());
        Assert.AreEqual(2, new RecentArchiveService(_settings).GetAll().Count, "重新打开同一路径应更新原条目，且可从磁盘恢复。");
    }

    [TestMethod]
    public async Task RecentArchives_StripIndexAndThumbnailDoNotRecordUntilVisibleImageCompletes()
    {
        var path = CreateArchive(".zip");
        new ReaderPreferenceService(_settings).Save(path, new ReaderPreference("strip", false, 100, 100));
        var dispatcher = new DeferredDispatcher();
        var decoded = 0;
        var reader = CreateReader(dispatcher, new ReadingProgressService(_settings), decodeStrip: _ =>
        {
            decoded++;
            return null!;
        });

        await reader.LoadArchiveAsync(path);
        await reader.ChapterLoading.WaitAsync(Timeout);
        Assert.AreEqual(3, reader.StripImages.Count);
        Assert.AreEqual(0, _recentArchives.GetAll().Count);
        await reader.LoadThumbnailAsync(reader.CreateThumbnails()[0], CancellationToken.None);
        await dispatcher.WaitForPendingAsync(1, Timeout);
        dispatcher.FlushFirst();
        Assert.AreEqual(0, decoded, "缩略图解码不应调用可视条漫解码器。");
        Assert.AreEqual(0, _recentArchives.GetAll().Count);

        await reader.LoadStripImageAsync(reader.StripImages[0]);
        Assert.AreEqual(0, _recentArchives.GetAll().Count, "已取到图片字节但尚未执行可视图回调时不能记录。");
        await dispatcher.WaitForPendingAsync(1, Timeout);
        dispatcher.FlushFirst();
        Assert.AreEqual(1, decoded);
        Assert.IsFalse(reader.StripImages[0].IsLoading);
        Assert.IsFalse(reader.StripImages[0].HasError);
        Assert.AreEqual(path, _recentArchives.GetAll().Single().FilePath);

        var otherPath = Path.Combine(_container, "Other book.cbz");
        Assert.IsTrue(_recentArchives.RecordOpened(otherPath));
        await reader.LoadStripImageAsync(reader.StripImages[1]);
        dispatcher.FlushFirst();
        CollectionAssert.AreEqual(new[] { otherPath, path }, _recentArchives.GetAll().Select(entry => entry.FilePath).ToArray());
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RecentArchives_FailedOrUnloadedStripImageDoesNotRecord(bool unload)
    {
        var path = CreateArchive(".cbz");
        new ReaderPreferenceService(_settings).Save(path, new ReaderPreference("strip", false, 100, 100));
        var dispatcher = new DeferredDispatcher();
        var decoded = 0;
        var reader = CreateReader(dispatcher, new ReadingProgressService(_settings), decodeStrip: _ =>
        {
            decoded++;
            throw new IOException("条漫图片损坏");
        });
        await reader.LoadArchiveAsync(path);
        await reader.ChapterLoading.WaitAsync(Timeout);
        var item = reader.StripImages[0];
        await reader.LoadStripImageAsync(item);
        await dispatcher.WaitForPendingAsync(1, Timeout);
        if (unload) reader.UnloadStripImage(item);

        dispatcher.FlushFirst();

        Assert.AreEqual(unload ? 0 : 1, decoded);
        Assert.AreEqual(!unload, item.HasError);
        Assert.IsFalse(item.IsLoading);
        Assert.AreEqual(0, _recentArchives.GetAll().Count);
    }

    [TestMethod]
    public async Task RecentArchives_StaleStripCallbackCannotRecordArchiveFromNewSession()
    {
        var firstPath = CreateArchive(".cbz");
        var secondPath = CreateArchive(".zip");
        var preferences = new ReaderPreferenceService(_settings);
        preferences.Save(firstPath, new ReaderPreference("strip", false, 100, 100));
        preferences.Save(secondPath, new ReaderPreference("strip", false, 100, 100));
        var dispatcher = new DeferredDispatcher();
        var reader = CreateReader(dispatcher, new ReadingProgressService(_settings), decodeStrip: _ => null!);
        await reader.LoadArchiveAsync(firstPath);
        await reader.ChapterLoading.WaitAsync(Timeout);
        await reader.LoadStripImageAsync(reader.StripImages[0]);
        await dispatcher.WaitForPendingAsync(1, Timeout);

        await reader.LoadArchiveAsync(secondPath);
        await reader.ChapterLoading.WaitAsync(Timeout);
        dispatcher.FlushFirst();
        Assert.AreEqual(0, _recentArchives.GetAll().Count, "旧会话的图片回调不能把新压缩包错误地记为已打开。");
        await reader.LoadStripImageAsync(reader.StripImages[0]);
        dispatcher.FlushFirst();
        Assert.AreEqual(secondPath, _recentArchives.GetAll().Single().FilePath);
    }

    [TestMethod]
    public async Task RecentArchives_LocalLibraryReadingDoesNotCreateArchiveHistory()
    {
        var localRoot = Path.Combine(_container, "library", "Directory book.zip");
        var chapter = Path.Combine(localRoot, "Chapter 1");
        Directory.CreateDirectory(chapter);
        File.WriteAllBytes(Path.Combine(chapter, "1.png"), [1]);
        var dispatcher = new DeferredDispatcher();
        var progress = new ReadingProgressService(_settings);
        var reader = CreateReader(dispatcher, progress);

        await reader.LoadAsync(localRoot);
        await FinishChapterAsync(reader, dispatcher);

        Assert.AreEqual(1, reader.TotalImages);
        Assert.IsNotNull(progress.Get(localRoot));
        Assert.AreEqual(0, _recentArchives.GetAll().Count, "书库目录即使名字以 .zip 结尾也不能进入文件历史。");
    }

    [TestMethod]
    public async Task RecentArchives_HistorySaveFailureDoesNotInterruptSuccessfulPageOrProgress()
    {
        var path = CreateArchive(".cbz");
        Directory.CreateDirectory(Path.Combine(_settings.SettingsDirectory, "recent-archives.json"));
        var dispatcher = new DeferredDispatcher();
        var progress = new ReadingProgressService(_settings);
        var reader = CreateReader(dispatcher, progress);

        await reader.LoadArchiveAsync(path);
        await FinishChapterAsync(reader, dispatcher);

        Assert.IsFalse(reader.IsLoading);
        Assert.IsFalse(reader.HasFailedPagedImage);
        Assert.AreEqual(string.Empty, reader.PageError);
        Assert.AreEqual(0, progress.Get(path)!.PageIndex);
        Assert.AreEqual(0, _recentArchives.GetAll().Count);
    }

    [TestMethod]
    public async Task RecentArchives_MovedFileKeepsHistoryAndProgressAndRestoresPageAfterMovingBack()
    {
        var path = CreateArchive(".cbz");
        var movedPath = Path.Combine(_container, "Moved archive.cbz");
        var otherPath = Path.Combine(_container, "Other recent book.zip");
        var archiveService = new ArchiveReaderService();
        var backend = TestServiceFactory.CreateClient(_jmComic, _scheduler, _library, _exporter,
            TestServiceFactory.CreateReader(_library, archiveService), _settings);
        var dispatcher = new DeferredDispatcher();
        var progress = new ReadingProgressService(_settings);
        var decodedPages = new List<byte[]>();
        var reader = new ReaderPageViewModel(backend, _settings, progress, new ReaderPreferenceService(_settings), dispatcher,
            (bytes, _) =>
            {
                decodedPages.Add(bytes);
                return Task.FromResult<BitmapImage>(null!);
            }, new ReadingBookmarkService(_settings), _recentArchives);
        _readers.Add(reader);

        await reader.LoadArchiveAsync(path);
        await FinishChapterAsync(reader, dispatcher);
        await reader.JumpToPageAsync(2);
        await ApplyPagedAsync(reader, dispatcher);
        await reader.ImagePreloading.WaitAsync(Timeout);
        reader.Deactivate();
        Assert.IsFalse(archiveService.IsAuthorizedArchive(path));
        Assert.IsTrue(_recentArchives.RecordOpened(otherPath));
        var historyBefore = _recentArchives.GetAll().ToArray();
        var progressBefore = progress.Get(path);
        Assert.AreEqual(1, progressBefore!.PageIndex);
        Assert.AreEqual(FirstChapter, progressBefore.ChapterDirectoryName);

        // Both paths resolve directly inside this test's GUID directory before either move.
        Assert.AreEqual(Path.GetFullPath(_container), Path.GetDirectoryName(Path.GetFullPath(path)));
        Assert.AreEqual(Path.GetFullPath(_container), Path.GetDirectoryName(Path.GetFullPath(movedPath)));
        File.Move(path, movedPath);
        var decodedBeforeMissingOpen = decodedPages.Count;
        await reader.LoadArchiveAsync(historyBefore.Single(entry => entry.FilePath == path).FilePath);

        Assert.IsFalse(reader.IsLoading);
        StringAssert.Contains(reader.PageError, "无法读取压缩包");
        StringAssert.Contains(reader.PageError, "移动");
        Assert.AreEqual(0, reader.Chapters.Count);
        Assert.AreEqual(0, reader.TotalImages);
        Assert.IsNull(reader.SelectedChapter);
        Assert.IsNull(reader.CurrentImage);
        Assert.AreEqual(decodedBeforeMissingOpen, decodedPages.Count);
        Assert.IsFalse(archiveService.IsAuthorizedArchive(path));
        Assert.IsFalse(archiveService.IsAuthorizedArchive(movedPath));
        CollectionAssert.AreEqual(historyBefore, _recentArchives.GetAll().ToArray(), "失败重开不能改写时间或重新排序最近记录。");
        CollectionAssert.AreEqual(historyBefore, new RecentArchiveService(_settings).GetAll().ToArray());
        Assert.AreEqual(progressBefore, progress.Get(path));
        Assert.AreEqual(progressBefore, new ReadingProgressService(_settings).Get(path));
        Assert.IsNull(progress.Get(movedPath));

        File.Move(movedPath, path);
        await reader.LoadArchiveAsync(path);
        await FinishChapterAsync(reader, dispatcher);

        Assert.AreEqual(string.Empty, reader.PageError);
        Assert.IsTrue(archiveService.IsAuthorizedArchive(path));
        Assert.AreEqual(FirstChapter, reader.SelectedChapter!.DirName);
        Assert.AreEqual(1, reader.CurrentImageIndex);
        CollectionAssert.AreEqual(new byte[] { 2 }, decodedPages[^1]);
        CollectionAssert.AreEqual(new[] { path, otherPath }, _recentArchives.GetAll().Select(entry => entry.FilePath).ToArray());
        Assert.AreEqual(2, _recentArchives.GetAll().Count);
    }

    private ReaderPageViewModel CreateReader(IDispatcher dispatcher, ReadingProgressService progress,
        Func<byte[], CancellationToken, Task<BitmapImage>>? decode = null, Func<byte[], BitmapImage>? decodeStrip = null)
    {
        var reader = new ReaderPageViewModel(_backend, _settings, progress, new ReaderPreferenceService(_settings), dispatcher,
            decode ?? ((_, _) => Task.FromResult<BitmapImage>(null!)), new ReadingBookmarkService(_settings), _recentArchives, decodeStrip);
        _readers.Add(reader);
        return reader;
    }

    private static async Task FinishChapterAsync(ReaderPageViewModel reader, DeferredDispatcher dispatcher)
    {
        await reader.ChapterLoading.WaitAsync(Timeout);
        if (!reader.IsStripMode) await ApplyPagedAsync(reader, dispatcher);
    }

    private static async Task ApplyPagedAsync(ReaderPageViewModel reader, DeferredDispatcher dispatcher)
    {
        await dispatcher.WaitForPendingAsync(1, Timeout);
        dispatcher.FlushFirst();
        await reader.PagedImageApplication.WaitAsync(Timeout);
    }

    private string CreateArchive(string extension)
    {
        var path = Path.Combine(_container, "Archive fixture" + extension);
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        AddEntry(archive, FirstChapter + "/10.png", [10]);
        AddEntry(archive, FirstChapter + "/2.png", [2]);
        AddEntry(archive, FirstChapter + "/1.png", [1]);
        AddEntry(archive, SecondChapter + "/1.png", [11]);
        AddEntry(archive, "ComicInfo.xml", [0]);
        return path;
    }

    private static void AddEntry(ZipArchive archive, string path, byte[] bytes)
    {
        using var stream = archive.CreateEntry(path, CompressionLevel.NoCompression).Open();
        stream.Write(bytes);
    }
}
