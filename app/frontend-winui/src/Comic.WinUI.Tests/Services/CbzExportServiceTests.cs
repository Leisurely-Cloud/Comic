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
}
