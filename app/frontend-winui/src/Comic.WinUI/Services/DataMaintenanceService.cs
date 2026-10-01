using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Comic.WinUI.Models;
using Comic.WinUI.Services.Native;

namespace Comic.WinUI.Services;

public sealed record DataBackupPreview(DateTimeOffset CreatedAt, int AppFileCount, int HistoryFileCount, int MangaCount, long Bytes);
public sealed record DataRepairIssue(string Path, string Description, bool CanRepair, string? BackupHash = null, string? DamagedHash = null);

/// <summary>仅处理白名单中的应用数据。恢复先暂存，进程启动时在服务加载前提交。</summary>
public sealed class DataMaintenanceService
{
    private const int MaxFileBytes = 8 * 1024 * 1024;
    private const int MaxBackupBytes = 64 * 1024 * 1024;
    private static readonly string[] AppFiles = ["app-settings.json", "reading-progress.json", "reader-preferences.json", "search-history.json"];
    private static readonly string[] StateFiles = ["task_history.json", "task_queue.json"];
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private readonly string _settingsDirectory;
    private readonly LibraryStorageService _library;
    private readonly DownloadSchedulerService _downloads;

    public DataMaintenanceService(ApplicationSettingsService settings, LibraryStorageService library, DownloadSchedulerService downloads)
    {
        _settingsDirectory = settings.SettingsDirectory;
        _library = library;
        _downloads = downloads;
    }

    public bool HasPendingRestore => File.Exists(Path.Combine(_settingsDirectory, "pending-restore.zip"));

    public void ExportBackup(string destination, CancellationToken cancellationToken = default)
    {
        _downloads.RunWithIdleLibrary(() =>
        {
            _downloads.SaveQueue();
            WriteBackup(destination, _settingsDirectory, _library.StorageRoot, cancellationToken);
            return true;
        });
    }

    public DataBackupPreview PreviewBackup(string path)
    {
        var backup = ReadBackup(path);
        EnsureSameLibrary(backup.Manifest.StorageRoot, _library.StorageRoot);
        return new DataBackupPreview(backup.Manifest.CreatedAt,
            backup.Files.Count(file => file.Key.StartsWith("app/", StringComparison.Ordinal)),
            backup.Files.Count(file => file.Key.StartsWith("state/", StringComparison.Ordinal)),
            backup.Files.Count(file => file.Key.StartsWith("metadata/", StringComparison.Ordinal)),
            backup.Files.Sum(file => (long)Encoding.UTF8.GetByteCount(file.Value)));
    }

    public void StageRestore(string path)
    {
        _downloads.RunWithIdleLibrary(() =>
        {
            var backup = ReadBackup(path);
            EnsureSameLibrary(backup.Manifest.StorageRoot, _library.StorageRoot);
            foreach (var key in backup.Files.Keys) ResolveTarget(key, _settingsDirectory, _library.StorageRoot);
            var pending = Path.Combine(_settingsDirectory, "pending-restore.zip");
            // 从已验证内容重新打包，避免预览后输入文件被替换。
            WriteArchive(pending, backup.Manifest, backup.Files);
            return true;
        });
    }

    public void CancelRestore()
    {
        var pending = Path.Combine(_settingsDirectory, "pending-restore.zip");
        if (File.Exists(pending)) File.Delete(pending);
    }

    internal static string? ApplyPendingRestore(string settingsDirectory)
    {
        var pending = Path.Combine(settingsDirectory, "pending-restore.zip");
        var journalPath = Path.Combine(settingsDirectory, "restore-transaction.json");
        if (File.Exists(journalPath))
        {
            try
            {
                var journal = AtomicJsonFile.Read<RestoreJournal>(journalPath, Options)
                    ?? throw new InvalidDataException("恢复事务记录损坏，请保留文件并手动检查。");
                var settings = new ApplicationSettingsService(settingsDirectory);
                EnsureSameLibrary(journal.StorageRoot, settings.StorageRoot);
                Rollback(journal, settingsDirectory);
                if (File.Exists(pending)) File.Move(pending, pending + ".failed-" + Guid.NewGuid().ToString("N"));
                File.Delete(journalPath);
                return "上次数据恢复被中断，已回滚到恢复前的数据；请重新选择备份。";
            }
            catch (Exception ex)
            {
                return $"上次数据恢复的回滚未完成，已保留事务记录与备份，请先检查文件权限：{ex.Message}";
            }
        }
        if (!File.Exists(pending)) return null;
        RestoreJournal? originals = null;
        try
        {
            var settings = new ApplicationSettingsService(settingsDirectory);
            var root = Path.GetFullPath(settings.StorageRoot);
            var backup = ReadBackup(pending);
            EnsureSameLibrary(backup.Manifest.StorageRoot, root);
            var targets = backup.Files.ToDictionary(file => ResolveTarget(file.Key, settingsDirectory, root), file => file.Value);
            var rollback = Path.Combine(settingsDirectory, "backups", $"before-restore-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.zip");
            WriteBackup(rollback, settingsDirectory, root, CancellationToken.None);
            originals = new RestoreJournal { StorageRoot = root };
            foreach (var file in backup.Files)
            {
                var target = ResolveTarget(file.Key, settingsDirectory, root);
                originals.Files.Add(file.Key, new RestoreOriginal
                {
                    Primary = File.Exists(target) ? File.ReadAllBytes(target) : null,
                    Backup = File.Exists(target + ".bak") ? File.ReadAllBytes(target + ".bak") : null,
                });
            }
            // 先持久化回滚快照，再替换任何数据；闪退或断电后下一次启动也能恢复。
            AtomicJsonFile.Write(journalPath, originals, Options);
            foreach (var target in targets)
            {
                AtomicJsonFile.WriteText(target.Key, target.Value);
            }
            File.Delete(pending);
            File.Delete(journalPath);
            return $"数据恢复完成；恢复前备份：{rollback}";
        }
        catch (Exception ex)
        {
            try
            {
                if (originals is not null && File.Exists(journalPath)) Rollback(originals, settingsDirectory);
                // 保留失败的恢复包供用户检查，不让后续每次启动重复应用。
                if (File.Exists(pending)) File.Move(pending, pending + ".failed-" + Guid.NewGuid().ToString("N"));
                if (File.Exists(journalPath)) File.Delete(journalPath);
            }
            catch (Exception rollbackError)
            {
                return $"数据恢复与回滚未完成，已保留事务记录和备份：{ex.Message}；{rollbackError.Message}";
            }
            return $"数据恢复未完成，已回滚：{ex.Message}";
        }
    }

    private static void Rollback(RestoreJournal journal, string settingsDirectory)
    {
        // 全部路径预检通过后才开始回滚，拒绝篡改记录或目录链接。
        var targets = journal.Files.ToDictionary(file => ResolveTarget(file.Key, settingsDirectory, journal.StorageRoot), file => file.Value);
        foreach (var target in targets)
        {
            RestoreBytes(target.Key, target.Value.Primary);
            RestoreBytes(target.Key + ".bak", target.Value.Backup);
        }
    }

    private static void RestoreBytes(string path, byte[]? bytes)
    {
        if (bytes is null) { if (File.Exists(path)) File.Delete(path); return; }
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".rollback";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(true);
            }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    internal sealed class RestoreJournal
    {
        public string StorageRoot { get; set; } = string.Empty;
        public Dictionary<string, RestoreOriginal> Files { get; set; } = [];
    }

    internal sealed class RestoreOriginal
    {
        public byte[]? Primary { get; set; }
        public byte[]? Backup { get; set; }
    }

    public IReadOnlyList<DataRepairIssue> PreviewRepairs(CancellationToken cancellationToken = default)
    {
        var issues = new List<DataRepairIssue>();
        foreach (var file in EnumerateDataFiles(_settingsDirectory, _library.StorageRoot))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var json = File.ReadAllText(file.Path);
            if (IsValidJson(file.Key, json)) continue;
            var backupPath = file.Path + ".bak";
            var backup = File.Exists(backupPath) ? File.ReadAllText(backupPath) : null;
            var repairable = backup is not null && IsValidJson(file.Key, backup);
            issues.Add(new DataRepairIssue(file.Path,
                repairable ? "数据文件损坏，可从上一份有效备份恢复（重启后生效）" : "数据文件损坏，缺少有效备份；请从导出的备份恢复",
                repairable, repairable ? Hash(backup!) : null, Hash(json)));
        }
        foreach (var manga in new DirectoryInfo(_library.StorageRoot).EnumerateDirectories())
        {
            if (manga.Name.StartsWith('.') || manga.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
            cancellationToken.ThrowIfCancellationRequested();
            var metadata = _library.LoadLibraryMetadata(manga.FullName);
            foreach (var chapter in metadata?.DownloadedChapters ?? [])
            {
                var path = Path.GetFullPath(Path.Combine(manga.FullName, chapter.DirName));
                if (!IsUnder(path, manga.FullName)) continue;
                if (!Directory.Exists(path) || _library.EnumerateImages(path).Count < chapter.ImageCount)
                    issues.Add(new DataRepairIssue(path, "章节目录缺失或图片数量不足，可在追更中心补下载", false));
            }
            foreach (var file in manga.EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = true }))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (file.Name.EndsWith(".part", StringComparison.OrdinalIgnoreCase))
                    issues.Add(new DataRepairIssue(file.FullName, "未完成下载文件；继续任务会重新下载该图片", false));
                else if (file.Length == 0 && new[] { ".jpg", ".jpeg", ".png", ".webp", ".gif", ".bmp" }.Contains(file.Extension.ToLowerInvariant()))
                    issues.Add(new DataRepairIssue(file.FullName, "图片为空，可重试对应下载任务", false));
            }
        }
        return issues;
    }

    public int StageRepairs(IReadOnlyList<DataRepairIssue> preview)
    {
        return _downloads.RunWithIdleLibrary(() =>
        {
            if (HasPendingRestore) throw new InvalidOperationException("已有待恢复的备份，请先完成或取消恢复。");
            var knownFiles = EnumerateDataFiles(_settingsDirectory, _library.StorageRoot).ToDictionary(file => file.Path, file => file.Key, StringComparer.OrdinalIgnoreCase);
            var files = new Dictionary<string, string>();
            foreach (var issue in preview.Where(issue => issue.CanRepair))
            {
                if (!knownFiles.TryGetValue(issue.Path, out var key)) throw new InvalidOperationException("修复目标已变化，请重新检查。");
                var current = File.ReadAllText(issue.Path);
                var backup = File.ReadAllText(issue.Path + ".bak");
                if (Hash(current) != issue.DamagedHash || Hash(backup) != issue.BackupHash || !IsValidJson(key, backup))
                    throw new InvalidOperationException("文件或备份已变化，请重新检查。");
                files.Add(key, backup);
            }
            if (files.Count > 0) WriteArchive(Path.Combine(_settingsDirectory, "pending-restore.zip"), NewManifest(_library.StorageRoot, files), files);
            return files.Count;
        });
    }

    private static void WriteBackup(string destination, string settingsDirectory, string root, CancellationToken token)
    {
        var files = new Dictionary<string, string>();
        foreach (var file in EnumerateDataFiles(settingsDirectory, root))
        {
            token.ThrowIfCancellationRequested();
            if (new FileInfo(file.Path).Length > MaxFileBytes) throw new InvalidDataException("数据文件过大，无法备份。");
            var json = File.ReadAllText(file.Path);
            // 保留损坏数据的原件，备份使用可读取的上一版本。
            if (!IsValidJson(file.Key, json))
            {
                json = File.Exists(file.Path + ".bak") ? File.ReadAllText(file.Path + ".bak") : string.Empty;
                if (!IsValidJson(file.Key, json)) throw new InvalidDataException($"数据文件损坏且无有效备份：{file.Path}");
            }
            files.Add(file.Key, json);
        }
        if (files.Values.Sum(json => (long)Encoding.UTF8.GetByteCount(json)) > MaxBackupBytes) throw new InvalidDataException("备份数据超过 64 MB。");
        WriteArchive(destination, NewManifest(root, files), files);
    }

    private static IEnumerable<(string Key, string Path)> EnumerateDataFiles(string settingsDirectory, string root)
    {
        foreach (var file in AppFiles)
        {
            var path = Path.Combine(settingsDirectory, file);
            if (File.Exists(path)) yield return ("app/" + file, path);
        }
        foreach (var file in StateFiles)
        {
            var path = Path.Combine(root, ".comic_state", file);
            if (File.Exists(path)) yield return ("state/" + file, path);
        }
        if (!Directory.Exists(root)) yield break;
        foreach (var manga in new DirectoryInfo(root).EnumerateDirectories())
        {
            if (manga.Name.StartsWith('.') || manga.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
            var path = Path.Combine(manga.FullName, "元数据.json");
            if (File.Exists(path)) yield return ($"metadata/{manga.Name}/元数据.json", path);
        }
    }

    private static BackupManifest NewManifest(string root, Dictionary<string, string> files) => new()
    {
        StorageRoot = Path.GetFullPath(root),
        CreatedAt = DateTimeOffset.Now,
        Hashes = files.ToDictionary(file => file.Key, file => Hash(file.Value)),
    };

    private static void WriteArchive(string destination, BackupManifest manifest, Dictionary<string, string> files)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destination))!);
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var zip = ZipFile.Open(temporary, ZipArchiveMode.Create))
            {
                using (var writer = new StreamWriter(zip.CreateEntry("manifest.json").Open(), new UTF8Encoding(false)))
                    writer.Write(JsonSerializer.Serialize(manifest, Options));
                foreach (var file in files)
                {
                    using var writer = new StreamWriter(zip.CreateEntry(file.Key).Open(), new UTF8Encoding(false));
                    writer.Write(file.Value);
                }
            }
            File.Move(temporary, destination, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static (BackupManifest Manifest, Dictionary<string, string> Files) ReadBackup(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        if (zip.Entries.Count > 20000 || zip.Entries.Sum(entry => entry.Length) > MaxBackupBytes ||
            zip.Entries.GroupBy(entry => entry.FullName, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1))
            throw new InvalidDataException("备份内容过大。");
        var manifestEntry = zip.GetEntry("manifest.json") ?? throw new InvalidDataException("不是漫画下载器的数据备份。");
        var manifest = JsonSerializer.Deserialize<BackupManifest>(ReadEntry(manifestEntry), Options)
            ?? throw new InvalidDataException("备份清单无效。");
        if (manifest.Version != 1 || string.IsNullOrWhiteSpace(manifest.StorageRoot) || manifest.Hashes is null)
            throw new InvalidDataException("备份格式不受支持。");
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in zip.Entries.Where(entry => entry.FullName != "manifest.json"))
        {
            ValidateKey(entry.FullName);
            var json = ReadEntry(entry);
            if (!manifest.Hashes.TryGetValue(entry.FullName, out var hash) || Hash(json) != hash || !IsValidJson(entry.FullName, json))
                throw new InvalidDataException($"备份内容损坏：{entry.FullName}");
            if (!files.TryAdd(entry.FullName, json)) throw new InvalidDataException("备份包含重复文件。");
        }
        if (files.Count != manifest.Hashes.Count) throw new InvalidDataException("备份文件不完整。");
        if (files.TryGetValue("app/app-settings.json", out var settingsJson))
        {
            var settings = JsonSerializer.Deserialize<ApplicationSettingsService.UserPreferences>(settingsJson, Options)!;
            if (!string.IsNullOrWhiteSpace(settings.StorageRoot)) EnsureSameLibrary(settings.StorageRoot, manifest.StorageRoot);
        }
        return (manifest, files);
    }

    private static string ReadEntry(ZipArchiveEntry entry)
    {
        if (entry.Length > MaxFileBytes) throw new InvalidDataException("备份文件过大。");
        using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static void ValidateKey(string key)
    {
        var parts = key.Split('/');
        if (key.Contains('\\') || parts.Any(part => string.IsNullOrWhiteSpace(part) || part is "." or ".." ||
            part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || part.EndsWith('.') || part.EndsWith(' ')))
            throw new InvalidDataException("备份含不安全的文件路径。");
        if (parts.Length == 2 && parts[0] == "app" && AppFiles.Contains(parts[1])) return;
        if (parts.Length == 2 && parts[0] == "state" && StateFiles.Contains(parts[1])) return;
        if (parts.Length == 3 && parts[0] == "metadata" && !parts[1].StartsWith('.') && parts[2] == "元数据.json") return;
        throw new InvalidDataException("备份含不支持的文件。");
    }

    private static string ResolveTarget(string key, string settingsDirectory, string root)
    {
        ValidateKey(key);
        var parts = key.Split('/');
        var parent = parts[0] == "app" ? settingsDirectory : parts[0] == "state" ? Path.Combine(root, ".comic_state") : Path.Combine(root, parts[1]);
        if (parts[0] == "metadata" && !Directory.Exists(parent)) throw new InvalidOperationException($"备份中的漫画目录已不存在：{parts[1]}");
        var target = Path.GetFullPath(Path.Combine(parent, parts[^1]));
        var boundary = parts[0] == "app" ? settingsDirectory : root;
        if (!IsUnder(target, boundary)) throw new InvalidDataException("恢复路径超出数据目录。");
        for (var current = new DirectoryInfo(parent); current is not null; current = current.Parent)
        {
            if (current.Exists && current.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new InvalidDataException("恢复目标包含目录链接。");
            if (LibraryStorageService.SamePath(current.FullName, boundary)) break;
        }
        foreach (var file in new[] { target, target + ".bak" })
            if (File.Exists(file) && File.GetAttributes(file).HasFlag(FileAttributes.ReparsePoint)) throw new InvalidDataException("恢复目标是文件链接。");
        return target;
    }

    private static bool IsUnder(string path, string root) => path.StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    private static void EnsureSameLibrary(string source, string current)
    {
        if (!LibraryStorageService.SamePath(source, current)) throw new InvalidOperationException("请先将下载目录切换到备份对应的书库，再恢复数据。");
    }
    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    private static bool IsValidJson(string key, string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            var array = key.EndsWith("search-history.json", StringComparison.Ordinal) || key.StartsWith("state/", StringComparison.Ordinal);
            if (document.RootElement.ValueKind != (array ? JsonValueKind.Array : JsonValueKind.Object)) return false;
            return key switch
            {
                "app/app-settings.json" => JsonSerializer.Deserialize<ApplicationSettingsService.UserPreferences>(text, Options) is not null,
                "app/reading-progress.json" => JsonSerializer.Deserialize<ReadingProgressService.ReadingProgressDocument>(text, Options)?.Entries is { } entries && entries.All(entry => entry is not null),
                "app/reader-preferences.json" => JsonSerializer.Deserialize<ReaderPreferenceService.ReaderPreferenceDocument>(text, Options) is { Manga: not null, Shortcuts: not null },
                "app/search-history.json" => JsonSerializer.Deserialize<List<SearchHistoryEntry>>(text, Options) is { } searches && searches.All(entry => entry is not null),
                "state/task_history.json" => JsonSerializer.Deserialize<List<DownloadHistoryItem>>(text, Options) is { } history && history.All(entry => entry is not null),
                "state/task_queue.json" => JsonSerializer.Deserialize<List<DownloadSchedulerService.QueueItem>>(text, Options) is { } queue && queue.All(DownloadSchedulerService.IsValidQueueItem),
                _ => JsonSerializer.Deserialize<LibraryMetadata>(text, Options) is { Authors: not null, DownloadedChapters: not null, LastFailedChapterRecords: not null },
            };
        }
        catch (JsonException) { return false; }
    }

    private sealed class BackupManifest
    {
        public int Version { get; set; } = 1;
        public string StorageRoot { get; set; } = string.Empty;
        public DateTimeOffset CreatedAt { get; set; }
        public Dictionary<string, string> Hashes { get; set; } = [];
    }
}
