using System.Collections.Generic;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

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

    [JsonPropertyName("download_speed_bytes_per_second")]
    public double DownloadSpeedBytesPerSecond { get; set; }

    [JsonPropertyName("local_skipped_chapter_count")]
    public int LocalSkippedChapterCount { get; set; }

    [JsonPropertyName("requested_chapter_count")]
    public int RequestedChapterCount { get; set; }

    [JsonPropertyName("task_error")]
    public ApiError? TaskError { get; set; }

    [JsonPropertyName("logs")]
    public List<DownloadLogEntry> Logs { get; set; } = [];

    [JsonPropertyName("chapters")]
    public List<DownloadChapterProgressDto> Chapters { get; set; } = [];
}

public sealed class DownloadChapterProgressDto
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; set; } = "pending";

    [JsonPropertyName("completed_images")]
    public int CompletedImages { get; set; }

    [JsonPropertyName("total_images")]
    public int TotalImages { get; set; }

    [JsonPropertyName("progress")]
    public double Progress { get; set; }

    [JsonPropertyName("error")]
    public string Error { get; set; } = string.Empty;

    [JsonPropertyName("directory_name")]
    public string DirectoryName { get; set; } = string.Empty;
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

public sealed class DownloadHistoryItem : ObservableObject
{
    private bool _isSelected;

    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;

    [JsonPropertyName("site")]
    public string SiteKey { get; set; } = string.Empty;

    [JsonPropertyName("manga_title")]
    public string MangaTitle { get; set; } = string.Empty;

    [JsonPropertyName("author")]
    public string Author { get; set; } = string.Empty;

    [JsonPropertyName("site_name")]
    public string SiteName { get; set; } = string.Empty;

    [JsonPropertyName("cover_url")]
    public string CoverUrl { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("progress")]
    public double Progress { get; set; }

    [JsonPropertyName("completed_chapter_count")]
    public int CompletedChapterCount { get; set; }

    [JsonPropertyName("total_chapter_count")]
    public int TotalChapterCount { get; set; }

    [JsonPropertyName("downloaded_this_run_chapter_count")]
    public int DownloadedThisRunChapterCount { get; set; }

    [JsonPropertyName("root_dir")]
    public string RootDir { get; set; } = string.Empty;

    [JsonPropertyName("task_error")]
    public ApiError? TaskError { get; set; }

    [JsonPropertyName("finished_at")]
    public string FinishedAt { get; set; } = string.Empty;

    [JsonIgnore]
    public string DisplayTitle => string.IsNullOrWhiteSpace(MangaTitle) ? "未命名漫画" : MangaTitle;

    [JsonIgnore]
    public string AuthorDisplay => string.IsNullOrWhiteSpace(Author) ? "作者未知" : Author;

    [JsonIgnore]
    public string SiteDisplay => string.IsNullOrWhiteSpace(SiteName)
        ? SiteCatalog.GetDisplayName(SiteKey)
        : SiteName;

    [JsonIgnore]
    public string StatusLabel => Status switch
    {
        "pending" => "等待中",
        "running" => "下载中",
        "paused" => "已暂停",
        "pausing" => "正在暂停",
        "stopping" => "正在停止",
        "stopped" => "已停止",
        "completed" => "已完成",
        "partial" => "部分完成",
        "failed" => "下载失败",
        _ => "状态未知",
    };

    [JsonIgnore]
    public string StatusGlyph => Status switch
    {
        "completed" => "\uE73E",
        "partial" => "\uE9D9",
        "failed" => "\uE783",
        "stopped" => "\uE71A",
        _ => "\uE896",
    };

    [JsonIgnore]
    public string ChapterProgressText => TotalChapterCount > 0
        ? $"已完成 {CompletedChapterCount} / {TotalChapterCount} 章" +
          (DownloadedThisRunChapterCount > 0
              ? $" · 本次补下载 {DownloadedThisRunChapterCount} 章"
              : string.Empty)
        : "暂无章节统计";

    [JsonIgnore]
    public string AggregateChapterProgressText => TotalChapterCount > 0
        ? $"已完成 {CompletedChapterCount} / {TotalChapterCount} 章"
        : "暂无章节统计";

    [JsonIgnore]
    public string ThisRunProgressText => DownloadedThisRunChapterCount > 0
        ? $"本次补下载 {DownloadedThisRunChapterCount} 章"
        : string.Empty;

    [JsonIgnore]
    public bool HasThisRunProgress => DownloadedThisRunChapterCount > 0;

    [JsonIgnore]
    public string ProgressText => $"{Math.Clamp(Progress, 0, 100):0}%";

    [JsonIgnore]
    public string FinishedAtDisplay => DateTime.TryParse(FinishedAt, out var finishedAt)
        ? finishedAt.ToString("yyyy-MM-dd HH:mm")
        : "时间未知";

    [JsonIgnore]
    public bool HasError => TaskError is not null && !string.IsNullOrWhiteSpace(TaskError.Message);

    [JsonIgnore]
    public string ErrorText => TaskError?.Message ?? string.Empty;

    [JsonIgnore]
    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
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
