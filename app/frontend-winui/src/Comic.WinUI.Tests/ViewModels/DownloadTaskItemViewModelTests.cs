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
}
