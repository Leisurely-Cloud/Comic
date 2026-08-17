namespace Comic.WinUI.Models;

public static class SiteCatalog
{
    public const string Key = "jmcomic";
    public const string DisplayName = "禁漫天堂";

    public static string GetDisplayName(string siteKey)
    {
        return string.Equals(siteKey, Key, System.StringComparison.OrdinalIgnoreCase)
            ? DisplayName
            : "未知来源";
    }

    public static string GetKey(string displayName) => Key;
}
