using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Comic.WinUI.Models;

public sealed class LibraryItemDto
{
    [JsonPropertyName("manga_title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("site_name")]
    public string SiteName { get; set; } = string.Empty;

    [JsonPropertyName("author")]
    public string Author { get; set; } = string.Empty;

    [JsonPropertyName("root_dir")]
    public string RootDir { get; set; } = string.Empty;

    [JsonPropertyName("manga_url")]
    public string MangaUrl { get; set; } = string.Empty;

    [JsonPropertyName("cover_url")]
    public string CoverUrl { get; set; } = string.Empty;

    [JsonPropertyName("downloaded_chapter_count")]
    public int DownloadedChapterCount { get; set; }

    [JsonPropertyName("last_downloaded_chapter_title")]
    public string LastDownloadedChapterTitle { get; set; } = string.Empty;

    [JsonPropertyName("is_favorite")]
    public bool IsFavorite { get; set; }

    [JsonPropertyName("duplicate_directory_count")]
    public int DuplicateDirectoryCount { get; set; }
}

public sealed class LibraryListResponse
{
    [JsonPropertyName("items")]
    public List<LibraryItemDto> Items { get; set; } = [];

    [JsonPropertyName("total")]
    public int Total { get; set; }

    [JsonPropertyName("page")]
    public int Page { get; set; }

    [JsonPropertyName("page_size")]
    public int PageSize { get; set; }
}

public sealed class LibraryCheckUpdatesResponse
{
    [JsonPropertyName("items")]
    public List<LibraryUpdateItem> Items { get; set; } = [];

    [JsonPropertyName("checked_count")]
    public int CheckedCount { get; set; }

    [JsonPropertyName("failed_count")]
    public int FailedCount { get; set; }
}

public sealed class JmLibraryImportPreview
{
    public string SourceRoot { get; init; } = string.Empty;
    public int DetectedMangaCount { get; init; }
    public int NewMangaCount { get; init; }
    public int ExistingMangaCount { get; init; }
    public int ImportableChapterCount { get; init; }
    public int ExistingChapterCount { get; init; }
    public int ConflictChapterCount { get; init; }
    public int SkippedDirectoryCount { get; init; }
    public int ImportableImageCount { get; init; }
    public long ImportableBytes { get; init; }

    public bool HasImportableContent => ImportableChapterCount > 0;
}

public sealed class JmLibraryImportResult
{
    public int ImportedMangaCount { get; init; }
    public int UpdatedMangaCount { get; init; }
    public int ImportedChapterCount { get; init; }
    public int ExistingChapterCount { get; init; }
    public int ConflictChapterCount { get; init; }
    public int SkippedDirectoryCount { get; init; }
    public int FailedMangaCount { get; init; }
    public List<string> Failures { get; init; } = [];
}

public sealed class LibraryUpdateItem
{
    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("manga_url")]
    public string MangaUrl { get; set; } = string.Empty;

    [JsonPropertyName("cover_url")]
    public string CoverUrl { get; set; } = string.Empty;

    [JsonPropertyName("author")]
    public string Author { get; set; } = string.Empty;

    [JsonPropertyName("root_dir")]
    public string RootDir { get; set; } = string.Empty;

    [JsonPropertyName("has_update")]
    public bool HasUpdate { get; set; }

    [JsonPropertyName("remote_chapter_count")]
    public int RemoteChapterCount { get; set; }

    [JsonPropertyName("local_chapter_count")]
    public int LocalChapterCount { get; set; }

    [JsonPropertyName("missing_chapters")]
    public List<MangaChapterDto> MissingChapters { get; set; } = [];

    [JsonPropertyName("latest_chapter")]
    public string LatestChapter { get; set; } = string.Empty;

    [JsonPropertyName("check_failed")]
    public bool CheckFailed { get; set; }

    [JsonPropertyName("error_message")]
    public string ErrorMessage { get; set; } = string.Empty;
}

public sealed class ExportCbzResponse
{
    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("task_id")]
    public string TaskId { get; set; } = string.Empty;

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;
}

public sealed class ExportCbzProgress
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("manga_title")]
    public string MangaTitle { get; set; } = string.Empty;

    [JsonPropertyName("current_chapter")]
    public string CurrentChapter { get; set; } = string.Empty;

    [JsonPropertyName("current_index")]
    public int CurrentIndex { get; set; }

    [JsonPropertyName("total_chapters")]
    public int TotalChapters { get; set; }

    [JsonPropertyName("exported_count")]
    public int ExportedCount { get; set; }

    [JsonPropertyName("export_dir")]
    public string ExportDir { get; set; } = string.Empty;

    [JsonPropertyName("skipped_chapters")]
    public List<string> SkippedChapters { get; set; } = [];

    [JsonPropertyName("error")]
    public string? Error { get; set; }
}
