using Comic.WinUI.Services.Native;
using System.Diagnostics;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Comic.WinUI.Tests.Services;

[TestClass]
public sealed class LibraryIndexTests
{
    private string _root = null!;
    [TestInitialize] public void Setup() => _root = Path.Combine(Path.GetTempPath(), "ComicIndexTests", Guid.NewGuid().ToString("N"));
    [TestCleanup] public void Cleanup() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    private string Add(string name)
    {
        var chapter = Path.Combine(_root, name, "001_第一话");
        Directory.CreateDirectory(chapter);
        File.WriteAllBytes(Path.Combine(chapter, "001.jpg"), [1, 2]);
        return Path.GetDirectoryName(chapter)!;
    }

    [TestMethod]
    public void UnchangedLibrary_DoesNotRescanImagesOrRecalculateSize()
    {
        Add("漫画甲"); Add("漫画乙");
        using var library = new LibraryStorageService(_root);
        Assert.AreEqual(2, library.EnumerateLibraryEntries().Count);
        var baseline = library.IndexedEntryBuildCount;
        for (var i = 0; i < 20; i++) Assert.AreEqual(2, library.EnumerateLibraryEntries().Count);
        Assert.AreEqual(baseline, library.IndexedEntryBuildCount);
    }

    [TestMethod]
    public void MetadataChange_RebuildsOnlyOneMangaAndKeepsOtherEntry()
    {
        var manga = Add("漫画甲"); Add("漫画乙");
        using var library = new LibraryStorageService(_root);
        library.EnumerateLibraryEntries();
        var baseline = library.IndexedEntryBuildCount;
        library.ToggleFavorite(manga);
        Assert.IsTrue(library.EnumerateLibraryEntries().Single(item => item.RootDirectory == manga).IsFavorite);
        Assert.AreEqual(baseline + 1, library.IndexedEntryBuildCount);
    }

    [TestMethod]
    public void CancelledRebuild_PreservesInvalidationUntilNextQuery()
    {
        var manga = Add("漫画甲");
        using var index = new LibraryDirectoryIndex(_root);
        index.GetEntries((_, _) => null);
        Assert.AreEqual(1, index.RebuiltEntryCount);
        index.Invalidate(manga);
        using var cancellation = new CancellationTokenSource();
        var attempts = 0;

        Assert.ThrowsExactly<OperationCanceledException>(() => index.GetEntries((_, _) =>
        {
            attempts++;
            cancellation.Cancel();
            return null;
        }, cancellation.Token));

        Assert.AreEqual(1, attempts);
        Assert.AreEqual(1, index.RebuiltEntryCount, "取消的重建不能替换原缓存。");
        index.GetEntries((_, _) => { attempts++; return null; });
        Assert.AreEqual(2, attempts, "下次查询仍应重建已失效的漫画。");
        Assert.AreEqual(2, index.RebuiltEntryCount);
    }

    [TestMethod]
    public void CancelledLibraryQuery_DoesNotBuildIndex()
    {
        Add("漫画甲");
        using var library = new LibraryStorageService(_root);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.ThrowsExactly<OperationCanceledException>(() => library.EnumerateLibraryEntries(cancellation.Token));
        Assert.AreEqual(0, library.IndexedEntryBuildCount);
        Assert.AreEqual(1, library.EnumerateLibraryEntries().Count);
    }

    [TestMethod]
    public async Task ExternalImageResize_UpdatesDiskUsageAndDeletionRemovesEntry()
    {
        var manga = Add("漫画甲");
        using var library = new LibraryStorageService(_root);
        Assert.AreEqual(2L, library.EnumerateLibraryEntries().Single().DiskUsageBytes);
        File.WriteAllBytes(Path.Combine(manga, "001_第一话", "001.jpg"), [1, 2, 3, 4, 5]);
        for (var i = 0; i < 100 && library.EnumerateLibraryEntries().Single().DiskUsageBytes != 5; i++) await Task.Delay(20);
        Assert.AreEqual(5L, library.EnumerateLibraryEntries().Single().DiskUsageBytes);
        Directory.Delete(manga, true);
        Assert.AreEqual(0, library.EnumerateLibraryEntries().Count);
    }

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void ThousandManga_WarmQueriesReuseAllEntries()
    {
        for (var i = 0; i < 1000; i++) Add($"漫画{i:D4}");
        using var library = new LibraryStorageService(_root);
        var timer = Stopwatch.StartNew();
        Assert.AreEqual(1000, library.EnumerateLibraryEntries().Count);
        var cold = timer.Elapsed.TotalMilliseconds;
        var baseline = library.IndexedEntryBuildCount;
        timer.Restart();
        Assert.AreEqual(1000, library.EnumerateLibraryEntries().Count);
        TestContext.WriteLine($"1000 部漫画：首次索引 {cold:N1} ms，重复查询 {timer.Elapsed.TotalMilliseconds:N1} ms。");
        Assert.AreEqual(baseline, library.IndexedEntryBuildCount);
    }
}
