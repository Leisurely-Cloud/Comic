using Comic.WinUI.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Comic.WinUI.Tests.Models;

[TestClass]
public sealed class DownloadHistoryItemTests
{
    [TestMethod]
    public void DisplayProperties_UseChineseStatusAndReadableProgress()
    {
        var item = new DownloadHistoryItem
        {
            MangaTitle = "测试漫画",
            Author = "作者甲",
            SiteKey = SiteCatalog.Key,
            Status = "completed",
            Progress = 100,
            CompletedChapterCount = 12,
            TotalChapterCount = 12,
            DownloadedThisRunChapterCount = 2,
            FinishedAt = "2026-08-17 18:30:00",
        };

        Assert.AreEqual("已完成", item.StatusLabel);
        Assert.AreEqual("已完成 12 / 12 章 · 本次补下载 2 章", item.ChapterProgressText);
        Assert.AreEqual("已完成 12 / 12 章", item.AggregateChapterProgressText);
        Assert.AreEqual("本次补下载 2 章", item.ThisRunProgressText);
        Assert.IsTrue(item.HasThisRunProgress);
        Assert.AreEqual("100%", item.ProgressText);
        Assert.AreEqual("2026-08-17 18:30", item.FinishedAtDisplay);
        Assert.AreEqual(SiteCatalog.DisplayName, item.SiteDisplay);
    }

    [TestMethod]
    public void DisplayProperties_ProvideFallbacksForLegacyHistory()
    {
        var item = new DownloadHistoryItem { Status = "stopped" };

        Assert.AreEqual("未命名漫画", item.DisplayTitle);
        Assert.AreEqual("作者未知", item.AuthorDisplay);
        Assert.AreEqual("已停止", item.StatusLabel);
        Assert.AreEqual("暂无章节统计", item.ChapterProgressText);
        Assert.AreEqual("时间未知", item.FinishedAtDisplay);
    }

    [TestMethod]
    public void IsSelected_NotifiesTheHistorySelectionUi()
    {
        var item = new DownloadHistoryItem();
        var changedProperty = string.Empty;
        item.PropertyChanged += (_, args) => changedProperty = args.PropertyName ?? string.Empty;

        item.IsSelected = true;

        Assert.IsTrue(item.IsSelected);
        Assert.AreEqual(nameof(DownloadHistoryItem.IsSelected), changedProperty);
    }

    [TestMethod]
    public void FailureDetails_ShowChapterReasonsAndEnableRetry()
    {
        var item = new DownloadHistoryItem
        {
            Url = "https://18comic.vip/album/123",
            Status = "partial",
            FailureDetails =
            [
                new DownloadFailureDetail { ChapterId = "65", Title = "第65话", Reason = "没有可下载图片" },
                new DownloadFailureDetail { ChapterId = "69", Title = "第69话", Reason = "请求超时" },
            ],
        };

        Assert.IsTrue(item.HasFailureDetails);
        Assert.IsTrue(item.IsRetryable);
        Assert.IsTrue(item.CanRetry);
        Assert.AreEqual("第65话：没有可下载图片；第69话：请求超时", item.FailureDetailsText);

        item.IsRetrying = true;

        Assert.IsFalse(item.CanRetry);
    }
}
