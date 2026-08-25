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

    [TestMethod]
    public void DownloadConcurrencyAndRetry_ArePersistedAndClamped()
    {
        var settings = new ApplicationSettingsService(_container);
        settings.UpdatePreferences(
            ApplicationSettingsService.SystemTheme,
            ApplicationSettingsService.SelectNone,
            true,
            ApplicationSettingsService.ReaderPaged,
            100,
            20,
            8,
            5);

        Assert.AreEqual(8, settings.DownloadConcurrency);
        Assert.AreEqual(5, settings.ChapterRetryCount);

        settings.UpdatePreferences(
            ApplicationSettingsService.SystemTheme,
            ApplicationSettingsService.SelectNone,
            true,
            ApplicationSettingsService.ReaderPaged,
            100,
            20,
            99,
            0);

        Assert.AreEqual(8, settings.DownloadConcurrency);
        Assert.AreEqual(1, settings.ChapterRetryCount);

        var reloaded = new ApplicationSettingsService(_container);
        Assert.AreEqual(8, reloaded.DownloadConcurrency);
        Assert.AreEqual(1, reloaded.ChapterRetryCount);
    }

    [TestMethod]
    public void DownloadDirectoryLayout_IsPersistedAndInvalidValuesFallBackToOrganized()
    {
        var settings = new ApplicationSettingsService(_container);
        settings.UpdatePreferences(
            ApplicationSettingsService.SystemTheme,
            ApplicationSettingsService.SelectNone,
            true,
            ApplicationSettingsService.ReaderPaged,
            100,
            20,
            3,
            3,
            ApplicationSettingsService.DirectoryLayoutJmCompatible);

        Assert.AreEqual(ApplicationSettingsService.DirectoryLayoutJmCompatible, settings.DownloadDirectoryLayout);
        Assert.AreEqual(
            ApplicationSettingsService.DirectoryLayoutJmCompatible,
            new ApplicationSettingsService(_container).DownloadDirectoryLayout);

        settings.UpdatePreferences(
            ApplicationSettingsService.SystemTheme,
            ApplicationSettingsService.SelectNone,
            true,
            ApplicationSettingsService.ReaderPaged,
            100,
            20,
            3,
            3,
            "unknown-layout");

        Assert.AreEqual(ApplicationSettingsService.DirectoryLayoutOrganized, settings.DownloadDirectoryLayout);
    }

    [TestMethod]
    public void DownloadConcurrencyAndRetry_DefaultToThree()
    {
        var settings = new ApplicationSettingsService(_container);
        Assert.AreEqual(3, settings.DownloadConcurrency);
        Assert.AreEqual(3, settings.ChapterRetryCount);
    }

    [TestMethod]
    public void StripZoom_DefaultsToFortyAndAllowsThirtyPercent()
    {
        var settings = new ApplicationSettingsService(_container);
        Assert.AreEqual(40, settings.DefaultStripZoomPercent);

        settings.UpdatePreferences(
            ApplicationSettingsService.SystemTheme,
            ApplicationSettingsService.SelectNone,
            true,
            ApplicationSettingsService.ReaderStrip,
            30,
            20);
        Assert.AreEqual(30, settings.DefaultStripZoomPercent);

        settings.UpdatePreferences(
            ApplicationSettingsService.SystemTheme,
            ApplicationSettingsService.SelectNone,
            true,
            ApplicationSettingsService.ReaderStrip,
            10,
            20);
        Assert.AreEqual(30, settings.DefaultStripZoomPercent);
    }
}
