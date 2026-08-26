using System.Collections.Specialized;
using Comic.WinUI.Models;
using Comic.WinUI.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Comic.WinUI.Tests.ViewModels;

[TestClass]
public sealed class DownloadTaskItemViewModelTests
{
    [TestMethod]
    public void UpdateFrom_FormatsDownloadSpeedAsMegabytesPerSecond()
    {
        var viewModel = DownloadTaskItemViewModel.FromDto(new DownloadTaskDto
        {
            Id = "task-speed",
            DownloadSpeedBytesPerSecond = 2.5 * 1024 * 1024,
        });

        Assert.AreEqual("2.50 MB/s", viewModel.DownloadSpeedText);

        viewModel.UpdateFrom(new DownloadTaskDto { Id = "task-speed" });

        Assert.AreEqual("0.00 MB/s", viewModel.DownloadSpeedText);
    }

    [TestMethod]
    public void UpdateFrom_MapsChapterImageProgress()
    {
        var viewModel = DownloadTaskItemViewModel.FromDto(new DownloadTaskDto
        {
            Id = "task-1",
            Chapters =
            [
                new DownloadChapterProgressDto
                {
                    Id = "chapter-1",
                    Title = "第1话",
                    Status = "running",
                    CompletedImages = 7,
                    TotalImages = 20,
                    Progress = 35,
                },
                new DownloadChapterProgressDto
                {
                    Id = "chapter-2",
                    Title = "第2话",
                    Status = "pending",
                },
            ],
        });

        Assert.IsTrue(viewModel.HasChapters);
        Assert.AreEqual(2, viewModel.Chapters.Count);
        Assert.AreEqual("7/20 页", viewModel.Chapters[0].ImageProgressText);
        Assert.AreEqual("下载中", viewModel.Chapters[0].StatusLabel);
        Assert.AreEqual("35%", viewModel.Chapters[0].ProgressText);
        Assert.AreEqual("等待下载", viewModel.Chapters[1].ImageProgressText);
    }

    [TestMethod]
    public void FailedTask_ExposesRetryAndExactChapterReason()
    {
        var viewModel = DownloadTaskItemViewModel.FromDto(new DownloadTaskDto
        {
            Id = "task-failed",
            Status = "partial",
            TaskError = new ApiError { Code = "download_failed", Message = "完成 1/2 章，失败章节 1 个" },
            Chapters =
            [
                new DownloadChapterProgressDto { Id = "65", Title = "第65话", Status = "failed", Error = "没有可下载图片" },
                new DownloadChapterProgressDto { Id = "66", Title = "第66话", Status = "completed" },
            ],
        });

        Assert.IsTrue(viewModel.CanRetryFailures);
        Assert.IsTrue(viewModel.HasFailureReasonSummary);
        Assert.AreEqual("第65话：没有可下载图片", viewModel.FailureReasonSummary);
    }

    [TestMethod]
    public void UpdateFrom_PreservesLiveChapterItemAndRemovesStaleItems()
    {
        var viewModel = DownloadTaskItemViewModel.FromDto(new DownloadTaskDto
        {
            Id = "task-1",
            Chapters =
            [
                new DownloadChapterProgressDto { Id = "chapter-1", Title = "第1话" },
                new DownloadChapterProgressDto { Id = "chapter-2", Title = "第2话" },
            ],
        });
        var liveItem = viewModel.Chapters[0];

        viewModel.UpdateFrom(new DownloadTaskDto
        {
            Id = "task-1",
            Chapters =
            [
                new DownloadChapterProgressDto
                {
                    Id = "chapter-1",
                    Title = "第1话",
                    Status = "completed",
                    CompletedImages = 20,
                    TotalImages = 20,
                    Progress = 100,
                },
            ],
        });

        Assert.AreEqual(1, viewModel.Chapters.Count);
        Assert.AreSame(liveItem, viewModel.Chapters[0]);
        Assert.AreEqual("已完成", liveItem.StatusLabel);
        Assert.AreEqual("20/20 页", liveItem.ImageProgressText);
        Assert.AreEqual(100, liveItem.Progress);
    }

    [TestMethod]
    public void UpdateFrom_PreservesIndividualChapterSelectionAndBatchVisibility()
    {
        var viewModel = DownloadTaskItemViewModel.FromDto(new DownloadTaskDto
        {
            Id = "task-selection",
            Chapters =
            [
                new DownloadChapterProgressDto { Id = "chapter-1", Title = "第1话" },
                new DownloadChapterProgressDto { Id = "chapter-2", Title = "第2话" },
            ],
        });
        viewModel.IsBatchMode = true;
        viewModel.Chapters[1].IsSelected = true;

        viewModel.UpdateFrom(new DownloadTaskDto
        {
            Id = "task-selection",
            Chapters =
            [
                new DownloadChapterProgressDto { Id = "chapter-1", Title = "第1话", Status = "completed" },
                new DownloadChapterProgressDto { Id = "chapter-2", Title = "第2话", Status = "running" },
            ],
        });

        Assert.AreEqual(1, viewModel.SelectedChapterCount);
        Assert.IsFalse(viewModel.Chapters[0].IsSelected);
        Assert.IsTrue(viewModel.Chapters[1].IsSelected);
        Assert.IsTrue(viewModel.Chapters.All(chapter => chapter.IsBatchMode));

        viewModel.IsBatchMode = false;

        Assert.AreEqual(0, viewModel.SelectedChapterCount);
        Assert.IsTrue(viewModel.Chapters.All(chapter => !chapter.IsBatchMode));
    }

    [TestMethod]
    public void UpdateFrom_AppendsNewLogsWithoutResettingCollection()
    {
        // 轮询是 150ms 一次。原来每次都 Clear() 再重填,绑定的 ListView 会整表重建。
        var viewModel = DownloadTaskItemViewModel.FromDto(new DownloadTaskDto
        {
            Id = "task-logs",
            Logs = [Log("第一条")],
        });
        var firstEntry = viewModel.Logs[0];

        var actions = new List<NotifyCollectionChangedAction>();
        viewModel.Logs.CollectionChanged += (_, e) => actions.Add(e.Action);

        viewModel.UpdateFrom(new DownloadTaskDto
        {
            Id = "task-logs",
            Logs = [Log("第一条"), Log("第二条"), Log("第三条")],
        });

        Assert.AreEqual(3, viewModel.Logs.Count);
        Assert.AreSame(firstEntry, viewModel.Logs[0], "已有日志项不应被重建。");
        Assert.AreEqual("第三条", viewModel.LatestLogMessage);
        CollectionAssert.AreEqual(
            new[] { NotifyCollectionChangedAction.Add, NotifyCollectionChangedAction.Add },
            actions,
            "只应产生两次 Add,不应出现 Reset。");
    }

    [TestMethod]
    public void UpdateFrom_EmitsNothingWhenLogsUnchanged()
    {
        var viewModel = DownloadTaskItemViewModel.FromDto(new DownloadTaskDto
        {
            Id = "task-logs-idle",
            Logs = [Log("唯一一条")],
        });

        var changed = false;
        viewModel.Logs.CollectionChanged += (_, _) => changed = true;

        viewModel.UpdateFrom(new DownloadTaskDto
        {
            Id = "task-logs-idle",
            Logs = [Log("唯一一条")],
        });

        Assert.AreEqual(1, viewModel.Logs.Count);
        Assert.IsFalse(changed, "日志没有新增时不应触发任何集合变更。");
    }

    [TestMethod]
    public void UpdateFrom_RebuildsLogsWhenServerListShrinks()
    {
        var viewModel = DownloadTaskItemViewModel.FromDto(new DownloadTaskDto
        {
            Id = "task-logs-trim",
            Logs = [Log("a"), Log("b"), Log("c")],
        });

        viewModel.UpdateFrom(new DownloadTaskDto
        {
            Id = "task-logs-trim",
            Logs = [Log("z")],
        });

        Assert.AreEqual(1, viewModel.Logs.Count);
        Assert.AreEqual("z", viewModel.Logs[0].Message);
    }

    [TestMethod]
    public void UpdateFrom_ReusesEveryChapterItemWhenOrderChanges()
    {
        // 章节匹配改成了先建字典再查,这里确认复用与顺序语义没变。
        var viewModel = DownloadTaskItemViewModel.FromDto(new DownloadTaskDto
        {
            Id = "task-chapters",
            Chapters =
            [
                new DownloadChapterProgressDto { Id = "c1", Title = "第1话" },
                new DownloadChapterProgressDto { Id = "c2", Title = "第2话" },
                new DownloadChapterProgressDto { Id = "c3", Title = "第3话" },
            ],
        });
        var original = viewModel.Chapters.ToDictionary(chapter => chapter.Id);

        // 服务端顺序变化 + 新增一章 + 删除一章。
        viewModel.UpdateFrom(new DownloadTaskDto
        {
            Id = "task-chapters",
            Chapters =
            [
                new DownloadChapterProgressDto { Id = "c3", Title = "第3话", Status = "completed" },
                new DownloadChapterProgressDto { Id = "c1", Title = "第1话", Status = "running" },
                new DownloadChapterProgressDto { Id = "c4", Title = "第4话" },
            ],
        });

        Assert.AreEqual(3, viewModel.Chapters.Count);
        var byId = viewModel.Chapters.ToDictionary(chapter => chapter.Id);
        Assert.AreSame(original["c1"], byId["c1"]);
        Assert.AreSame(original["c3"], byId["c3"]);
        Assert.IsFalse(byId.ContainsKey("c2"), "服务端已移除的章节应被删掉。");
        Assert.IsTrue(byId.ContainsKey("c4"), "服务端新增的章节应被加入。");
        Assert.AreEqual("已完成", byId["c3"].StatusLabel);
        Assert.AreEqual("下载中", byId["c1"].StatusLabel);
    }

    private static DownloadLogEntry Log(string message) =>
        new() { Time = "00:00:00", Tag = "info", Message = message };
}
