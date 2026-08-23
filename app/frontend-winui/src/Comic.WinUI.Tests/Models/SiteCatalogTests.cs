using Comic.WinUI.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Comic.WinUI.Tests.Models;

[TestClass]
public class SiteCatalogTests
{
    [TestMethod]
    public void Catalog_MapsJmComicIdentity()
    {
        Assert.AreEqual("禁漫天堂", SiteCatalog.GetDisplayName("jmcomic"));
        Assert.AreEqual("jmcomic", SiteCatalog.GetKey("禁漫天堂"));
    }

    [TestMethod]
    public void UnknownSource_DoesNotExposeRemovedSiteNames()
    {
        Assert.AreEqual("未知来源", SiteCatalog.GetDisplayName("removed"));
    }
}
