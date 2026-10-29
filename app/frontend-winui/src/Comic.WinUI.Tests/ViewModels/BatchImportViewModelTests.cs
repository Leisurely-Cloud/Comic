using Comic.WinUI.Models;
using Comic.WinUI.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Comic.WinUI.Tests.ViewModels;

[TestClass]
public sealed class BatchImportViewModelTests
{
    [TestMethod]
    public async Task Preview_ResolvesUniqueValidEntriesWithoutCreatingDownloads()
    {
        var resolved = new List<string>();
        var created = 0;
        using var vm = new BatchImportViewModel(
            (request, _) =>
            {
                resolved.Add(request.Url);
                Assert.AreEqual(SiteCatalog.Key, request.SiteKey);
                return Task.FromResult(Manga(request.Url));
            },
            (_, _) => { created++; return Task.FromResult(Accepted("unused")); });
        vm.InputText = "123 JM123 broken 456";

        Assert.IsFalse(vm.CanSubmit);
        Assert.AreEqual(0, resolved.Count);
        await vm.PreviewAsync();

        CollectionAssert.AreEqual(new[] { "123", "456" }, resolved);
        Assert.AreEqual(0, created);
        Assert.AreEqual(2, vm.Items.Count(item => item.IsResolved));
        Assert.IsTrue(vm.Items.Single(item => item.MangaId is null).StatusText.StartsWith("无效输入"));
        Assert.IsTrue(vm.CanSubmit);
    }

    [TestMethod]
    public async Task OversizedInput_CannotResolveOrSubmit()
    {
        var calls = 0;
        using var vm = new BatchImportViewModel(
            (request, _) => { calls++; return Task.FromResult(Manga(request.Url)); },
            (_, _) => { calls++; return Task.FromResult(Accepted("unused")); });
        vm.InputText = string.Join('\n', Enumerable.Range(1, 101));

        await vm.PreviewAsync();
        await vm.SubmitSelectedAsync();

        Assert.AreEqual(0, calls);
        Assert.IsFalse(vm.CanPreview);
        Assert.IsFalse(vm.CanSubmit);
        StringAssert.Contains(vm.StatusText, "超出限制");
    }

    [TestMethod]
    public async Task Submission_OnlyCreatesSelectedResolvedEntries_WithExistingConfiguration()
    {
        var submitted = new List<DownloadCreateRequest>();
        var accepted = new List<string>();
        using var vm = new BatchImportViewModel(
            (request, _) => request.Url == "789" ? throw new InvalidOperationException("站点异常") : Task.FromResult(Manga(request.Url)),
            (request, _) => { submitted.Add(request); return Task.FromResult(Accepted(request.Url)); },
            task => accepted.Add(task.Id));
        vm.InputText = "123 456 789";
        await vm.PreviewAsync();
        vm.Items[1].IsSelected = false;
        vm.Items[2].IsSelected = true; // A failed row remains ineligible even when set programmatically.

        await vm.SubmitSelectedAsync();

        Assert.AreEqual(1, submitted.Count);
        Assert.AreEqual("https://example.test/album/123", submitted[0].Url);
        Assert.AreEqual(SiteCatalog.Key, submitted[0].SiteKey);
        Assert.AreEqual("winui", submitted[0].Source);
        CollectionAssert.AreEqual(new[] { "https://example.test/photo/123" }, submitted[0].Chapters);
        Assert.AreEqual(1, accepted.Count);
        Assert.AreEqual(1, vm.SubmittedItems.Count);
        Assert.IsFalse(vm.Items[0].CanSelect);
    }

    [TestMethod]
    public async Task PartialFailure_RetryOnlyResubmitsFailedItems()
    {
        var counts = new Dictionary<string, int>();
        using var vm = new BatchImportViewModel(
            (request, _) => Task.FromResult(Manga(request.Url)),
            (request, _) =>
            {
                counts[request.Url] = counts.GetValueOrDefault(request.Url) + 1;
                if (request.Url.EndsWith("456") && counts[request.Url] == 1)
                    throw new InvalidOperationException("暂时失败");
                return Task.FromResult(Accepted(request.Url));
            });
        vm.InputText = "123 456";
        await vm.PreviewAsync();
        await vm.SubmitSelectedAsync();

        Assert.IsTrue(vm.Items[0].IsSubmitted);
        Assert.IsFalse(vm.Items[1].IsSubmitted);
        StringAssert.Contains(vm.Items[1].StatusText, "入队失败");
        Assert.IsTrue(vm.CanSubmit);

        await vm.SubmitSelectedAsync();
        await vm.SubmitSelectedAsync();

        Assert.AreEqual(1, counts["https://example.test/album/123"]);
        Assert.AreEqual(2, counts["https://example.test/album/456"]);
        Assert.AreEqual(2, vm.SubmittedItems.Count);
        Assert.IsFalse(vm.CanSubmit);
    }

    [TestMethod]
    public async Task EditingInput_InvalidatesResolvedPreview_ButRetainsSuccessfulReceipts()
    {
        var submissions = 0;
        using var vm = new BatchImportViewModel(
            (request, _) => Task.FromResult(Manga(request.Url)),
            (request, _) => { submissions++; return Task.FromResult(Accepted(request.Url)); });
        vm.InputText = "123";
        await vm.PreviewAsync();
        vm.InputText = "456";

        await vm.SubmitSelectedAsync();
        Assert.AreEqual(0, submissions);
        Assert.IsFalse(vm.CanSubmit);
        Assert.IsFalse(vm.Items[0].IsResolved);

        await vm.PreviewAsync();
        await vm.SubmitSelectedAsync();
        vm.InputText = "JM456 789";
        await vm.PreviewAsync();
        await vm.SubmitSelectedAsync();

        Assert.AreEqual(2, submissions);
        Assert.AreEqual(2, vm.SubmittedItems.Count);
        Assert.IsTrue(vm.Items[0].IsSubmitted);
    }

    [TestMethod]
    public async Task OldPreviewThatIgnoresCancellation_CannotReplaceNewInputOrBusyState()
    {
        var old = new TaskCompletionSource<MangaResolveResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken oldToken = default;
        using var vm = new BatchImportViewModel(
            (request, token) =>
            {
                if (request.Url != "123") return Task.FromResult(Manga(request.Url));
                oldToken = token;
                return old.Task;
            }, (_, _) => throw new AssertFailedException("预览不应创建任务"));
        vm.InputText = "123";
        var oldPreview = vm.PreviewAsync();
        Assert.IsTrue(vm.IsBusy);

        vm.InputText = "456";
        Assert.IsTrue(oldToken.IsCancellationRequested);
        await vm.PreviewAsync();
        var status = vm.StatusText;
        old.SetResult(Manga("123"));
        await oldPreview;

        Assert.AreEqual(1, vm.Items.Count);
        Assert.AreEqual("456", vm.Items[0].MangaId);
        Assert.AreEqual("漫画456", vm.Items[0].Title);
        Assert.AreEqual(status, vm.StatusText);
        Assert.IsFalse(vm.IsBusy);
        Assert.IsTrue(vm.CanSubmit);
    }

    [TestMethod]
    public async Task CancelPreview_PreservesResolvedItemsAndRejectsLateResults()
    {
        var pending = new TaskCompletionSource<MangaResolveResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var vm = new BatchImportViewModel(
            (request, _) => request.Url == "123" ? Task.FromResult(Manga("123")) : pending.Task,
            (_, _) => throw new AssertFailedException("预览不应创建任务"));
        vm.InputText = "123 456";
        var preview = vm.PreviewAsync();

        vm.CancelOperation();
        Assert.IsFalse(vm.IsBusy);
        Assert.IsTrue(vm.CanSubmit);
        pending.SetResult(Manga("456"));
        await preview;

        Assert.IsTrue(vm.Items[0].IsResolved);
        Assert.IsFalse(vm.Items[1].IsResolved);
        StringAssert.Contains(vm.Items[1].StatusText, "已取消");
    }

    [TestMethod]
    public async Task CancellationDuringSubmission_PreservesAcceptedTasksAndDoesNotStartFurtherItems()
    {
        var pending = new TaskCompletionSource<BatchImportSubmissionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var receipts = new List<string>();
        using var vm = new BatchImportViewModel(
            (request, _) => Task.FromResult(Manga(request.Url)),
            (request, _) => ++calls == 1 ? Task.FromResult(Accepted(request.Url)) : pending.Task,
            task => receipts.Add(task.Id));
        vm.InputText = "123 456 789";
        await vm.PreviewAsync();
        var submit = vm.SubmitSelectedAsync();

        vm.CancelOperation();
        Assert.IsTrue(vm.Items[0].IsSubmitted);
        Assert.AreEqual(1, vm.SubmittedItems.Count);
        pending.SetResult(Accepted("456")); // The backend accepted the in-flight item before noticing cancellation.
        await submit;

        Assert.AreEqual(2, calls);
        Assert.AreEqual(2, vm.SubmittedItems.Count);
        Assert.AreEqual(2, receipts.Count);
        Assert.IsTrue(vm.Items[1].IsSubmitted);
        Assert.IsFalse(vm.Items[2].IsSubmitted);
    }

    [TestMethod]
    public async Task Dispose_CancelsRequestAndBlocksLateTaskCallback()
    {
        var pending = new TaskCompletionSource<BatchImportSubmissionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken requestToken = default;
        var callbacks = 0;
        var vm = new BatchImportViewModel(
            (request, _) => Task.FromResult(Manga(request.Url)),
            (_, token) => { requestToken = token; return pending.Task; },
            _ => callbacks++);
        vm.InputText = "123";
        await vm.PreviewAsync();
        var submit = vm.SubmitSelectedAsync();

        vm.Dispose();
        vm.Dispose();
        pending.SetResult(Accepted("123"));
        await submit;

        Assert.IsTrue(requestToken.IsCancellationRequested);
        Assert.AreEqual(0, callbacks);
        Assert.IsFalse(vm.CanPreview);
        Assert.IsFalse(vm.CanSubmit);
    }

    [TestMethod]
    public async Task MismatchedMetadataAndEmptyChapters_CannotBeSubmitted()
    {
        using var vm = new BatchImportViewModel(
            (request, _) => Task.FromResult(request.Url == "123" ? Manga("999") : new MangaResolveResponse { MangaId = "456" }),
            (_, _) => throw new AssertFailedException("无效元数据不应提交"));
        vm.InputText = "123 456";

        await vm.PreviewAsync();
        await vm.SubmitSelectedAsync();

        Assert.IsFalse(vm.CanSubmit);
        StringAssert.Contains(vm.Items[0].StatusText, "编号与输入不一致");
        StringAssert.Contains(vm.Items[1].StatusText, "没有可下载章节");
    }

    [TestMethod]
    public async Task ReusedAndAlreadyDownloadedTaskResults_AreExplainedAndNotSubmittedAgain()
    {
        using var vm = new BatchImportViewModel(
            (request, _) => Task.FromResult(Manga(request.Url)),
            (request, _) => Task.FromResult(request.Url.EndsWith("123")
                ? new BatchImportSubmissionResult(new() { Id = "existing" }, AlreadyQueued: true)
                : new BatchImportSubmissionResult(new() { Id = "local", Status = "completed", LocalSkippedChapterCount = 1 })));
        vm.InputText = "123 456";

        await vm.PreviewAsync();
        await vm.SubmitSelectedAsync();

        StringAssert.Contains(vm.Items[0].StatusText, "沿用现有任务");
        StringAssert.Contains(vm.Items[1].StatusText, "本地已完整下载");
        Assert.IsFalse(vm.CanSubmit);
    }

    private static MangaResolveResponse Manga(string id) => new()
    {
        MangaId = id,
        Title = $"漫画{id}",
        MangaUrl = $"https://example.test/album/{id}",
        Chapters = [new() { Title = "第 1 章", Url = $"https://example.test/photo/{id}" }],
    };

    private static BatchImportSubmissionResult Accepted(string id) => new(new() { Id = id, Status = "pending" });
}
