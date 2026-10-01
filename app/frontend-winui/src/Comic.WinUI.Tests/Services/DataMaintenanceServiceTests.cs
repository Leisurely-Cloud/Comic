using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Comic.WinUI.Services;
using Comic.WinUI.Models;
using Comic.WinUI.Services.Native;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Comic.WinUI.Tests.Services;

[TestClass]
public sealed class DataMaintenanceServiceTests
{
    private string _root = null!;
    private string _settingsDir = null!;
    private string _libraryDir = null!;
    private string _mangaDir = null!;
    private ApplicationSettingsService _settings = null!;
    private LibraryStorageService _library = null!;
    private DownloadSchedulerService _scheduler = null!;
    private DataMaintenanceService _service = null!;
    private HttpClient _client = null!;

    [TestInitialize]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), "ComicMaintenanceTests", Guid.NewGuid().ToString("N"));
        _settingsDir = Path.Combine(_root, "settings");
        _libraryDir = Path.Combine(_root, "library");
        _mangaDir = Path.Combine(_libraryDir, "漫画");
        var chapter = Path.Combine(_mangaDir, "001_第一话");
        Directory.CreateDirectory(chapter);
        File.WriteAllBytes(Path.Combine(chapter, "001.jpg"), [1, 2, 3]);
        _settings = new ApplicationSettingsService(_settingsDir);
        _settings.UpdateStorageRoot(_libraryDir);
        _library = new LibraryStorageService(_libraryDir);
        _library.ToggleFavorite(_mangaDir);
        _client = new HttpClient();
        _scheduler = new DownloadSchedulerService(new JmComicService(_client), _library, _settings);
        _service = new DataMaintenanceService(_settings, _library, _scheduler);
    }

    [TestCleanup]
    public void Cleanup()
    {
        _scheduler.Dispose(); _library.Dispose(); _client.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    [TestMethod]
    public void Backup_RoundTripRestoresOnRestartAndKeepsImagesAndPreRestoreCopy()
    {
        var progress = new ReadingProgressService(_settingsDir);
        progress.Save(_mangaDir, "001_第一话", 7);
        File.WriteAllText(Path.Combine(_settingsDir, "secret.txt"), "credential-must-not-export");
        var zip = Path.Combine(_root, "data.zip");
        _service.ExportBackup(zip);
        using (var archive = ZipFile.OpenRead(zip))
        {
            Assert.IsFalse(archive.Entries.Any(entry => entry.FullName.EndsWith(".jpg") || entry.FullName.Contains("secret")));
        }
        Assert.AreEqual(1, _service.PreviewBackup(zip).MangaCount);
        _library.ToggleFavorite(_mangaDir);
        progress.Save(_mangaDir, "001_第一话", 9);
        _service.StageRestore(zip);
        Assert.IsFalse(_library.LoadLibraryMetadata(_mangaDir)!.IsFavorite);
        Assert.IsTrue(_service.HasPendingRestore);
        var status = DataMaintenanceService.ApplyPendingRestore(_settingsDir);
        Assert.IsTrue(status!.Contains("恢复完成"));
        Assert.IsTrue(_library.LoadLibraryMetadata(_mangaDir)!.IsFavorite);
        Assert.AreEqual(7, new ReadingProgressService(_settingsDir).Get(_mangaDir)!.PageIndex);
        Assert.AreEqual(1, Directory.GetFiles(Path.Combine(_settingsDir, "backups"), "*.zip").Length);
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, File.ReadAllBytes(Path.Combine(_mangaDir, "001_第一话", "001.jpg")));
        Assert.IsFalse(_service.HasPendingRestore);
    }

    [TestMethod]
    public void BackupRejectsTamperingAndTraversalBeforeStaging()
    {
        var zip = Path.Combine(_root, "data.zip");
        _service.ExportBackup(zip);
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Update))
        {
            var entry = archive.GetEntry("app/app-settings.json")!;
            entry.Delete();
            using var writer = new StreamWriter(archive.CreateEntry("app/app-settings.json").Open());
            writer.Write("{}");
        }
        Assert.ThrowsExactly<InvalidDataException>(() => _service.StageRestore(zip));
        Assert.IsFalse(_service.HasPendingRestore);
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Update))
        {
            using var writer = new StreamWriter(archive.CreateEntry("app/../../outside.json").Open());
            writer.Write("{}");
        }
        Assert.ThrowsExactly<InvalidDataException>(() => _service.PreviewBackup(zip));
    }

    [TestMethod]
    public void RepairPreview_PreservesDamagedDataAndRevalidatesChanges()
    {
        var path = Path.Combine(_settingsDir, "search-history.json");
        new SearchHistoryService(_settingsDir).Add("旧关键词", SiteCatalog.Key, SiteCatalog.DisplayName);
        new SearchHistoryService(_settingsDir).Add("新关键词", SiteCatalog.Key, SiteCatalog.DisplayName);
        File.WriteAllText(path, "broken");
        var preview = _service.PreviewRepairs();
        Assert.AreEqual(1, preview.Count);
        Assert.IsTrue(preview[0].CanRepair);
        Assert.AreEqual("broken", File.ReadAllText(path));
        File.WriteAllText(path, "changed-broken");
        Assert.ThrowsExactly<InvalidOperationException>(() => _service.StageRepairs(preview));
        Assert.AreEqual(1, _service.StageRepairs(_service.PreviewRepairs()));
        Assert.AreEqual("changed-broken", File.ReadAllText(path));
        Assert.IsTrue(DataMaintenanceService.ApplyPendingRestore(_settingsDir)!.Contains("恢复完成"));
        Assert.IsTrue(new SearchHistoryService(_settingsDir).GetAll().Any(entry => entry.Keyword == "旧关键词"));
        Assert.IsTrue(Directory.GetFiles(_settingsDir, "search-history.json.corrupt-*").Length > 0);
    }

    [TestMethod]
    public void BackupForOtherLibrary_IsRejectedAndCancelLeavesDataUntouched()
    {
        var zip = Path.Combine(_root, "data.zip");
        _service.ExportBackup(zip);
        _service.StageRestore(zip);
        _service.CancelRestore();
        Assert.IsFalse(_service.HasPendingRestore);
        _library.SwitchStorageRoot(Path.Combine(_root, "other"));
        Assert.ThrowsExactly<InvalidOperationException>(() => _service.StageRestore(zip));
    }

    [TestMethod]
    public void DamagedJsonWithoutBackup_AndIncompleteImagesAreReportOnly()
    {
        File.WriteAllText(Path.Combine(_settingsDir, "reader-preferences.json"), "broken");
        File.WriteAllBytes(Path.Combine(_mangaDir, "001_第一话", "002.jpg"), []);
        File.WriteAllBytes(Path.Combine(_mangaDir, "001_第一话", "003.jpg.part"), [5]);
        var preview = _service.PreviewRepairs();
        Assert.AreEqual(3, preview.Count);
        Assert.IsFalse(preview.Any(issue => issue.CanRepair));
        Assert.AreEqual(0, _service.StageRepairs(preview));
        Assert.IsFalse(_service.HasPendingRestore);
        Assert.IsTrue(File.Exists(Path.Combine(_mangaDir, "001_第一话", "003.jpg.part")));
    }

    [TestMethod]
    public void InterruptedRestore_RollsBackPersistedOriginalsOnNextStartup()
    {
        var path = Path.Combine(_settingsDir, "search-history.json");
        var before = Encoding.UTF8.GetBytes("[]");
        var backup = Encoding.UTF8.GetBytes("[ ]");
        var journal = new DataMaintenanceService.RestoreJournal
        {
            StorageRoot = _libraryDir,
            Files = new()
            {
                ["app/search-history.json"] = new() { Primary = before, Backup = backup },
                ["app/reader-preferences.json"] = new(),
            },
        };
        AtomicJsonFile.Write(Path.Combine(_settingsDir, "restore-transaction.json"), journal);
        File.WriteAllText(path, "[{}]");
        File.WriteAllText(path + ".bak", "[{}]");
        File.WriteAllText(Path.Combine(_settingsDir, "reader-preferences.json"), "{}");
        Assert.IsTrue(DataMaintenanceService.ApplyPendingRestore(_settingsDir)!.Contains("已回滚"));
        CollectionAssert.AreEqual(before, File.ReadAllBytes(path));
        CollectionAssert.AreEqual(backup, File.ReadAllBytes(path + ".bak"));
        Assert.IsFalse(File.Exists(Path.Combine(_settingsDir, "reader-preferences.json")));
        Assert.IsNull(DataMaintenanceService.ApplyPendingRestore(_settingsDir));
    }

    [TestMethod]
    public void RestoreFailureLeavesExistingDataUntouched()
    {
        var zip = Path.Combine(_root, "data.zip");
        _service.ExportBackup(zip);
        _service.StageRestore(zip);
        _library.ToggleFavorite(_mangaDir);
        var settingsBefore = File.ReadAllText(Path.Combine(_settingsDir, "app-settings.json"));
        Directory.Delete(_mangaDir, true);
        Assert.IsTrue(DataMaintenanceService.ApplyPendingRestore(_settingsDir)!.Contains("未完成"));
        Assert.AreEqual(settingsBefore, File.ReadAllText(Path.Combine(_settingsDir, "app-settings.json")));
        Assert.IsFalse(_service.HasPendingRestore);
        Assert.AreEqual(1, Directory.GetFiles(_settingsDir, "pending-restore.zip.failed-*").Length);
    }
}
