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
