using System.Text.Json;
using Comic.WinUI.Models;
using Comic.WinUI.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Comic.WinUI.Tests.Services;

[TestClass]
public sealed class ReadingProgressServiceTests
{
    private string _container = null!;

    [TestInitialize]
    public void Initialize()
    {
        _container = Path.Combine(Path.GetTempPath(), "Comic.WinUI.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_container);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_container)) Directory.Delete(_container, true);
    }

    [TestMethod]
    public void Progress_IsPersistedPerMangaAcrossInstances()
    {
        var firstRoot = Path.Combine(_container, "first-manga");
        var secondRoot = Path.Combine(_container, "second-manga");
        var service = new ReadingProgressService(_container);

        service.Save(firstRoot, "chapter-12", 34);
        service.Save(secondRoot, "chapter-3", 7);

        var reloaded = new ReadingProgressService(_container);
        var first = reloaded.Get(firstRoot + Path.DirectorySeparatorChar);
        var second = reloaded.Get(secondRoot);

        Assert.IsNotNull(first);
        Assert.AreEqual("chapter-12", first.ChapterDirectoryName);
        Assert.AreEqual(34, first.PageIndex);
        Assert.IsNotNull(second);
        Assert.AreEqual("chapter-3", second.ChapterDirectoryName);
        Assert.AreEqual(7, second.PageIndex);
    }

    [TestMethod]
    public void InvalidProgressFile_DoesNotBlockNewProgress()
    {
        File.WriteAllText(Path.Combine(_container, "reading-progress.json"), "not-json");
        var service = new ReadingProgressService(_container);
        var root = Path.Combine(_container, "manga");

        Assert.IsNull(service.Get(root));

        service.Save(root, "chapter-1", 2);
        var reloaded = new ReadingProgressService(_container).Get(root);

        Assert.IsNotNull(reloaded);
        Assert.AreEqual(2, reloaded.PageIndex);
    }

    [TestMethod]
    public void Remove_DeletesOnlySelectedMangaProgressAndPersists()
    {
        var firstRoot = Path.Combine(_container, "first-manga");
        var secondRoot = Path.Combine(_container, "second-manga");
        var service = new ReadingProgressService(_container);
        service.Save(firstRoot, "chapter-1", 3);
        service.Save(secondRoot, "chapter-2", 5);

        service.Remove(firstRoot);

        Assert.IsNull(service.Get(firstRoot));
        Assert.IsNotNull(service.Get(secondRoot));
        var reloaded = new ReadingProgressService(_container);
        Assert.IsNull(reloaded.Get(firstRoot));
        Assert.AreEqual(5, reloaded.Get(secondRoot)?.PageIndex);
    }

    [TestMethod]
    public void LegacyEntries_DeriveReadingStatusWithoutRewritingFile()
    {
        var root = Path.Combine(_container, "legacy");
        var json = JsonSerializer.Serialize(new
        {
            Entries = new[] { new ReadingProgressEntry(root, "chapter-3", 9, DateTimeOffset.UtcNow) },
        });
        var path = Path.Combine(_container, "reading-progress.json");
        File.WriteAllText(path, json);

        var service = new ReadingProgressService(_container);

        Assert.AreEqual(ReadingStatus.Reading, service.GetReadingStatus(root));
        Assert.AreEqual(ReadingStatus.ToRead, service.GetReadingStatus(Path.Combine(_container, "new")));
        Assert.AreEqual(9, service.Get(root)?.PageIndex);
        Assert.AreEqual(json, File.ReadAllText(path));
    }

    [TestMethod]
    public void ManualStatuses_PersistWithoutRequiringProgress()
    {
        var root = Path.Combine(_container, "manga");
        var service = new ReadingProgressService(_container);
        Assert.IsTrue(service.SetReadingStatus(root, ReadingStatus.Read));
        Assert.AreEqual(ReadingStatus.Read, new ReadingProgressService(_container).GetReadingStatus(root));
        Assert.IsNull(service.Get(root));

        Assert.IsTrue(service.SetReadingStatus(root + Path.DirectorySeparatorChar, ReadingStatus.Reading));
        Assert.AreEqual(ReadingStatus.Reading, new ReadingProgressService(_container).GetReadingStatus(root));

        service.Remove(root);
        Assert.AreEqual(ReadingStatus.ToRead, new ReadingProgressService(_container).GetReadingStatus(root));
    }

    [TestMethod]
    public void LegacyEntries_WithEmptyFieldsKeepValidProgressDuringMigration()
    {
        var root = Path.Combine(_container, "valid-manga");
        var ignoredRoot = Path.Combine(_container, "empty-chapter");
        File.WriteAllText(Path.Combine(_container, "reading-progress.json"), JsonSerializer.Serialize(new
        {
            Entries = new[]
            {
                new ReadingProgressEntry(root, "chapter-3", 9, DateTimeOffset.UtcNow),
                new ReadingProgressEntry(string.Empty, "chapter-1", 0, DateTimeOffset.UtcNow),
                new ReadingProgressEntry(ignoredRoot, string.Empty, 0, DateTimeOffset.UtcNow),
            },
        }));

        var service = new ReadingProgressService(_container);
        Assert.AreEqual(9, service.Get(root)?.PageIndex);
        Assert.AreEqual(ReadingStatus.Reading, service.GetReadingStatus(root));
        Assert.IsNull(service.Get(ignoredRoot));
        Assert.IsTrue(service.SetReadingStatus(root, ReadingStatus.Read));

        var reloaded = new ReadingProgressService(_container);
        Assert.AreEqual(9, reloaded.Get(root)?.PageIndex);
        Assert.AreEqual(ReadingStatus.Read, reloaded.GetReadingStatus(root));
        Assert.IsNull(reloaded.Get(ignoredRoot));
    }

    [TestMethod]
    public void SavingProgress_SetsReadingAndPreservesExplicitRead()
    {
        var root = Path.Combine(_container, "manga");
        var service = new ReadingProgressService(_container);
        service.Save(root, "chapter-1", 2);
        Assert.AreEqual(ReadingStatus.Reading, service.GetReadingStatus(root));
        Assert.IsTrue(service.SetReadingStatus(root, ReadingStatus.Read));

        service.Save(root, "chapter-2", 4);

        var reloaded = new ReadingProgressService(_container);
        Assert.AreEqual(ReadingStatus.Read, reloaded.GetReadingStatus(root));
        Assert.AreEqual("chapter-2", reloaded.Get(root)?.ChapterDirectoryName);
        Assert.AreEqual(4, reloaded.Get(root)?.PageIndex);
    }

    [TestMethod]
    public void MarkToRead_PreservesPageAndSamePageResumeReturnsToReading()
    {
        var root = Path.Combine(_container, "manga");
        var service = new ReadingProgressService(_container);
        service.Save(root, "chapter-3", 14);
        Assert.IsTrue(service.SetReadingStatus(root, ReadingStatus.ToRead));
        Assert.AreEqual(14, service.Get(root)?.PageIndex);
        service = new ReadingProgressService(_container);
        Assert.AreEqual(ReadingStatus.ToRead, service.GetReadingStatus(root));

        service.Save(root, "chapter-3", 14);

        var reloaded = new ReadingProgressService(_container);
        Assert.AreEqual(ReadingStatus.Reading, reloaded.GetReadingStatus(root));
        Assert.AreEqual(14, reloaded.Get(root)?.PageIndex);
    }

    [TestMethod]
    public void FailedManualStatusSave_ReturnsFalseAndRollsBackInMemory()
    {
        var root = Path.Combine(_container, "manga");
        var service = new ReadingProgressService(_container);
        service.Save(root, "chapter-1", 5);
        var path = Path.Combine(_container, "reading-progress.json");
        File.Move(path, path + ".saved");
        Directory.CreateDirectory(path);

        Assert.IsFalse(service.SetReadingStatus(root, ReadingStatus.Read));

        Assert.AreEqual(ReadingStatus.Reading, service.GetReadingStatus(root));
        Assert.AreEqual(5, service.Get(root)?.PageIndex);
        Assert.AreEqual(0, Directory.GetFiles(_container, "*.tmp").Length);
    }

    [TestMethod]
    [DataRow("null")]
    [DataRow("[null]")]
    [DataRow("[{\"RootDirectory\":\"test\",\"Status\":99}]")]
    public void InvalidStatuses_RecoverFromAcceptedBackup(string invalidStatuses)
    {
        var root = Path.Combine(_container, "manga");
        var service = new ReadingProgressService(_container);
        Assert.IsTrue(service.SetReadingStatus(root, ReadingStatus.Read));
        Assert.IsTrue(service.SetReadingStatus(root, ReadingStatus.Reading));
        File.WriteAllText(Path.Combine(_container, "reading-progress.json"),
            "{\"Entries\":[],\"Statuses\":" + invalidStatuses + "}");

        Assert.AreEqual(ReadingStatus.Read, new ReadingProgressService(_container).GetReadingStatus(root));
    }

    [TestMethod]
    public void ConcurrentUpdates_KeepStatusAndProgressForEveryManga()
    {
        var service = new ReadingProgressService(_container);
        var roots = Enumerable.Range(0, 12).Select(index => Path.Combine(_container, "manga-" + index)).ToArray();
        Parallel.For(0, roots.Length, index =>
        {
            service.Save(roots[index], "chapter-1", index);
            Assert.IsTrue(service.SetReadingStatus(roots[index], ReadingStatus.Read));
            Assert.AreEqual(ReadingStatus.Read, service.GetReadingStatus(roots[index]));
        });

        var reloaded = new ReadingProgressService(_container);
        for (var index = 0; index < roots.Length; index++)
        {
            Assert.AreEqual(index, reloaded.Get(roots[index])?.PageIndex);
            Assert.AreEqual(ReadingStatus.Read, reloaded.GetReadingStatus(roots[index]));
        }
    }
}
