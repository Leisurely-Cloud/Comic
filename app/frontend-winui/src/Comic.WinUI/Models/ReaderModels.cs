using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Comic.WinUI.Models;

public sealed class ReaderChaptersResponse
{
    [JsonPropertyName("manga_title")]
    public string MangaTitle { get; set; } = string.Empty;

    [JsonPropertyName("chapters")]
    public List<ReaderChapterDto> Chapters { get; set; } = [];
}

public sealed class ReaderChapterDto
{
    [JsonPropertyName("dir_name")]
    public string DirName { get; set; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("order")]
    public int Order { get; set; }

    [JsonPropertyName("image_count")]
    public int ImageCount { get; set; }
}

public sealed class ReaderImagesResponse
{
    [JsonPropertyName("images")]
    public List<string> Images { get; set; } = [];
}
