using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Comic.WinUI.Models;

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
