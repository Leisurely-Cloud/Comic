using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Comic.WinUI.Models;

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

    [JsonPropertyName("manga_title")]
    public string MangaTitle { get; set; } = string.Empty;

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

public sealed class DownloadHistoryItem
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;

    [JsonPropertyName("site")]
    public string SiteKey { get; set; } = string.Empty;

    [JsonPropertyName("manga_title")]
    public string MangaTitle { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("progress")]
    public double Progress { get; set; }

    [JsonPropertyName("completed_chapter_count")]
    public int CompletedChapterCount { get; set; }

    [JsonPropertyName("total_chapter_count")]
    public int TotalChapterCount { get; set; }

    [JsonPropertyName("root_dir")]
    public string RootDir { get; set; } = string.Empty;

    [JsonPropertyName("task_error")]
    public ApiError? TaskError { get; set; }

    [JsonPropertyName("finished_at")]
    public string FinishedAt { get; set; } = string.Empty;
}

public sealed class DownloadHistoryResponse
{
    [JsonPropertyName("items")]
    public List<DownloadHistoryItem> Items { get; set; } = [];

    [JsonPropertyName("total")]
    public int Total { get; set; }

    [JsonPropertyName("page")]
    public int Page { get; set; }

    [JsonPropertyName("page_size")]
    public int PageSize { get; set; }
}

public sealed class BatchActionResponse
{
    [JsonPropertyName("stopped")]
    public List<string> Stopped { get; set; } = [];

    [JsonPropertyName("deleted")]
    public List<string> Deleted { get; set; } = [];

    [JsonPropertyName("failed")]
    public List<string> Failed { get; set; } = [];
}
