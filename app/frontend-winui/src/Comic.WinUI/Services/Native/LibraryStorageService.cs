using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Comic.WinUI.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualBasic.FileIO;

namespace Comic.WinUI.Services.Native;

/// <summary>
/// 书库存储服务:管理存储根目录、漫画元数据读写,以及章节目录的枚举与路径校验。
/// 下载调度、CBZ 导出与阅读器都依赖本服务提供的统一目录约定。
/// </summary>
public sealed class LibraryStorageService
{
    private static readonly string[] ImageExtensions = [".jpg", ".jpeg", ".png", ".webp", ".gif", ".bmp"];
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = SnakeCaseNamingPolicy.Instance,
        WriteIndented = true,
    };

    private readonly ILogger<LibraryStorageService> _logger;
    private readonly Action<string> _recycleDirectory;
    private readonly object _libraryCacheLock = new();
    private IReadOnlyList<LibraryEntry>? _libraryCache;
    private DateTime _libraryCacheAtUtc;
    private static readonly TimeSpan LibraryCacheLifetime = TimeSpan.FromSeconds(2);

    public LibraryStorageService(ApplicationSettingsService applicationSettings, ILogger<LibraryStorageService>? logger = null)
        : this(applicationSettings.StorageRoot, logger)
    {
    }

    internal LibraryStorageService(
        string storageRoot,
        ILogger<LibraryStorageService>? logger = null,
        Action<string>? recycleDirectory = null)
    {
        _logger = logger ?? NullLogger<LibraryStorageService>.Instance;
        _recycleDirectory = recycleDirectory ?? MoveDirectoryToRecycleBin;
        var requestedStorageRoot = Path.GetFullPath(storageRoot);
        Directory.CreateDirectory(requestedStorageRoot);
        StorageRoot = ResolveExistingDirectoryPath(requestedStorageRoot);
    }

    public string StorageRoot { get; private set; }

    public Task<SettingsResponse> GetSettingsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new SettingsResponse
        {
            StorageRoot = StorageRoot,
            LegacyRoot = string.Empty,
            DownloadRunnerConfigured = true,
            SupportedSites = [SiteCatalog.Key],
        });
    }

    /// <summary>切换存储根目录:创建并解析目标目录,更新 <see cref="StorageRoot"/>。</summary>
    public string SwitchStorageRoot(string requestedRoot)
    {
        var requested = Path.GetFullPath(Environment.ExpandEnvironmentVariables(requestedRoot.Trim()));
        Directory.CreateDirectory(requested);
        StorageRoot = ResolveExistingDirectoryPath(requested);
        InvalidateLibraryCache();
        return StorageRoot;
    }

    public void InvalidateLibraryCache()
    {
        lock (_libraryCacheLock)
        {
            _libraryCache = null;
            _libraryCacheAtUtc = default;
        }
    }

    // ---- 元数据 ----

    public LibraryMetadata? LoadLibraryMetadata(string rootDirectory)
    {
        try
        {
            var path = Path.Combine(rootDirectory, "元数据.json");
            if (!File.Exists(path)) return null;

            var json = File.ReadAllText(path);
            var metadata = JsonSerializer.Deserialize<LibraryMetadata>(json, JsonOptions) ?? new LibraryMetadata();
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return metadata;

            if (string.IsNullOrWhiteSpace(metadata.MangaTitle))
            {
                metadata.MangaTitle = ReadMetadataString(document.RootElement, "name", "title", "comic_name", "book_name");
            }

            if (metadata.Authors.Count == 0)
            {
                metadata.Authors = ReadMetadataStrings(document.RootElement, "author", "authors", "writer", "writers", "artist", "artists");
            }

            return metadata;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "读取漫画元数据失败: {Path}", Path.Combine(rootDirectory, "元数据.json"));
            return null;
        }
    }

    public void SaveLibraryMetadata(
        JmMangaInfo manga,
        string mangaUrl,
        string rootDirectory,
        bool completed,
        IReadOnlyCollection<FailedChapterRecord> failures)
    {
        if (manga is null || string.IsNullOrWhiteSpace(rootDirectory)) return;
        var downloaded = EnumerateChapterContents(rootDirectory).Select(chapter => new DownloadedChapterRecord
        {
            Order = ChapterOrder(chapter.Directory.Name),
            DirName = chapter.Directory.Name,
            Title = ChapterTitle(chapter.Directory.Name),
            ImageCount = chapter.Images.Count,
        }).OrderBy(record => record.Order).ToList();
        var now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        var existing = LoadLibraryMetadata(rootDirectory);
        var metadata = new LibraryMetadata
        {
            SchemaVersion = 1,
            SiteKey = SiteCatalog.Key,
            SiteName = SiteCatalog.DisplayName,
            MangaTitle = manga.Title,
            Authors = manga.Authors
                .Where(author => !string.IsNullOrWhiteSpace(author))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList(),
            MangaUrl = mangaUrl,
            RootDirectory = rootDirectory,
            CoverUrl = manga.CoverUrl,
            TotalChapters = manga.Chapters.Count,
            DownloadedChapterCount = downloaded.Count,
            LastDownloadedChapterTitle = downloaded.LastOrDefault()?.Title ?? string.Empty,
            LastDownloadedChapterOrder = downloaded.LastOrDefault()?.Order,
            DownloadedChapters = downloaded,
            Completed = completed && failures.Count == 0 && downloaded.Count >= manga.Chapters.Count,
            CreatedAt = existing?.CreatedAt is { Length: > 0 } created ? created : now,
            SavedAt = now,
            LastFailedChapterRecords = failures.ToList(),
            LastFailedChapterCount = failures.Count,
        };
        WriteJsonAtomically(Path.Combine(rootDirectory, "元数据.json"), metadata);
        InvalidateLibraryCache();
    }

    public IReadOnlyList<LibraryEntry> EnumerateLibraryEntries()
    {
        lock (_libraryCacheLock)
        {
            if (_libraryCache is not null && DateTime.UtcNow - _libraryCacheAtUtc < LibraryCacheLifetime)
            {
                return _libraryCache;
            }
        }

        var entries = new List<LibraryEntry>();
        foreach (var directory in new DirectoryInfo(StorageRoot).EnumerateDirectories())
        {
            if (directory.Name.StartsWith('.') || directory.Name.EndsWith("_CBZ", StringComparison.OrdinalIgnoreCase)) continue;
            var chapters = EnumerateChapterDirectories(directory.FullName);
            if (chapters.Count == 0) continue;
            var metadata = LoadLibraryMetadata(directory.FullName);
            var ordered = OrderChapterDirectories(chapters).ToList();
            var fallbackCover = EnumerateImages(directory.FullName).FirstOrDefault()
                ?? EnumerateImages(ordered[0].FullName).FirstOrDefault()
                ?? string.Empty;
            entries.Add(new LibraryEntry(
                metadata?.MangaTitle is { Length: > 0 } metadataTitle ? metadataTitle : directory.Name,
                metadata is null ? string.Empty : string.Join("、", metadata.Authors
                    .Where(author => !string.IsNullOrWhiteSpace(author))
                    .Distinct(StringComparer.OrdinalIgnoreCase)),
                metadata?.SiteName is { Length: > 0 } siteName ? siteName : "本地漫画",
                directory.FullName,
                metadata?.MangaUrl ?? string.Empty,
                metadata?.CoverUrl is { Length: > 0 } coverUrl ? coverUrl : fallbackCover,
                ordered.Count,
                metadata?.LastDownloadedChapterTitle is { Length: > 0 } lastTitle ? lastTitle : ChapterTitle(ordered[^1].Name),
                ParseDate(metadata?.SavedAt) ?? directory.LastWriteTime,
                metadata?.IsFavorite ?? false,
                metadata?.Completed ?? false,
                0));
        }
        var deduplicated = entries
            .GroupBy(LibraryIdentityKey, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var best = group
                    .OrderByDescending(entry => entry.Completed)
                    .ThenByDescending(entry => entry.DownloadedChapterCount)
                    .ThenByDescending(entry => entry.SavedAt)
                    .First();
                return best with
                {
                    IsFavorite = group.Any(entry => entry.IsFavorite),
                    DuplicateDirectoryCount = group.Count() - 1,
                };
            })
            .ToList();
        var result = deduplicated.AsReadOnly();
        lock (_libraryCacheLock)
        {
            _libraryCache = result;
            _libraryCacheAtUtc = DateTime.UtcNow;
        }
        return result;
    }

    private static string LibraryIdentityKey(LibraryEntry entry)
    {
        var mangaId = JmComicService.ParseMangaId(entry.MangaUrl).MangaId;
        return mangaId is null
            ? $"path:{entry.RootDirectory}"
            : $"{SiteCatalog.Key}:{mangaId}";
    }

    /// <summary>切换漫画的收藏状态,返回切换后的状态。</summary>
    public bool ToggleFavorite(string rootDirectory)
    {
        var resolvedRoot = ResolveLibraryRoot(rootDirectory);
        var metadata = LoadLibraryMetadata(resolvedRoot) ?? new LibraryMetadata
        {
            SiteKey = SiteCatalog.Key,
            SiteName = SiteCatalog.DisplayName,
            MangaTitle = Path.GetFileName(resolvedRoot),
            RootDirectory = resolvedRoot,
            MangaUrl = string.Empty,
        };
        metadata.IsFavorite = !metadata.IsFavorite;
        metadata.SavedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        WriteJsonAtomically(Path.Combine(resolvedRoot, "元数据.json"), metadata);
        InvalidateLibraryCache();
        return metadata.IsFavorite;
    }

    /// <summary>
    /// 将漫画目录移入 Windows 回收站。同一站点漫画 ID 对应的重复目录会一并处理，
    /// 避免删除当前展示目录后，被去重隐藏的旧目录重新出现在书库中。
    /// </summary>
    public int DeleteManga(string rootDirectory)
    {
        var resolvedRoot = ResolveLibraryRoot(rootDirectory);
        var selectedMetadata = LoadLibraryMetadata(resolvedRoot);
        var mangaId = JmComicService.ParseMangaId(selectedMetadata?.MangaUrl ?? string.Empty).MangaId;
        var targets = new List<string> { resolvedRoot };

        if (mangaId is not null)
        {
            foreach (var directory in new DirectoryInfo(StorageRoot).EnumerateDirectories())
            {
                if (SamePath(directory.FullName, resolvedRoot) ||
                    directory.Name.StartsWith('.') ||
                    directory.Name.EndsWith("_CBZ", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var metadata = LoadLibraryMetadata(directory.FullName);
                var candidateId = JmComicService.ParseMangaId(metadata?.MangaUrl ?? string.Empty).MangaId;
                if (candidateId != mangaId || EnumerateChapterDirectories(directory.FullName).Count == 0) continue;

                // 每个待删除目录都必须独立通过受管路径校验，不能仅凭元数据中的 ID 删除。
                targets.Add(ResolveLibraryRoot(directory.FullName));
            }
        }

        var uniqueTargets = targets
            .Distinct(StringComparer.OrdinalIgnoreCase)
            // 先处理重复目录，最后处理用户当前看到的主目录。
            .OrderBy(path => SamePath(path, resolvedRoot))
            .ToList();
        var deletedCount = 0;
        try
        {
            foreach (var target in uniqueTargets)
            {
                _recycleDirectory(target);
                deletedCount++;
            }
        }
        finally
        {
            InvalidateLibraryCache();
        }

        return deletedCount;
    }

    // ---- 目录与文件工具 ----

    public List<DirectoryInfo> EnumerateChapterDirectories(string rootDirectory)
    {
        try
        {
            return new DirectoryInfo(rootDirectory).EnumerateDirectories()
                .Where(directory => IsPotentialChapterDirectoryName(directory.Name) && ContainsImageFile(directory.FullName))
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "枚举章节目录失败: {Path}", rootDirectory);
            return [];
        }
    }

    internal List<ChapterContent> EnumerateChapterContents(string rootDirectory)
    {
        try
        {
            var chapters = new List<ChapterContent>();
            foreach (var directory in new DirectoryInfo(rootDirectory).EnumerateDirectories())
            {
                if (!IsPotentialChapterDirectoryName(directory.Name)) continue;
                var images = EnumerateImages(directory.FullName);
                if (images.Count > 0) chapters.Add(new ChapterContent(directory, images));
            }
            return chapters;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "枚举章节内容失败: {Path}", rootDirectory);
            return [];
        }
    }

    public List<string> EnumerateImages(string chapterDirectory)
    {
        try
        {
            return Directory.EnumerateFiles(chapterDirectory)
                .Where(file => ImageExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
                .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "枚举图片失败: {Path}", chapterDirectory);
            return [];
        }
    }

    public bool ContainsImageFile(string directory)
    {
        try
        {
            return Directory.EnumerateFiles(directory)
                .Any(file => ImageExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }

    public static bool IsPotentialChapterDirectoryName(string name)
    {
        return !string.IsNullOrWhiteSpace(name) &&
            Path.GetFileName(name) == name &&
            !name.StartsWith(".", StringComparison.Ordinal) &&
            !name.EndsWith("_CBZ", StringComparison.OrdinalIgnoreCase);
    }

    public IOrderedEnumerable<DirectoryInfo> OrderChapterDirectories(IEnumerable<DirectoryInfo> directories) =>
        directories.OrderBy(directory => ChapterOrder(directory.Name))
            .ThenBy(directory => directory.Name, StringComparer.OrdinalIgnoreCase);

    public static int ChapterOrder(string directoryName)
    {
        var match = Regex.Match(directoryName, @"\d+");
        return match.Success && int.TryParse(match.Value, out var order) ? order : int.MaxValue;
    }

    public static string ChapterTitle(string directoryName)
    {
        var separator = directoryName.IndexOf('_');
        return separator > 0 &&
            separator < directoryName.Length - 1 &&
            directoryName[..separator].All(char.IsDigit)
                ? directoryName[(separator + 1)..]
                : directoryName;
    }

    // ---- 受管路径校验 ----

    public string ResolveLibraryRoot(string rootDirectory)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory)) throw new ArgumentException("漫画目录不存在");
        if (!Directory.Exists(rootDirectory)) throw new DirectoryNotFoundException("漫画目录不存在");
        var candidate = ResolveExistingDirectoryPath(rootDirectory);
        if (!SamePath(Directory.GetParent(candidate)?.FullName, StorageRoot) || EnumerateChapterDirectories(candidate).Count == 0)
            throw new UnauthorizedAccessException("漫画目录不在受管理的书库中");
        return candidate;
    }

    public string ResolveChapterDirectory(string rootDirectory, string chapterDirectoryName)
    {
        var root = ResolveLibraryRoot(rootDirectory);
        if (string.IsNullOrWhiteSpace(chapterDirectoryName) ||
            Path.GetFileName(chapterDirectoryName) != chapterDirectoryName ||
            !IsPotentialChapterDirectoryName(chapterDirectoryName))
            throw new ArgumentException("章节目录名称无效");
        var chapterPath = Path.GetFullPath(Path.Combine(root, chapterDirectoryName));
        if (!Directory.Exists(chapterPath))
            throw new DirectoryNotFoundException("章节目录不存在");
        var chapter = ResolveExistingDirectoryPath(chapterPath);
        if (!SamePath(Directory.GetParent(chapter)?.FullName, root) || !ContainsImageFile(chapter))
            throw new UnauthorizedAccessException("章节目录不在当前漫画目录中");
        return chapter;
    }

    public string ResolveReaderImage(string imagePath)
    {
        if (string.IsNullOrWhiteSpace(imagePath)) throw new ArgumentException("图片路径无效");
        if (!File.Exists(imagePath) || !ImageExtensions.Contains(Path.GetExtension(imagePath), StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException("图片路径无效");
        var candidate = ResolveExistingFilePath(imagePath);
        var imageDirectory = Directory.GetParent(candidate)?.FullName;
        if (imageDirectory is null)
            throw new UnauthorizedAccessException("图片不在受管理的书库中");

        var imageDirectoryParent = Directory.GetParent(imageDirectory)?.FullName;
        var isMangaCover = SamePath(imageDirectoryParent, StorageRoot) &&
            EnumerateChapterDirectories(imageDirectory).Count > 0;
        if (isMangaCover) return candidate;

        var mangaDirectory = imageDirectoryParent;
        var isChapterImage = mangaDirectory is not null &&
            IsPotentialChapterDirectoryName(Path.GetFileName(imageDirectory)) &&
            SamePath(Directory.GetParent(mangaDirectory)?.FullName, StorageRoot) &&
            ContainsImageFile(imageDirectory);
        if (!isChapterImage)
            throw new UnauthorizedAccessException("图片不在受管理的书库中");
        return candidate;
    }

    // ---- 通用工具 ----

    public void WriteJsonAtomically<T>(string path, T value)
    {
        try
        {
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(value, JsonOptions), new System.Text.UTF8Encoding(false));
            File.Move(temporary, path, true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "原子写入失败: {Path}", path);
        }
    }

    public static DateTime? ParseDate(string? value) => DateTime.TryParse(value, out var result) ? result : null;

    public static bool SamePath(string? left, string? right) =>
        !string.IsNullOrWhiteSpace(left) && !string.IsNullOrWhiteSpace(right) &&
        string.Equals(Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);

    public static string ResolveExistingDirectoryPath(string path)
    {
        var directory = new DirectoryInfo(Path.GetFullPath(path));
        return Path.GetFullPath(directory.ResolveLinkTarget(true)?.FullName ?? directory.FullName);
    }

    public static string ResolveExistingFilePath(string path)
    {
        var file = new FileInfo(Path.GetFullPath(path));
        return Path.GetFullPath(file.ResolveLinkTarget(true)?.FullName ?? file.FullName);
    }

    private static void MoveDirectoryToRecycleBin(string directory)
    {
        FileSystem.DeleteDirectory(
            directory,
            UIOption.OnlyErrorDialogs,
            RecycleOption.SendToRecycleBin,
            UICancelOption.ThrowException);
    }

    private static string ReadMetadataString(JsonElement root, params string[] propertyNames)
    {
        foreach (var property in root.EnumerateObject())
        {
            if (!propertyNames.Contains(property.Name, StringComparer.OrdinalIgnoreCase) ||
                property.Value.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var value = property.Value.GetString()?.Trim();
            if (!string.IsNullOrWhiteSpace(value)) return value;
        }

        return string.Empty;
    }

    private static List<string> ReadMetadataStrings(JsonElement root, params string[] propertyNames)
    {
        var values = new List<string>();
        foreach (var property in root.EnumerateObject())
        {
            if (!propertyNames.Contains(property.Name, StringComparer.OrdinalIgnoreCase)) continue;

            if (property.Value.ValueKind == JsonValueKind.String)
            {
                AddMetadataString(values, property.Value.GetString());
            }
            else if (property.Value.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in property.Value.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String) AddMetadataString(values, item.GetString());
                }
            }
        }

        return values.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static void AddMetadataString(List<string> values, string? value)
    {
        var trimmed = value?.Trim();
        if (!string.IsNullOrWhiteSpace(trimmed)) values.Add(trimmed);
    }
}

public sealed record LibraryEntry(
    string Title,
    string Author,
    string SiteName,
    string RootDirectory,
    string MangaUrl,
    string CoverUrl,
    int DownloadedChapterCount,
    string LastDownloadedChapterTitle,
    DateTime SavedAt,
    bool IsFavorite,
    bool Completed,
    int DuplicateDirectoryCount);

internal sealed record ChapterContent(DirectoryInfo Directory, IReadOnlyList<string> Images);

public sealed class LibraryMetadata
{
    [JsonPropertyName("schema_version")] public int SchemaVersion { get; set; }
    [JsonPropertyName("site_key")] public string SiteKey { get; set; } = string.Empty;
    [JsonPropertyName("site_name")] public string SiteName { get; set; } = string.Empty;
    [JsonPropertyName("manga_title")] public string MangaTitle { get; set; } = string.Empty;
    [JsonPropertyName("authors")] public List<string> Authors { get; set; } = [];
    [JsonPropertyName("manga_url")] public string MangaUrl { get; set; } = string.Empty;
    [JsonPropertyName("root_dir")] public string RootDirectory { get; set; } = string.Empty;
    [JsonPropertyName("cover_url")] public string CoverUrl { get; set; } = string.Empty;
    [JsonPropertyName("total_chapters")] public int TotalChapters { get; set; }
    [JsonPropertyName("downloaded_chapter_count")] public int DownloadedChapterCount { get; set; }
    [JsonPropertyName("last_downloaded_chapter_title")] public string LastDownloadedChapterTitle { get; set; } = string.Empty;
    [JsonPropertyName("last_downloaded_chapter_order")] public int? LastDownloadedChapterOrder { get; set; }
    [JsonPropertyName("downloaded_chapters")] public List<DownloadedChapterRecord> DownloadedChapters { get; set; } = [];
    [JsonPropertyName("completed")] public bool Completed { get; set; }
    [JsonPropertyName("is_favorite")] public bool IsFavorite { get; set; }
    [JsonPropertyName("saved_at")] public string SavedAt { get; set; } = string.Empty;
    [JsonPropertyName("created_at")] public string CreatedAt { get; set; } = string.Empty;
    [JsonPropertyName("last_failed_chapter_records")] public List<FailedChapterRecord> LastFailedChapterRecords { get; set; } = [];
    [JsonPropertyName("last_failed_chapter_count")] public int LastFailedChapterCount { get; set; }
}

public sealed class DownloadedChapterRecord
{
    [JsonPropertyName("order")] public int Order { get; set; }
    [JsonPropertyName("dir_name")] public string DirName { get; set; } = string.Empty;
    [JsonPropertyName("title")] public string Title { get; set; } = string.Empty;
    [JsonPropertyName("image_count")] public int ImageCount { get; set; }
}

public sealed class FailedChapterRecord
{
    [JsonPropertyName("order")] public int Order { get; set; }
    [JsonPropertyName("slug")] public string Slug { get; set; } = string.Empty;
    [JsonPropertyName("title")] public string Title { get; set; } = string.Empty;
    [JsonPropertyName("reason")] public string Reason { get; set; } = string.Empty;
}
