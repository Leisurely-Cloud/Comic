using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Comic.WinUI.Services.Native;
using Comic.WinUI.Tests.Services;
using Comic.WinUI.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Comic.WinUI.Tests.ViewModels;

[TestClass]
public sealed class RankingPageViewModelTests
{
    private string _container = null!;
    private FakeHttpMessageHandler _handler = null!;
    private JmComicService _jmComic = null!;
    private DownloadSchedulerService _scheduler = null!;
    private CbzExportService _exporter = null!;

    [TestInitialize]
    public void Initialize()
    {
        _container = Path.Combine(Path.GetTempPath(), "Comic.WinUI.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_container);
        _handler = new FakeHttpMessageHandler(BuildRankingResponse);
    }

    [TestCleanup]
    public void Cleanup()
    {
        _scheduler?.Dispose();
        _exporter?.Dispose();
        _jmComic?.Dispose();
        _handler?.Dispose();
        if (Directory.Exists(_container)) Directory.Delete(_container, true);
    }

    [TestMethod]
    public async Task Pagination_ShowsTwentyItemsPerPageAndKeepsGlobalRanks()
    {
        var viewModel = CreateViewModel();
        viewModel.SelectedSection = "最多点赞";
        await WaitUntilAsync(() => !viewModel.IsLoading && viewModel.RankingItems.Count == 20);

        Assert.AreEqual(1, viewModel.CurrentPage);
        Assert.AreEqual("1", viewModel.RankingItems[0].RankText);
        Assert.AreEqual("20", viewModel.RankingItems[^1].RankText);
        Assert.IsFalse(viewModel.CanGoPrevious);
        Assert.IsTrue(viewModel.CanGoNext);

        await viewModel.NextPageCommand.ExecuteAsync(null);

        Assert.AreEqual(2, viewModel.CurrentPage);
        Assert.AreEqual(5, viewModel.RankingItems.Count);
        Assert.AreEqual("21", viewModel.RankingItems[0].RankText);
        Assert.AreEqual("25", viewModel.RankingItems[^1].RankText);
        Assert.IsTrue(viewModel.CanGoPrevious);

        // 站点没有总页数，首次进入末页时仍需探测一次下一批；空响应不能切到空白第 3 页。
        await viewModel.NextPageCommand.ExecuteAsync(null);
        Assert.AreEqual(2, viewModel.CurrentPage);
        Assert.AreEqual(5, viewModel.RankingItems.Count);
        Assert.IsFalse(viewModel.CanGoNext);

        await viewModel.PreviousPageCommand.ExecuteAsync(null);
        Assert.AreEqual(1, viewModel.CurrentPage);
        Assert.AreEqual(20, viewModel.RankingItems.Count);
    }

    [TestMethod]
    public async Task ContentCategory_FiltersLoadedRankingAndUsesSimplifiedTitle()
    {
        var viewModel = CreateViewModel();
        viewModel.SelectedSection = "最多点赞";
        await WaitUntilAsync(() => !viewModel.IsLoading && viewModel.RankingItems.Count == 20);

        Assert.IsTrue(viewModel.Categories.Any(category => category.Title == "同人"));
        Assert.IsTrue(viewModel.Categories.Any(category => category.Title == "单本"));
        Assert.AreEqual("同人", viewModel.RankingItems[0].CategoryDisplay);
        Assert.IsTrue(viewModel.RankingItems[0].HasCategory);

        viewModel.SelectedCategory = viewModel.Categories.Single(category => category.Title == "单本");
        await WaitUntilAsync(() => !viewModel.IsLoading && viewModel.RankingItems.Count == 12);

        Assert.IsTrue(viewModel.RankingItems.All(item =>
            int.Parse(item.Title.Split(' ')[^1]) % 2 == 0));
    }

    private RankingPageViewModel CreateViewModel()
    {
        var settings = TestServiceFactory.CreateSettings(Path.Combine(_container, "settings"));
        var library = TestServiceFactory.CreateLibrary(Path.Combine(_container, "library"));
        _jmComic = TestServiceFactory.CreateOfflineJmComic(_handler);
        _scheduler = TestServiceFactory.CreateScheduler(_jmComic, library);
        _exporter = TestServiceFactory.CreateExporter(library);
        var reader = TestServiceFactory.CreateReader(library);
        var client = TestServiceFactory.CreateClient(
            _jmComic,
            _scheduler,
            library,
            _exporter,
            reader,
            settings);
        return new RankingPageViewModel(client, new ImmediateDispatcher());
    }

    private static HttpResponseMessage BuildRankingResponse(HttpRequestMessage request)
    {
        Assert.AreEqual("/search", request.RequestUri?.AbsolutePath);
        var page = ReadQueryValue(request.RequestUri!, "page");
        var items = page == "1"
            ? Enumerable.Range(1, 25).Select(index => new
            {
                id = (1000 + index).ToString(),
                name = $"测试作品 {index}",
                author = new[] { "测试作者" },
                update_at = "1700000000",
                category = new
                {
                    id = index % 2 == 0 ? "2" : "1",
                    title = index % 2 == 0 ? "單本" : "同人",
                },
            }).ToArray()
            : [];
        var payload = JsonSerializer.SerializeToUtf8Bytes(new { content = items });
        var tokenParam = request.Headers.GetValues("tokenparam").Single();
        var timestamp = long.Parse(tokenParam.Split(',')[0]);
        var encrypted = EncryptWithSecret(timestamp, "185Hcomic3PAPP7R", payload);
        var envelope = JsonSerializer.Serialize(new { code = 200, data = encrypted });
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(envelope, Encoding.UTF8, "application/json"),
        };
    }

    private static string ReadQueryValue(Uri uri, string name)
    {
        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            if (parts.Length == 2 && Uri.UnescapeDataString(parts[0]) == name)
                return Uri.UnescapeDataString(parts[1]);
        }
        return string.Empty;
    }

    private static string EncryptWithSecret(long timestamp, string secret, byte[] plaintext)
    {
        var keyText = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(timestamp + secret))).ToLowerInvariant();
        using var aes = Aes.Create();
        aes.Key = Encoding.ASCII.GetBytes(keyText);
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.PKCS7;
        using var encryptor = aes.CreateEncryptor();
        return Convert.ToBase64String(encryptor.TransformFinalBlock(plaintext, 0, plaintext.Length));
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (condition()) return;
            await Task.Delay(10);
        }
        Assert.Fail("等待排行榜加载完成超时。");
    }
}
