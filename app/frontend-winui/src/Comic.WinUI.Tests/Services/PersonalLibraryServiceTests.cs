using Comic.WinUI.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Comic.WinUI.Tests.Services;

[TestClass]
public sealed class PersonalLibraryServiceTests
{
    private string _directory = null!;
    private string Manga => Path.Combine(_directory, "manga");

    [TestInitialize]
    public void Initialize() => Directory.CreateDirectory(_directory = Path.Combine(Path.GetTempPath(), "Comic.WinUI.Tests", Guid.NewGuid().ToString("N")));

    [TestCleanup]
    public void Cleanup() => Directory.Delete(_directory, true);

    [TestMethod]
    public void Bookmarks_UpsertOnePerPageAndPersistSeparately()
    {
        var service = new ReadingBookmarkService(_directory);
        var initial = service.Save(Manga, "chapter1", "第一章", 3, "笔记");
        var updated = service.Save(Manga + Path.DirectorySeparatorChar, "chapter1", "第一章", 3, " 修改 ");
        service.Save(Manga, "chapter2", "第二章", 3, "");
        service.Save(Manga + "-other", "chapter1", "第一章", 3, "另一部");
        Assert.AreEqual(initial.Id, updated.Id);
        var reloaded = new ReadingBookmarkService(_directory).Get(Manga);
        Assert.AreEqual(2, reloaded.Count);
        Assert.IsTrue(reloaded.Any(entry => entry.Note == "修改" && entry.Label == "第一章 · 第 4 页"));
        Assert.IsFalse(Directory.Exists(Manga));
    }

    [TestMethod]
    public void Bookmarks_RemoveCannotDeleteAnotherManga()
    {
        var service = new ReadingBookmarkService(_directory);
        var entry = service.Save(Manga, "chapter", "章", 0, "");
        service.Remove(Manga + "-other", entry.Id);
        Assert.AreEqual(1, service.Get(Manga).Count);
        service.Remove(Manga, entry.Id);
        Assert.AreEqual(0, new ReadingBookmarkService(_directory).Get(Manga).Count);
    }

    [TestMethod]
    public void Bookmarks_InvalidPageAndOversizedNotesAreRejected()
    {
        var service = new ReadingBookmarkService(_directory);
        Assert.ThrowsExactly<ArgumentException>(() => service.Save(Manga, "chapter", "章", -1, ""));
        Assert.ThrowsExactly<ArgumentException>(() => service.Save(Manga, "chapter", "章", 0, new string('a', 2001)));
        Assert.AreEqual(0, service.Get(Manga).Count);
    }

    [TestMethod]
    public void Bookmarks_CorruptPrimaryRecoversBackup()
    {
        var service = new ReadingBookmarkService(_directory);
        service.Save(Manga, "chapter", "章", 0, "旧备注");
        service.Save(Manga, "chapter", "章", 0, "新备注");
        File.WriteAllText(Path.Combine(_directory, "reading-bookmarks.json"), "bad json");
        Assert.AreEqual("旧备注", new ReadingBookmarkService(_directory).Get(Manga).Single().Note);
    }

    [TestMethod]
    public void Bookmarks_FailedWriteDoesNotMutateMemory()
    {
        var service = new ReadingBookmarkService(_directory);
        Directory.CreateDirectory(Path.Combine(_directory, "reading-bookmarks.json"));
        Assert.ThrowsExactly<IOException>(() => service.Save(Manga, "chapter", "章", 0, ""));
        Assert.AreEqual(0, service.Get(Manga).Count);
    }

    [TestMethod]
    public void OnlineIdentity_DoesNotTreatUrlAsLocalPath()
    {
        var service = new ReadingBookmarkService(_directory);
        service.Save("https://example.org/album/123?tracking=1", "5", "第五章", 2, "在线");
        Assert.AreEqual(1, service.Get("https://example.org/album/123/").Count);
        Assert.AreEqual(0, service.Get(Manga).Count);
    }

    [TestMethod]
    public void Shelves_RenamePreservesMembershipAndDeletePreservesTags()
    {
        var service = new LibraryOrganizationService(_directory);
        var shelf = service.SaveShelf(null, "待看");
        service.SetBooks([Manga], [shelf.Id], ["长篇"]);
        service.SaveShelf(shelf.Id, "正在阅读");
        Assert.IsTrue(service.Matches(Manga, shelf.Id, "长篇"));
        Assert.AreEqual("正在阅读", service.GetShelves().Single().Name);
        service.RemoveShelf(shelf.Id);
        var reloaded = new LibraryOrganizationService(_directory);
        Assert.AreEqual(0, reloaded.Get(Manga).ShelfIds.Length);
        CollectionAssert.AreEqual(new[] { "长篇" }, reloaded.Get(Manga).Tags);
        Assert.IsFalse(Directory.Exists(Manga));
    }

    [TestMethod]
    public void Shelves_BatchReplaceNormalizesTagsAndReturnsCopies()
    {
        var service = new LibraryOrganizationService(_directory);
        var shelf = service.SaveShelf(null, "完成");
        service.SetBooks([Manga, Manga + "2", Manga + "/"], [shelf.Id, shelf.Id], [" 科幻 ", "科幻", "ABC", "abc", ""]);
        var book = service.Get(Manga);
        Assert.AreEqual(2, book.Tags.Length);
        book.Tags[0] = "mutation";
        book.ShelfIds[0] = "mutation";
        Assert.IsTrue(service.Matches(Manga, shelf.Id, "科幻"));
        Assert.IsTrue(service.Matches(Manga + "2", shelf.Id, "abc"));
        service.SetBooks([Manga], [], []);
        Assert.IsFalse(service.Matches(Manga, shelf.Id, ""));
        Assert.IsTrue(service.Matches(Manga + "2", shelf.Id, ""));
    }

    [TestMethod]
    public void Shelves_ValidateNamesAndUnknownMembershipBeforeWrite()
    {
        var service = new LibraryOrganizationService(_directory);
        service.SaveShelf(null, "Name");
        Assert.ThrowsExactly<ArgumentException>(() => service.SaveShelf(null, " name "));
        Assert.ThrowsExactly<ArgumentException>(() => service.SaveShelf(null, " "));
        Assert.ThrowsExactly<ArgumentException>(() => service.SetBooks([Manga], ["missing"], []));
        Assert.ThrowsExactly<ArgumentException>(() => service.SetBooks([Manga], [], [new string('a', 41)]));
        Assert.AreEqual(0, service.Get(Manga).Tags.Length);
    }

    [TestMethod]
    public void Shelves_FailedWriteDoesNotAddShelf()
    {
        var service = new LibraryOrganizationService(_directory);
        Directory.CreateDirectory(Path.Combine(_directory, "library-organization.json"));
        Assert.ThrowsExactly<IOException>(() => service.SaveShelf(null, "书架"));
        Assert.AreEqual(0, service.GetShelves().Count);
    }

    [TestMethod]
    public void StructurallyInvalidPersonalData_RecoversValidBackup()
    {
        var bookmarks = new ReadingBookmarkService(_directory);
        bookmarks.Save(Manga, "chapter", "章", 0, "有效");
        bookmarks.Save(Manga, "chapter", "章", 1, "第二页");
        File.WriteAllText(Path.Combine(_directory, "reading-bookmarks.json"), "{\"Entries\":[null]}");
        Assert.AreEqual("有效", new ReadingBookmarkService(_directory).Get(Manga).Single().Note);
        var organization = new LibraryOrganizationService(_directory);
        organization.SaveShelf(null, "有效书架");
        organization.SaveShelf(null, "第二书架");
        File.WriteAllText(Path.Combine(_directory, "library-organization.json"), "{\"Shelves\":null,\"Books\":[]}");
        Assert.AreEqual("有效书架", new LibraryOrganizationService(_directory).GetShelves().Single().Name);
    }
}
