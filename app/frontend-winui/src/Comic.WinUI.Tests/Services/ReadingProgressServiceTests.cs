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
}
