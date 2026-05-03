using System.Text.Json;
using System.Text.Json.Serialization;

namespace Comic.WinUI.Models;

/// <summary>
/// Naming policy that converts camelCase/PascalCase property names to snake_case.
/// </summary>
internal sealed class SnakeCaseNamingPolicy : JsonNamingPolicy
{
    public static readonly SnakeCaseNamingPolicy Instance = new();

    public override string ConvertName(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;

        var sb = new System.Text.StringBuilder(name.Length + 4);
        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];
            if (char.IsUpper(c))
            {
                if (i > 0) sb.Append('_');
                sb.Append(char.ToLowerInvariant(c));
            }
            else
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }
}

public sealed class ApiError
{
    [JsonPropertyName("code")]
    public string Code { get; set; } = string.Empty;

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;
}

public sealed class HealthResponse
{
    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("storage_root")]
    public string StorageRoot { get; set; } = string.Empty;

    [JsonPropertyName("pid")]
    public int Pid { get; set; }
}

public sealed class MangaResolveRequest
{
    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;

    [JsonPropertyName("site")]
    public string SiteKey { get; set; } = string.Empty;
}

public sealed class MangaResolveResponse
{
    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("site_name")]
    public string SiteName { get; set; } = string.Empty;

    [JsonPropertyName("url")]
    public string MangaUrl { get; set; } = string.Empty;

    [JsonPropertyName("latest_chapter")]
    public string LatestChapter { get; set; } = string.Empty;

    [JsonPropertyName("chapters")]
    public List<MangaChapterDto> Chapters { get; set; } = [];

    [JsonPropertyName("cover_url")]
    public string CoverUrl { get; set; } = string.Empty;

    [JsonPropertyName("detail_hint")]
    public string DetailHint { get; set; } = string.Empty;
}

public sealed class MangaChapterDto
{
    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;
}

public sealed class DownloadCreateRequest
{
    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;

    [JsonPropertyName("site")]
    public string SiteKey { get; set; } = string.Empty;

    [JsonPropertyName("source")]
    public string Source { get; set; } = string.Empty;

    [JsonPropertyName("chapters")]
    public List<string>? Chapters { get; set; }
}

public sealed class DownloadTaskDto
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;

    [JsonPropertyName("site")]
    public string SiteKey { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("status_text")]
    public string StatusText { get; set; } = string.Empty;

    [JsonPropertyName("progress")]
    public double Progress { get; set; }

    [JsonPropertyName("task_error")]
    public ApiError? TaskError { get; set; }

    [JsonPropertyName("logs")]
    public List<DownloadLogEntry> Logs { get; set; } = [];
}

public sealed class DownloadLogEntry
{
    [JsonPropertyName("time")]
    public string Time { get; set; } = string.Empty;

    [JsonPropertyName("tag")]
    public string Tag { get; set; } = string.Empty;

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;
}

public sealed class DownloadListResponse
{
    [JsonPropertyName("items")]
    public List<DownloadTaskDto> Items { get; set; } = [];
}

public sealed class DownloadActionResponse
{
    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;
}

public sealed class LibraryItemDto
{
    [JsonPropertyName("manga_title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("site_name")]
    public string SiteName { get; set; } = string.Empty;

    [JsonPropertyName("root_dir")]
    public string RootDir { get; set; } = string.Empty;

    [JsonPropertyName("manga_url")]
    public string MangaUrl { get; set; } = string.Empty;

    [JsonPropertyName("downloaded_chapter_count")]
    public int DownloadedChapterCount { get; set; }

    [JsonPropertyName("last_downloaded_chapter_title")]
    public string LastDownloadedChapterTitle { get; set; } = string.Empty;
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

public sealed class SettingsResponse
{
    [JsonPropertyName("storage_root")]
    public string StorageRoot { get; set; } = string.Empty;

    [JsonPropertyName("legacy_root")]
    public string LegacyRoot { get; set; } = string.Empty;

    [JsonPropertyName("download_runner_configured")]
    public bool DownloadRunnerConfigured { get; set; }

    [JsonPropertyName("supported_sites")]
    public List<string> SupportedSites { get; set; } = [];
}

public sealed class LibraryCheckUpdatesResponse
{
    [JsonPropertyName("items")]
    public List<LibraryUpdateItem> Items { get; set; } = [];
}

public sealed class LibraryUpdateItem
{
    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("has_update")]
    public bool HasUpdate { get; set; }

    [JsonPropertyName("remote_chapter_count")]
    public int RemoteChapterCount { get; set; }

    [JsonPropertyName("local_chapter_count")]
    public int LocalChapterCount { get; set; }
}

public sealed class ExportCbzResponse
{
    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;
}

public sealed class SseDownloadEvent
{
    public int EventId { get; set; }
    public string EventName { get; set; } = string.Empty;
    public string JsonPayload { get; set; } = string.Empty;
}

public sealed class SearchResponse
{
    [JsonPropertyName("items")]
    public List<SearchResultItem> Items { get; set; } = [];

    [JsonPropertyName("total")]
    public int Total { get; set; }
}

public sealed class SearchResultItem
{
    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;

    [JsonPropertyName("cover_url")]
    public string CoverUrl { get; set; } = string.Empty;

    [JsonPropertyName("latest_chapter")]
    public string LatestChapter { get; set; } = string.Empty;

    [JsonPropertyName("update_time")]
    public string UpdateTime { get; set; } = string.Empty;
}
