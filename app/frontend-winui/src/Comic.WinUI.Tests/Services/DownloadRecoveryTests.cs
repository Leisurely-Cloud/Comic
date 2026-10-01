using System.Text.Json;
using Comic.WinUI.Models;
using Comic.WinUI.Services;
using Comic.WinUI.Services.Native;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Comic.WinUI.Tests.Services;

[TestClass]
public sealed class DownloadRecoveryTests
{
    private string _root = null!;
    [TestInitialize] public void Setup() => _root = Path.Combine(Path.GetTempPath(), "ComicRecoveryTests", Guid.NewGuid().ToString("N"));
    [TestCleanup] public void Cleanup() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    [TestMethod]
    public async Task Restart_RestoresRequestsAsPausedAndRetainsFailuresWithoutNetwork()
    {
        var library = TestServiceFactory.CreateLibrary(_root);
        var path = Path.Combine(_root, ".comic_state", "task_queue.json");
        AtomicJsonFile.Write(path, new[]
        {
            new { Request = new DownloadCreateRequest { Url = "https://18comic.vip/album/123", Chapters = ["456"] }, Task = new DownloadTaskDto { Id = "one", Status = "running", MangaTitle = "漫画" }, Order = 1 },
            new { Request = new DownloadCreateRequest { Url = "https://18comic.vip/album/456" }, Task = new DownloadTaskDto { Id = "two", Status = "failed" }, Order = 2 },
        });
        using var client = new HttpClient();
        using (var scheduler = new DownloadSchedulerService(new JmComicService(client), library))
        {
            var tasks = await scheduler.GetDownloadsAsync();
            Assert.AreEqual(2, tasks.Items.Count);
            Assert.AreEqual("two", tasks.Items[0].Id);
            Assert.AreEqual("failed", tasks.Items[0].Status);
            Assert.AreEqual("paused", tasks.Items[1].Status);
            Assert.IsTrue(tasks.Items[1].StatusText.Contains("已恢复"));
            await scheduler.StopDownloadAsync("one");
            Assert.AreEqual("stopped", (await scheduler.GetDownloadAsync("one")).Status);
        }
        using var restored = new DownloadSchedulerService(new JmComicService(client), library);
        Assert.AreEqual("stopped", (await restored.GetDownloadAsync("one")).Status);
        await restored.BatchDeleteDownloadsAsync(["one"]);
        Assert.AreEqual(1, (await restored.GetDownloadsAsync()).Items.Count);
    }

    [TestMethod]
    public async Task Shutdown_PreservesScheduledQueueAndSelectedChapters()
    {
        var settings = TestServiceFactory.CreateSettings(Path.Combine(_root, "settings"));
        settings.UpdateDownloadPlan(1, 0, true, DateTime.Now.AddHours(2).TimeOfDay);
        var library = TestServiceFactory.CreateLibrary(Path.Combine(_root, "library"));
        using var client = new HttpClient();
        string id;
        using (var scheduler = new DownloadSchedulerService(new JmComicService(client), library, settings))
        {
            id = (await scheduler.CreateDownloadAsync(new DownloadCreateRequest { Url = "123", Chapters = ["456"] })).Id;
            scheduler.SaveQueue();
        }
        using var restored = new DownloadSchedulerService(new JmComicService(client), library, settings);
        var task = await restored.GetDownloadAsync(id);
        Assert.AreEqual("paused", task.Status);
        using var queue = JsonDocument.Parse(File.ReadAllText(Path.Combine(library.StorageRoot, ".comic_state", "task_queue.json")));
        Assert.AreEqual("456", queue.RootElement[0].GetProperty("request").GetProperty("chapters")[0].GetString());
    }

    [TestMethod]
    public async Task MalformedQueueItems_DoNotCrashStartupOrPeriodicSaving()
    {
        using var library = TestServiceFactory.CreateLibrary(_root);
        AtomicJsonFile.WriteText(Path.Combine(_root, ".comic_state", "task_queue.json"), """
            [null,{"request":{"url":"123"},"task":{"id":"bad","status":"paused","logs":null}},
             {"request":{"url":"456"},"task":{"id":"valid","status":"paused"}}]
            """);
        using var client = new HttpClient();
        using var scheduler = new DownloadSchedulerService(new JmComicService(client), library);
        var tasks = await scheduler.GetDownloadsAsync();
        Assert.AreEqual(1, tasks.Items.Count);
        Assert.AreEqual("valid", tasks.Items[0].Id);
        scheduler.SaveQueue();
    }

    [TestMethod]
    public void CorruptPrimary_UsesPreviousValidSnapshotAndPreservesDamagedFile()
    {
        var path = Path.Combine(_root, "state.json");
        AtomicJsonFile.Write(path, new[] { "old" });
        AtomicJsonFile.Write(path, new[] { "new" });
        File.WriteAllText(path, "{broken");
        CollectionAssert.AreEqual(new[] { "old" }, AtomicJsonFile.Read<string[]>(path));
        AtomicJsonFile.Write(path, new[] { "recovered" });
        CollectionAssert.AreEqual(new[] { "old" }, AtomicJsonFile.Read<string[]>(path + ".bak"));
        Assert.AreEqual(1, Directory.GetFiles(_root, "*.corrupt-*").Length);
    }

    [TestMethod]
    public async Task AutomaticRecovery_ResumesWaitingTasksButKeepsUserPausedTasks()
    {
        var settings = TestServiceFactory.CreateSettings(Path.Combine(_root, "settings"));
        settings.UpdateResumeDownloadsOnStartup(true);
        settings.UpdateDownloadPlan(1, 0, true, DateTime.Now.AddHours(2).TimeOfDay);
        using var library = TestServiceFactory.CreateLibrary(Path.Combine(_root, "library"));
        AtomicJsonFile.Write(Path.Combine(library.StorageRoot, ".comic_state", "task_queue.json"), new[]
        {
            new { Request = new DownloadCreateRequest { Url = "123" }, Task = new DownloadTaskDto { Id = "waiting", Status = "scheduled" }, Order = 1 },
            new { Request = new DownloadCreateRequest { Url = "456" }, Task = new DownloadTaskDto { Id = "paused", Status = "paused" }, Order = 2 },
        });
        using var handler = FakeHttpMessageHandler.AlwaysFails();
        using var client = new HttpClient(handler);
        using var scheduler = new DownloadSchedulerService(new JmComicService(client), library, settings);
        for (var i = 0; i < 100 && (await scheduler.GetDownloadAsync("waiting")).Status != "scheduled"; i++) await Task.Delay(10);
        Assert.AreEqual("scheduled", (await scheduler.GetDownloadAsync("waiting")).Status);
        Assert.AreEqual("paused", (await scheduler.GetDownloadAsync("paused")).Status);
        Assert.AreEqual(0, handler.RequestedUris.Count);
    }

    [TestMethod]
    public void LocalCompleteness_DoesNotSkipMissingOrEmptyImagesAndMetadataKeepsFavorite()
    {
        using var library = TestServiceFactory.CreateLibrary(_root);
        var manga = Path.Combine(_root, "测试漫画");
        var chapter = Path.Combine(manga, "001_第一话");
        Directory.CreateDirectory(chapter);
        File.WriteAllBytes(Path.Combine(chapter, "001.jpg"), [1]);
        AtomicJsonFile.WriteText(Path.Combine(manga, "元数据.json"), """{"is_favorite":true,"downloaded_chapters":[{"order":1,"dir_name":"001_第一话","image_count":2}]}""");
        using var client = new HttpClient();
        using var scheduler = new DownloadSchedulerService(new JmComicService(client), library);
        Assert.AreEqual(0, scheduler.GetLocalDownloadedChapters(manga).Count);
        File.WriteAllBytes(Path.Combine(chapter, "002.jpg"), []);
        Assert.AreEqual(0, scheduler.GetLocalDownloadedChapters(manga).Count);
        File.WriteAllBytes(Path.Combine(chapter, "002.jpg"), [2]);
        Assert.AreEqual(1, scheduler.GetLocalDownloadedChapters(manga).Count);
        library.SaveLibraryMetadata(new JmMangaInfo("123", "测试漫画", "", [new JmChapter(1, "456", "第一话")], null, "", []), "123", manga, true, []);
        Assert.IsTrue(library.LoadLibraryMetadata(manga)!.IsFavorite);
    }
}
