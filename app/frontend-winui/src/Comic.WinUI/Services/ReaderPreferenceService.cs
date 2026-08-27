using System.Text;
using System.Text.Json;

namespace Comic.WinUI.Services;

public sealed class ReaderPreferenceService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private readonly object _gate = new();
    private readonly string _filePath;
    private ReaderPreferenceDocument _document;

    public ReaderPreferenceService(ApplicationSettingsService settings) : this(settings.SettingsDirectory) { }

    internal ReaderPreferenceService(string directory)
    {
        Directory.CreateDirectory(directory);
        _filePath = Path.Combine(directory, "reader-preferences.json");
        _document = Load();
    }

    public ReaderPreference Get(string mangaKey, string defaultMode, int defaultStripZoom)
    {
        lock (_gate)
        {
            if (_document.Manga.TryGetValue(NormalizeKey(mangaKey), out var value)) return value with { };
            return new ReaderPreference(defaultMode, false, defaultStripZoom, 100);
        }
    }

    public void Save(string mangaKey, ReaderPreference preference)
    {
        if (string.IsNullOrWhiteSpace(mangaKey)) return;
        lock (_gate)
        {
            _document.Manga[NormalizeKey(mangaKey)] = preference;
            Persist();
        }
    }

    public ReaderShortcutPreference Shortcuts
    {
        get { lock (_gate) return _document.Shortcuts with { }; }
    }

    public void SaveShortcuts(ReaderShortcutPreference shortcuts)
    {
        lock (_gate) { _document.Shortcuts = shortcuts; Persist(); }
    }

    private ReaderPreferenceDocument Load()
    {
        try
        {
            return File.Exists(_filePath)
                ? JsonSerializer.Deserialize<ReaderPreferenceDocument>(File.ReadAllText(_filePath), JsonOptions) ?? new()
                : new();
        }
        catch { return new(); }
    }

    private void Persist()
    {
        try
        {
            var temporary = _filePath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(_document, JsonOptions), new UTF8Encoding(false));
            File.Move(temporary, _filePath, true);
        }
        catch { }
    }

    private static string NormalizeKey(string value)
    {
        try { return Path.GetFullPath(value).TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant(); }
        catch { return value.Trim().ToUpperInvariant(); }
    }

    private sealed class ReaderPreferenceDocument
    {
        public Dictionary<string, ReaderPreference> Manga { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public ReaderShortcutPreference Shortcuts { get; set; } = new("Left", "Right", "F11", true);
    }
}

public sealed record ReaderPreference(string Mode, bool RightToLeft, int StripZoomPercent, int PagedZoomPercent);
public sealed record ReaderShortcutPreference(string PreviousKey, string NextKey, string FullscreenKey, bool TapRightToAdvance);
