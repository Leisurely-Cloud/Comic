using System;
using System.Text.Json;
using Comic.WinUI.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Comic.WinUI.Tests.Models;

[TestClass]
public class SearchModelsTests
{
    [TestMethod]
    public void SearchHistoryEntry_DefaultTimestamp_ShouldBeRecent()
    {
        var before = DateTimeOffset.Now;
        var entry = new SearchHistoryEntry();
        var after = DateTimeOffset.Now;

        Assert.IsTrue(entry.Timestamp >= before);
        Assert.IsTrue(entry.Timestamp <= after);
    }

    [TestMethod]
    public void SearchHistoryEntry_DefaultValues_ShouldBeEmpty()
    {
        var entry = new SearchHistoryEntry();

        Assert.AreEqual(string.Empty, entry.Keyword);
        Assert.AreEqual(string.Empty, entry.SiteKey);
        Assert.AreEqual(string.Empty, entry.SiteName);
        Assert.AreEqual(0, entry.ResultCount);
    }

    [TestMethod]
    public void SearchHistoryEntry_SetProperties_ShouldWork()
    {
        var entry = new SearchHistoryEntry
        {
            Keyword = "test",
            SiteKey = "baozimh",
            SiteName = "包子漫画",
            ResultCount = 10,
            Timestamp = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero)
        };

        Assert.AreEqual("test", entry.Keyword);
        Assert.AreEqual("baozimh", entry.SiteKey);
        Assert.AreEqual("包子漫画", entry.SiteName);
        Assert.AreEqual(10, entry.ResultCount);
    }

    [TestMethod]
    public void SearchHistoryEntry_JsonSerialization_ShouldRoundTrip()
    {
        var entry = new SearchHistoryEntry
        {
            Keyword = "test",
            SiteKey = "baozimh",
            SiteName = "包子漫画",
            ResultCount = 10,
            Timestamp = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero)
        };

        var json = JsonSerializer.Serialize(entry);
        var deserialized = JsonSerializer.Deserialize<SearchHistoryEntry>(json);

        Assert.IsNotNull(deserialized);
        Assert.AreEqual(entry.Keyword, deserialized.Keyword);
        Assert.AreEqual(entry.SiteKey, deserialized.SiteKey);
        Assert.AreEqual(entry.SiteName, deserialized.SiteName);
        Assert.AreEqual(entry.ResultCount, deserialized.ResultCount);
    }

    [TestMethod]
    public void SearchResponse_DefaultValues_ShouldBeEmpty()
    {
        var response = new SearchResponse();

        Assert.IsNotNull(response.Items);
        Assert.AreEqual(0, response.Items.Count);
        Assert.AreEqual(0, response.Total);
    }

    [TestMethod]
    public void SearchResultItem_DefaultValues_ShouldBeEmpty()
    {
        var item = new SearchResultItem();

        Assert.AreEqual(string.Empty, item.Title);
        Assert.AreEqual(string.Empty, item.Url);
        Assert.AreEqual(string.Empty, item.CoverUrl);
        Assert.AreEqual(string.Empty, item.LatestChapter);
        Assert.AreEqual(string.Empty, item.UpdateTime);
    }
}
