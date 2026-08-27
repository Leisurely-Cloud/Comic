using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace Comic.WinUI.Services;

public sealed class ReadingProgressService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private readonly object _gate = new();
    private readonly string _filePath;
    private Dictionary<string, ReadingProgressEntry> _entries;

    public ReadingProgressService(ApplicationSettingsService applicationSettings)
        : this(applicationSettings.SettingsDirectory)
    {
    }

    internal ReadingProgressService(string directory)
    {
        Directory.CreateDirectory(directory);
        _filePath = Path.Combine(directory, "reading-progress.json");
        _entries = LoadFromDisk();
    }

    public ReadingProgressEntry? Get(string rootDirectory)
    {
        var key = NormalizeRootDirectory(rootDirectory);
        if (string.IsNullOrEmpty(key)) return null;

        lock (_gate)
        {
            return _entries.TryGetValue(key, out var entry)
                ? entry with { }
                : null;
        }
    }

    public DateTimeOffset? GetLastReadAt(string rootDirectory) => Get(rootDirectory)?.UpdatedAtUtc;

    public void Save(string rootDirectory, string chapterDirectoryName, int pageIndex)
    {
        var key = NormalizeRootDirectory(rootDirectory);
        if (string.IsNullOrEmpty(key) || string.IsNullOrWhiteSpace(chapterDirectoryName)) return;

        pageIndex = Math.Max(0, pageIndex);

        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var existing) &&
                string.Equals(existing.ChapterDirectoryName, chapterDirectoryName, StringComparison.Ordinal) &&
                existing.PageIndex == pageIndex)
            {
                return;
            }

            _entries[key] = new ReadingProgressEntry(
                key,
                chapterDirectoryName,
                pageIndex,
                DateTimeOffset.UtcNow);
            SaveToDisk();
        }
    }

    public void Remove(string rootDirectory)
    {
        var key = NormalizeRootDirectory(rootDirectory);
        if (string.IsNullOrEmpty(key)) return;

        lock (_gate)
        {
            if (!_entries.Remove(key)) return;
            SaveToDisk();
        }
    }

    private Dictionary<string, ReadingProgressEntry> LoadFromDisk()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return new Dictionary<string, ReadingProgressEntry>(StringComparer.OrdinalIgnoreCase);
            }

            var document = JsonSerializer.Deserialize<ReadingProgressDocument>(
                File.ReadAllText(_filePath),
                JsonOptions);
            return (document?.Entries ?? [])
                .Where(entry => !string.IsNullOrWhiteSpace(entry.RootDirectory) &&
                                !string.IsNullOrWhiteSpace(entry.ChapterDirectoryName))
                .GroupBy(entry => NormalizeRootDirectory(entry.RootDirectory), StringComparer.OrdinalIgnoreCase)
                .Where(group => !string.IsNullOrEmpty(group.Key))
                .ToDictionary(
                    group => group.Key,
                    group =>
                    {
                        var latest = group.OrderByDescending(entry => entry.UpdatedAtUtc).First();
                        return latest with
                        {
                            RootDirectory = group.Key,
                            PageIndex = Math.Max(0, latest.PageIndex),
                        };
                    },
                    StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new Dictionary<string, ReadingProgressEntry>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private void SaveToDisk()
    {
        try
        {
            var document = new ReadingProgressDocument
            {
                Entries = _entries.Values
                    .OrderByDescending(entry => entry.UpdatedAtUtc)
                    .ToList(),
            };
            var temporaryPath = _filePath + ".tmp";
            File.WriteAllText(
                temporaryPath,
                JsonSerializer.Serialize(document, JsonOptions),
                new UTF8Encoding(false));
            File.Move(temporaryPath, _filePath, true);
        }
        catch
        {
            // Reading progress is helpful state and must never block the reader.
        }
    }

    private static string NormalizeRootDirectory(string rootDirectory)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory)) return string.Empty;

        try
        {
            var fullPath = Path.GetFullPath(rootDirectory.Trim());
            var pathRoot = Path.GetPathRoot(fullPath);
            return !string.IsNullOrEmpty(pathRoot) && fullPath.Length == pathRoot.Length
                ? fullPath
                : fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return rootDirectory.Trim();
        }
    }

    private sealed class ReadingProgressDocument
    {
        public List<ReadingProgressEntry> Entries { get; set; } = [];
    }
}

public sealed record ReadingProgressEntry(
    string RootDirectory,
    string ChapterDirectoryName,
    int PageIndex,
    DateTimeOffset UpdatedAtUtc);
