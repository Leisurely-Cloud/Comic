using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Comic.WinUI.Models;

namespace Comic.WinUI.Services;

public sealed class SearchHistoryService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private const int MaxEntries = 50;

    private readonly string _filePath;
    private List<SearchHistoryEntry> _cache = [];

    public SearchHistoryService()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Comic.WinUI");
        Directory.CreateDirectory(dir);
        _filePath = Path.Combine(dir, "search-history.json");
        _cache = LoadFromDisk();
    }

    public IReadOnlyList<SearchHistoryEntry> GetAll() => _cache;

    public IReadOnlyList<SearchHistoryEntry> Search(string keyword, int maxResults = 10)
    {
        if (string.IsNullOrWhiteSpace(keyword))
            return _cache.Take(maxResults).ToList();

        var lower = keyword.Trim().ToLowerInvariant();
        return _cache
            .Where(e => e.Keyword.Contains(lower, StringComparison.OrdinalIgnoreCase))
            .Take(maxResults)
            .ToList();
    }

    public void Add(string keyword, string siteKey, string siteName, int resultCount = 0)
    {
        if (string.IsNullOrWhiteSpace(keyword)) return;

        keyword = keyword.Trim();

        _cache.RemoveAll(e =>
            string.Equals(e.Keyword, keyword, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(e.SiteKey, siteKey, StringComparison.Ordinal));

        _cache.Insert(0, new SearchHistoryEntry
        {
            Keyword = keyword,
            SiteKey = siteKey,
            SiteName = siteName,
            Timestamp = DateTime.Now,
            ResultCount = resultCount,
        });

        if (_cache.Count > MaxEntries)
            _cache = _cache.Take(MaxEntries).ToList();

        SaveToDisk();
    }

    public void Remove(string keyword, string siteKey)
    {
        _cache.RemoveAll(e =>
            string.Equals(e.Keyword, keyword, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(e.SiteKey, siteKey, StringComparison.Ordinal));
        SaveToDisk();
    }

    public void Clear()
    {
        _cache.Clear();
        SaveToDisk();
    }

    private List<SearchHistoryEntry> LoadFromDisk()
    {
        try
        {
            if (!File.Exists(_filePath)) return [];
            var json = File.ReadAllText(_filePath);
            return JsonSerializer.Deserialize<List<SearchHistoryEntry>>(json) ?? [];
        }
        catch
        {
            return [];
        }
    }

    private void SaveToDisk()
    {
        try
        {
            var json = JsonSerializer.Serialize(_cache, JsonOptions);
            File.WriteAllText(_filePath, json);
        }
        catch
        {
            // Best effort persistence
        }
    }
}
