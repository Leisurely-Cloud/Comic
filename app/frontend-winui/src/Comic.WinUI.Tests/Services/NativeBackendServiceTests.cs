using Comic.WinUI.Models;
using Comic.WinUI.Services.Native;
using System.Diagnostics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.IO.Compression;
using System.Xml.Linq;

namespace Comic.WinUI.Tests.Services;

[TestClass]
public sealed class NativeBackendServiceTests
{
    private string _container = null!;
    private string _storageRoot = null!;
    private string _mangaRoot = null!;
    private string _chapterRoot = null!;
    private string _imagePath = null!;

    [TestInitialize]
    public void Initialize()
    {
        _container = Path.Combine(Path.GetTempPath(), "Comic.WinUI.Tests", Guid.NewGuid().ToString("N"));
        _storageRoot = Path.Combine(_container, "library");
        _mangaRoot = Path.Combine(_storageRoot, "测试漫画");
        _chapterRoot = Path.Combine(_mangaRoot, "001_第一话");
        _imagePath = Path.Combine(_chapterRoot, "001.jpg");
        Directory.CreateDirectory(_chapterRoot);
        File.WriteAllBytes(_imagePath, [1, 2, 3, 4]);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_container)) Directory.Delete(_container, true);
    }

    [TestMethod]
    public async Task LibraryAndReader_UseManagedStorageTree()
    {
        File.WriteAllText(
            Path.Combine(_mangaRoot, "元数据.json"),
            """{"manga_title":"测试漫画","authors":["作者甲","作者乙"],"cover_url":"https://example.test/cover.jpg"}""");
        using var httpClient = new HttpClient();
        using var service = new NativeBackendService(new JmComicService(httpClient), _storageRoot);

        var library = await service.GetLibraryAsync(string.Empty, 1, 20);
        Assert.AreEqual(1, library.Total);
        Assert.AreEqual("测试漫画", library.Items.Single().Title);
        Assert.AreEqual("作者甲、作者乙", library.Items.Single().Author);
        Assert.AreEqual("https://example.test/cover.jpg", library.Items.Single().CoverUrl);

        var authorSearch = await service.GetLibraryAsync("作者乙", 1, 20);
        Assert.AreEqual(1, authorSearch.Total);

        var chapters = await service.GetReaderChaptersAsync(_mangaRoot);
        Assert.AreEqual("测试漫画", chapters.MangaTitle);
        Assert.AreEqual("第一话", chapters.Chapters.Single().Title);
        Assert.AreEqual(1, chapters.Chapters.Single().ImageCount);

        var images = await service.GetChapterImagesAsync(_mangaRoot, "001_第一话");
        Assert.AreEqual(Path.GetFullPath(_imagePath), images.Images.Single());
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 4 }, await service.GetImageBytesAsync(_imagePath));
    }

    [TestMethod]
    public async Task DownloadHistory_EnrichesLegacyEntriesFromLibraryMetadata()
    {
        File.WriteAllText(
            Path.Combine(_mangaRoot, "\u5143\u6570\u636e.json"),
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
        using var service = new NativeBackendService(new JmComicService(httpClient), _storageRoot);

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
        using (var service = new NativeBackendService(new JmComicService(httpClient), _storageRoot))
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
        using var verificationService = new NativeBackendService(new JmComicService(verificationClient), _storageRoot);
        var persisted = await verificationService.GetDownloadHistoryAsync(1, 20);
        Assert.AreEqual(2, persisted.Total);
        Assert.IsFalse(persisted.Items.Any(item => item.Id == "history-2"));
    }

    [TestMethod]
    public async Task LibraryAndReader_RecognizeLegacyNamedChapterFolders()
    {
        var legacyRoot = Path.Combine(_storageRoot, "旧版漫画");
        var chapter2 = Path.Combine(legacyRoot, "第2话 开始");
        var chapter10 = Path.Combine(legacyRoot, "第10话 结局");
        var temporaryChapter = Path.Combine(legacyRoot, ".下载中_第11话");
        Directory.CreateDirectory(chapter2);
        Directory.CreateDirectory(chapter10);
        Directory.CreateDirectory(temporaryChapter);
        var coverPath = Path.Combine(legacyRoot, "cover.jpg");
        var chapter2Image = Path.Combine(chapter2, "0001.jpg");
        File.WriteAllBytes(coverPath, [9, 8, 7]);
        File.WriteAllBytes(chapter2Image, [2]);
        File.WriteAllBytes(Path.Combine(chapter10, "0001.png"), [10]);
        File.WriteAllBytes(Path.Combine(temporaryChapter, "0001.jpg"), [11]);
        Directory.CreateDirectory(Path.Combine(legacyRoot, "说明文件"));
        File.WriteAllText(
            Path.Combine(legacyRoot, "元数据.json"),
            """{"name":"旧版元数据书名","author":["旧作者甲","旧作者甲","旧作者乙"]}""");

        using var httpClient = new HttpClient();
        using var service = new NativeBackendService(new JmComicService(httpClient), _storageRoot);

        var library = await service.GetLibraryAsync("旧作者乙", 1, 20);
        var item = library.Items.Single();
        Assert.AreEqual("旧版元数据书名", item.Title);
        Assert.AreEqual("旧作者甲、旧作者乙", item.Author);
        Assert.AreEqual("本地漫画", item.SiteName);
        Assert.AreEqual(Path.GetFullPath(coverPath), item.CoverUrl);
        Assert.AreEqual(2, item.DownloadedChapterCount);
        Assert.AreEqual("第10话 结局", item.LastDownloadedChapterTitle);

        var chapters = await service.GetReaderChaptersAsync(legacyRoot);
        CollectionAssert.AreEqual(
            new[] { "第2话 开始", "第10话 结局" },
            chapters.Chapters.Select(chapter => chapter.Title).ToArray());

        var images = await service.GetChapterImagesAsync(legacyRoot, "第2话 开始");
        Assert.AreEqual(Path.GetFullPath(chapter2Image), images.Images.Single());
        CollectionAssert.AreEqual(new byte[] { 9, 8, 7 }, await service.GetImageBytesAsync(coverPath));
    }

    [TestMethod]
    public async Task Reader_RejectsPathsOutsideManagedStorage()
    {
        var outsideManga = Path.Combine(_container, "outside", "外部漫画");
        Directory.CreateDirectory(Path.Combine(outsideManga, "001_第一话"));

        using var httpClient = new HttpClient();
        using var service = new NativeBackendService(new JmComicService(httpClient), _storageRoot);

        await Assert.ThrowsExceptionAsync<UnauthorizedAccessException>(
            () => service.GetReaderChaptersAsync(outsideManga));
        await Assert.ThrowsExceptionAsync<ArgumentException>(
            () => service.GetChapterImagesAsync(_mangaRoot, "..\\outside"));
    }

    [TestMethod]
    public async Task ExportCbz_ReportsCompletionAndWritesComicInfo()
    {
        File.WriteAllText(
            Path.Combine(_mangaRoot, "元数据.json"),
            """{"manga_title":"测试漫画","manga_url":"https://18comic.vip/album/100"}""");
        using var httpClient = new HttpClient();
        using var service = new NativeBackendService(new JmComicService(httpClient), _storageRoot);

        var response = await service.ExportCbzAsync(_mangaRoot);
        ExportCbzProgress progress;
        var timeout = Stopwatch.StartNew();
        do
        {
            await Task.Delay(20);
            progress = await service.GetExportProgressAsync(response.TaskId);
            Assert.IsTrue(timeout.Elapsed < TimeSpan.FromSeconds(60), "CBZ export did not finish within 60 seconds.");
        }
        while (progress.Status == "running");

        Assert.AreEqual("completed", progress.Status);
        Assert.AreEqual(1, progress.TotalChapters);
        Assert.AreEqual(1, progress.CurrentIndex);
        Assert.AreEqual(1, progress.ExportedCount);
        Assert.AreEqual(Path.Combine(_storageRoot, "测试漫画_CBZ"), progress.ExportDir);
        Assert.AreEqual(0, progress.SkippedChapters.Count);

        var archivePath = Path.Combine(progress.ExportDir, "001_第一话.cbz");
        Assert.IsTrue(File.Exists(archivePath));
        Assert.IsFalse(File.Exists(archivePath + ".tmp"));
        using var archive = ZipFile.OpenRead(archivePath);
        Assert.IsNotNull(archive.GetEntry("001.jpg"));
        var comicInfoEntry = archive.GetEntry("ComicInfo.xml");
        Assert.IsNotNull(comicInfoEntry);
        using var reader = new StreamReader(comicInfoEntry.Open());
        var comicInfo = XDocument.Parse(await reader.ReadToEndAsync());
        Assert.AreEqual("测试漫画", comicInfo.Root?.Element("Series")?.Value);
        Assert.AreEqual("第一话", comicInfo.Root?.Element("Title")?.Value);
        Assert.AreEqual("1", comicInfo.Root?.Element("PageCount")?.Value);
    }

    [TestMethod]
    public async Task Settings_CanSwitchManagedStorageRoot()
    {
        var nextStorageRoot = Path.Combine(_container, "next-library");
        using var httpClient = new HttpClient();
        using var service = new NativeBackendService(new JmComicService(httpClient), _storageRoot);

        var result = await service.UpdateSettingsAsync(
            new SettingsUpdateRequest { StorageRoot = nextStorageRoot });

        Assert.AreEqual(Path.GetFullPath(nextStorageRoot), result.StorageRoot);
        Assert.AreEqual(Path.GetFullPath(nextStorageRoot), service.StorageRoot);
        Assert.IsTrue(Directory.Exists(Path.Combine(nextStorageRoot, ".comic_state")));
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

        var selected = NativeBackendService.SelectChapters(
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

        NativeBackendService.DeleteChapterDirectories(
            _mangaRoot,
            new JmChapter(2, "chapter-2", "第二话"),
            "002_第二话");

        Assert.IsFalse(Directory.Exists(chapterTwo));
        Assert.IsFalse(Directory.Exists(temporaryChapterTwo));
        Assert.IsTrue(Directory.Exists(_chapterRoot));
        Assert.IsTrue(Directory.Exists(chapterTwenty));
    }
}
