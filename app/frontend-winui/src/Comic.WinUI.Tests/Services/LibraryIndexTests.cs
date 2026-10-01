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
