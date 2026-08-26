using System.Collections.Generic;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Comic.WinUI.Models;
using Comic.WinUI.Services;
using Comic.WinUI.Services.Native;
using Comic.WinUI.Tests.Services;
using Comic.WinUI.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Comic.WinUI.Tests.ViewModels;

/// <summary>
/// 这些测试之所以能存在,是因为调度器抽成了 IDispatcher。
/// 在那之前 DownloadPageViewModel 在构造函数里调 DispatcherQueue.GetForCurrentThread(),
/// 非 UI 线程返回 null,整个类型在测试里根本构造不出来。
/// </summary>
[TestClass]
public sealed class DownloadPageViewModelTests
{
    private string _container = null!;
    private string _storageRoot = null!;
    private FakeHttpMessageHandler _handler = null!;
    private DownloadSchedulerService _scheduler = null!;
    private CbzExportService _exporter = null!;

    [TestInitialize]
    public void Initialize()
    {
        _container = Path.Combine(Path.GetTempPath(), "Comic.WinUI.Tests", Guid.NewGuid().ToString("N"));
        _storageRoot = Path.Combine(_container, "library");
        Directory.CreateDirectory(_storageRoot);
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
    public void PanelStates_AreMutuallyExclusiveInEveryCombination()
    {
        // 选章面板三层(空状态/内容/解析遮罩)叠在同一个 Grid 格子里靠 Visibility 互斥。
        // 遮罩用的是半透明卡片色,挡不住下面的层,所以任意时刻必须恰好只有一层为真,
        // 否则两层文字会直接叠在一起(这个 bug 真的跑到 UI 上过)。
        var viewModel = CreateViewModel();
        var manga = new MangaResolveResponse { Title = "测试漫画" };

        (MangaResolveResponse? Manga, bool Resolving, string Expected)[] cases =
        [
            (null, false, nameof(viewModel.ShowEmptyState)),
            (null, true, nameof(viewModel.IsResolving)),
            (manga, true, nameof(viewModel.IsResolving)),
            (manga, false, nameof(viewModel.ShowMangaContent)),
        ];

        foreach (var (currentManga, resolving, expected) in cases)
        {
            viewModel.CurrentManga = currentManga;
            viewModel.IsResolving = resolving;

            var visible = new List<string>();
            if (viewModel.ShowEmptyState) visible.Add(nameof(viewModel.ShowEmptyState));
            if (viewModel.ShowMangaContent) visible.Add(nameof(viewModel.ShowMangaContent));
            if (viewModel.IsResolving) visible.Add(nameof(viewModel.IsResolving));

            Assert.AreEqual(
                1,
                visible.Count,
                $"manga={currentManga is not null}, resolving={resolving} 时可见层为 [{string.Join(", ", visible)}]，应当恰好一层。");
            Assert.AreEqual(expected, visible[0]);
        }
    }

    [TestMethod]
    public void PanelStates_NotifyWhenResolvingChanges()
    {
        // 绑定靠 PropertyChanged 刷新。忘记通知的话属性值是对的、界面却不动,
        // 表现和状态算错一模一样。
        var viewModel = CreateViewModel();
        var changed = new List<string>();
        viewModel.PropertyChanged += (_, e) => changed.Add(e.PropertyName ?? string.Empty);

        viewModel.IsResolving = true;

        CollectionAssert.Contains(changed, nameof(viewModel.ShowEmptyState));
        CollectionAssert.Contains(changed, nameof(viewModel.ShowMangaContent));
    }

    [TestMethod]
    public void PanelStates_NotifyWhenCurrentMangaChanges()
    {
        var viewModel = CreateViewModel();
        var changed = new List<string>();
        viewModel.PropertyChanged += (_, e) => changed.Add(e.PropertyName ?? string.Empty);

        viewModel.CurrentManga = new MangaResolveResponse { Title = "测试漫画" };

        CollectionAssert.Contains(changed, nameof(viewModel.ShowEmptyState));
        CollectionAssert.Contains(changed, nameof(viewModel.ShowMangaContent));
    }

    [TestMethod]
    public void Dispose_IsIdempotentAndSafeBeforeAnyWorkStarted()
    {
        // 页面离开时会调 Dispose 取消轮询;没开始过轮询、或重复调用都不应该抛。
        var viewModel = CreateViewModel();

        viewModel.Dispose();
        viewModel.Dispose();
    }

    [TestMethod]
    public void ApplyDownloadNotice_ShowsVisibleMessageWhenLocalFilesAreSkipped()
    {
        var viewModel = CreateViewModel();

        viewModel.ApplyDownloadNotice(new DownloadTaskDto
        {
            MangaTitle = "测试漫画",
            Status = "completed",
            StatusText = "本地已下载，已跳过",
            LocalSkippedChapterCount = 2,
            RequestedChapterCount = 2,
        });

        Assert.IsTrue(viewModel.HasDownloadNotice);
        StringAssert.Contains(viewModel.DownloadNotice, "测试漫画");
        StringAssert.Contains(viewModel.DownloadNotice, "已跳过重复下载");
    }

    [TestMethod]
    public void ApplyDownloadNotice_ShowsHowManyMissingChaptersWillBeDownloaded()
    {
        var viewModel = CreateViewModel();

        viewModel.ApplyDownloadNotice(new DownloadTaskDto
        {
            MangaTitle = "测试漫画",
            Status = "running",
            LocalSkippedChapterCount = 344,
            RequestedChapterCount = 345,
        });

        Assert.IsTrue(viewModel.HasDownloadNotice);
        StringAssert.Contains(viewModel.DownloadNotice, "本地已有 344 章");
        StringAssert.Contains(viewModel.DownloadNotice, "缺少的 1 章");
    }

    [TestMethod]
    public void CommentPagination_UsesTenItemsPerPageAndUpdatesNavigationState()
    {
        var viewModel = CreateViewModel();
        viewModel.CurrentManga = new MangaResolveResponse { Title = "测试漫画" };
        viewModel.CommentTotal = 25;

        Assert.AreEqual("第 1 / 3 页", viewModel.CommentPageSummary);
        Assert.IsFalse(viewModel.CanGoPreviousComments);
        Assert.IsTrue(viewModel.CanGoNextComments);

        viewModel.CommentPage = 3;

        Assert.AreEqual("第 3 / 3 页", viewModel.CommentPageSummary);
        Assert.IsTrue(viewModel.CanGoPreviousComments);
        Assert.IsFalse(viewModel.CanGoNextComments);
    }

    [TestMethod]
    public void MangaDetails_FormatsIdentityStatisticsAndTags()
    {
        var viewModel = CreateViewModel();
        viewModel.CurrentManga = new MangaResolveResponse
        {
            MangaId = "1459797",
            SiteName = "禁漫天堂",
            Author = "测试作者",
            AddedAt = "2026-08-25",
            TotalViews = "123456",
            Likes = "789",
            CommentCount = "173",
            Tags = ["全彩", "恋爱"],
            Description = "漫画简介",
            Chapters = [new MangaChapterDto { Title = "第1话" }],
        };

        Assert.AreEqual("JM 1459797 · 禁漫天堂", viewModel.CurrentMangaIdentityText);
        Assert.AreEqual("作者：测试作者", viewModel.CurrentMangaAuthorText);
        Assert.AreEqual("发布：2026-08-25", viewModel.CurrentMangaAddedAtText);
        Assert.AreEqual("标签：全彩 · 恋爱", viewModel.CurrentMangaTagsText);
        Assert.AreEqual("浏览 123,456  ·  点赞 789  ·  评论 173  ·  章节 1", viewModel.CurrentMangaStatsText);
        Assert.AreEqual("漫画简介", viewModel.CurrentMangaDescription);
    }

    [TestMethod]
    public async Task ResolveDirectUrl_PopulatesSearchResultAndChapterPanelsConsistently()
    {
        _handler.Dispose();
        _handler = new FakeHttpMessageHandler(request =>
        {
            var payload = request.RequestUri?.AbsolutePath switch
            {
                "/album" =>
                    """
                    {
                      "id": "1460139",
                      "name": "Sage's Healing [AI Generated]",
                      "author": [],
                      "series": []
                    }
                    """,
                "/forum" => """{ "total": "0", "list": [] }""",
                _ => throw new AssertFailedException($"未预期的请求：{request.RequestUri}"),
            };
            return BuildEncryptedResponse(request, payload);
        });
        var viewModel = CreateViewModel();

        await viewModel.ResolveDirectUrlAsync(" https://18comic.vip/album/1460139 ");

        Assert.IsNotNull(viewModel.CurrentManga);
        Assert.IsTrue(viewModel.HasSearchResults);
        Assert.AreEqual(1, viewModel.SearchResults.Count);
        Assert.AreEqual("Sage's Healing [AI Generated]", viewModel.SearchResults[0].Title);
        Assert.AreEqual("已定位漫画: Sage's Healing [AI Generated]", viewModel.SearchStatusText);
        Assert.AreEqual("https://18comic.vip/album/1460139", viewModel.SearchKeyword);
        Assert.IsFalse(viewModel.CanLoadMoreSearchResults);
        Assert.IsTrue(viewModel.IsDirectMangaSelection);
        Assert.IsFalse(viewModel.ShowSearchResultsSection);
    }

    private static HttpResponseMessage BuildEncryptedResponse(HttpRequestMessage request, string json)
    {
        var tokenParam = request.Headers.GetValues("tokenparam").Single();
        var timestamp = long.Parse(tokenParam.Split(',')[0]);
        var keyText = Convert.ToHexString(MD5.HashData(
            Encoding.UTF8.GetBytes(timestamp + "185Hcomic3PAPP7R"))).ToLowerInvariant();
        using var aes = Aes.Create();
        aes.Key = Encoding.ASCII.GetBytes(keyText);
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.PKCS7;
        using var encryptor = aes.CreateEncryptor();
        var plaintext = Encoding.UTF8.GetBytes(json);
        var encrypted = Convert.ToBase64String(
            encryptor.TransformFinalBlock(plaintext, 0, plaintext.Length));
        var envelope = JsonSerializer.Serialize(new { code = 200, data = encrypted });
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(envelope, Encoding.UTF8, "application/json"),
        };
    }

    private DownloadPageViewModel CreateViewModel()
    {
        var settings = TestServiceFactory.CreateSettings(Path.Combine(_container, "settings"));
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

        return new DownloadPageViewModel(
            backendClient,
            new DownloadEventStream(backendClient),
            new ShellViewModel(settings),
            new SearchHistoryService(Path.Combine(_container, "history")),
            settings,
            NullLogger<DownloadPageViewModel>.Instance,
            new ImmediateDispatcher());
    }
}
