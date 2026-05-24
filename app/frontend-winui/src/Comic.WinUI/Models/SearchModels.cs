using System;
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

public sealed class SearchHistoryEntry
{
    [JsonPropertyName("keyword")]
    public string Keyword { get; set; } = string.Empty;

    [JsonPropertyName("site_key")]
    public string SiteKey { get; set; } = string.Empty;

    [JsonPropertyName("site_name")]
    public string SiteName { get; set; } = string.Empty;

    [JsonPropertyName("timestamp")]
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.Now;

    [JsonPropertyName("result_count")]
    public int ResultCount { get; set; }
}

public sealed class RankingResponse
{
    [JsonPropertyName("items")]
    public List<RankingItem> Items { get; set; } = [];

    [JsonPropertyName("total")]
    public int Total { get; set; }

    [JsonPropertyName("section")]
    public string Section { get; set; } = string.Empty;

    [JsonPropertyName("available_sections")]
    public Dictionary<string, string> AvailableSections { get; set; } = new();

    [JsonPropertyName("is_single_page")]
    public bool IsSinglePage { get; set; }
}

public sealed class RankingItem
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

    [JsonPropertyName("section")]
    public string Section { get; set; } = string.Empty;

    [JsonPropertyName("detail_hint")]
    public string DetailHint { get; set; } = string.Empty;

    [JsonPropertyName("detail_section_label")]
    public string DetailSectionLabel { get; set; } = string.Empty;
}

public sealed class RankingSectionsResponse
{
    [JsonPropertyName("site")]
    public string Site { get; set; } = string.Empty;

    [JsonPropertyName("site_name")]
    public string SiteName { get; set; } = string.Empty;

    [JsonPropertyName("sections")]
    public Dictionary<string, string> Sections { get; set; } = new();
}
