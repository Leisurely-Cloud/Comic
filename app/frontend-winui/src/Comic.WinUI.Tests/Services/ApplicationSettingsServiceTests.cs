using Comic.WinUI.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Comic.WinUI.Tests.Services;

[TestClass]
public sealed class ApplicationSettingsServiceTests
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
    public void Preferences_ArePersistedAcrossInstances()
    {
        var storageRoot = Path.Combine(_container, "downloads");
        var settings = new ApplicationSettingsService(_container);
        settings.UpdateStorageRoot(storageRoot);
        settings.UpdatePreferences(
            ApplicationSettingsService.DarkTheme,
            ApplicationSettingsService.SelectLatest,
            false,
            ApplicationSettingsService.ReaderStrip,
            140,
            30);

        var reloaded = new ApplicationSettingsService(_container);

        Assert.AreEqual(Path.GetFullPath(storageRoot), reloaded.StorageRoot);
        Assert.AreEqual(ApplicationSettingsService.DarkTheme, reloaded.Theme);
        Assert.AreEqual(ApplicationSettingsService.SelectLatest, reloaded.ChapterSelectionMode);
        Assert.IsFalse(reloaded.ExpandNavigationPane);
        Assert.AreEqual(ApplicationSettingsService.ReaderStrip, reloaded.DefaultReaderMode);
        Assert.AreEqual(140, reloaded.DefaultStripZoomPercent);
        Assert.AreEqual(30, reloaded.LibraryPageSize);
    }
}
