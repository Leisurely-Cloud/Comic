using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Comic.WinUI.Models;
using Comic.WinUI.Services.Native;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Comic.WinUI.Tests.Services;

[TestClass]
public sealed class EmptyChapterTests
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Comic.EmptyChapterTests", Guid.NewGuid().ToString("N"));

    [TestInitialize]
    public void Initialize() => Directory.CreateDirectory(_root);

    [TestCleanup]
    public void Cleanup() => Directory.Delete(_root, true);

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ExplicitlyEmptyImages_ReportsSourceUnavailableWithoutWritingFiles(bool onlineRead)
    {
        var retained = Path.Combine(_root, "existing.jpg");
        File.WriteAllBytes(retained, [9, 8, 7]);
        using var handler = new FakeHttpMessageHandler(request => Envelope(request, """{"id":650,"images":[]}"""));
        using var client = new HttpClient(handler);
        using var service = new JmComicService(client, Options());

        var error = await Assert.ThrowsExactlyAsync<JmChapterUnavailableException>(async () =>
        {
            if (onlineRead) await service.GetChapterImageSourcesAsync(650);
            else await service.DownloadChapterAsync(new JmChapter(65, "650", "第65话"), _root, 1);
        });

        Assert.AreEqual("650", error.ChapterId);
        Assert.IsTrue(error.Message.Contains("源站") && error.Message.Contains("暂无图片"));
        Assert.AreEqual(1, handler.RequestedUris.Count);
        CollectionAssert.AreEqual(new byte[] { 9, 8, 7 }, File.ReadAllBytes(retained));
        Assert.AreEqual(1, Directory.GetFileSystemEntries(_root).Length);
    }

    [TestMethod]
    [DataRow("{}")]
    [DataRow("{\"images\":null}")]
    [DataRow("{\"images\":[\"unsupported.bin\"]}")]
    public async Task InvalidImageMetadata_IsNotClassifiedAsAnExplicitEmptyChapter(string payload)
    {
        using var handler = new FakeHttpMessageHandler(request => Envelope(request, payload));
        using var client = new HttpClient(handler);
        using var service = new JmComicService(client, Options());

        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => service.GetChapterImageSourcesAsync(650));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Scheduler_EmptyChaptersAreNotAutomaticallyRetriedAndOtherChaptersContinue(bool secondAvailable)
    {
        var libraryRoot = Path.Combine(_root, "library");
        Directory.CreateDirectory(libraryRoot);
        var retained = Path.Combine(libraryRoot, "existing.jpg");
        File.WriteAllBytes(retained, [9, 8, 7]);
        var chapterRequests = 0;
        using var handler = new FakeHttpMessageHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/album") return Envelope(request,
                """{"id":123,"name":"中性测试漫画","series":[{"id":"650","name":"第65话","sort":"65"},{"id":"690","name":"第69话","sort":"69"}]}""");
            if (path == "/chapter")
            {
                Interlocked.Increment(ref chapterRequests);
                var available = secondAvailable && request.RequestUri.Query.Contains("id=690");
                return Envelope(request, available ? """{"id":690,"images":["001.jpg"]}""" : """{"images":[]}""");
            }
            if (path == "/chapter_view_template") return new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent("<script>var scramble_id = 220980;</script>") };
            if (path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)) return new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new ByteArrayContent([0xff, 0xd8, 0xff, 0xd9]) };
            throw new AssertFailedException($"Unexpected request: {path}");
        });
        using var client = new HttpClient(handler);
        using var library = TestServiceFactory.CreateLibrary(libraryRoot);
        using var service = new JmComicService(client, Options());
        using var scheduler = new DownloadSchedulerService(service, library);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var created = await scheduler.CreateDownloadAsync(new DownloadCreateRequest { Url = "123", Chapters = ["650", "690"] }, timeout.Token);
        try
        {
            DownloadTaskDto task;
            do
            {
                task = await scheduler.GetDownloadAsync(created.Id, timeout.Token);
                if (!DownloadSchedulerService.IsTerminal(task.Status)) await Task.Delay(20, timeout.Token);
            } while (!DownloadSchedulerService.IsTerminal(task.Status));

            Assert.AreEqual(2, chapterRequests, "A confirmed empty list must not trigger identical automatic retries.");
            Assert.IsTrue(task.Chapters.Single(chapter => chapter.Id == "650").Error.Contains("暂无图片"));
            Assert.AreEqual(secondAvailable ? "completed" : "failed", task.Chapters.Single(chapter => chapter.Id == "690").Status);
            Assert.AreEqual(secondAvailable ? 1 : 0, task.Chapters.Count(chapter => chapter.Status == "completed"));
            Assert.IsFalse(task.Logs.Any(log => log.Message.Contains("准备重试")));
            CollectionAssert.AreEqual(new byte[] { 9, 8, 7 }, File.ReadAllBytes(retained));
        }
        finally
        {
            await scheduler.StopDownloadAsync(created.Id);
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (!(await scheduler.BatchDeleteDownloadsAsync([created.Id], cleanup.Token)).Deleted.Contains(created.Id))
                await Task.Delay(20, cleanup.Token);
        }
    }

    private static JmSiteOptions Options() => new()
    {
        ApiDomains = ["api.fixture.test"],
        ImageDomain = "images.fixture.test",
    };

    private static HttpResponseMessage Envelope(HttpRequestMessage request, string payload)
    {
        var timestamp = request.Headers.GetValues("tokenparam").Single().Split(',')[0];
        var key = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(timestamp + "185Hcomic3PAPP7R"))).ToLowerInvariant();
        using var aes = Aes.Create();
        aes.Key = Encoding.ASCII.GetBytes(key);
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.PKCS7;
        using var encryptor = aes.CreateEncryptor();
        var bytes = Encoding.UTF8.GetBytes(payload);
        var encrypted = Convert.ToBase64String(encryptor.TransformFinalBlock(bytes, 0, bytes.Length));
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new { code = 200, data = encrypted }), Encoding.UTF8, "application/json"),
        };
    }
}
