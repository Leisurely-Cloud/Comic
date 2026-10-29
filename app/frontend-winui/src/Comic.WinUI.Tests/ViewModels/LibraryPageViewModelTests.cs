using Comic.WinUI.Models;
using Comic.WinUI.Services;
using Comic.WinUI.Services.Native;
using Comic.WinUI.Tests.Services;
using Comic.WinUI.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Comic.WinUI.Tests.ViewModels;

[TestClass]
public sealed class LibraryPageViewModelTests
{
    private string _directory = null!;
    private string _libraryRoot = null!;
    private HttpClient _http = null!;
    private JmComicService _jm = null!;
    private LibraryStorageService _library = null!;
    private DownloadSchedulerService _scheduler = null!;
    private CbzExportService _exporter = null!;
    private LibraryPageViewModel _viewModel = null!;
    private ApplicationSettingsService _settings = null!;
    private ReadingProgressService _progress = null!;
    private RecentArchiveService _recentArchives = null!;

    [TestInitialize]
    public void Initialize()
    {
        _directory = Path.Combine(Path.GetTempPath(), "Comic.WinUI.Tests", Guid.NewGuid().ToString("N"));
        _libraryRoot = Path.Combine(_directory, "library");
        for (var index = 0; index < 21; index++)
        {
            var chapter = Path.Combine(_libraryRoot, $"漫画{index:00}", "001_第一章");
            Directory.CreateDirectory(chapter);
            File.WriteAllBytes(Path.Combine(chapter, "001.jpg"), [1]);
        }
        _settings = TestServiceFactory.CreateSettings(Path.Combine(_directory, "settings"));
        _progress = new(_settings);
        _recentArchives = new(_settings);
        _http = new(FakeHttpMessageHandler.AlwaysFails());
        _jm = new(_http);
        _library = TestServiceFactory.CreateLibrary(_libraryRoot);
        _scheduler = TestServiceFactory.CreateScheduler(_jm, _library);
        _exporter = TestServiceFactory.CreateExporter(_library);
        var client = TestServiceFactory.CreateClient(_jm, _scheduler, _library, _exporter,
            TestServiceFactory.CreateReader(_library), _settings, readingProgress: _progress);
        _viewModel = new(client, new(client), _settings, _progress, recentArchives: _recentArchives);
    }

    [TestCleanup]
    public void Cleanup()
    {
        _scheduler.Dispose(); _exporter.Dispose(); _library.Dispose(); _jm.Dispose(); _http.Dispose();
        Directory.Delete(_directory, true);
    }

    [TestMethod]
    public async Task OrganizeAndSearch_ShareStoreAndApplyFilterBeforePagination()
    {
        await _viewModel.LoadAsync();
        var roots = _viewModel.Items.Take(2).Select(item => item.RootDir).ToArray();
        var shelf = _viewModel.Organization.SaveShelf(null, "待看");
        _viewModel.OrganizeBooks(roots, [shelf.Id], ["测试"]);
        _viewModel.RefreshShelves();
        _viewModel.SelectedShelf = _viewModel.Shelves.Single(item => item.Id == shelf.Id);
        _viewModel.TagFilter = "测试";
        await _viewModel.SearchAsync();
        Assert.AreEqual(2, _viewModel.Items.Count);
        Assert.AreEqual(1, _viewModel.TotalPages);
        Assert.IsTrue(_viewModel.Items.All(item => item.OrganizationText == "待看 · #测试"));
        Assert.AreEqual("待看 · #测试", _viewModel.SelectedOrganizationText);
        _viewModel.Items[0].IsMarked = true;
        await _viewModel.LoadAsync();
        Assert.IsTrue(_viewModel.Items.All(item => !item.IsMarked));
    }

    [TestMethod]
    public async Task ShrinkingLibrary_ClampsPageToBackendResult()
    {
        await _viewModel.LoadAsync();
        await _viewModel.NextPage();
        Assert.IsTrue(_viewModel.CanGoPrevious);
        var remaining = _viewModel.Items.Single().RootDir;
        foreach (var directory in Directory.GetDirectories(_libraryRoot).Where(path => path != remaining && !Path.GetFileName(path).StartsWith('.')))
            Directory.Delete(directory, true);
        _library.InvalidateLibraryCache();
        await _viewModel.LoadAsync();
        Assert.IsFalse(_viewModel.CanGoPrevious);
        Assert.IsFalse(_viewModel.CanGoNext);
        StringAssert.Contains(_viewModel.PageSummary, "第 1 / 1 页");
    }

    [TestMethod]
    public async Task ReadingStatusChange_RefreshesCountsDetailsAndActiveFilter()
    {
        await _viewModel.LoadAsync();
        var selectedRoot = _viewModel.SelectedItem!.RootDir;
        Assert.AreEqual("待读", _viewModel.SelectedReadingStatusText);

        await _viewModel.SetSelectedReadingStatusAsync(ReadingStatus.Read);

        Assert.AreEqual(selectedRoot, _viewModel.SelectedItem?.RootDir);
        Assert.AreEqual("已读", _viewModel.SelectedReadingStatusText);
        Assert.AreEqual(1, _viewModel.ReadCount);
        Assert.AreEqual(20, _viewModel.ToReadCount);
        Assert.AreEqual(ReadingStatus.Read, new ReadingProgressService(_settings).GetReadingStatus(selectedRoot));
        _viewModel.ReadingFilterIndex = 3;
        await _viewModel.SearchAsync();
        Assert.AreEqual(selectedRoot, _viewModel.Items.Single().RootDir);

        await _viewModel.SetSelectedReadingStatusAsync(ReadingStatus.ToRead);

        Assert.AreEqual(0, _viewModel.Items.Count);
        Assert.AreEqual(0, _viewModel.ReadCount);
        Assert.AreEqual(21, _viewModel.ToReadCount);
        Assert.IsFalse(_viewModel.CanSetReadingStatus);
        Assert.AreEqual("未选择漫画", _viewModel.SelectedReadingStatusText);
    }

    [TestMethod]
    public async Task ShowReading_ListsInProgressAndExposesResumeTarget()
    {
        await _viewModel.LoadAsync();
        var first = _viewModel.Items[0].RootDir;
        var second = _viewModel.Items[1].RootDir;
        _progress.Save(first, "001_第一章", 4);
        _progress.Save(second, "001_第一章", 0);
        Assert.IsTrue(_progress.SetReadingStatus(second, ReadingStatus.Read));

        await _viewModel.ShowReadingCommand.ExecuteAsync(null);

        Assert.AreEqual(2, _viewModel.ReadingFilterIndex);
        Assert.AreEqual(first, _viewModel.Items.Single().RootDir);
        Assert.AreEqual("在读", _viewModel.SelectedReadingStatusText);
        Assert.AreEqual(first, _viewModel.ContinueReadingRoot);
        Assert.IsTrue(_viewModel.CanContinueReading);
        Assert.AreEqual(1, _viewModel.ReadingCount);
        Assert.AreEqual(1, _viewModel.ReadCount);
        Assert.AreEqual(19, _viewModel.ToReadCount);
        StringAssert.Contains(_viewModel.SelectedContinueReading, "第 5 页");
    }

    [TestMethod]
    public async Task ReadingStatusSaveFailure_ReportsErrorWithoutChangingCardOrSuccessMessage()
    {
        await _viewModel.LoadAsync();
        var root = _viewModel.SelectedItem!.RootDir;
        Directory.CreateDirectory(Path.Combine(_settings.SettingsDirectory, "reading-progress.json"));

        await _viewModel.SetSelectedReadingStatusAsync(ReadingStatus.Read);

        Assert.AreEqual(ReadingStatus.ToRead, _progress.GetReadingStatus(root));
        Assert.AreEqual("待读", _viewModel.SelectedReadingStatusText);
        Assert.AreEqual(0, _viewModel.ReadCount);
        StringAssert.Contains(_viewModel.PageError, "保存失败");
        Assert.AreEqual(string.Empty, _viewModel.LibraryStatus);
        Assert.IsFalse(_viewModel.IsSettingReadingStatus);
        Assert.IsTrue(_viewModel.CanSetReadingStatus);
    }

    [TestMethod]
    public async Task RecentArchives_ShowSavedProgressAndRetainMissingFilesWithoutAddingLibraryBooks()
    {
        var rootArchive = Path.Combine(_directory, "Root pages.cbz");
        var nestedArchive = Path.Combine(_directory, "Nested chapters.zip");
        var unopenedArchive = Path.Combine(_directory, "Moved or deleted.cbz");
        Assert.IsTrue(_recentArchives.RecordOpened(rootArchive));
        Assert.IsTrue(_recentArchives.RecordOpened(nestedArchive));
        Assert.IsTrue(_recentArchives.RecordOpened(unopenedArchive));
        _progress.Save(rootArchive, "/", 4);
        _progress.Save(nestedArchive, "Book/Chapter 2/", 1);
        await _viewModel.LoadAsync();

        var recent = _viewModel.GetRecentArchives();

        CollectionAssert.AreEqual(new[] { unopenedArchive, nestedArchive, rootArchive }, recent.Select(entry => entry.FilePath).ToArray());
        Assert.AreEqual("Moved or deleted", recent[0].Title);
        Assert.AreEqual("尚无阅读进度", recent[0].ProgressText);
        Assert.AreEqual("Chapter 2 · 第 2 页", recent[1].ProgressText);
        Assert.AreEqual("根目录 · 第 5 页", recent[2].ProgressText);
        Assert.IsTrue(recent.All(entry => !File.Exists(entry.FilePath)), "缺失文件应保留给用户重试或主动移除，枚举历史无需创建文件。");
        Assert.AreEqual(20, _viewModel.Items.Count);
        Assert.AreEqual(21, _viewModel.ToReadCount);
        Assert.AreEqual(0, _viewModel.ReadingCount, "压缩包的阅读进度不应混入目录书库的状态统计。");
        Assert.AreEqual(_recentArchives.GetAll()[0].LastOpenedAtUtc, recent[0].LastOpenedAtUtc);
    }

    [TestMethod]
    public void RecentArchives_RemoveAndClearOnlyHistoryAndPreserveSourceProgressAndPreferences()
    {
        var firstPath = Path.Combine(_directory, "First book.cbz");
        var secondPath = Path.Combine(_directory, "Second book.zip");
        File.WriteAllBytes(firstPath, [1, 2, 3]);
        File.WriteAllBytes(secondPath, [4, 5, 6]);
        Assert.IsTrue(_recentArchives.RecordOpened(firstPath));
        Assert.IsTrue(_recentArchives.RecordOpened(secondPath));
        _progress.Save(firstPath, "/", 1);
        _progress.Save(secondPath, "Chapter 2", 3);
        var firstProgress = _progress.Get(firstPath);
        var secondProgress = _progress.Get(secondPath);
        Assert.IsTrue(_progress.SetReadingStatus(firstPath, ReadingStatus.Read));
        var preferences = new ReaderPreferenceService(_settings);
        preferences.Save(firstPath, new ReaderPreference("strip", true, 170, 140));
        var preferenceBefore = preferences.Get(firstPath, "paged", 100);
        _viewModel.PageError = "旧错误";

        Assert.IsTrue(_viewModel.RemoveRecentArchive(firstPath));
        Assert.AreEqual(string.Empty, _viewModel.PageError);
        Assert.AreEqual(secondPath, _viewModel.GetRecentArchives().Single().FilePath);
        Assert.IsTrue(_viewModel.ClearRecentArchives());

        Assert.AreEqual(0, _viewModel.GetRecentArchives().Count);
        Assert.AreEqual(0, new RecentArchiveService(_settings).GetAll().Count);
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, File.ReadAllBytes(firstPath));
        CollectionAssert.AreEqual(new byte[] { 4, 5, 6 }, File.ReadAllBytes(secondPath));
        Assert.AreEqual(firstProgress, _progress.Get(firstPath));
        Assert.AreEqual(secondProgress, _progress.Get(secondPath));
        Assert.AreEqual(ReadingStatus.Read, _progress.GetReadingStatus(firstPath));
        Assert.AreEqual(preferenceBefore, new ReaderPreferenceService(_settings).Get(firstPath, "paged", 100));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void RecentArchives_RemoveOrClearSaveFailureKeepsEntriesAndReportsError(bool clear)
    {
        var path = Path.Combine(_directory, "Retained book.cbz");
        Assert.IsTrue(_recentArchives.RecordOpened(path));
        var before = _viewModel.GetRecentArchives().Single();
        var historyFile = Path.Combine(_settings.SettingsDirectory, "recent-archives.json");
        File.Delete(historyFile);
        Directory.CreateDirectory(historyFile);

        var saved = clear ? _viewModel.ClearRecentArchives() : _viewModel.RemoveRecentArchive(path);

        Assert.IsFalse(saved);
        StringAssert.Contains(_viewModel.PageError, clear ? "清空失败" : "移除失败");
        Assert.AreEqual(before, _viewModel.GetRecentArchives().Single(), "写入失败后界面必须保留原记录供重试。");
    }

    [TestMethod]
    [DataRow(".CBZ")]
    [DataRow(".ZIP")]
    public async Task ArchiveDrop_AcceptsSingleArchiveWithoutOpeningItOrChangingReadingData(string extension)
    {
        var path = Path.Combine(_directory, "Dropped book" + extension);
        using (var archive = System.IO.Compression.ZipFile.Open(path, System.IO.Compression.ZipArchiveMode.Create))
        using (var stream = archive.CreateEntry("Chapter 2/001.png").Open())
            stream.Write([1, 2, 3, 4]);
        var sourceHash = System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path));
        var previousArchive = Path.Combine(_directory, "Previous book.cbz");
        Assert.IsTrue(_recentArchives.RecordOpened(previousArchive));
        _progress.Save(path, "Chapter 2", 3);
        var previousHistory = _recentArchives.GetAll().ToArray();
        var previousProgress = _progress.Get(path);
        var historyPath = Path.Combine(_settings.SettingsDirectory, "recent-archives.json");
        var progressPath = Path.Combine(_settings.SettingsDirectory, "reading-progress.json");
        var historyBytes = File.ReadAllBytes(historyPath);
        var progressBytes = File.ReadAllBytes(progressPath);
        _viewModel.PageError = "旧错误";

        string? resolved;
        // Exclusive access proves the drop resolver does not inspect or open the archive contents.
        using (File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            resolved = await _viewModel.ResolveDroppedArchiveAsync(_ => Task.FromResult<IReadOnlyList<ArchiveDropItem>>([new(path, true)]));

        Assert.AreEqual(Path.GetFullPath(path), resolved);
        Assert.AreEqual(string.Empty, _viewModel.PageError);
        Assert.IsFalse(_viewModel.IsResolvingArchiveDrop);
        CollectionAssert.AreEqual(sourceHash, System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)));
        CollectionAssert.AreEqual(previousHistory, _recentArchives.GetAll().ToArray());
        CollectionAssert.AreEqual(historyBytes, File.ReadAllBytes(historyPath));
        CollectionAssert.AreEqual(progressBytes, File.ReadAllBytes(progressPath));
        Assert.AreEqual(previousProgress, _progress.Get(path));
        Assert.AreEqual(previousProgress, new ReadingProgressService(_settings).Get(path));
        Assert.AreEqual(0, _viewModel.Items.Count, "拖放校验不能向目录书库加入归档文件。");
    }

    [TestMethod]
    public async Task ArchiveDrop_AcceptsMissingAbsolutePathAndLeavesActualFileValidationToReader()
    {
        var path = Path.Combine(_directory, "Unavailable device", "Missing book.cbz");
        Assert.IsFalse(File.Exists(path));

        var resolved = await _viewModel.ResolveDroppedArchiveAsync(_ => Task.FromResult<IReadOnlyList<ArchiveDropItem>>([new(path, true)]));

        Assert.AreEqual(path, resolved);
        Assert.AreEqual(string.Empty, _viewModel.PageError);
        Assert.IsFalse(File.Exists(path));
        Assert.IsNull(_progress.Get(path));
        Assert.AreEqual(0, _recentArchives.GetAll().Count);
    }

    [TestMethod]
    [DataRow("empty")]
    [DataRow("multiple")]
    [DataRow("directory")]
    [DataRow("unsupported")]
    [DataRow("relative")]
    [DataRow("blank")]
    [DataRow("whitespace")]
    [DataRow("null-path")]
    public async Task ArchiveDrop_RejectsInputsThatCannotRepresentOneLocalArchive(string input)
    {
        var path = Path.Combine(_directory, "Book.cbz");
        IReadOnlyList<ArchiveDropItem> items = input switch
        {
            "empty" => [],
            "multiple" => [new(path, true), new(Path.Combine(_directory, "Other.zip"), true)],
            "directory" => [new(path, false)],
            "unsupported" => [new(Path.ChangeExtension(path, ".rar"), true)],
            "relative" => [new("Book.cbz", true)],
            "blank" => [new(string.Empty, true)],
            "whitespace" => [new("   ", true)],
            "null-path" => [new(null!, true)],
            _ => throw new ArgumentOutOfRangeException(nameof(input)),
        };

        var resolved = await _viewModel.ResolveDroppedArchiveAsync(_ => Task.FromResult(items));

        Assert.IsNull(resolved);
        Assert.IsFalse(string.IsNullOrWhiteSpace(_viewModel.PageError), "无效拖放应说明原因，而不是静默尝试导航。");
        Assert.IsFalse(_viewModel.IsResolvingArchiveDrop);
        Assert.AreEqual(0, _recentArchives.GetAll().Count);
        Assert.IsNull(_progress.Get(path));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ArchiveDrop_ProviderFailureReportsErrorAndAllowsLaterRequest(bool unauthorized)
    {
        var error = unauthorized ? new UnauthorizedAccessException("denied") : (Exception)new IOException("unavailable");

        var resolved = await _viewModel.ResolveDroppedArchiveAsync(_ => Task.FromException<IReadOnlyList<ArchiveDropItem>>(error));

        Assert.IsNull(resolved);
        Assert.IsFalse(string.IsNullOrWhiteSpace(_viewModel.PageError));
        Assert.IsFalse(_viewModel.IsResolvingArchiveDrop);
        Assert.AreEqual(0, _recentArchives.GetAll().Count);
        var path = Path.Combine(_directory, "Retry.zip");
        Assert.AreEqual(path, await _viewModel.ResolveDroppedArchiveAsync(_ => Task.FromResult<IReadOnlyList<ArchiveDropItem>>([new(path, true)])));
        Assert.AreEqual(string.Empty, _viewModel.PageError);
    }

    [TestMethod]
    public async Task ArchiveDrop_PreCanceledRequestDoesNotInvokeProviderOrPublishError()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var invoked = false;

        var resolved = await _viewModel.ResolveDroppedArchiveAsync(_ =>
        {
            invoked = true;
            return Task.FromResult<IReadOnlyList<ArchiveDropItem>>([]);
        }, cancellation.Token);

        Assert.IsNull(resolved);
        Assert.IsFalse(invoked);
        Assert.IsFalse(_viewModel.IsResolvingArchiveDrop);
        Assert.AreEqual(string.Empty, _viewModel.PageError);
    }

    [TestMethod]
    public async Task ArchiveDrop_WhilePendingDoesNotInvokeSecondProviderOrLoseBusyState()
    {
        var pending = new TaskCompletionSource<IReadOnlyList<ArchiveDropItem>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstPath = Path.Combine(_directory, "First.cbz");
        var first = _viewModel.ResolveDroppedArchiveAsync(_ => pending.Task);
        Assert.IsTrue(_viewModel.IsResolvingArchiveDrop);
        var secondInvoked = false;

        var second = await _viewModel.ResolveDroppedArchiveAsync(_ =>
        {
            secondInvoked = true;
            return Task.FromResult<IReadOnlyList<ArchiveDropItem>>([new(Path.Combine(_directory, "Second.zip"), true)]);
        });

        Assert.IsNull(second);
        Assert.IsFalse(secondInvoked);
        Assert.IsTrue(_viewModel.IsResolvingArchiveDrop);
        pending.SetResult([new(firstPath, true)]);
        Assert.AreEqual(firstPath, await first);
        Assert.IsFalse(_viewModel.IsResolvingArchiveDrop);
        Assert.AreEqual(string.Empty, _viewModel.PageError);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ArchiveDrop_CanceledOldProviderCannotPublishOrClearNewRequest(bool oldProviderFails)
    {
        var oldPending = new TaskCompletionSource<IReadOnlyList<ArchiveDropItem>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var newPending = new TaskCompletionSource<IReadOnlyList<ArchiveDropItem>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldPath = Path.Combine(_directory, "Old.cbz");
        var newPath = Path.Combine(_directory, "Current.zip");
        CancellationToken oldToken = default;
        var oldRequest = _viewModel.ResolveDroppedArchiveAsync(token =>
        {
            oldToken = token;
            return oldPending.Task;
        });
        Assert.IsTrue(_viewModel.IsResolvingArchiveDrop);

        _viewModel.CancelArchiveDrop();

        Assert.IsTrue(oldToken.IsCancellationRequested);
        Assert.IsFalse(_viewModel.IsResolvingArchiveDrop);
        var newRequest = _viewModel.ResolveDroppedArchiveAsync(_ => newPending.Task);
        Assert.IsTrue(_viewModel.IsResolvingArchiveDrop);
        if (oldProviderFails) oldPending.SetException(new IOException("late failure"));
        else oldPending.SetResult([new(oldPath, true)]);

        Assert.IsNull(await oldRequest, "旧拖放已取消，迟到结果不能成为导航目标。");
        Assert.IsTrue(_viewModel.IsResolvingArchiveDrop, "旧请求的 finally 不能清除新请求的加载状态。");
        Assert.AreEqual(string.Empty, _viewModel.PageError, "旧 provider 的迟到异常不能覆盖新会话界面。");
        newPending.SetResult([new(newPath, true)]);
        Assert.AreEqual(newPath, await newRequest);
        Assert.IsFalse(_viewModel.IsResolvingArchiveDrop);
        Assert.AreEqual(0, _recentArchives.GetAll().Count);
        Assert.IsNull(_progress.Get(oldPath));
        Assert.IsNull(_progress.Get(newPath));
    }

    [TestMethod]
    public async Task ArchiveDrop_CallerCancellationDoesNotPublishProviderCancellationAsFailure()
    {
        using var cancellation = new CancellationTokenSource();
        var pending = new TaskCompletionSource<IReadOnlyList<ArchiveDropItem>>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken providerToken = default;
        var request = _viewModel.ResolveDroppedArchiveAsync(token =>
        {
            providerToken = token;
            return pending.Task;
        }, cancellation.Token);
        Assert.IsTrue(_viewModel.IsResolvingArchiveDrop);

        cancellation.Cancel();
        Assert.IsTrue(providerToken.IsCancellationRequested);
        pending.SetCanceled(providerToken);

        Assert.IsNull(await request);
        Assert.IsFalse(_viewModel.IsResolvingArchiveDrop);
        Assert.AreEqual(string.Empty, _viewModel.PageError);
    }
}
