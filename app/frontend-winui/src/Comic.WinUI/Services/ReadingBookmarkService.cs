using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Comic.WinUI.Services;

/// <summary>Personal bookmarks are stored separately from downloaded manga metadata.</summary>
public sealed class ReadingBookmarkService
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };
    private readonly object _gate = new();
    private readonly string _path;
    private BookmarkDocument _document;

    public ReadingBookmarkService(ApplicationSettingsService settings) : this(settings.SettingsDirectory) { }

    internal ReadingBookmarkService(string directory)
    {
        _path = Path.Combine(directory, "reading-bookmarks.json");
        _document = AtomicJsonFile.Read<BookmarkDocument>(_path, Options, IsValid) ?? new();
    }

    public IReadOnlyList<ReadingBookmark> Get(string manga)
    {
        var key = PersonalLibraryIdentity.Normalize(manga);
        lock (_gate)
            return _document.Entries.Where(entry => PersonalLibraryIdentity.Equals(entry.Manga, key))
                .OrderByDescending(entry => entry.UpdatedAtUtc).ToArray();
    }

    public ReadingBookmark Save(string manga, string chapter, string chapterTitle, int pageIndex, string note)
    {
        var key = PersonalLibraryIdentity.Normalize(manga);
        if (key.Length == 0 || string.IsNullOrWhiteSpace(chapter) || pageIndex < 0 || pageIndex == int.MaxValue)
            throw new ArgumentException("请选择有效的漫画、章节和页码。");
        note = (note ?? string.Empty).Trim();
        if (note.Length > 2000) throw new ArgumentException("书签备注不能超过 2000 个字符。");
        lock (_gate)
        {
            var entries = _document.Entries.ToList();
            var existing = entries.FindIndex(entry => PersonalLibraryIdentity.Equals(entry.Manga, key)
                && entry.Chapter == chapter && entry.PageIndex == pageIndex);
            var bookmark = new ReadingBookmark(existing < 0 ? Guid.NewGuid().ToString("N") : entries[existing].Id,
                key, chapter, chapterTitle ?? chapter, pageIndex, note, DateTimeOffset.UtcNow);
            if (existing < 0) entries.Add(bookmark);
            else entries[existing] = bookmark;
            var next = new BookmarkDocument { Entries = entries };
            AtomicJsonFile.Write(_path, next, Options);
            _document = next; // A failed durable write must not appear successful in memory.
            return bookmark;
        }
    }

    public void Remove(string manga, string id)
    {
        lock (_gate)
        {
            var next = new BookmarkDocument
            {
                Entries = _document.Entries.Where(entry => entry.Id != id || !PersonalLibraryIdentity.Equals(entry.Manga, manga)).ToList(),
            };
            AtomicJsonFile.Write(_path, next, Options);
            _document = next;
        }
    }

    internal static bool IsValid(BookmarkDocument document) => document.Entries is not null
        && document.Entries.All(entry => entry is not null && !string.IsNullOrWhiteSpace(entry.Id)
            && PersonalLibraryIdentity.IsValid(entry.Manga) && !string.IsNullOrWhiteSpace(entry.Chapter)
            && entry.ChapterTitle is not null && entry.PageIndex >= 0 && entry.PageIndex < int.MaxValue && entry.Note is not null && entry.Note.Length <= 2000)
        && document.Entries.Select(entry => entry.Id).Distinct().Count() == document.Entries.Count;

    internal sealed class BookmarkDocument
    {
        public List<ReadingBookmark> Entries { get; set; } = [];
    }
}

public sealed record ReadingBookmark(string Id, string Manga, string Chapter, string ChapterTitle, int PageIndex,
    string Note, DateTimeOffset UpdatedAtUtc)
{
    public string Label => $"{ChapterTitle} · 第 {PageIndex + 1} 页";
}

internal static class PersonalLibraryIdentity
{
    public static bool IsValid(string value)
    {
        try { return Normalize(value).Length > 0; }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    public static string Normalize(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        value = value.Trim();
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && (uri.Scheme == "http" || uri.Scheme == "https"))
            return uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
    }

    public static bool Equals(string first, string second) => string.Equals(Normalize(first), Normalize(second), StringComparison.OrdinalIgnoreCase);
}
