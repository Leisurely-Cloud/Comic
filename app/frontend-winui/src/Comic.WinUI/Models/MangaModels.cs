using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Comic.WinUI.Models;

public sealed class MangaResolveRequest
{
    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;

    [JsonPropertyName("site")]
    public string SiteKey { get; set; } = string.Empty;
}

public sealed class MangaResolveResponse
{
    [JsonPropertyName("id")]
    public string MangaId { get; set; } = string.Empty;

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

    [JsonPropertyName("author")]
    public string Author { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("tags")]
    public List<string> Tags { get; set; } = [];

    [JsonPropertyName("added_at")]
    public string AddedAt { get; set; } = string.Empty;

    [JsonPropertyName("total_views")]
    public string TotalViews { get; set; } = string.Empty;

    [JsonPropertyName("likes")]
    public string Likes { get; set; } = string.Empty;

    [JsonPropertyName("comment_count")]
    public string CommentCount { get; set; } = string.Empty;

    [JsonPropertyName("is_favorite")]
    public bool IsFavorite { get; set; }
}

public sealed class MangaChapterDto
{
    [JsonPropertyName("order")]
    public int Order { get; set; }

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;
}
