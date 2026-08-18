using Comic.WinUI.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Comic.WinUI.Tests.Services;

[TestClass]
public sealed class SearchHistoryServiceTests
{
    private string _directory = null!;

    [TestInitialize]
    public void Initialize()
    {
        _directory = Path.Combine(Path.GetTempPath(), "Comic.WinUI.Tests", Guid.NewGuid().ToString("N"));
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }

    [TestMethod]
    public void Add_DeduplicatesSameKeywordAndCapsAtFifty()
    {
        var service = new SearchHistoryService(_directory);

        service.Add("海贼王", "jmcomic", "禁漫天堂", 5);
        service.Add("海贼王", "jmcomic", "禁漫天堂", 8);
        Assert.AreEqual(1, service.GetAll().Count);
        Assert.AreEqual(8, service.GetAll()[0].ResultCount);

        for (var index = 0; index < 60; index++)
        {
            service.Add($"关键词{index}", "jmcomic", "禁漫天堂");
        }

        Assert.AreEqual(50, service.GetAll().Count);
        Assert.AreEqual("关键词59", service.GetAll()[0].Keyword);
    }

    [TestMethod]
    public void Search_MatchesKeywordsCaseInsensitively()
    {
        var service = new SearchHistoryService(_directory);
        service.Add("One Piece", "jmcomic", "禁漫天堂");
        service.Add("Naruto", "jmcomic", "禁漫天堂");

        var matches = service.Search("one");
        Assert.AreEqual(1, matches.Count);
        Assert.AreEqual("One Piece", matches[0].Keyword);

        var emptyMatches = service.Search("");
        Assert.AreEqual(2, emptyMatches.Count);
    }

    [TestMethod]
    public void RemoveAndClear_PersistToDisk()
    {
        var service = new SearchHistoryService(_directory);
        service.Add("漫画A", "jmcomic", "禁漫天堂");
        service.Add("漫画B", "jmcomic", "禁漫天堂");
        service.Add("漫画C", "jmcomic", "禁漫天堂");

        service.Remove("漫画B", "jmcomic");
        var reopened = new SearchHistoryService(_directory);
        Assert.AreEqual(2, reopened.GetAll().Count);
        Assert.IsFalse(reopened.GetAll().Any(entry => entry.Keyword == "漫画B"));

        service.Clear();
        var cleared = new SearchHistoryService(_directory);
        Assert.AreEqual(0, cleared.GetAll().Count);
    }
}
