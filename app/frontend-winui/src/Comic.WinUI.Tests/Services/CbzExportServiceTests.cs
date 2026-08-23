using System.Diagnostics;
using System.IO.Compression;
using System.Xml.Linq;
using Comic.WinUI.Services.Native;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Comic.WinUI.Tests.Services;

[TestClass]
public sealed class CbzExportServiceTests
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
    public async Task ExportCbz_ReportsCompletionAndWritesComicInfo()
    {
        File.WriteAllText(
            Path.Combine(_mangaRoot, "元数据.json"),
            """{"manga_title":"测试漫画","manga_url":"https://18comic.vip/album/100"}""");
        var library = TestServiceFactory.CreateLibrary(_storageRoot);
        using var service = TestServiceFactory.CreateExporter(library);

        var response = await service.ExportCbzAsync(_mangaRoot);
        Comic.WinUI.Models.ExportCbzProgress progress;
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
    public async Task ExportCbz_UsesMetadataTitleForNumericJmChapterDirectory()
    {
        var jmRoot = Path.Combine(_storageRoot, "200");
        var jmChapter = Path.Combine(jmRoot, "1");
        Directory.CreateDirectory(jmChapter);
        File.WriteAllBytes(Path.Combine(jmChapter, "00001.jpg"), [1, 2, 3]);
        File.WriteAllText(
            Path.Combine(jmRoot, "元数据.json"),
            """{"manga_title":"JM 导出测试","manga_url":"https://18comic.vip/album/200","downloaded_chapters":[{"order":1,"dir_name":"1","title":"第1话 正式标题","image_count":1}]}""");
        var library = TestServiceFactory.CreateLibrary(_storageRoot);
        using var service = TestServiceFactory.CreateExporter(library);

        var response = await service.ExportCbzAsync(jmRoot);
        var progress = await WaitUntilFinishedAsync(service, response.TaskId);

        Assert.AreEqual("completed", progress.Status);
        Assert.AreEqual("第1话 正式标题", progress.CurrentChapter);
        var archivePath = Path.Combine(progress.ExportDir, "1.cbz");
        using var archive = ZipFile.OpenRead(archivePath);
        var comicInfoEntry = archive.GetEntry("ComicInfo.xml");
        Assert.IsNotNull(comicInfoEntry);
        using var reader = new StreamReader(comicInfoEntry.Open());
        var comicInfo = XDocument.Parse(await reader.ReadToEndAsync());
        Assert.AreEqual("第1话 正式标题", comicInfo.Root?.Element("Title")?.Value);
    }

    [TestMethod]
    public async Task CancelExport_StopsMidRunAndKeepsAlreadyWrittenArchives()
    {
        // 章节要足够多,确保观察到"已开始"之后仍有大量剩余工作可以被取消。
        CreateBulkChapters(count: 150, imageBytes: 48 * 1024);
        var library = TestServiceFactory.CreateLibrary(_storageRoot);
        using var service = TestServiceFactory.CreateExporter(library);

        var response = await service.ExportCbzAsync(_mangaRoot);

        // 等到导出确实跑起来(至少完成一章)再取消,避免在启动前就取消导致测试失去意义。
        var timeout = Stopwatch.StartNew();
        Comic.WinUI.Models.ExportCbzProgress progress;
        do
        {
            await Task.Delay(5);
            progress = await service.GetExportProgressAsync(response.TaskId);
            Assert.IsTrue(timeout.Elapsed < TimeSpan.FromSeconds(60), "导出未在 60 秒内开始。");
        }
        while (progress.Status == "running" && progress.CurrentIndex < 1);

        Assert.AreEqual("running", progress.Status, "导出应当还在进行,否则章节量不足以覆盖取消路径。");
        Assert.IsTrue(await service.CancelExportAsync(response.TaskId), "对运行中的任务取消应当成功。");

        timeout.Restart();
        do
        {
            await Task.Delay(10);
            progress = await service.GetExportProgressAsync(response.TaskId);
            Assert.IsTrue(timeout.Elapsed < TimeSpan.FromSeconds(60), "导出取消后未在 60 秒内收尾。");
        }
        while (progress.Status == "running");

        Assert.AreEqual("cancelled", progress.Status);
        Assert.IsTrue(
            progress.CurrentIndex < progress.TotalChapters,
            $"取消应当发生在全部完成之前,实际 {progress.CurrentIndex}/{progress.TotalChapters}。");

        // 已经写完的 CBZ 要保留,半成品 .tmp 要清掉。
        Assert.IsTrue(Directory.Exists(progress.ExportDir));
        Assert.IsTrue(Directory.GetFiles(progress.ExportDir, "*.cbz").Length > 0, "已完成的章节包应当保留。");
        Assert.AreEqual(0, Directory.GetFiles(progress.ExportDir, "*.tmp").Length, "不应残留 .tmp 半成品。");
    }

    [TestMethod]
    public async Task CancelExport_ReturnsFalseForUnknownTask()
    {
        var library = TestServiceFactory.CreateLibrary(_storageRoot);
        using var service = TestServiceFactory.CreateExporter(library);

        Assert.IsFalse(await service.CancelExportAsync("not-a-real-task"));
    }

    [TestMethod]
    public async Task CancelExport_ReturnsFalseOnceExportFinished()
    {
        var library = TestServiceFactory.CreateLibrary(_storageRoot);
        using var service = TestServiceFactory.CreateExporter(library);

        var response = await service.ExportCbzAsync(_mangaRoot);
        await WaitUntilFinishedAsync(service, response.TaskId);

        Assert.IsFalse(await service.CancelExportAsync(response.TaskId), "已结束的任务不应再被取消。");
    }

    [TestMethod]
    public async Task ExportCbz_PrunesFinishedTaskWhenNextExportStarts()
    {
        // 已结束的任务必须被清掉,否则 _exports 会随会话无界增长。
        var library = TestServiceFactory.CreateLibrary(_storageRoot);
        using var service = TestServiceFactory.CreateExporter(library);

        var first = await service.ExportCbzAsync(_mangaRoot);
        var firstProgress = await WaitUntilFinishedAsync(service, first.TaskId);
        Assert.AreEqual("completed", firstProgress.Status);

        var second = await service.ExportCbzAsync(_mangaRoot);
        await WaitUntilFinishedAsync(service, second.TaskId);

        Assert.ThrowsExactly<KeyNotFoundException>(
            () => service.GetExportProgressAsync(first.TaskId).GetAwaiter().GetResult(),
            "上一个已结束的导出任务应当已被清理。");
    }

    private static async Task<Comic.WinUI.Models.ExportCbzProgress> WaitUntilFinishedAsync(
        CbzExportService service,
        string taskId)
    {
        var timeout = Stopwatch.StartNew();
        Comic.WinUI.Models.ExportCbzProgress progress;
        do
        {
            await Task.Delay(10);
            progress = await service.GetExportProgressAsync(taskId);
            Assert.IsTrue(timeout.Elapsed < TimeSpan.FromSeconds(60), "导出未在 60 秒内结束。");
        }
        while (progress.Status == "running");
        return progress;
    }

    /// <summary>批量造章节。用随机字节让 deflate 无法走捷径,保证导出有可观测的耗时。</summary>
    private void CreateBulkChapters(int count, int imageBytes)
    {
        var random = new Random(20260819);
        var payload = new byte[imageBytes];
        for (var index = 2; index <= count; index++)
        {
            var chapter = Path.Combine(_mangaRoot, $"{index:000}_第{index}话");
            Directory.CreateDirectory(chapter);
            random.NextBytes(payload);
            File.WriteAllBytes(Path.Combine(chapter, "001.jpg"), payload);
        }
    }
}
