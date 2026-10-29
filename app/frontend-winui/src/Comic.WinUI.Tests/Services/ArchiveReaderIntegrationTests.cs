using System.IO.Compression;
using Comic.WinUI.Services.Native;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Comic.WinUI.Tests.Services;

[TestClass]
public sealed class ArchiveReaderIntegrationTests
{
    private string _container = null!;
    private string _storageRoot = null!;
    private string _archivePath = null!;
    private LibraryStorageService _library = null!;
    private ReaderService _reader = null!;

    [TestInitialize]
    public void Initialize()
    {
        _container = Path.Combine(Path.GetTempPath(), "Comic.WinUI.ArchiveIntegrationTests", Guid.NewGuid().ToString("N"));
        _storageRoot = Path.Combine(_container, "library");
        Directory.CreateDirectory(_container);
        _archivePath = Path.Combine(_container, "outside-library.cbz");
        using (var archive = ZipFile.Open(_archivePath, ZipArchiveMode.Create))
        {
            using var image = archive.CreateEntry("1.png").Open();
            image.Write([1, 2, 3]);
        }
        _library = TestServiceFactory.CreateLibrary(_storageRoot);
        _reader = TestServiceFactory.CreateReader(_library);
    }

    [TestCleanup]
    public void Cleanup()
    {
        _reader.CloseArchive(_archivePath);
        _library.Dispose();
        if (Directory.Exists(_container)) Directory.Delete(_container, true);
    }

    [TestMethod]
    public async Task ExternalArchive_RequiresExplicitOpenBeforeAnyReaderAccess()
    {
        var denied = await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() => _reader.GetReaderChaptersAsync(_archivePath));
        StringAssert.Contains(denied.Message, "请先");
        await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() => _reader.GetChapterImagesAsync(_archivePath, "/"));

        var original = File.ReadAllBytes(_archivePath);
        var chapters = await _reader.OpenArchiveAsync(_archivePath);
        Assert.AreEqual("outside-library", chapters.MangaTitle);
        Assert.AreEqual("/", chapters.Chapters.Single().DirName);
        var images = await _reader.GetChapterImagesAsync(_archivePath, "/");
        Assert.IsTrue(ArchiveReaderService.IsArchiveImageToken(images.Images.Single()));
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, await _reader.GetImageBytesAsync(images.Images.Single()));
        CollectionAssert.AreEqual(original, File.ReadAllBytes(_archivePath));
        Assert.AreEqual(1, Directory.GetFiles(_container).Length, "阅读不应在源目录解压图片。");
    }

    [TestMethod]
    public async Task AuthorizedArchive_DoesNotGrantRawExternalImageAccess()
    {
        await _reader.OpenArchiveAsync(_archivePath);
        var outsideImage = Path.Combine(_container, "unselected.png");
        File.WriteAllBytes(outsideImage, [4, 5]);
        await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() => _reader.GetImageBytesAsync(outsideImage));
        await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() => _reader.GetImageBytesAsync("comic-archive://" + _archivePath + "/1.png"));
    }

    [TestMethod]
    public async Task ClosingArchive_RevokesOldTokensAndReopenCreatesNewSession()
    {
        await _reader.OpenArchiveAsync(_archivePath);
        var oldToken = (await _reader.GetChapterImagesAsync(_archivePath, "/")).Images.Single();
        _reader.CloseArchive(_archivePath);
        await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() => _reader.GetImageBytesAsync(oldToken));
        await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() => _reader.GetReaderChaptersAsync(_archivePath));

        await _reader.OpenArchiveAsync(_archivePath);
        var newToken = (await _reader.GetChapterImagesAsync(_archivePath, "/")).Images.Single();
        Assert.AreNotEqual(oldToken, newToken);
        await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() => _reader.GetImageBytesAsync(oldToken));
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, await _reader.GetImageBytesAsync(newToken));
    }

    [TestMethod]
    public async Task LibraryDirectoryWithZipExtension_ContinuesUsingLibraryReader()
    {
        var mangaRoot = Path.Combine(_storageRoot, "comic.zip");
        var chapter = Path.Combine(mangaRoot, "001_Test");
        Directory.CreateDirectory(chapter);
        File.WriteAllBytes(Path.Combine(chapter, "1.png"), [9]);

        var chapters = await _reader.GetReaderChaptersAsync(mangaRoot);
        Assert.AreEqual("001_Test", chapters.Chapters.Single().DirName);
        var images = await _reader.GetChapterImagesAsync(mangaRoot, chapters.Chapters.Single().DirName);
        Assert.IsFalse(ArchiveReaderService.IsArchiveImageToken(images.Images.Single()));
        CollectionAssert.AreEqual(new byte[] { 9 }, await _reader.GetImageBytesAsync(images.Images.Single()));
    }

    [TestMethod]
    public async Task CancelledArchiveOpen_LeavesFileUnauthorizedAndCanRetry()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => _reader.OpenArchiveAsync(_archivePath, cancellation.Token));
        await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() => _reader.GetReaderChaptersAsync(_archivePath));
        Assert.AreEqual(1, (await _reader.OpenArchiveAsync(_archivePath)).Chapters.Count);
    }
}
