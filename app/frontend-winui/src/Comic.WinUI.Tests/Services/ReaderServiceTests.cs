using Comic.WinUI.Services.Native;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Comic.WinUI.Tests.Services;

[TestClass]
public sealed class ReaderServiceTests
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
    public async Task Reader_ListsChaptersAndReadsImages()
    {
        File.WriteAllText(
            Path.Combine(_mangaRoot, "元数据.json"),
            """{"manga_title":"测试漫画","authors":["作者甲"]}""");
        var library = TestServiceFactory.CreateLibrary(_storageRoot);
        var service = TestServiceFactory.CreateReader(library);

        var chapters = await service.GetReaderChaptersAsync(_mangaRoot);
        Assert.AreEqual("测试漫画", chapters.MangaTitle);
        Assert.AreEqual("第一话", chapters.Chapters.Single().Title);
        Assert.AreEqual(1, chapters.Chapters.Single().ImageCount);

        var images = await service.GetChapterImagesAsync(_mangaRoot, "001_第一话");
        Assert.AreEqual(Path.GetFullPath(_imagePath), images.Images.Single());
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 4 }, await service.GetImageBytesAsync(_imagePath));
    }

    [TestMethod]
    public async Task Reader_RecognizesLegacyNamedChapterFolders()
    {
        var legacyRoot = Path.Combine(_storageRoot, "旧版漫画");
        var chapter2 = Path.Combine(legacyRoot, "第2话 开始");
        var chapter10 = Path.Combine(legacyRoot, "第10话 结局");
        Directory.CreateDirectory(chapter2);
        Directory.CreateDirectory(chapter10);
        var coverPath = Path.Combine(legacyRoot, "cover.jpg");
        var chapter2Image = Path.Combine(chapter2, "0001.jpg");
        File.WriteAllBytes(coverPath, [9, 8, 7]);
        File.WriteAllBytes(chapter2Image, [2]);
        File.WriteAllBytes(Path.Combine(chapter10, "0001.png"), [10]);

        var library = TestServiceFactory.CreateLibrary(_storageRoot);
        var service = TestServiceFactory.CreateReader(library);

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

        var library = TestServiceFactory.CreateLibrary(_storageRoot);
        var service = TestServiceFactory.CreateReader(library);

        await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(
            () => service.GetReaderChaptersAsync(outsideManga));
        await Assert.ThrowsExactlyAsync<ArgumentException>(
            () => service.GetChapterImagesAsync(_mangaRoot, "..\\outside"));
    }
}
