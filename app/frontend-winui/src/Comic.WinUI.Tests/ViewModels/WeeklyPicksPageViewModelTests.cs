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
public sealed class WeeklyPicksPageViewModelTests
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
        _handler = new FakeHttpMessageHandler(BuildResponse);
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
    public async Task Initialize_LoadsNewestIssueAndFiltersByOfficialType()
    {
        var viewModel = CreateViewModel();

        await viewModel.InitializeAsync();

        Assert.AreEqual("254", viewModel.SelectedIssue?.Id);
        Assert.AreEqual(2, viewModel.Items.Count);
        Assert.AreEqual("官方推荐 2 部", viewModel.ResultSummary);

        viewModel.SelectedType = viewModel.Types.Single(type => type.Id == "hanman");
        await WaitUntilAsync(() => !viewModel.IsLoading && viewModel.Items.Count == 1);

        Assert.AreEqual(1, viewModel.Items.Count);
        Assert.AreEqual("韩漫作品", viewModel.Items[0].Title);
        Assert.AreEqual("同人", viewModel.Items[0].CategoryDisplay);
    }

    [TestMethod]
    public async Task ContentCategory_FiltersCurrentOfficialResults()
    {
        var viewModel = CreateViewModel();
        await viewModel.InitializeAsync();

        Assert.IsTrue(viewModel.Categories.Any(category => category.Title == "同人"));
        Assert.IsTrue(viewModel.Categories.Any(category => category.Title == "单本"));

        viewModel.SelectedCategory = viewModel.Categories.Single(category => category.Title == "单本");

        Assert.AreEqual(1, viewModel.Items.Count);
        Assert.AreEqual("日漫作品", viewModel.Items[0].Title);
    }

    [TestMethod]
    public async Task ItemCommand_RequestsExistingChapterSelectionFlow()
    {
        var viewModel = CreateViewModel();
        await viewModel.InitializeAsync();
        string? requestedUrl = null;
        viewModel.DownloadMangaRequested += (_, url) => requestedUrl = url;

        viewModel.DownloadMangaCommand.Execute(viewModel.Items[0].Url);

        Assert.AreEqual("https://18comic.vip/album/1001", requestedUrl);
    }

    private WeeklyPicksPageViewModel CreateViewModel()
    {
        var settings = TestServiceFactory.CreateSettings(Path.Combine(_container, "settings"));
        var library = TestServiceFactory.CreateLibrary(Path.Combine(_container, "library"));
        _jmComic = TestServiceFactory.CreateOfflineJmComic(_handler);
        _scheduler = TestServiceFactory.CreateScheduler(_jmComic, library);
        _exporter = TestServiceFactory.CreateExporter(library);
        var reader = TestServiceFactory.CreateReader(library);
        var client = TestServiceFactory.CreateClient(_jmComic, _scheduler, library, _exporter, reader, settings);
        return new WeeklyPicksPageViewModel(client, new ImmediateDispatcher());
    }

    private static HttpResponseMessage BuildResponse(HttpRequestMessage request)
    {
        var json = request.RequestUri?.AbsolutePath switch
        {
            "/week" =>
                """
                {
                  "categories": [
                    { "id": "253", "time": "旧一期" },
                    { "id": "254", "time": "最新一期" }
                  ],
                  "type": [
                    { "id": "hanman", "title": "韩漫" },
                    { "id": "manga", "title": "日漫" }
                  ]
                }
                """,
            "/week/filter" =>
                BuildWeeklyItemsJson(ReadQueryValue(request.RequestUri!, "type")),
            _ => throw new AssertFailedException($"未预期的请求：{request.RequestUri}"),
        };
        var tokenParam = request.Headers.GetValues("tokenparam").Single();
        var timestamp = long.Parse(tokenParam.Split(',')[0]);
        var payload = Encoding.UTF8.GetBytes(json);
        var encrypted = EncryptWithSecret(timestamp, "185Hcomic3PAPP7R", payload);
        var envelope = JsonSerializer.Serialize(new { code = 200, data = encrypted });
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(envelope, Encoding.UTF8, "application/json"),
        };
    }

    private static string BuildWeeklyItemsJson(string type)
    {
        var items = type switch
        {
            "hanman" =>
                """[{ "id": "1001", "name": "韩漫作品", "author": ["作者甲"], "category": { "id": "1", "title": "同人" } }]""",
            "manga" =>
                """[{ "id": "1002", "name": "日漫作品", "author": ["作者乙"], "category": { "id": "2", "title": "單本" } }]""",
            _ =>
                """[{ "id": "1001", "name": "韩漫作品", "author": ["作者甲"], "category": { "id": "1", "title": "同人" } }, { "id": "1002", "name": "日漫作品", "author": ["作者乙"], "category": { "id": "2", "title": "單本" } }]""",
        };
        return $$"""{"total":"{{(type.Length == 0 ? 2 : 1)}}","list":{{items}}}""";
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

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (condition()) return;
            await Task.Delay(10);
        }
        Assert.Fail("等待每周必看筛选完成超时。");
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
}
