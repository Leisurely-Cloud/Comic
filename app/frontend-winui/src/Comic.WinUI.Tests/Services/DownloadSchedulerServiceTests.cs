using System.Diagnostics;
using Comic.WinUI.Models;
using Comic.WinUI.Services;
using Comic.WinUI.Services.Native;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Comic.WinUI.Tests.Services;

[TestClass]
public sealed class DownloadSchedulerServiceTests
{
    private string _container = null!;
    private string _storageRoot = null!;
    private string _mangaRoot = null!;
    private string _chapterRoot = null!;

    [TestInitialize]
    public void Initialize()
    {
        _container = Path.Combine(Path.GetTempPath(), "Comic.WinUI.Tests", Guid.NewGuid().ToString("N"));
        _storageRoot = Path.Combine(_container, "library");
        _mangaRoot = Path.Combine(_storageRoot, "测试漫画");
        _chapterRoot = Path.Combine(_mangaRoot, "001_第一话");
        Directory.CreateDirectory(_chapterRoot);
        File.WriteAllBytes(Path.Combine(_chapterRoot, "001.jpg"), [1, 2, 3, 4]);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_container)) Directory.Delete(_container, true);
    }

    [TestMethod]
    public async Task DownloadHistory_EnrichesLegacyEntriesFromLibraryMetadata()
    {
        File.WriteAllText(
            Path.Combine(_mangaRoot, "元数据.json"),
            """{"manga_title":"测试漫画","site_name":"禁漫天堂","authors":["作者甲","作者乙"],"manga_url":"https://18comic.vip/album/123","cover_url":"https://example.test/history-cover.jpg","last_failed_chapter_records":[{"order":65,"slug":"650","title":"第65话","reason":"没有可下载图片"}]}""");
        var stateDirectory = Path.Combine(_storageRoot, ".comic_state");
        Directory.CreateDirectory(stateDirectory);
        File.WriteAllText(
            Path.Combine(stateDirectory, "task_history.json"),
            System.Text.Json.JsonSerializer.Serialize(new[]
            {
                new DownloadHistoryItem
                {
                    Id = "legacy-history",
                    SiteKey = SiteCatalog.Key,
                    MangaTitle = "测试漫画",
                    RootDir = _mangaRoot,
                    Status = "completed",
                    Progress = 100,
                    CompletedChapterCount = 1,
                    TotalChapterCount = 1,
                },
            }));

        using var httpClient = new HttpClient();
        var library = TestServiceFactory.CreateLibrary(_storageRoot);
        using var service = TestServiceFactory.CreateScheduler(new JmComicService(httpClient), library);

        var history = await service.GetDownloadHistoryAsync(1, 20);
        var item = history.Items.Single();

        Assert.AreEqual("作者甲、作者乙", item.Author);
        Assert.AreEqual("禁漫天堂", item.SiteName);
        Assert.AreEqual("https://example.test/history-cover.jpg", item.CoverUrl);
        Assert.AreEqual("https://18comic.vip/album/123", item.Url);
        Assert.AreEqual(1, item.FailureDetails.Count);
        Assert.AreEqual("650", item.FailureDetails[0].ChapterId);
        Assert.AreEqual("没有可下载图片", item.FailureDetails[0].Reason);
    }

    [TestMethod]
    public async Task DownloadHistory_DeletesOnlyRequestedRecordsAndPersistsTheChange()
    {
        var stateDirectory = Path.Combine(_storageRoot, ".comic_state");
        Directory.CreateDirectory(stateDirectory);
        var historyPath = Path.Combine(stateDirectory, "task_history.json");
        File.WriteAllText(
            historyPath,
            System.Text.Json.JsonSerializer.Serialize(new[]
            {
                new DownloadHistoryItem { Id = "history-1", MangaTitle = "漫画一", Status = "completed" },
                new DownloadHistoryItem { Id = "history-2", MangaTitle = "漫画二", Status = "failed" },
                new DownloadHistoryItem { Id = "history-3", MangaTitle = "漫画三", Status = "stopped" },
            }));

        using var httpClient = new HttpClient();
        var library = TestServiceFactory.CreateLibrary(_storageRoot);
        using (var service = TestServiceFactory.CreateScheduler(new JmComicService(httpClient), library))
        {
            var removed = await service.DeleteDownloadHistoryAsync(["history-2"]);
            Assert.AreEqual(1, removed);
            var remaining = await service.GetDownloadHistoryAsync(1, 20);
            Assert.AreEqual(2, remaining.Total);
            CollectionAssert.AreEquivalent(
                new[] { "history-1", "history-3" },
                remaining.Items.Select(item => item.Id).ToArray());
        }

        using var verificationClient = new HttpClient();
        var verificationLibrary = TestServiceFactory.CreateLibrary(_storageRoot);
        using var verificationService = TestServiceFactory.CreateScheduler(new JmComicService(verificationClient), verificationLibrary);
        var persisted = await verificationService.GetDownloadHistoryAsync(1, 20);
        Assert.AreEqual(2, persisted.Total);
        Assert.IsFalse(persisted.Items.Any(item => item.Id == "history-2"));
    }

    [TestMethod]
    public async Task DownloadHistory_ConsolidatesSameMangaAndShowsAggregateLocalProgress()
    {
        File.WriteAllText(
            Path.Combine(_mangaRoot, "元数据.json"),
            """{"manga_title":"测试漫画","manga_url":"https://18comic.vip/album/123","total_chapters":1}""");
        var stateDirectory = Path.Combine(_storageRoot, ".comic_state");
        Directory.CreateDirectory(stateDirectory);
        File.WriteAllText(
            Path.Combine(stateDirectory, "task_history.json"),
            System.Text.Json.JsonSerializer.Serialize(new[]
            {
                new DownloadHistoryItem
                {
                    Id = "old-stopped",
                    Url = "https://18comic.vip/album/123",
                    SiteKey = SiteCatalog.Key,
                    MangaTitle = "测试漫画",
                    RootDir = _mangaRoot,
                    Status = "stopped",
                    CompletedChapterCount = 0,
                    TotalChapterCount = 1,
                },
                new DownloadHistoryItem
                {
                    Id = "latest-completed",
                    Url = "https://18comic.vip/album/123",
                    SiteKey = SiteCatalog.Key,
                    MangaTitle = "测试漫画",
                    RootDir = _mangaRoot,
                    Status = "completed",
                    Progress = 100,
                    CompletedChapterCount = 1,
                    TotalChapterCount = 1,
                },
            }));
        using var handler = FakeHttpMessageHandler.AlwaysFails();
        var library = TestServiceFactory.CreateLibrary(_storageRoot);
        using var service = TestServiceFactory.CreateScheduler(
            TestServiceFactory.CreateOfflineJmComic(handler), library);

        var history = await service.GetDownloadHistoryAsync(1, 20);
        var item = history.Items.Single();

        Assert.AreEqual("latest-completed", item.Id);
        Assert.AreEqual(1, item.CompletedChapterCount);
        Assert.AreEqual(1, item.TotalChapterCount);
        Assert.AreEqual(1, item.DownloadedThisRunChapterCount);
        Assert.AreEqual("已完成 1 / 1 章 · 本次补下载 1 章", item.ChapterProgressText);
    }

    [TestMethod]
    public void SelectChapters_AcceptsChapterUrlsAndLegacyTitles()
    {
        var manga = new JmMangaInfo(
            "100",
            "测试漫画",
            string.Empty,
            [
                new JmChapter(1, "201", "第1话"),
                new JmChapter(2, "202", "第2话"),
                new JmChapter(3, "203", "第3话"),
            ],
            null,
            string.Empty,
            []);

        var selected = DownloadSchedulerService.SelectChapters(
            manga,
            ["https://18comic.vip/photo/201", "第3话"]);

        CollectionAssert.AreEqual(
            new[] { "201", "203" },
            selected.Select(chapter => chapter.Id).ToArray());
    }

    [TestMethod]
    public void DeleteChapterDirectories_RemovesOnlyMatchingFinalAndTemporaryFolders()
    {
        var chapterTwo = Path.Combine(_mangaRoot, "002_第二话");
        var temporaryChapterTwo = Path.Combine(_mangaRoot, ".下载中_002_第二话");
        var jmChapterTwo = Path.Combine(_mangaRoot, "2");
        var temporaryJmChapterTwo = Path.Combine(_mangaRoot, ".下载中_2");
        var chapterTwenty = Path.Combine(_mangaRoot, "020_第二十话");
        Directory.CreateDirectory(chapterTwo);
        Directory.CreateDirectory(temporaryChapterTwo);
        Directory.CreateDirectory(jmChapterTwo);
        Directory.CreateDirectory(temporaryJmChapterTwo);
        Directory.CreateDirectory(chapterTwenty);
        File.WriteAllBytes(Path.Combine(chapterTwo, "001.jpg"), [2]);
        File.WriteAllBytes(Path.Combine(temporaryChapterTwo, "001.jpg"), [3]);
        File.WriteAllBytes(Path.Combine(chapterTwenty, "001.jpg"), [20]);

        DownloadSchedulerService.DeleteChapterDirectories(
            _mangaRoot,
            new JmChapter(2, "chapter-2", "第二话"),
            "002_第二话");

        Assert.IsFalse(Directory.Exists(chapterTwo));
        Assert.IsFalse(Directory.Exists(temporaryChapterTwo));
        Assert.IsFalse(Directory.Exists(jmChapterTwo));
        Assert.IsFalse(Directory.Exists(temporaryJmChapterTwo));
        Assert.IsTrue(Directory.Exists(_chapterRoot));
        Assert.IsTrue(Directory.Exists(chapterTwenty));
    }

    [TestMethod]
    public async Task GetDownloads_OrdersTasksByCreationNewestFirst()
    {
        // 任务号是 Guid 前 8 位,没有时间序,不能拿它当排序键。
        using var handler = FakeHttpMessageHandler.AlwaysFails();
        var library = TestServiceFactory.CreateLibrary(_storageRoot);
        using var service = TestServiceFactory.CreateScheduler(
            TestServiceFactory.CreateOfflineJmComic(handler), library);

        var createdInOrder = new List<string>();
        for (var index = 1; index <= 6; index++)
        {
            var task = await service.CreateDownloadAsync(new DownloadCreateRequest
            {
                Url = $"https://18comic.vip/album/{index}",
            });
            createdInOrder.Add(task.Id);
        }

        var listed = await service.GetDownloadsAsync();
        var expected = createdInOrder.AsEnumerable().Reverse().ToList();

        CollectionAssert.AreEqual(
            expected,
            listed.Items.Select(item => item.Id).ToList(),
            "任务列表应当按创建时间倒序,最新的在最前。");
    }

    [TestMethod]
    public async Task CreateDownload_RoutesSiteTrafficThroughTheInjectedTransport()
    {
        // 这条断言保证离线替身真的接上了:测试不应该有任何机会打到真实站点。
        using var handler = FakeHttpMessageHandler.AlwaysFails();
        var library = TestServiceFactory.CreateLibrary(_storageRoot);
        using var service = TestServiceFactory.CreateScheduler(
            TestServiceFactory.CreateOfflineJmComic(handler), library);

        var task = await service.CreateDownloadAsync(new DownloadCreateRequest
        {
            Url = "https://18comic.vip/album/424242",
        });

        var timeout = Stopwatch.StartNew();
        DownloadTaskDto snapshot;
        do
        {
            await Task.Delay(10);
            snapshot = await service.GetDownloadAsync(task.Id);
            Assert.IsTrue(timeout.Elapsed < TimeSpan.FromSeconds(30), "任务未在 30 秒内结束。");
        }
        while (!DownloadSchedulerService.IsTerminal(snapshot.Status));

        Assert.AreEqual("failed", snapshot.Status, "站点全部域名返回失败时任务应当失败。");
        Assert.IsTrue(handler.RequestedUris.Count > 0, "站点请求应当经过注入的替身传输层。");
        Assert.IsTrue(
            handler.RequestedUris.All(uri => uri.StartsWith("https://", StringComparison.Ordinal)),
            "替身记录到的应当全部是站点 API 请求。");
    }

    [TestMethod]
    public async Task CreateDownload_ReturnsExistingActiveTaskForTheSameMangaAndSelection()
    {
        using var handler = FakeHttpMessageHandler.BlocksUntilCancelled();
        var library = TestServiceFactory.CreateLibrary(_storageRoot);
        using var service = TestServiceFactory.CreateScheduler(
            TestServiceFactory.CreateOfflineJmComic(handler), library);
        var request = new DownloadCreateRequest
        {
            Url = "https://18comic.vip/album/123",
            Chapters = ["https://18comic.vip/photo/456"],
        };

        var first = await service.CreateDownloadAsync(request);
        await handler.WaitForRequestAsync(TimeSpan.FromSeconds(5));
        var duplicate = await service.CreateDownloadAsync(new DownloadCreateRequest
        {
            Url = "123",
            Chapters = ["https://18comic.vip/photo/456"],
        });
        var tasks = await service.GetDownloadsAsync();

        Assert.AreEqual(first.Id, duplicate.Id);
        Assert.AreEqual(1, tasks.Items.Count);
    }

    [TestMethod]
    public void IsAlreadyDownloaded_UsesMangaIdentityAndEverySelectedChapter()
    {
        var manga = new JmMangaInfo(
            "123",
            "同名漫画",
            string.Empty,
            [new JmChapter(1, "11", "第1话"), new JmChapter(2, "12", "第2话")],
            null,
            string.Empty,
            []);
        var metadata = new LibraryMetadata
        {
            MangaUrl = "https://18comic.vip/album/123",
            DownloadedChapters =
            [
                new DownloadedChapterRecord { Order = 1, DirName = "001_第1话", ImageCount = 50 },
            ],
        };

        Assert.IsTrue(DownloadSchedulerService.IsAlreadyDownloaded(metadata, manga, [manga.Chapters[0]]));
        Assert.IsFalse(DownloadSchedulerService.IsAlreadyDownloaded(metadata, manga, manga.Chapters));

        metadata.MangaUrl = "https://18comic.vip/album/999";
        Assert.IsFalse(DownloadSchedulerService.IsAlreadyDownloaded(metadata, manga, [manga.Chapters[0]]));
    }

    [TestMethod]
    public void LegacyLibraryWithoutSourceId_IsMatchedByTitleAndActualChapterFiles()
    {
        var legacyRoot = Path.Combine(_storageRoot, "旧版目录名");
        Directory.CreateDirectory(Path.Combine(legacyRoot, "第1话 标题"));
        Directory.CreateDirectory(Path.Combine(legacyRoot, "第2话 标题"));
        File.WriteAllBytes(Path.Combine(legacyRoot, "第1话 标题", "001.jpg"), [1]);
        File.WriteAllBytes(Path.Combine(legacyRoot, "第2话 标题", "001.jpg"), [2]);
        File.WriteAllText(
            Path.Combine(legacyRoot, "元数据.json"),
            """{"manga_title":"旧版漫画","manga_url":"","downloaded_chapters":[]}""");
        var library = TestServiceFactory.CreateLibrary(_storageRoot);
        using var service = TestServiceFactory.CreateScheduler(
            TestServiceFactory.CreateOfflineJmComic(FakeHttpMessageHandler.AlwaysFails()), library);
        var manga = new JmMangaInfo(
            "123",
            "旧版漫画",
            string.Empty,
            [new JmChapter(1, "11", "第1话"), new JmChapter(2, "12", "第2话")],
            null,
            string.Empty,
            []);

        var resolvedRoot = service.ResolveMangaRootDirectory(manga);
        var local = service.GetLocalDownloadedChapters(resolvedRoot);

        Assert.AreEqual(legacyRoot, resolvedRoot);
        CollectionAssert.AreEquivalent(new[] { 1, 2 }, local.Keys.ToArray());
    }

    [TestMethod]
    public void JmCompatibleLayout_UsesNumericMangaAndChapterDirectoryNames()
    {
        var settings = TestServiceFactory.CreateSettings(Path.Combine(_container, "settings"));
        settings.UpdatePreferences(
            ApplicationSettingsService.SystemTheme,
            ApplicationSettingsService.SelectNone,
            true,
            ApplicationSettingsService.ReaderPaged,
            100,
            20,
            3,
            3,
            ApplicationSettingsService.DirectoryLayoutJmCompatible);
        var library = TestServiceFactory.CreateLibrary(_storageRoot);
        using var service = TestServiceFactory.CreateScheduler(
            TestServiceFactory.CreateOfflineJmComic(FakeHttpMessageHandler.AlwaysFails()),
            library,
            settings);
        var manga = new JmMangaInfo(
            "456",
            "格式测试",
            string.Empty,
            [new JmChapter(7, "77", "第7话")],
            null,
            string.Empty,
            []);

        Assert.AreEqual(Path.Combine(_storageRoot, "456"), service.ResolveMangaRootDirectory(manga));
        Assert.AreEqual(
            "7",
            DownloadSchedulerService.PreferredChapterDirectoryName(
                manga.Chapters[0],
                Path.Combine(_storageRoot, "456"),
                manga.Id));
    }

    [TestMethod]
    public void ExistingNumericJmDirectory_IsReusedRegardlessOfCurrentLayout()
    {
        var jmRoot = Path.Combine(_storageRoot, "789");
        var jmChapter = Path.Combine(jmRoot, "1");
        Directory.CreateDirectory(jmChapter);
        File.WriteAllBytes(Path.Combine(jmChapter, "00001.jpg"), [1]);
        var library = TestServiceFactory.CreateLibrary(_storageRoot);
        using var service = TestServiceFactory.CreateScheduler(
            TestServiceFactory.CreateOfflineJmComic(FakeHttpMessageHandler.AlwaysFails()), library);
        var manga = new JmMangaInfo(
            "789",
            "已存在的 JM 目录",
            string.Empty,
            [new JmChapter(1, "11", "第1话")],
            null,
            string.Empty,
            []);

        Assert.AreEqual(jmRoot, service.ResolveMangaRootDirectory(manga));
        Assert.AreEqual(
            "1",
            DownloadSchedulerService.PreferredChapterDirectoryName(manga.Chapters[0], jmRoot, manga.Id));
    }

    [TestMethod]
    public async Task ResumePreparation_ReusesCachedMangaWithoutAnotherSiteRequest()
    {
        using var handler = FakeHttpMessageHandler.AlwaysFails();
        var library = TestServiceFactory.CreateLibrary(_storageRoot);
        using var service = TestServiceFactory.CreateScheduler(
            TestServiceFactory.CreateOfflineJmComic(handler), library);
        var cached = new JmMangaInfo(
            "123",
            "已解析漫画",
            string.Empty,
            [new JmChapter(1, "11", "第1话")],
            null,
            string.Empty,
            []);

        var result = await service.ResolveMangaInfoForRunAsync(
            cached,
            "https://18comic.vip/album/123",
            CancellationToken.None);

        Assert.AreSame(cached, result);
        Assert.AreEqual(0, handler.RequestedUris.Count);
    }

    [TestMethod]
    public async Task Dispose_WithActiveTasksDoesNotThrowAndIsIdempotent()
    {
        using var handler = FakeHttpMessageHandler.BlocksUntilCancelled();
        var library = TestServiceFactory.CreateLibrary(_storageRoot);
        var service = TestServiceFactory.CreateScheduler(
            TestServiceFactory.CreateOfflineJmComic(handler), library);

        var task = await service.CreateDownloadAsync(new DownloadCreateRequest
        {
            Url = "https://18comic.vip/album/1",
        });
        await handler.WaitForRequestAsync(TimeSpan.FromSeconds(5));

        service.Dispose();
        service.Dispose();

        var snapshot = await WaitUntilTerminalAsync(service, task.Id);
        Assert.AreEqual("stopped", snapshot.Status);
        Assert.IsFalse(service.HasActiveTasks());
    }

    [TestMethod]
    public async Task StopDownload_CancelsBlockedRequestAndReachesStoppedState()
    {
        using var handler = FakeHttpMessageHandler.BlocksUntilCancelled();
        var library = TestServiceFactory.CreateLibrary(_storageRoot);
        using var service = TestServiceFactory.CreateScheduler(
            TestServiceFactory.CreateOfflineJmComic(handler), library);

        var task = await service.CreateDownloadAsync(new DownloadCreateRequest
        {
            Url = "https://18comic.vip/album/2",
        });
        await handler.WaitForRequestAsync(TimeSpan.FromSeconds(5));

        var stop = await service.StopDownloadAsync(task.Id);
        var snapshot = await WaitUntilTerminalAsync(service, task.Id);

        Assert.AreEqual("stopping", stop.Status);
        Assert.AreEqual("stopped", snapshot.Status);
        Assert.IsFalse(service.HasActiveTasks());
    }

    [TestMethod]
    public async Task ResumeDownload_RestartsAStoppedTaskWithAFreshCancellationToken()
    {
        var requestCount = 0;
        var firstRequestStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new FakeHttpMessageHandler(async (_, cancellationToken) =>
        {
            var current = Interlocked.Increment(ref requestCount);
            if (current == 1)
            {
                firstRequestStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            return new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable);
        });
        var library = TestServiceFactory.CreateLibrary(_storageRoot);
        using var service = TestServiceFactory.CreateScheduler(
            TestServiceFactory.CreateOfflineJmComic(handler), library);

        var task = await service.CreateDownloadAsync(new DownloadCreateRequest
        {
            Url = "https://18comic.vip/album/3",
        });
        await firstRequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await service.StopDownloadAsync(task.Id);
        var stopped = await WaitUntilTerminalAsync(service, task.Id);

        var resumed = await service.ResumeDownloadAsync(task.Id);

        Assert.IsTrue(resumed.Status is "pending" or "running");
        var timeout = Stopwatch.StartNew();
        while (Volatile.Read(ref requestCount) < 2)
        {
            Assert.IsTrue(timeout.Elapsed < TimeSpan.FromSeconds(5), "继续后没有发起新的下载请求。");
            await Task.Delay(10);
        }
        Assert.AreEqual("stopped", stopped.Status);
        Assert.IsTrue(service.HasActiveTasks() || DownloadSchedulerService.IsTerminal((await service.GetDownloadAsync(task.Id)).Status));
    }

    [TestMethod]
    public async Task ResumeDownload_DuringStoppingWaitsForShutdownThenRestarts()
    {
        var requestCount = 0;
        var firstRequestStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new FakeHttpMessageHandler(async (_, cancellationToken) =>
        {
            var current = Interlocked.Increment(ref requestCount);
            if (current == 1)
            {
                firstRequestStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            return new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable);
        });
        var library = TestServiceFactory.CreateLibrary(_storageRoot);
        using var service = TestServiceFactory.CreateScheduler(
            TestServiceFactory.CreateOfflineJmComic(handler), library);

        var task = await service.CreateDownloadAsync(new DownloadCreateRequest
        {
            Url = "https://18comic.vip/album/4",
        });
        await firstRequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var stop = await service.StopDownloadAsync(task.Id);
        var resumed = await service.ResumeDownloadAsync(task.Id).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.AreEqual("stopping", stop.Status);
        Assert.IsTrue(resumed.Status is "pending" or "running");
        var timeout = Stopwatch.StartNew();
        while (Volatile.Read(ref requestCount) < 2)
        {
            Assert.IsTrue(timeout.Elapsed < TimeSpan.FromSeconds(5), "正在停止时点击继续后没有重新发起请求。");
            await Task.Delay(10);
        }
    }

    [TestMethod]
    public async Task RetryDownload_RestartsAFailedTask()
    {
        var requestCount = 0;
        using var handler = new FakeHttpMessageHandler((_, _) =>
        {
            Interlocked.Increment(ref requestCount);
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable));
        });
        var library = TestServiceFactory.CreateLibrary(_storageRoot);
        using var service = TestServiceFactory.CreateScheduler(
            TestServiceFactory.CreateOfflineJmComic(handler), library);

        var task = await service.CreateDownloadAsync(new DownloadCreateRequest
        {
            Url = "https://18comic.vip/album/5",
        });
        var failed = await WaitUntilTerminalAsync(service, task.Id);
        var requestsBeforeRetry = Volatile.Read(ref requestCount);

        await service.RetryDownloadAsync(task.Id);

        var timeout = Stopwatch.StartNew();
        while (Volatile.Read(ref requestCount) <= requestsBeforeRetry)
        {
            Assert.IsTrue(timeout.Elapsed < TimeSpan.FromSeconds(5), "重试后没有重新发起下载请求。");
            await Task.Delay(10);
        }
        Assert.AreEqual("failed", failed.Status);
    }

    private static async Task<DownloadTaskDto> WaitUntilTerminalAsync(
        DownloadSchedulerService service,
        string taskId)
    {
        var timeout = Stopwatch.StartNew();
        while (true)
        {
            var snapshot = await service.GetDownloadAsync(taskId);
            if (DownloadSchedulerService.IsTerminal(snapshot.Status)) return snapshot;
            Assert.IsTrue(timeout.Elapsed < TimeSpan.FromSeconds(5), "下载任务取消后未在 5 秒内结束。");
            await Task.Delay(10);
        }
    }
}
