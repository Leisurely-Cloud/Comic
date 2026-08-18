using Comic.WinUI.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Comic.WinUI.ViewModels;

public partial class SearchResultItemViewModel : ObservableObject
{
    [ObservableProperty]
    public partial string Title { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string MangaUrl { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string CoverUrl { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string LatestChapter { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Author { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string UpdateTime { get; set; } = string.Empty;

    public static SearchResultItemViewModel FromSearch(SearchResultItem item)
    {
        return new SearchResultItemViewModel
        {
            Title = item.Title,
            MangaUrl = item.Url,
            CoverUrl = item.CoverUrl,
            LatestChapter = item.LatestChapter,
            Author = item.Author,
            UpdateTime = item.UpdateTime,
        };
    }

    /// <summary>从精确解析结果(JM 编号/链接)构建列表项。</summary>
    public static SearchResultItemViewModel FromResolved(MangaResolveResponse detail)
    {
        return new SearchResultItemViewModel
        {
            Title = detail.Title,
            MangaUrl = detail.MangaUrl,
            CoverUrl = detail.CoverUrl,
            LatestChapter = detail.LatestChapter,
            UpdateTime = string.Empty,
        };
    }
}
