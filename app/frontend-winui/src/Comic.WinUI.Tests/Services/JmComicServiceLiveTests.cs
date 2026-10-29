using System.Security.Cryptography;
using Comic.WinUI.Models;
using Comic.WinUI.Services.Native;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Comic.WinUI.Tests.Services;

[TestClass]
public sealed class JmComicServiceLiveTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [TestCategory("Live")]
    public async Task RankingEndpoint_ReturnsAtLeastOneItem()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("COMIC_RUN_LIVE_TESTS"),
                "1",
                StringComparison.Ordinal))
        {
            Assert.Inconclusive("设置 COMIC_RUN_LIVE_TESTS=1 后才运行联网测试。");
        }

        using var service = new JmComicService(new HttpClient());
        var result = await service
            .GetRankingAsync("最新更新", 1)
            .WaitAsync(TimeSpan.FromSeconds(45));

        Assert.IsTrue(result.Items.Count > 0, "JM 排行榜接口返回了空列表。");
        Assert.IsTrue(result.Items.All(item => !string.IsNullOrWhiteSpace(item.Url)));
    }

    [TestMethod]
    [TestCategory("Live")]
    public async Task PublicSmallChapter_OnlineReadAndSupplementPreserveExistingFiles()
    {
        if (Environment.GetEnvironmentVariable("COMIC_RUN_LIVE_TESTS") != "1")
            Assert.Inconclusive("设置 COMIC_RUN_LIVE_TESTS=1 后才运行联网测试。");

        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var token = timeout.Token;
        using var client = new HttpClient();
        using var service = new JmComicService(client);
        var ranking = await service.GetRankingAsync("最新更新", 1, token);
        JmMangaInfo? manga = null;
        JmChapter? chapter = null;
        IReadOnlyList<JmImageSource>? sources = null;
        foreach (var candidate in ranking.Items.Take(20))
        {
            var detail = await service.GetMangaInfoFromUrlAsync(candidate.Url, token);
            var first = detail.Chapters.FirstOrDefault();
            if (first is null) continue;
            var images = await service.GetChapterImageSourcesAsync(int.Parse(first.Id), token);
            if (images.Count is < 2 or > 24) continue;
            manga = detail;
            chapter = first;
            sources = images;
            break;
        }
        if (manga is null || chapter is null || sources is null)
        {
            Assert.Inconclusive("候选中未找到 2–24 页的小章节，没有下载大章节。");
            return;
        }

        var onlineBytes = await service.FetchChapterImageAsync(sources[0], token);
        Assert.IsTrue(onlineBytes.Length > 0);
        // This fixture has no connection to COMIC_DOWNLOAD_DIR or the user's library.
        var fixture = Path.Combine(Path.GetTempPath(), "Comic.WinUI.LiveTests", Guid.NewGuid().ToString("N"));
        var library = new LibraryStorageService(Path.Combine(fixture, "library"));
        var root = Path.Combine(library.StorageRoot, "public-small-sample");
        Directory.CreateDirectory(root);
        using var scheduler = new DownloadSchedulerService(service, library);
        string? taskId = null;
        var workerStopped = true;
        try
        {
            var downloaded = await service.DownloadChapterAsync(chapter, root, 2, token);
            var directory = Path.Combine(root, downloaded.DirectoryName);
            var files = library.EnumerateImages(directory);
            Assert.AreEqual(sources.Count, files.Count);
            Assert.IsTrue(files.All(path => new FileInfo(path).Length > 0));
            library.SaveLibraryMetadata(manga, $"https://18comic.vip/album/{manga.Id}", root, false, []);
            var retained = files.Skip(1).ToDictionary(path => path, path => SHA256.HashData(File.ReadAllBytes(path)));
            var missing = files[0];
            Assert.IsTrue(Path.GetFullPath(missing).StartsWith(fixture + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
            File.Delete(missing); // Remove only a file just created by this test.

            var request = new DownloadCreateRequest { Url = $"https://18comic.vip/album/{manga.Id}", Chapters = [chapter.Id] };
            var task = await scheduler.CreateDownloadAsync(request, token);
            taskId = task.Id;
            workerStopped = false;
            var repaired = await WaitForTerminalAsync(scheduler, taskId, token);
            Assert.AreEqual("completed", repaired.Status, "公开样本补下载未完成。");
            Assert.AreEqual(0, repaired.LocalSkippedChapterCount);
            Assert.AreEqual(sources.Count, library.EnumerateImages(directory).Count);
            Assert.IsTrue(new FileInfo(missing).Length > 0);
            foreach (var pair in retained)
                CollectionAssert.AreEqual(pair.Value, SHA256.HashData(File.ReadAllBytes(pair.Key)), "补下载改写了已有图片。");
            await StopAndRemoveAsync(scheduler, taskId);
            taskId = null;
            workerStopped = true;

            task = await scheduler.CreateDownloadAsync(request, token);
            taskId = task.Id;
            workerStopped = false;
            var duplicate = await WaitForTerminalAsync(scheduler, taskId, token);
            Assert.AreEqual("completed", duplicate.Status);
            Assert.AreEqual(1, duplicate.LocalSkippedChapterCount);
            Assert.IsFalse(Directory.EnumerateFiles(fixture, "*.part", SearchOption.AllDirectories).Any());
            TestContext.WriteLine($"公开接口验证：在线图片非空，{sources.Count} 页章节首次下载完成，缺失 1 页已补回，已有 {retained.Count} 页 SHA-256 未变，完整章节再次请求已跳过。");
        }
        finally
        {
            if (taskId is not null)
            {
                await StopAndRemoveAsync(scheduler, taskId);
                workerStopped = true;
            }
            scheduler.Dispose();
            // Never remove a fixture while an independent scheduler worker could still write to it.
            if (workerStopped && Directory.Exists(fixture)) Directory.Delete(fixture, true);
        }
    }

    private static async Task<DownloadTaskDto> WaitForTerminalAsync(DownloadSchedulerService scheduler, string id, CancellationToken token)
    {
        while (true)
        {
            var snapshot = await scheduler.GetDownloadAsync(id, token);
            if (DownloadSchedulerService.IsTerminal(snapshot.Status)) return snapshot;
            await Task.Delay(50, token);
        }
    }

    private static async Task StopAndRemoveAsync(DownloadSchedulerService scheduler, string id)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await scheduler.StopDownloadAsync(id, timeout.Token);
        while (true)
        {
            var result = await scheduler.BatchDeleteDownloadsAsync([id], timeout.Token);
            if (result.Deleted.Contains(id)) return; // Deletion requires the worker to have finished.
            await Task.Delay(25, timeout.Token);
        }
    }
}
