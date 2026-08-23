using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Comic.WinUI.Services;
using Comic.WinUI.Services.Native;
using Comic.WinUI.Tests.Services;
using Comic.WinUI.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Comic.WinUI.Tests.ViewModels;

/// <summary>
/// 分页阅读器的过期回调测试。
/// 这里只驱动「过期」那条分支:token 检查排在 new BitmapImage() 之前,所以过期回调
/// 会提前返回、完全不碰 WinRT,因此不需要 UI 线程。正常分支要构造 BitmapImage,
/// 在单元测试里跑不了,不在这里覆盖。
/// </summary>
[TestClass]
public sealed class ReaderPageViewModelTests
{
    private string _container = null!;
    private string _storageRoot = null!;
    private string _mangaRoot = null!;
    private FakeHttpMessageHandler _handler = null!;
    private DownloadSchedulerService _scheduler = null!;
    private CbzExportService _exporter = null!;

    private const string FirstChapterDir = "001_第一话";
    private const string SecondChapterDir = "002_第二话";

    [TestInitialize]
    public void Initialize()
    {
        _container = Path.Combine(Path.GetTempPath(), "Comic.WinUI.Tests", Guid.NewGuid().ToString("N"));
        _storageRoot = Path.Combine(_container, "library");
        _mangaRoot = Path.Combine(_storageRoot, "测试漫画");

        // 第一章图多一些,才能让恢复的页码 5 落在有效范围内。
        CreateChapter(FirstChapterDir, imageCount: 8);
        CreateChapter(SecondChapterDir, imageCount: 3);
        _handler = FakeHttpMessageHandler.AlwaysFails();
    }

    [TestCleanup]
    public void Cleanup()
    {
        _scheduler?.Dispose();
        _exporter?.Dispose();
        _handler?.Dispose();
        if (Directory.Exists(_container)) Directory.Delete(_container, true);
    }

    [TestMethod]
    public async Task StaleImageCallback_DoesNotWriteOldPageIndexIntoNewChapter()
    {
        var dispatcher = new DeferredDispatcher();
        var progressService = new ReadingProgressService(Path.Combine(_container, "progress"));
        // 预置进度:第一话第 6 页。这样第一章会从 index 5 开始,过期回调带的就是 5,
        // 而不是 0 —— 否则写进新章节也看不出区别。
        progressService.Save(_mangaRoot, FirstChapterDir, 5);

        var viewModel = CreateViewModel(dispatcher, progressService);

        await viewModel.LoadAsync(_mangaRoot);
        Assert.AreEqual(FirstChapterDir, viewModel.SelectedChapter?.DirName);

        // 第一章的图片回调已入队但还没执行。
        await dispatcher.WaitForPendingAsync(1, TimeSpan.FromSeconds(15));

        // 切到第二章:这会取消 _imageCts,上面那个已入队的回调就成了过期回调。
        viewModel.SelectedChapter = viewModel.Chapters
            .Single(chapter => chapter.DirName == SecondChapterDir);

        // 等第二章也走到入队,确保此时 TotalImages 已是第二章的值。
        // 否则 SaveReadingProgress 会因为 TotalImages<=0 提前返回,
        // 那样即使没有 token 检查测试也会「通过」,变成假绿。
        await dispatcher.WaitForPendingAsync(2, TimeSpan.FromSeconds(15));

        dispatcher.FlushFirst(); // 执行第一章那个过期回调

        var saved = progressService.Get(_mangaRoot);
        Assert.IsNotNull(saved);
        Assert.AreEqual(
            FirstChapterDir,
            saved.ChapterDirectoryName,
            "过期回调不应把上一章的页码写进新章节的阅读进度。");
        Assert.AreEqual(5, saved.PageIndex);
    }

    [TestMethod]
    public async Task OnlineStripMode_BuildsLazyItemsWithoutDownloadingAllImages()
    {
        _handler.Dispose();
        var imageRequestCount = 0;
        _handler = new FakeHttpMessageHandler(request =>
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            if (path.EndsWith("/album", StringComparison.OrdinalIgnoreCase))
            {
                return EncryptedApiResponse(
                    request,
                    """{"id":"456","name":"在线条漫测试","series":[{"id":"123","name":"第一话"}]}""");
            }

            if (path.EndsWith("/chapter", StringComparison.OrdinalIgnoreCase))
            {
                return EncryptedApiResponse(
                    request,
                    """{"id":"123","name":"第一话","images":["001.jpg","002.jpg","003.jpg"]}""");
            }

            if (path.EndsWith("/chapter_view_template", StringComparison.OrdinalIgnoreCase))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("<script>var scramble_id = 220980;</script>")
                };
            }

            if (path.Contains("/media/photos/", StringComparison.OrdinalIgnoreCase))
            {
                Interlocked.Increment(ref imageRequestCount);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent([1, 2, 3])
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        var dispatcher = new DeferredDispatcher();
        var progressService = new ReadingProgressService(Path.Combine(_container, "progress"));
        var viewModel = CreateViewModel(dispatcher, progressService, useStripMode: true);

        await viewModel.LoadOnlineAsync("https://18comic.vip/album/456");
        await WaitUntilAsync(() => viewModel.StripImages.Count == 3, TimeSpan.FromSeconds(5));

        Assert.IsTrue(viewModel.CanToggleReaderMode);
        Assert.IsTrue(viewModel.IsStripMode);
        Assert.AreEqual("在线条漫测试", viewModel.MangaTitle);
        Assert.AreEqual(3, viewModel.TotalImages);
        Assert.AreEqual(3, viewModel.SelectedChapter?.ImageCount);
        Assert.AreEqual(3, viewModel.StripImages.Count);
        Assert.IsTrue(viewModel.StripImages.All(item => item.Image is null));
        Assert.AreEqual(0, Volatile.Read(ref imageRequestCount));

        // 模拟第一个虚拟化元素进入可视区，只允许请求这一页，不能顺带拉完整章。
        await viewModel.LoadStripImageAsync(viewModel.StripImages[0]);
        Assert.AreEqual(1, Volatile.Read(ref imageRequestCount));
    }

    private ReaderPageViewModel CreateViewModel(
        IDispatcher dispatcher,
        ReadingProgressService progressService,
        bool useStripMode = false)
    {
        var settings = TestServiceFactory.CreateSettings(Path.Combine(_container, "settings"));
        if (useStripMode)
        {
            settings.UpdatePreferences(
                ApplicationSettingsService.SystemTheme,
                ApplicationSettingsService.SelectNone,
                true,
                ApplicationSettingsService.ReaderStrip,
                ApplicationSettingsService.DefaultStripZoom,
                20);
        }
        var library = TestServiceFactory.CreateLibrary(_storageRoot);
        var jmComic = TestServiceFactory.CreateOfflineJmComic(_handler);
        _scheduler = TestServiceFactory.CreateScheduler(jmComic, library);
        _exporter = TestServiceFactory.CreateExporter(library);
        var reader = TestServiceFactory.CreateReader(library);
        var backendClient = TestServiceFactory.CreateClient(
            jmComic,
            _scheduler,
            library,
            _exporter,
            reader,
            settings);

        return new ReaderPageViewModel(backendClient, settings, progressService, dispatcher);
    }

    private static HttpResponseMessage EncryptedApiResponse(HttpRequestMessage request, string json)
    {
        var timestamp = long.Parse(request.Headers.GetValues("tokenparam").Single().Split(',')[0]);
        var plaintext = Encoding.UTF8.GetBytes(json);
        var keyText = Convert.ToHexString(MD5.HashData(
            Encoding.UTF8.GetBytes(timestamp + "185Hcomic3PAPP7R"))).ToLowerInvariant();
        using var aes = Aes.Create();
        aes.Key = Encoding.ASCII.GetBytes(keyText);
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.PKCS7;
        using var encryptor = aes.CreateEncryptor();
        var encrypted = Convert.ToBase64String(
            encryptor.TransformFinalBlock(plaintext, 0, plaintext.Length));
        var envelope = JsonSerializer.Serialize(new { code = 200, data = encrypted });
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(envelope, Encoding.UTF8, "application/json")
        };
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        while (!condition())
        {
            await Task.Delay(10, cancellation.Token);
        }
    }

    private void CreateChapter(string directoryName, int imageCount)
    {
        var chapter = Path.Combine(_mangaRoot, directoryName);
        Directory.CreateDirectory(chapter);
        for (var index = 1; index <= imageCount; index++)
        {
            File.WriteAllBytes(Path.Combine(chapter, $"{index:000}.jpg"), [1, 2, 3, 4]);
        }
    }
}
