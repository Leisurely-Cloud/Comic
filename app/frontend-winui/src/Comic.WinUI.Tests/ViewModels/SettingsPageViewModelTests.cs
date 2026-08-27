using Comic.WinUI.Services;
using Comic.WinUI.Services.Native;
using Comic.WinUI.Tests.Services;
using Comic.WinUI.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Comic.WinUI.Tests.ViewModels;

[TestClass]
public sealed class SettingsPageViewModelTests
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
    public void InitialUpdateState_ShowsGuidanceWithoutEmptyDetailsOrProgress()
    {
        using var httpClient = new HttpClient();
        var settings = TestServiceFactory.CreateSettings(Path.Combine(_container, "settings"));
        var library = TestServiceFactory.CreateLibrary(_storageRoot);
        using var scheduler = TestServiceFactory.CreateScheduler(new JmComicService(httpClient), library);
        using var exporter = TestServiceFactory.CreateExporter(library);
        var backendClient = TestServiceFactory.CreateClient(
            new JmComicService(httpClient), scheduler, library, exporter,
            TestServiceFactory.CreateReader(library), settings);
        var pageViewModel = new SettingsPageViewModel(
            backendClient,
            settings,
            new ShellViewModel(settings),
            new SearchHistoryService(Path.Combine(_container, "history")));

        Assert.IsTrue(pageViewModel.AppUpdateStatus.Contains("尚未检查更新", StringComparison.Ordinal));
        Assert.IsFalse(pageViewModel.HasAppUpdateNotes);
        Assert.IsFalse(pageViewModel.IsDownloadingAppUpdate);
    }

    [TestMethod]
    public async Task SaveAsync_KeepsOtherSettings_WhenStorageRootUpdateFails()
    {
        using var httpClient = new HttpClient();
        var settings = TestServiceFactory.CreateSettings(Path.Combine(_container, "settings"));
        var library = TestServiceFactory.CreateLibrary(_storageRoot);
        using var scheduler = TestServiceFactory.CreateScheduler(new JmComicService(httpClient), library);
        using var exporter = TestServiceFactory.CreateExporter(library);
        var reader = TestServiceFactory.CreateReader(library);
        var backendClient = TestServiceFactory.CreateClient(
            new JmComicService(httpClient),
            scheduler,
            library,
            exporter,
            reader,
            settings);
        var shellViewModel = new ShellViewModel(settings);
        var searchHistory = new SearchHistoryService(Path.Combine(_container, "history"));
        var pageViewModel = new SettingsPageViewModel(backendClient, settings, shellViewModel, searchHistory);

        pageViewModel.StorageRoot = "   "; // 空目录会触发 UpdateSettingsAsync 拒绝
        pageViewModel.SelectedLibraryPageSize = pageViewModel.LibraryPageSizeOptions
            .First(option => option.Key == "10");
        pageViewModel.SelectedDownloadDirectoryLayout = pageViewModel.DownloadDirectoryLayoutOptions
            .First(option => option.Key == ApplicationSettingsService.DirectoryLayoutJmCompatible);

        await pageViewModel.SaveCommand.ExecuteAsync(null);

        // 普通设置已保存,即使目录更新失败。
        Assert.AreEqual(10, settings.LibraryPageSize);
        Assert.AreEqual(ApplicationSettingsService.DirectoryLayoutJmCompatible, settings.DownloadDirectoryLayout);
        Assert.IsTrue(pageViewModel.SettingsError.Contains("下载目录", StringComparison.Ordinal));
        Assert.IsTrue(pageViewModel.SaveStatus.Length > 0);
    }
}
