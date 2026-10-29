using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Comic.WinUI.Services;

/// <summary>Local-only virtual shelves and tags; never moves files or changes official favorites.</summary>
public sealed class LibraryOrganizationService
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };
    private readonly object _gate = new();
    private readonly string _path;
    private OrganizationDocument _document;

    public LibraryOrganizationService(ApplicationSettingsService settings) : this(settings.SettingsDirectory) { }

    internal LibraryOrganizationService(string directory)
    {
        _path = Path.Combine(directory, "library-organization.json");
        _document = AtomicJsonFile.Read<OrganizationDocument>(_path, Options, IsValid) ?? new();
    }

    public IReadOnlyList<LocalBookshelf> GetShelves()
    {
        lock (_gate) return _document.Shelves.OrderBy(shelf => shelf.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public LocalBookshelf SaveShelf(string? id, string name)
    {
        name = name.Trim();
        if (name.Length is 0 or > 40) throw new ArgumentException("书架名称需要 1–40 个字符。");
        lock (_gate)
        {
            if (_document.Shelves.Any(shelf => shelf.Id != id && string.Equals(shelf.Name, name, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("已存在同名书架。");
            if (id is not null && !_document.Shelves.Any(shelf => shelf.Id == id)) throw new ArgumentException("书架已不存在。");
            var shelf = new LocalBookshelf(id ?? Guid.NewGuid().ToString("N"), name);
            Commit(new() { Shelves = _document.Shelves.Where(item => item.Id != shelf.Id).Append(shelf).ToList(), Books = _document.Books });
            return shelf;
        }
    }

    public void RemoveShelf(string id)
    {
        lock (_gate)
            Commit(new()
            {
                Shelves = _document.Shelves.Where(shelf => shelf.Id != id).ToList(),
                Books = _document.Books.Select(book => book with { ShelfIds = book.ShelfIds.Where(shelf => shelf != id).ToArray() }).ToList(),
            });
    }

    public LocalBookOrganization Get(string manga)
    {
        lock (_gate)
        {
            var book = _document.Books.FirstOrDefault(item => PersonalLibraryIdentity.Equals(item.Manga, manga));
            return book is null ? new(PersonalLibraryIdentity.Normalize(manga), [], [])
                : book with { ShelfIds = book.ShelfIds.ToArray(), Tags = book.Tags.ToArray() };
        }
    }

    /// <summary>Explicit replace semantics, atomically applied to the whole selection.</summary>
    public void SetBooks(IEnumerable<string> manga, IEnumerable<string> shelfIds, IEnumerable<string> tags)
    {
        var keys = manga.Select(PersonalLibraryIdentity.Normalize).Where(key => key.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (keys.Length == 0) throw new ArgumentException("请先选择漫画。");
        var shelves = shelfIds.Distinct().ToArray();
        var normalizedTags = tags.Select(tag => tag.Trim()).Where(tag => tag.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (normalizedTags.Length > 20 || normalizedTags.Any(tag => tag.Length > 40))
            throw new ArgumentException("每部漫画最多 20 个标签，每个标签最多 40 个字符。");
        lock (_gate)
        {
            if (shelves.Any(id => !_document.Shelves.Any(shelf => shelf.Id == id))) throw new ArgumentException("选择的书架已不存在，请重新选择。");
            Commit(new()
            {
                Shelves = _document.Shelves,
                Books = _document.Books.Where(book => !keys.Contains(book.Manga, StringComparer.OrdinalIgnoreCase))
                    .Concat(keys.Select(key => new LocalBookOrganization(key, shelves, normalizedTags))).ToList(),
            });
        }
    }

    public bool Matches(string manga, string? shelfId, string? tag)
    {
        var book = Get(manga);
        return (string.IsNullOrEmpty(shelfId) || book.ShelfIds.Contains(shelfId))
            && (string.IsNullOrWhiteSpace(tag) || book.Tags.Contains(tag.Trim(), StringComparer.OrdinalIgnoreCase));
    }

    internal Func<string, bool> CreateFilter(string? shelfId, string? tag)
    {
        if (string.IsNullOrEmpty(shelfId) && string.IsNullOrWhiteSpace(tag)) return _ => true;
        lock (_gate)
        {
            var matches = _document.Books.Where(book =>
                (string.IsNullOrEmpty(shelfId) || book.ShelfIds.Contains(shelfId))
                && (string.IsNullOrWhiteSpace(tag) || book.Tags.Contains(tag.Trim(), StringComparer.OrdinalIgnoreCase)))
                .Select(book => PersonalLibraryIdentity.Normalize(book.Manga)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            return manga => matches.Contains(PersonalLibraryIdentity.Normalize(manga));
        }
    }

    private void Commit(OrganizationDocument next)
    {
        AtomicJsonFile.Write(_path, next, Options);
        _document = next;
    }

    internal static bool IsValid(OrganizationDocument document) => document.Shelves is not null && document.Books is not null
        && document.Shelves.All(shelf => shelf is not null && !string.IsNullOrWhiteSpace(shelf.Id) && !string.IsNullOrWhiteSpace(shelf.Name) && shelf.Name.Length <= 40)
        && document.Shelves.Select(shelf => shelf.Id).Distinct().Count() == document.Shelves.Count
        && document.Books.All(book => book is not null && PersonalLibraryIdentity.IsValid(book.Manga) && book.ShelfIds is not null
            && book.ShelfIds.All(id => document.Shelves.Any(shelf => shelf.Id == id)) && book.Tags is not null
            && book.Tags.Length <= 20 && book.Tags.All(tag => !string.IsNullOrWhiteSpace(tag) && tag.Length <= 40));

    internal sealed class OrganizationDocument
    {
        public List<LocalBookshelf> Shelves { get; set; } = [];
        public List<LocalBookOrganization> Books { get; set; } = [];
    }
}

public sealed record LocalBookshelf(string Id, string Name);
public sealed record LocalBookOrganization(string Manga, string[] ShelfIds, string[] Tags);
