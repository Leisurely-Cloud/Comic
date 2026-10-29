using System.Text.Json;
using Comic.WinUI.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Comic.WinUI.Tests.Services;

[TestClass]
public sealed class RecentArchiveServiceTests
{
    private string _root = null!;
    private string _settings = null!;
    private string HistoryPath => Path.Combine(_settings, "recent-archives.json");

    [TestInitialize]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), "ComicRecentArchivesTests", Guid.NewGuid().ToString("N"));
        _settings = Path.Combine(_root, "settings");
        Directory.CreateDirectory(_settings);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    [TestMethod]
    public void RecordOpened_NormalizesDeduplicatesMovesToFrontAndReloads()
    {
        var service = new RecentArchiveService(_settings);
        var first = Path.Combine(_root, "Book.cbz");
        var second = Path.Combine(_root, "其他漫画.ZIP");
        Assert.IsTrue(service.RecordOpened(Path.Combine(_root, "sub", "..", "Book.cbz")));
        var firstOpened = service.GetAll().Single().LastOpenedAtUtc;
        Assert.IsTrue(service.RecordOpened(second));
        Assert.IsTrue(service.RecordOpened(first.ToUpperInvariant()));

        var actual = new RecentArchiveService(_settings).GetAll();
        Assert.AreEqual(2, actual.Count);
        Assert.IsTrue(string.Equals(first, actual[0].FilePath, StringComparison.OrdinalIgnoreCase));
        Assert.AreEqual(second, actual[1].FilePath);
        Assert.IsTrue(actual[0].LastOpenedAtUtc >= firstOpened);
        Assert.AreEqual(TimeSpan.Zero, actual[0].LastOpenedAtUtc.Offset);
        Assert.AreEqual("其他漫画", actual[1].Title);
        Assert.IsFalse(File.ReadAllText(HistoryPath).Contains("Title", StringComparison.Ordinal));
    }

    [TestMethod]
    public void RecordOpened_EvictsOldestAtCapAndGetAllDoesNotExposeMutableCache()
    {
        var service = new RecentArchiveService(_settings);
        for (var index = 0; index < RecentArchiveService.MaxEntries + 3; index++)
            Assert.IsTrue(service.RecordOpened(Path.Combine(_root, $"Book-{index}.cbz")));

        var entries = service.GetAll();
        Assert.AreEqual(RecentArchiveService.MaxEntries, entries.Count);
        Assert.AreEqual("Book-22", entries[0].Title);
        Assert.AreEqual("Book-3", entries[^1].Title);
        ((RecentArchiveEntry[])entries)[0] = new RecentArchiveEntry(Path.Combine(_root, "injected.zip"), DateTimeOffset.UtcNow);
        Assert.AreEqual("Book-22", service.GetAll()[0].Title);
        Assert.AreEqual(RecentArchiveService.MaxEntries, new RecentArchiveService(_settings).GetAll().Count);
    }

    [TestMethod]
    public void MissingArchiveIsRetainedAndHistoryRemovalLeavesFileProgressAndBookmarksUntouched()
    {
        var archive = Path.Combine(_root, "Book.cbz");
        File.WriteAllBytes(archive, [1, 2, 3]);
        var progress = new ReadingProgressService(_settings);
        progress.Save(archive, "/", 4);
        var bookmarks = new ReadingBookmarkService(_settings);
        bookmarks.Save(archive, "/", "根目录", 4, "保留这页");
        var progressBefore = File.ReadAllText(Path.Combine(_settings, "reading-progress.json"));
        var bookmarksBefore = File.ReadAllText(Path.Combine(_settings, "reading-bookmarks.json"));
        var service = new RecentArchiveService(_settings);
        Assert.IsTrue(service.RecordOpened(archive));
        Assert.IsTrue(service.Remove(archive));
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, File.ReadAllBytes(archive));
        Assert.IsTrue(service.RecordOpened(archive));
        File.Delete(archive);

        service = new RecentArchiveService(_settings);
        Assert.AreEqual(archive, service.GetAll().Single().FilePath);
        Assert.IsTrue(service.Remove(archive.ToUpperInvariant()));
        Assert.IsTrue(service.Remove(archive));
        Assert.AreEqual(0, new RecentArchiveService(_settings).GetAll().Count);
        Assert.IsTrue(service.RecordOpened(Path.Combine(_root, "Other.zip")));
        Assert.IsTrue(service.Clear());
        Assert.IsTrue(service.Clear());
        Assert.AreEqual(0, new RecentArchiveService(_settings).GetAll().Count);
        Assert.AreEqual(progressBefore, File.ReadAllText(Path.Combine(_settings, "reading-progress.json")));
        Assert.AreEqual(bookmarksBefore, File.ReadAllText(Path.Combine(_settings, "reading-bookmarks.json")));
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("relative.cbz")]
    [DataRow("C:relative.zip")]
    [DataRow("https://example.invalid/book.cbz")]
    public void RecordOpened_RejectsNonAbsolutePathsWithoutWriting(string? input)
    {
        var service = new RecentArchiveService(_settings);
        Assert.IsFalse(service.RecordOpened(input!));
        Assert.IsFalse(service.Remove(input!));
        Assert.AreEqual(0, service.GetAll().Count);
        Assert.IsFalse(File.Exists(HistoryPath));
    }

    [TestMethod]
    public void RecordOpened_RejectsUnsupportedPathsInvalidNamesAndDirectories()
    {
        var service = new RecentArchiveService(_settings);
        var directory = Path.Combine(_root, "folder.cbz");
        Directory.CreateDirectory(directory);
        foreach (var path in new[]
        {
            Path.Combine(_root, "book.rar"), Path.Combine(_root, "bad?.cbz"),
            Path.Combine(_root, "book.cbz:stream.zip"), Path.Combine(_root, "folder ", "book.zip"), directory,
        })
        {
            Assert.IsFalse(service.RecordOpened(path), path);
        }
        Assert.IsFalse(File.Exists(HistoryPath));
    }

    [TestMethod]
    [DataRow("broken")]
    [DataRow("{}")]
    [DataRow("{\"Entries\":null}")]
    [DataRow("{\"Entries\":[null]}")]
    [DataRow("{\"Entries\":[{\"FilePath\":\"relative.cbz\",\"LastOpenedAtUtc\":\"2026-10-22T00:00:00Z\"}]}")]
    public void Reload_RejectsMalformedDocumentAndUsesValidBackup(string malformed)
    {
        var archive = Path.Combine(_root, "Book.cbz");
        Assert.IsTrue(new RecentArchiveService(_settings).RecordOpened(archive));
        File.Copy(HistoryPath, HistoryPath + ".bak");
        File.WriteAllText(HistoryPath, malformed);

        Assert.AreEqual(archive, new RecentArchiveService(_settings).GetAll().Single().FilePath);
        Assert.AreEqual(malformed, File.ReadAllText(HistoryPath), "读取历史不会重写原件。");
    }

    [TestMethod]
    public void Reload_RejectsMissingTimestampsDuplicatesAndOversizedDocuments()
    {
        var archive = Path.Combine(_root, "Book.cbz");
        foreach (var entries in new[]
        {
            new[] { new RecentArchiveEntry(archive, default) },
            new[] { new RecentArchiveEntry(archive, DateTimeOffset.UtcNow), new RecentArchiveEntry(archive.ToUpperInvariant(), DateTimeOffset.UtcNow) },
            Enumerable.Range(0, RecentArchiveService.MaxEntries + 1)
                .Select(index => new RecentArchiveEntry(Path.Combine(_root, $"{index}.cbz"), DateTimeOffset.UtcNow)).ToArray(),
        })
        {
            File.WriteAllText(HistoryPath, JsonSerializer.Serialize(new RecentArchiveService.RecentArchiveDocument { Entries = entries.ToList() }));
            Assert.AreEqual(0, new RecentArchiveService(_settings).GetAll().Count);
        }
    }

    [TestMethod]
    public void Reload_OrdersAndNormalizesSavedTimestampsWithoutCheckingArchiveExistence()
    {
        var early = new RecentArchiveEntry(Path.Combine(_root, "missing-first.cbz"), new DateTimeOffset(2026, 10, 20, 10, 0, 0, TimeSpan.FromHours(8)));
        var later = new RecentArchiveEntry(Path.Combine(_root, "missing-later.zip"), early.LastOpenedAtUtc.AddHours(1));
        File.WriteAllText(HistoryPath, JsonSerializer.Serialize(new RecentArchiveService.RecentArchiveDocument { Entries = [early, later] }));

        var entries = new RecentArchiveService(_settings).GetAll();
        Assert.AreEqual(later.FilePath, entries[0].FilePath);
        Assert.AreEqual(TimeSpan.Zero, entries[0].LastOpenedAtUtc.Offset);
        Assert.AreEqual(early.LastOpenedAtUtc, entries[1].LastOpenedAtUtc);
    }

    [TestMethod]
    public void FailedInitialWrite_DoesNotAddInMemoryHistory()
    {
        Directory.CreateDirectory(HistoryPath);
        var service = new RecentArchiveService(_settings);
        Assert.IsFalse(service.RecordOpened(Path.Combine(_root, "Book.cbz")));
        Assert.AreEqual(0, service.GetAll().Count);
        Assert.AreEqual(0, Directory.GetFiles(_settings, "*.tmp").Length);
    }

    [TestMethod]
    public void FailedWrites_DoNotReorderRemoveOrClearExistingHistory()
    {
        var archive = Path.Combine(_root, "Book.cbz");
        var service = new RecentArchiveService(_settings);
        Assert.IsTrue(service.RecordOpened(archive));
        var before = service.GetAll().Single();
        using (var locked = new FileStream(HistoryPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.IsFalse(service.RecordOpened(Path.Combine(_root, "Other.zip")));
            Assert.IsFalse(service.Remove(archive));
            Assert.IsFalse(service.Clear());
            Assert.AreEqual(before, service.GetAll().Single());
        }

        Assert.AreEqual(before, new RecentArchiveService(_settings).GetAll().Single());
        Assert.AreEqual(0, Directory.GetFiles(_settings, "*.tmp").Length);
        Assert.IsTrue(service.Clear());
        Assert.AreEqual(0, service.GetAll().Count);
    }
}
