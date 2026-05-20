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
