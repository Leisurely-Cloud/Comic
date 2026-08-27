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
    public void Library_RecognizesMetadataFreeJmDirectoryLayout()
    {
        var jmRoot = Path.Combine(_storageRoot, "123");
        var jmChapter = Path.Combine(jmRoot, "1");
        Directory.CreateDirectory(jmChapter);
        File.WriteAllBytes(Path.Combine(jmChapter, "00001.jpg"), [1]);
        var service = TestServiceFactory.CreateLibrary(_storageRoot);

        var entry = service.EnumerateLibraryEntries().Single(item => item.RootDirectory == jmRoot);

        Assert.AreEqual("JM123", entry.Title);
        Assert.AreEqual("禁漫天堂", entry.SiteName);
        Assert.AreEqual("https://18comic.vip/album/123", entry.MangaUrl);

        Assert.IsTrue(service.ToggleFavorite(jmRoot));
        var favorite = service.EnumerateLibraryEntries().Single(item => item.RootDirectory == jmRoot);
        Assert.AreEqual("JM123", favorite.Title);
        Assert.AreEqual("禁漫天堂", favorite.SiteName);
        Assert.AreEqual("https://18comic.vip/album/123", favorite.MangaUrl);
        Assert.IsTrue(favorite.IsFavorite);
    }

    [TestMethod]
    public void SaveMetadata_PreservesRemoteChapterTitleForNumericDirectories()
    {
        var jmRoot = Path.Combine(_storageRoot, "456");
        var jmChapter = Path.Combine(jmRoot, "1");
        Directory.CreateDirectory(jmChapter);
        File.WriteAllBytes(Path.Combine(jmChapter, "00001.jpg"), [1]);
        var service = TestServiceFactory.CreateLibrary(_storageRoot);
        var manga = new JmMangaInfo(
            "456",
            "数字目录漫画",
            string.Empty,
            [new JmChapter(1, "11", "第1话 正式标题")],
            null,
            string.Empty,
            []);

        service.SaveLibraryMetadata(
            manga,
            "https://18comic.vip/album/456",
            jmRoot,
            completed: true,
            failures: []);

        var metadata = service.LoadLibraryMetadata(jmRoot);
        Assert.IsNotNull(metadata);
        Assert.AreEqual("第1话 正式标题", metadata.DownloadedChapters.Single().Title);
        Assert.AreEqual("第1话 正式标题", metadata.LastDownloadedChapterTitle);
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

        Assert.IsFalse(service.EnumerateLibraryEntries().Single().IsFavorite);
        Assert.IsTrue(service.ToggleFavorite(_mangaRoot));
        Assert.IsTrue(service.EnumerateLibraryEntries().Single().IsFavorite);
        Assert.IsFalse(service.ToggleFavorite(_mangaRoot));
        Assert.IsTrue(service.ToggleFavorite(_mangaRoot));

        var reloaded = TestServiceFactory.CreateLibrary(_storageRoot);
        Assert.IsTrue(reloaded.EnumerateLibraryEntries().Single().IsFavorite);
    }

    [TestMethod]
    public void Library_DeduplicatesSameMangaIdAndPrefersCompletedDirectory()
    {
        File.WriteAllText(
            Path.Combine(_mangaRoot, "元数据.json"),
            """{"manga_title":"测试漫画","manga_url":"https://18comic.vip/album/123","completed":true}""");
        var duplicateRoot = Path.Combine(_storageRoot, "测试漫画 [123]");
        Directory.CreateDirectory(Path.Combine(duplicateRoot, "001_第一话"));
        Directory.CreateDirectory(Path.Combine(duplicateRoot, "002_第二话"));
        File.WriteAllBytes(Path.Combine(duplicateRoot, "001_第一话", "001.jpg"), [1]);
        File.WriteAllBytes(Path.Combine(duplicateRoot, "002_第二话", "001.jpg"), [2]);
        File.WriteAllText(
            Path.Combine(duplicateRoot, "元数据.json"),
            """{"manga_title":"测试漫画","manga_url":"https://18comic.vip/album/123","completed":false}""");
        var service = TestServiceFactory.CreateLibrary(_storageRoot);

        var entry = service.EnumerateLibraryEntries().Single();

        Assert.AreEqual(_mangaRoot, entry.RootDirectory);
        Assert.AreEqual(1, entry.DuplicateDirectoryCount);
    }

    [TestMethod]
    public void DeleteManga_RecyclesPrimaryAndDuplicateDirectoriesAndRefreshesCache()
    {
        File.WriteAllText(
            Path.Combine(_mangaRoot, "元数据.json"),
            """{"manga_title":"测试漫画","manga_url":"https://18comic.vip/album/123"}""");
        var duplicateRoot = Path.Combine(_storageRoot, "测试漫画 [123]");
        var duplicateChapter = Path.Combine(duplicateRoot, "001_第一话");
        Directory.CreateDirectory(duplicateChapter);
        File.WriteAllBytes(Path.Combine(duplicateChapter, "001.jpg"), [1]);
        File.WriteAllText(
            Path.Combine(duplicateRoot, "元数据.json"),
            """{"manga_title":"测试漫画","manga_url":"https://18comic.vip/album/123"}""");
        var jmRoot = Path.Combine(_storageRoot, "123");
        var jmChapter = Path.Combine(jmRoot, "1");
        Directory.CreateDirectory(jmChapter);
        File.WriteAllBytes(Path.Combine(jmChapter, "00001.jpg"), [3]);
        var recycled = new List<string>();
        var service = new LibraryStorageService(
            _storageRoot,
            recycleDirectory: path =>
            {
                recycled.Add(path);
                Directory.Delete(path, true);
            });
        Assert.AreEqual(1, service.EnumerateLibraryEntries().Count);

        var deletedCount = service.DeleteManga(_mangaRoot);

        Assert.AreEqual(3, deletedCount);
        CollectionAssert.AreEquivalent(
            new[] { Path.GetFullPath(_mangaRoot), Path.GetFullPath(duplicateRoot), Path.GetFullPath(jmRoot) },
            recycled);
        Assert.IsFalse(Directory.Exists(_mangaRoot));
        Assert.IsFalse(Directory.Exists(duplicateRoot));
        Assert.IsFalse(Directory.Exists(jmRoot));
        Assert.AreEqual(0, service.EnumerateLibraryEntries().Count);
    }

    [TestMethod]
    public void DeleteManga_RejectsDirectoryOutsideManagedLibrary()
    {
        var outsideRoot = Path.Combine(_container, "outside");
        var outsideChapter = Path.Combine(outsideRoot, "001_第一话");
        Directory.CreateDirectory(outsideChapter);
        File.WriteAllBytes(Path.Combine(outsideChapter, "001.jpg"), [1]);
        var recycleCalled = false;
        var service = new LibraryStorageService(
            _storageRoot,
            recycleDirectory: _ => recycleCalled = true);

        Assert.ThrowsExactly<UnauthorizedAccessException>(() => service.DeleteManga(outsideRoot));
        Assert.IsFalse(recycleCalled);
        Assert.IsTrue(Directory.Exists(outsideRoot));
    }

    [TestMethod]
    public void DuplicateCleanup_RequiresExactPreviewAndPreservesPrimaryDirectory()
    {
        File.WriteAllText(Path.Combine(_mangaRoot, "元数据.json"),
            """{"manga_title":"测试漫画","manga_url":"https://18comic.vip/album/123"}""");
        var duplicateRoot = Path.Combine(_storageRoot, "测试漫画 [123]");
        var duplicateChapter = Path.Combine(duplicateRoot, "001_第一话");
        Directory.CreateDirectory(duplicateChapter);
        File.WriteAllBytes(Path.Combine(duplicateChapter, "001.jpg"), [1, 2]);
        File.WriteAllText(Path.Combine(duplicateRoot, "元数据.json"),
            """{"manga_title":"测试漫画","manga_url":"https://18comic.vip/album/123"}""");
        var recycled = new List<string>();
        var service = new LibraryStorageService(_storageRoot, recycleDirectory: path =>
        {
            recycled.Add(path);
            Directory.Delete(path, true);
        });

        var preview = service.PreviewDuplicateCleanup(_mangaRoot);

        Assert.AreEqual(1, preview.Items.Count);
        Assert.IsGreaterThanOrEqualTo(2, preview.TotalBytes);
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            service.CleanupDuplicateDirectories(_mangaRoot, []));
        Assert.AreEqual(1, service.CleanupDuplicateDirectories(
            _mangaRoot, preview.Items.Select(item => item.Directory).ToList()));
        Assert.IsTrue(Directory.Exists(_mangaRoot));
        Assert.IsFalse(Directory.Exists(duplicateRoot));
        CollectionAssert.AreEqual(new[] { Path.GetFullPath(duplicateRoot) }, recycled);
    }

    [TestMethod]
    public void JmImport_ScansAndCopiesNewMangaWithoutChangingSource()
    {
        var source = Path.Combine(_container, "jm-source");
        var sourceManga = Path.Combine(source, "456");
        Directory.CreateDirectory(Path.Combine(sourceManga, "1"));
        Directory.CreateDirectory(Path.Combine(sourceManga, "2"));
        Directory.CreateDirectory(Path.Combine(source, "无法识别"));
        File.WriteAllBytes(Path.Combine(sourceManga, "1", "00001.jpg"), [1]);
        File.WriteAllBytes(Path.Combine(sourceManga, "2", "00001.jpg"), [2]);
        File.WriteAllBytes(Path.Combine(sourceManga, "2", "00002.jpg"), [3]);
        var service = TestServiceFactory.CreateLibrary(_storageRoot);

        var preview = service.ScanJmImportDirectory(source);

        Assert.AreEqual(1, preview.DetectedMangaCount);
        Assert.AreEqual(1, preview.NewMangaCount);
        Assert.AreEqual(2, preview.ImportableChapterCount);
        Assert.AreEqual(3, preview.ImportableImageCount);
        Assert.AreEqual(1, preview.SkippedDirectoryCount);

        var result = service.ImportJmDirectory(source);
        var target = Path.Combine(_storageRoot, "456");

        Assert.AreEqual(1, result.ImportedMangaCount);
        Assert.AreEqual(2, result.ImportedChapterCount);
        Assert.IsTrue(File.Exists(Path.Combine(target, "1", "00001.jpg")));
        Assert.IsTrue(File.Exists(Path.Combine(target, "2", "00002.jpg")));
        Assert.IsTrue(File.Exists(Path.Combine(sourceManga, "2", "00002.jpg")), "导入不得移动或删除源文件");
        var metadata = service.LoadLibraryMetadata(target);
        Assert.IsNotNull(metadata);
        Assert.AreEqual("https://18comic.vip/album/456", metadata.MangaUrl);
        Assert.AreEqual(2, metadata.DownloadedChapterCount);
    }

    [TestMethod]
    public void JmImport_MergesMissingChaptersAndDoesNotOverwriteConflicts()
    {
        var target = Path.Combine(_storageRoot, "789");
        Directory.CreateDirectory(Path.Combine(target, "1"));
        Directory.CreateDirectory(Path.Combine(target, "3"));
        File.WriteAllBytes(Path.Combine(target, "1", "00001.jpg"), [1]);
        File.WriteAllBytes(Path.Combine(target, "3", "00001.jpg"), [3]);
        File.WriteAllBytes(Path.Combine(target, "3", "00002.jpg"), [3]);

        var source = Path.Combine(_container, "jm-source");
        var sourceManga = Path.Combine(source, "789");
        Directory.CreateDirectory(Path.Combine(sourceManga, "1"));
        Directory.CreateDirectory(Path.Combine(sourceManga, "2"));
        Directory.CreateDirectory(Path.Combine(sourceManga, "3"));
        File.WriteAllBytes(Path.Combine(sourceManga, "1", "00001.jpg"), [1]);
        File.WriteAllBytes(Path.Combine(sourceManga, "2", "00001.jpg"), [2]);
        File.WriteAllBytes(Path.Combine(sourceManga, "3", "00001.jpg"), [9]);
        var service = TestServiceFactory.CreateLibrary(_storageRoot);

        var preview = service.ScanJmImportDirectory(source);
        Assert.AreEqual(1, preview.ExistingMangaCount);
        Assert.AreEqual(1, preview.ImportableChapterCount);
        Assert.AreEqual(1, preview.ExistingChapterCount);
        Assert.AreEqual(1, preview.ConflictChapterCount);

        var result = service.ImportJmDirectory(source);

        Assert.AreEqual(1, result.UpdatedMangaCount);
        Assert.AreEqual(1, result.ImportedChapterCount);
        Assert.IsTrue(File.Exists(Path.Combine(target, "2", "00001.jpg")));
        Assert.AreEqual(2, Directory.GetFiles(Path.Combine(target, "3")).Length, "冲突章节不应被覆盖");
    }

    [TestMethod]
    public void JmImport_CopyFailureRollsBackNewManga()
    {
        var source = Path.Combine(_container, "jm-source");
        var sourceChapter = Path.Combine(source, "999", "1");
        Directory.CreateDirectory(sourceChapter);
        File.WriteAllBytes(Path.Combine(sourceChapter, "00001.jpg"), [1]);
        File.WriteAllBytes(Path.Combine(sourceChapter, "00002.jpg"), [2]);
        var copied = 0;
        var service = new LibraryStorageService(
            _storageRoot,
            copyFile: (from, to, overwrite) =>
            {
                if (++copied == 2) throw new IOException("模拟复制失败");
                File.Copy(from, to, overwrite);
            });

        var result = service.ImportJmDirectory(source);

        Assert.AreEqual(1, result.FailedMangaCount);
        Assert.IsFalse(Directory.Exists(Path.Combine(_storageRoot, "999")));
        Assert.IsFalse(Directory.Exists(Path.Combine(_storageRoot, ".comic_import")));
        Assert.IsTrue(File.Exists(Path.Combine(sourceChapter, "00002.jpg")));
    }
}
