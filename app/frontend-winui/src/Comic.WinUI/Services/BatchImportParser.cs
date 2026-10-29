using System.Text.RegularExpressions;
using Comic.WinUI.Models;

namespace Comic.WinUI.Services;

/// <summary>Only validates identifiers locally. No network requests or download side effects.</summary>
public static partial class BatchImportParser
{
    public const int MaximumItems = 100;
    public const int MaximumInputLength = 32768;

    public static BatchImportParseResult Parse(string? input)
    {
        var tokens = Separators().Split((input ?? string.Empty).Trim())
            .Where(value => value.Length > 0).ToArray();
        if (tokens.Length > MaximumItems || input?.Length > MaximumInputLength)
            return new([], tokens.Length, 0, true);

        var items = new List<BatchImportInput>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var duplicates = 0;
        foreach (var token in tokens)
        {
            var id = ParseId(token);
            if (id is null)
                items.Add(new(token, null, "无效输入：请使用正整数编号、JM 编号或 /album/编号 链接。"));
            else if (ids.Add(id))
                items.Add(new(token, id, string.Empty));
            else
                duplicates++;
        }
        return new(items, tokens.Length, duplicates, false);
    }

    private static string? ParseId(string value)
    {
        var number = value.StartsWith("JM", StringComparison.OrdinalIgnoreCase) ? value[2..] : value;
        if (!Digits().IsMatch(number))
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
                || uri.Scheme is not ("http" or "https")
                || string.IsNullOrWhiteSpace(uri.Host)
                || !string.IsNullOrEmpty(uri.UserInfo)) return null;

            var match = AlbumPath().Match(uri.AbsolutePath);
            if (!match.Success) return null;
            number = match.Groups[1].Value;
        }

        number = number.TrimStart('0');
        return number.Length is > 0 and <= 18 ? number : null;
    }

    [GeneratedRegex(@"[\s,;，；]+", RegexOptions.CultureInvariant)]
    private static partial Regex Separators();

    [GeneratedRegex(@"^[0-9]+$", RegexOptions.CultureInvariant)]
    private static partial Regex Digits();

    [GeneratedRegex(@"^/album/([0-9]+)/?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AlbumPath();
}
