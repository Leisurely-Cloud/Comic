using System.Diagnostics;
using Comic.WinUI.Models;
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
            """{"manga_title":"测试漫画","site_name":"禁漫天堂","authors":["作者甲","作者乙"],"manga_url":"https://18comic.vip/album/123","cover_url":"https://example.test/history-cover.jpg"}""");
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
        var chapterTwenty = Path.Combine(_mangaRoot, "020_第二十话");
        Directory.CreateDirectory(chapterTwo);
        Directory.CreateDirectory(temporaryChapterTwo);
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
    public async Task Dispose_WithActiveTasksDoesNotThrowAndIsIdempotent()
    {
        // 修复前 Dispose 会在仍在运行的 worker 底下直接释放它正在用的 CTS。
        // 这里只验证释放路径本身健壮(不抛、可重复调用);worker 侧的时序不便断言。
        using var handler = FakeHttpMessageHandler.AlwaysFails();
        var library = TestServiceFactory.CreateLibrary(_storageRoot);
        var service = TestServiceFactory.CreateScheduler(
            TestServiceFactory.CreateOfflineJmComic(handler), library);

        for (var index = 1; index <= 3; index++)
        {
            await service.CreateDownloadAsync(new DownloadCreateRequest
            {
                Url = $"https://18comic.vip/album/{index}",
            });
        }

        service.Dispose();
        service.Dispose();
    }
}
