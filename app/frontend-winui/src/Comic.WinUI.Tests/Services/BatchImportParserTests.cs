using Comic.WinUI.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Comic.WinUI.Tests.Services;

[TestClass]
public sealed class BatchImportParserTests
{
    [TestMethod]
    public void SupportedIdentifiers_NormalizeAndDeduplicateByMangaId()
    {
        var result = BatchImportParser.Parse("000123\r\nJM123;https://example.test/album/123/?from=share，jm456 789");

        Assert.AreEqual(5, result.InputCount);
        Assert.AreEqual(2, result.DuplicateCount);
        CollectionAssert.AreEqual(new[] { "123", "456", "789" }, result.Items.Select(item => item.MangaId).ToArray());
        Assert.IsFalse(result.ExceedsLimit);
    }

    [TestMethod]
    [DataRow("0")]
    [DataRow("JM000")]
    [DataRow("-123")]
    [DataRow("１２３")]
    [DataRow("JM")]
    [DataRow("https://example.test/photo/123")]
    [DataRow("https://example.test/album/123/extra")]
    [DataRow("https://user:password@example.test/album/123")]
    [DataRow("file:///album/123")]
    [DataRow("https://example.test/?id=123")]
    [DataRow("1234567890123456789")]
    public void InvalidInput_RemainsVisibleWithAnError(string input)
    {
        var result = BatchImportParser.Parse(input);

        Assert.AreEqual(1, result.Items.Count);
        Assert.IsNull(result.Items[0].MangaId);
        Assert.IsFalse(string.IsNullOrWhiteSpace(result.Items[0].Error));
    }

    [TestMethod]
    public void LimitCountsOriginalEntriesIncludingDuplicates_AndRejectsEntireOversizedBatch()
    {
        var accepted = BatchImportParser.Parse(string.Join('\n', Enumerable.Range(1, 100)));
        var rejected = BatchImportParser.Parse(string.Join('\n', Enumerable.Repeat("123", 101)));

        Assert.IsFalse(accepted.ExceedsLimit);
        Assert.AreEqual(100, accepted.Items.Count);
        Assert.IsTrue(rejected.ExceedsLimit);
        Assert.AreEqual(101, rejected.InputCount);
        Assert.AreEqual(0, rejected.Items.Count);
    }

    [TestMethod]
    public void EmptyInput_HasNoEntries_AndOversizedTextIsRejected()
    {
        Assert.AreEqual(0, BatchImportParser.Parse(" \r\n,； ;").InputCount);
        Assert.IsTrue(BatchImportParser.Parse(new string('1', BatchImportParser.MaximumInputLength + 1)).ExceedsLimit);
    }
}
