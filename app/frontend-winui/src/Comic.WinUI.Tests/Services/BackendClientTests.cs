using Comic.WinUI.Models;
using Comic.WinUI.Services.Native;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Comic.WinUI.Tests.Services;

[TestClass]
public sealed class BackendClientTests
{
    private string _container = null!;
    private string _storageRoot = null!;

    [TestInitialize]
    public void Initialize()
    {
        _container = Path.Combine(Path.GetTempPath(), "Comic.WinUI.Tests", Guid.NewGuid().ToString("N"));
        _storageRoot = Path.Combine(_container, "library");
        Directory.CreateDirectory(_storageRoot);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_container)) Directory.Delete(_container, true);
    }

    [TestMethod]
    public async Task Settings_CanSwitchManagedStorageRoot()
    {
        var nextStorageRoot = Path.Combine(_container, "next-library");
        using var httpClient = new HttpClient();
        var settings = TestServiceFactory.CreateSettings(Path.Combine(_container, "settings"));
        var library = TestServiceFactory.CreateLibrary(_storageRoot);
        using var scheduler = TestServiceFactory.CreateScheduler(new JmComicService(httpClient), library);
        using var exporter = TestServiceFactory.CreateExporter(library);
        var reader = TestServiceFactory.CreateReader(library);
        var client = TestServiceFactory.CreateClient(
            new JmComicService(httpClient),
            scheduler,
            library,
            exporter,
            reader,
            settings);

        var result = await client.UpdateSettingsAsync(
            new SettingsUpdateRequest { StorageRoot = nextStorageRoot });

        Assert.AreEqual(Path.GetFullPath(nextStorageRoot), result.StorageRoot);
        Assert.AreEqual(Path.GetFullPath(nextStorageRoot), library.StorageRoot);
        Assert.IsTrue(Directory.Exists(Path.Combine(nextStorageRoot, ".comic_state")));
        Assert.AreEqual(Path.GetFullPath(nextStorageRoot), settings.StorageRoot);
    }

    [TestMethod]
    public async Task Settings_RejectsEmptyStorageRoot()
    {
        using var httpClient = new HttpClient();
        var settings = TestServiceFactory.CreateSettings(Path.Combine(_container, "settings"));
        var library = TestServiceFactory.CreateLibrary(_storageRoot);
        using var scheduler = TestServiceFactory.CreateScheduler(new JmComicService(httpClient), library);
        using var exporter = TestServiceFactory.CreateExporter(library);
        var reader = TestServiceFactory.CreateReader(library);
        var client = TestServiceFactory.CreateClient(
            new JmComicService(httpClient),
            scheduler,
            library,
            exporter,
            reader,
            settings);

        await Assert.ThrowsExceptionAsync<BackendApiException>(
            () => client.UpdateSettingsAsync(new SettingsUpdateRequest { StorageRoot = "   " }));
    }
}
