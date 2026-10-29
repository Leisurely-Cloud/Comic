using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Comic.WinUI.Models;
using Comic.WinUI.Services;
using Comic.WinUI.Services.Native;
using Comic.WinUI.Tests.Services;
using Comic.WinUI.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Comic.WinUI.Tests.ViewModels;

[TestClass]
public sealed class BatchImportDownloadPageIntegrationTests
{
    private string _container = null!;
    private string _libraryRoot = null!;
    private FakeHttpMessageHandler _handler = null!;
    private DownloadSchedulerService _scheduler = null!;
    private CbzExportService _exporter = null!;
    private LibraryStorageService _library = null!;
    private DownloadPageViewModel _page = null!;

    [TestInitialize]
    public void Initialize()
    {
        _container = Path.Combine(Path.GetTempPath(), "ComicBatchImportTests", Guid.NewGuid().ToString("N"));
        _libraryRoot = Path.Combine(_container, "library");
        Directory.CreateDirectory(_libraryRoot);
        _handler = new FakeHttpMessageHandler(request =>
        {
            Assert.AreEqual("/album", request.RequestUri!.AbsolutePath,
                "批量预览和本地完整跳过不能请求章节图片。");
            return EncryptedResponse(request,
                """{"id":"123","name":"批量测试漫画","author":[],"series":[{"id":"456"},{"id":"789"}]}""");
        });
    }

    [TestCleanup]
    public void Cleanup()
    {
        _page?.Dispose();
        _scheduler?.Dispose();
        _exporter?.Dispose();
        _library?.Dispose();
        _handler.Dispose();
        if (Directory.Exists(_container)) Directory.Delete(_container, true);
    }

    [TestMethod]
    public async Task PreviewDoesNotCreateTasks_AndExactExistingSelectionIsReused()
    {
        WritePausedTask(["https://18comic.vip/photo/456", "https://18comic.vip/photo/789"]);
        CreatePage(schedule: true);
        _page.BatchImport.InputText = "JM123";

        await _page.BatchImport.PreviewAsync();
        Assert.AreEqual(1, (await _scheduler.GetDownloadsAsync()).Items.Count);
        Assert.AreEqual(0, _page.Tasks.Count);
        await _page.BatchImport.SubmitSelectedAsync();

        Assert.AreEqual(1, (await _scheduler.GetDownloadsAsync()).Items.Count);
        Assert.AreEqual("existing", _page.Tasks.Single().Id);
        Assert.AreEqual("existing", _page.BatchImport.SubmittedItems.Single().TaskId);
        StringAssert.Contains(_page.BatchImport.Items.Single().StatusText, "沿用现有任务");
        Assert.IsFalse(_page.BatchImport.CanSubmit);
    }

    [TestMethod]
    public async Task ExistingPartialSelection_DoesNotDiscardRemainingChaptersFromWholeMangaImport()
    {
        WritePausedTask(["https://18comic.vip/photo/456"]);
        CreatePage(schedule: true);
        _page.BatchImport.InputText = "123";
        await _page.BatchImport.PreviewAsync();

        await _page.BatchImport.SubmitSelectedAsync();

        var receipt = _page.BatchImport.SubmittedItems.Single();
        Assert.AreNotEqual("existing", receipt.TaskId);
        Assert.AreEqual(2, (await _scheduler.GetDownloadsAsync()).Items.Count);
        Assert.IsFalse(receipt.Message.Contains("沿用现有任务", StringComparison.Ordinal));
        _scheduler.SaveQueue();
        using var queue = JsonDocument.Parse(File.ReadAllText(Path.Combine(_libraryRoot, ".comic_state", "task_queue.json")));
        var created = queue.RootElement.EnumerateArray().Single(item => item.GetProperty("task").GetProperty("id").GetString() == receipt.TaskId);
        CollectionAssert.AreEqual(
            new[] { "https://18comic.vip/photo/456", "https://18comic.vip/photo/789" },
            created.GetProperty("request").GetProperty("chapters").EnumerateArray().Select(item => item.GetString()).ToArray());
        Assert.AreEqual(1, _handler.RequestedUris.Count);
    }

    [TestMethod]
    public async Task AlreadyDownloadedManga_SkipsImagesAndSurfacesExistingTaskNotice()
    {
        var mangaRoot = Path.Combine(_libraryRoot, "批量测试漫画");
        Directory.CreateDirectory(Path.Combine(mangaRoot, "001_第1话"));
        Directory.CreateDirectory(Path.Combine(mangaRoot, "002_第2话"));
        File.WriteAllBytes(Path.Combine(mangaRoot, "001_第1话", "001.jpg"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(mangaRoot, "002_第2话", "001.jpg"), [4, 5, 6]);
        File.WriteAllText(Path.Combine(mangaRoot, "元数据.json"),
            """{"manga_title":"批量测试漫画","manga_url":"https://18comic.vip/album/123","downloaded_chapters":[{"order":1,"dir_name":"001_第1话","image_count":1},{"order":2,"dir_name":"002_第2话","image_count":1}]}""");
        CreatePage(schedule: false);
        _page.BatchImport.InputText = "123";
        await _page.BatchImport.PreviewAsync();

        await _page.BatchImport.SubmitSelectedAsync();
        var taskId = _page.BatchImport.SubmittedItems.Single().TaskId;
        var timeout = Stopwatch.StartNew();
        while (!_page.HasDownloadNotice)
        {
            Assert.IsTrue(timeout.Elapsed < TimeSpan.FromSeconds(5), "本地重复下载提示未从任务订阅更新。");
            await Task.Delay(20);
        }

        var task = await _scheduler.GetDownloadAsync(taskId);
        Assert.AreEqual("completed", task.Status);
        Assert.AreEqual(2, task.LocalSkippedChapterCount);
        StringAssert.Contains(_page.DownloadNotice, "已跳过重复下载");
        Assert.IsTrue(_handler.RequestedUris.All(url => new Uri(url).AbsolutePath == "/album"));
        await _page.BatchImport.SubmitSelectedAsync();
        Assert.AreEqual(1, (await _scheduler.GetDownloadsAsync()).Items.Count);
    }

    private void WritePausedTask(List<string> chapters) => AtomicJsonFile.Write(
        Path.Combine(_libraryRoot, ".comic_state", "task_queue.json"),
        new[]
        {
            new
            {
                Request = new DownloadCreateRequest { Url = "123", Chapters = chapters },
                Task = new DownloadTaskDto { Id = "existing", Url = "https://18comic.vip/album/123", Status = "paused" },
                Order = 1,
            },
        });

    private void CreatePage(bool schedule)
    {
        var settings = TestServiceFactory.CreateSettings(Path.Combine(_container, "settings"));
        settings.UpdateDownloadPlan(1, 0, schedule, DateTime.Now.AddHours(3).TimeOfDay);
        _library = TestServiceFactory.CreateLibrary(_libraryRoot);
        var jmComic = TestServiceFactory.CreateOfflineJmComic(_handler);
        _scheduler = TestServiceFactory.CreateScheduler(jmComic, _library, settings);
        _exporter = TestServiceFactory.CreateExporter(_library);
        var backend = TestServiceFactory.CreateClient(
            jmComic, _scheduler, _library, _exporter, TestServiceFactory.CreateReader(_library), settings);
        _page = new DownloadPageViewModel(
            backend, new DownloadEventStream(backend), new ShellViewModel(settings),
            new SearchHistoryService(Path.Combine(_container, "history")), settings,
            NullLogger<DownloadPageViewModel>.Instance, new ImmediateDispatcher());
    }

    private static HttpResponseMessage EncryptedResponse(HttpRequestMessage request, string payload)
    {
        var timestamp = request.Headers.GetValues("tokenparam").Single().Split(',')[0];
        var key = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(timestamp + "185Hcomic3PAPP7R"))).ToLowerInvariant();
        using var aes = Aes.Create();
        aes.Key = Encoding.ASCII.GetBytes(key);
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.PKCS7;
        using var encryptor = aes.CreateEncryptor();
        var plain = Encoding.UTF8.GetBytes(payload);
        var encrypted = Convert.ToBase64String(encryptor.TransformFinalBlock(plain, 0, plain.Length));
        return new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new { code = 200, data = encrypted }), Encoding.UTF8, "application/json"),
        };
    }
}
