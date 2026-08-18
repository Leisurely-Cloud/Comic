using Comic.WinUI.Services.Native;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Comic.WinUI.Tests.Services;

[TestClass]
public sealed class LibraryStorageServiceTests
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
    public void Library_ReadsManagedMetadataAndFiltersByAuthor()
    {
        File.WriteAllText(
            Path.Combine(_mangaRoot, "元数据.json"),
            """{"manga_title":"测试漫画","authors":["作者甲","作者乙"],"cover_url":"https://example.test/cover.jpg"}""");
        var service = TestServiceFactory.CreateLibrary(_storageRoot);

        var entries = service.EnumerateLibraryEntries();

        Assert.AreEqual(1, entries.Count);
        Assert.AreEqual("测试漫画", entries.Single().Title);
        Assert.AreEqual("作者甲、作者乙", entries.Single().Author);
        Assert.AreEqual("https://example.test/cover.jpg", entries.Single().CoverUrl);
    }

    [TestMethod]
    public void Library_RecognizesLegacyNamedChapterFolders()
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

        var service = TestServiceFactory.CreateLibrary(_storageRoot);

        var entries = service.EnumerateLibraryEntries();
        var item = entries.Single(entry => entry.Title == "旧版元数据书名");
        Assert.AreEqual("旧作者甲、旧作者乙", item.Author);
        Assert.AreEqual("本地漫画", item.SiteName);
        Assert.AreEqual(Path.GetFullPath(coverPath), item.CoverUrl);
        Assert.AreEqual(2, item.DownloadedChapterCount);
        Assert.AreEqual("第10话 结局", item.LastDownloadedChapterTitle);
    }

    [TestMethod]
    public void SwitchStorageRoot_CreatesTargetDirectoryAndUpdatesRoot()
    {
        var nextStorageRoot = Path.Combine(_container, "next-library");
        var service = TestServiceFactory.CreateLibrary(_storageRoot);

        var resolved = service.SwitchStorageRoot(nextStorageRoot);

        Assert.AreEqual(Path.GetFullPath(nextStorageRoot), resolved);
        Assert.AreEqual(Path.GetFullPath(nextStorageRoot), service.StorageRoot);
        Assert.IsTrue(Directory.Exists(nextStorageRoot));
    }

    [TestMethod]
    public void ToggleFavorite_FlipsStateAndPersists()
    {
        File.WriteAllText(
            Path.Combine(_mangaRoot, "元数据.json"),
            """{"manga_title":"测试漫画"}""");
        var service = TestServiceFactory.CreateLibrary(_storageRoot);

        Assert.IsTrue(service.ToggleFavorite(_mangaRoot));
        Assert.IsFalse(service.ToggleFavorite(_mangaRoot));
        Assert.IsTrue(service.ToggleFavorite(_mangaRoot));

        var reloaded = TestServiceFactory.CreateLibrary(_storageRoot);
        Assert.IsTrue(reloaded.EnumerateLibraryEntries().Single().IsFavorite);
    }
}
