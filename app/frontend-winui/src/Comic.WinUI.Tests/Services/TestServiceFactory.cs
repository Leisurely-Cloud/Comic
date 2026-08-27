using Comic.WinUI.Services;
using Comic.WinUI.Services.Native;

namespace Comic.WinUI.Tests.Services;

/// <summary>按职责构造被测服务的测试工厂,保持测试与产品代码的依赖关系同步。</summary>
internal static class TestServiceFactory
{
    public static LibraryStorageService CreateLibrary(string storageRoot) => new(storageRoot);

    public static DownloadSchedulerService CreateScheduler(
        JmComicService jmComic,
        LibraryStorageService library,
        ApplicationSettingsService? settings = null) =>
        new(jmComic, library, settings);

    public static ReaderService CreateReader(LibraryStorageService library) => new(library);

    public static CbzExportService CreateExporter(LibraryStorageService library) => new(library);

    public static BackendClient CreateClient(
        JmComicService jmComic,
        DownloadSchedulerService scheduler,
        LibraryStorageService library,
        CbzExportService exporter,
        ReaderService reader,
        ApplicationSettingsService settings,
        IJmCredentialStore? jmCredentials = null,
        ReadingProgressService? readingProgress = null) =>
        new(jmComic, scheduler, library, exporter, reader, settings, jmCredentials, readingProgress);

    public static ApplicationSettingsService CreateSettings(string directory) => new(directory);

    /// <summary>用本地替身传输层构造站点服务,确保测试不会访问真实站点。</summary>
    public static JmComicService CreateOfflineJmComic(HttpMessageHandler handler) =>
        new(new HttpClient(handler));
}
