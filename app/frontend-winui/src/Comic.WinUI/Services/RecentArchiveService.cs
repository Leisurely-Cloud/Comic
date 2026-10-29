using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Comic.WinUI.Services;

/// <summary>Remembers explicitly opened archives without granting a reader session or accessing their contents.</summary>
public sealed class RecentArchiveService
{
    public const int MaxEntries = 20;
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };
    private readonly object _gate = new();
    private readonly string _path;
    private RecentArchiveDocument _document;

    public RecentArchiveService(ApplicationSettingsService settings) : this(settings.SettingsDirectory) { }

    internal RecentArchiveService(string directory)
    {
        _path = Path.Combine(directory, "recent-archives.json");
        var saved = AtomicJsonFile.Read<RecentArchiveDocument>(_path, Options, IsValid);
        _document = new RecentArchiveDocument
        {
            Entries = (saved?.Entries ?? []).Select(entry => entry with
            {
                FilePath = NormalizePath(entry.FilePath)!,
                LastOpenedAtUtc = entry.LastOpenedAtUtc.ToUniversalTime(),
            }).OrderByDescending(entry => entry.LastOpenedAtUtc).ToList(),
        };
    }

    public IReadOnlyList<RecentArchiveEntry> GetAll()
    {
        lock (_gate) return _document.Entries.ToArray();
    }

    /// <summary>Call only after the reader successfully opens an explicitly selected file.</summary>
    public bool RecordOpened(string path)
    {
        var normalized = NormalizePath(path);
        if (normalized is null || Directory.Exists(normalized)) return false;
        lock (_gate)
        {
            var entries = _document.Entries.Where(entry => !SamePath(entry.FilePath, normalized)).ToList();
            entries.Insert(0, new RecentArchiveEntry(normalized, DateTimeOffset.UtcNow));
            return Save(new RecentArchiveDocument { Entries = entries.Take(MaxEntries).ToList() });
        }
    }

    public bool Remove(string path)
    {
        var normalized = NormalizePath(path);
        if (normalized is null) return false;
        lock (_gate)
        {
            var entries = _document.Entries.Where(entry => !SamePath(entry.FilePath, normalized)).ToList();
            return entries.Count == _document.Entries.Count || Save(new RecentArchiveDocument { Entries = entries });
        }
    }

    public bool Clear()
    {
        lock (_gate) return _document.Entries.Count == 0 || Save(new RecentArchiveDocument());
    }

    private bool Save(RecentArchiveDocument next)
    {
        try
        {
            AtomicJsonFile.Write(_path, next, Options);
            _document = next;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }

    private static bool SamePath(string first, string second) => string.Equals(first, second, StringComparison.OrdinalIgnoreCase);

    private static string? NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            path = path.Trim();
            if (!Path.IsPathFullyQualified(path)) return null;
            var fullPath = Path.GetFullPath(path);
            var extension = Path.GetExtension(fullPath);
            if (!extension.Equals(".cbz", StringComparison.OrdinalIgnoreCase) && !extension.Equals(".zip", StringComparison.OrdinalIgnoreCase)) return null;
            var root = Path.GetPathRoot(fullPath)!;
            var segments = fullPath[root.Length..].Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar });
            if (segments.Any(segment => segment.Length == 0 || segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || segment.EndsWith('.') || segment.EndsWith(' '))) return null;
            return fullPath;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    internal static bool IsValid(RecentArchiveDocument document) => document.Entries is not null
        && document.Entries.Count <= MaxEntries
        && document.Entries.All(entry => entry is not null && NormalizePath(entry.FilePath) is not null && entry.LastOpenedAtUtc != default)
        && document.Entries.Select(entry => NormalizePath(entry.FilePath)!).Distinct(StringComparer.OrdinalIgnoreCase).Count() == document.Entries.Count;

    internal sealed class RecentArchiveDocument
    {
        [JsonRequired]
        public List<RecentArchiveEntry> Entries { get; set; } = [];
    }
}

public sealed record RecentArchiveEntry(string FilePath, DateTimeOffset LastOpenedAtUtc)
{
    [JsonIgnore]
    public string Title => Path.GetFileNameWithoutExtension(FilePath);
}
