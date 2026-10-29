using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Comic.WinUI.Models;

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
    private readonly Dictionary<string, ReadingStatus> _statuses = new(StringComparer.OrdinalIgnoreCase);

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

    public ReadingStatus GetReadingStatus(string rootDirectory)
    {
        var key = NormalizeRootDirectory(rootDirectory);
        lock (_gate)
        {
            if (_statuses.TryGetValue(key, out var status)) return status;
            return _entries.ContainsKey(key) ? ReadingStatus.Reading : ReadingStatus.ToRead;
        }
    }

    /// <summary>Persist manual status changes before reporting success; never discard the saved page.</summary>
    public bool SetReadingStatus(string rootDirectory, ReadingStatus status)
    {
        var key = NormalizeRootDirectory(rootDirectory);
        if (string.IsNullOrEmpty(key) || !Enum.IsDefined(status)) return false;

        lock (_gate)
        {
            var hadStatus = _statuses.TryGetValue(key, out var previous);
            if (hadStatus && previous == status) return true;
            _statuses[key] = status;
            if (SaveToDisk()) return true;
            if (hadStatus) _statuses[key] = previous;
            else _statuses.Remove(key);
            return false;
        }
    }

    public void Save(string rootDirectory, string chapterDirectoryName, int pageIndex)
    {
        var key = NormalizeRootDirectory(rootDirectory);
        if (string.IsNullOrEmpty(key) || string.IsNullOrWhiteSpace(chapterDirectoryName)) return;

        pageIndex = Math.Max(0, pageIndex);

        lock (_gate)
        {
            var hadStatus = _statuses.TryGetValue(key, out var status);
            var resumeToRead = hadStatus && status == ReadingStatus.ToRead;
            if (_entries.TryGetValue(key, out var existing) &&
                string.Equals(existing.ChapterDirectoryName, chapterDirectoryName, StringComparison.Ordinal) &&
                existing.PageIndex == pageIndex && !resumeToRead)
            {
                return;
            }

            _entries[key] = new ReadingProgressEntry(
                key,
                chapterDirectoryName,
                pageIndex,
                DateTimeOffset.UtcNow);
            if (resumeToRead) _statuses.Remove(key);
            if (SaveToDisk()) return;
            if (existing is not null) _entries[key] = existing;
            else _entries.Remove(key);
            if (resumeToRead) _statuses[key] = status;
        }
    }

    public void Remove(string rootDirectory)
    {
        var key = NormalizeRootDirectory(rootDirectory);
        if (string.IsNullOrEmpty(key)) return;

        lock (_gate)
        {
            _entries.Remove(key, out var entry);
            var hadStatus = _statuses.Remove(key, out var status);
            if (entry is null && !hadStatus) return;
            if (SaveToDisk()) return;
            if (entry is not null) _entries[key] = entry;
            if (hadStatus) _statuses[key] = status;
        }
    }

    private Dictionary<string, ReadingProgressEntry> LoadFromDisk()
    {
        try
        {
            var document = AtomicJsonFile.Read<ReadingProgressDocument>(_filePath, JsonOptions, IsValid);
            foreach (var state in document?.Statuses ?? [])
            {
                var key = NormalizeRootDirectory(state.RootDirectory);
                if (!string.IsNullOrEmpty(key) && Enum.IsDefined(state.Status)) _statuses[key] = state.Status;
            }
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

    private bool SaveToDisk()
    {
        try
        {
            var document = new ReadingProgressDocument
            {
                Entries = _entries.Values
                    .OrderByDescending(entry => entry.UpdatedAtUtc)
                    .ToList(),
                Statuses = _statuses.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(pair => new ReadingStatusEntry(pair.Key, pair.Value)).ToList(),
            };
            AtomicJsonFile.Write(_filePath, document, JsonOptions);
            return true;
        }
        catch
        {
            // Reading progress is helpful state and must never block the reader.
            return false;
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

    internal sealed class ReadingProgressDocument
    {
        public List<ReadingProgressEntry> Entries { get; set; } = [];
        public List<ReadingStatusEntry> Statuses { get; set; } = [];
    }

    internal static bool IsValid(ReadingProgressDocument document) =>
        document.Entries is not null && document.Entries.All(entry => entry is not null) &&
        document.Statuses is not null && document.Statuses.All(state => state is not null &&
            !string.IsNullOrWhiteSpace(state.RootDirectory) && Enum.IsDefined(state.Status));

    internal sealed record ReadingStatusEntry(string RootDirectory, ReadingStatus Status);
}

public sealed record ReadingProgressEntry(
    string RootDirectory,
    string ChapterDirectoryName,
    int PageIndex,
    DateTimeOffset UpdatedAtUtc);
