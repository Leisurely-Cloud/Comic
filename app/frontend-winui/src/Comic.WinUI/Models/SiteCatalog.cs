using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Comic.WinUI.Models;

public sealed class SiteOption
{
    public required string Key { get; init; }

    public required string DisplayName { get; init; }

    public override string ToString() => DisplayName;
}

public static class SiteCatalog
{
    private static readonly IReadOnlyDictionary<string, string> Names = new Dictionary<string, string>
    {
        [""] = "全部站点",
        ["baozimh"] = "包子漫画",
        ["mangacopy"] = "拷贝漫画",
        ["manhuagui"] = "漫画柜",
    };

    public static ReadOnlyCollection<SiteOption> DownloadSites { get; } = new(
    [
        new SiteOption { Key = "baozimh", DisplayName = "包子漫画" },
        new SiteOption { Key = "mangacopy", DisplayName = "拷贝漫画" },
        new SiteOption { Key = "manhuagui", DisplayName = "漫画柜" },
    ]);

    public static ReadOnlyCollection<SiteOption> LibrarySites { get; } = new(
    [
        new SiteOption { Key = "", DisplayName = "全部站点" },
        new SiteOption { Key = "baozimh", DisplayName = "包子漫画" },
        new SiteOption { Key = "mangacopy", DisplayName = "拷贝漫画" },
        new SiteOption { Key = "manhuagui", DisplayName = "漫画柜" },
    ]);

    public static string GetDisplayName(string siteKey)
    {
        return Names.TryGetValue(siteKey ?? string.Empty, out var displayName)
            ? displayName
            : (siteKey ?? string.Empty);
    }
}
