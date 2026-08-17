using System;
using System.IO;
using System.Text;
using System.Text.Json;

namespace Comic.WinUI.Services;

public sealed class ApplicationSettingsService
{
    public const string SystemTheme = "system";
    public const string LightTheme = "light";
    public const string DarkTheme = "dark";
    public const string SelectNone = "none";
    public const string SelectAll = "all";
    public const string SelectLatest = "latest";
    public const string ReaderPaged = "paged";
    public const string ReaderStrip = "strip";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private readonly string _filePath;
    private UserPreferences _preferences;

    public ApplicationSettingsService()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Comic.WinUI"))
    {
    }

    internal ApplicationSettingsService(string directory)
    {
        Directory.CreateDirectory(directory);
        _filePath = Path.Combine(directory, "app-settings.json");
        _preferences = Load();
    }

    public event EventHandler? ThemeChanged;

    public string StorageRoot =>
        string.IsNullOrWhiteSpace(_preferences.StorageRoot)
            ? ResolveDefaultStorageRoot()
            : Environment.ExpandEnvironmentVariables(_preferences.StorageRoot);

    public string Theme => NormalizeTheme(_preferences.Theme);

    public string ChapterSelectionMode => NormalizeChapterSelection(_preferences.ChapterSelectionMode);

    public bool ExpandNavigationPane => _preferences.ExpandNavigationPane;

    public string DefaultReaderMode => NormalizeReaderMode(_preferences.DefaultReaderMode);

    public int DefaultStripZoomPercent => NormalizeStripZoom(_preferences.DefaultStripZoomPercent);

    public int LibraryPageSize => NormalizeLibraryPageSize(_preferences.LibraryPageSize);

    public string SettingsDirectory => Path.GetDirectoryName(_filePath) ?? string.Empty;

    public void UpdatePreferences(string theme, string chapterSelectionMode)
    {
        UpdatePreferences(
            theme,
            chapterSelectionMode,
            ExpandNavigationPane,
            DefaultReaderMode,
            DefaultStripZoomPercent,
            LibraryPageSize);
    }

    public void UpdatePreferences(
        string theme,
        string chapterSelectionMode,
        bool expandNavigationPane,
        string defaultReaderMode,
        int defaultStripZoomPercent,
        int libraryPageSize)
    {
        var normalizedTheme = NormalizeTheme(theme);
        var themeChanged = !string.Equals(Theme, normalizedTheme, StringComparison.Ordinal);
        _preferences.Theme = normalizedTheme;
        _preferences.ChapterSelectionMode = NormalizeChapterSelection(chapterSelectionMode);
        _preferences.ExpandNavigationPane = expandNavigationPane;
        _preferences.DefaultReaderMode = NormalizeReaderMode(defaultReaderMode);
        _preferences.DefaultStripZoomPercent = NormalizeStripZoom(defaultStripZoomPercent);
        _preferences.LibraryPageSize = NormalizeLibraryPageSize(libraryPageSize);
        Save();
        if (themeChanged) ThemeChanged?.Invoke(this, EventArgs.Empty);
    }

    public void UpdateStorageRoot(string storageRoot)
    {
        _preferences.StorageRoot = Path.GetFullPath(storageRoot);
        Save();
    }

    public static string ResolveDefaultStorageRoot()
    {
        var configured = Environment.GetEnvironmentVariable("COMIC_DOWNLOAD_DIR")?.Trim();
        if (!string.IsNullOrWhiteSpace(configured))
            return Environment.ExpandEnvironmentVariables(configured);
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var downloads = Path.Combine(home, "Downloads");
        return Path.Combine(Directory.Exists(downloads) ? downloads : home, "ComicDownloads");
    }

    private UserPreferences Load()
    {
        try
        {
            return File.Exists(_filePath)
                ? JsonSerializer.Deserialize<UserPreferences>(File.ReadAllText(_filePath), JsonOptions) ?? new UserPreferences()
                : new UserPreferences();
        }
        catch
        {
            return new UserPreferences();
        }
    }

    private void Save()
    {
        var temporaryPath = _filePath + ".tmp";
        File.WriteAllText(
            temporaryPath,
            JsonSerializer.Serialize(_preferences, JsonOptions),
            new UTF8Encoding(false));
        File.Move(temporaryPath, _filePath, true);
    }

    private static string NormalizeTheme(string? value) => value switch
    {
        LightTheme => LightTheme,
        DarkTheme => DarkTheme,
        _ => SystemTheme,
    };

    private static string NormalizeChapterSelection(string? value) => value switch
    {
        SelectAll => SelectAll,
        SelectLatest => SelectLatest,
        _ => SelectNone,
    };

    private static string NormalizeReaderMode(string? value) => value switch
    {
        ReaderStrip => ReaderStrip,
        _ => ReaderPaged,
    };

    private static int NormalizeStripZoom(int value) => Math.Clamp(value, 50, 200);

    private static int NormalizeLibraryPageSize(int value) => value switch
    {
        10 => 10,
        30 => 30,
        50 => 50,
        _ => 20,
    };

    private sealed class UserPreferences
    {
        public string StorageRoot { get; set; } = string.Empty;
        public string Theme { get; set; } = SystemTheme;
        public string ChapterSelectionMode { get; set; } = SelectNone;
        public bool ExpandNavigationPane { get; set; } = true;
        public string DefaultReaderMode { get; set; } = ReaderPaged;
        public int DefaultStripZoomPercent { get; set; } = 100;
        public int LibraryPageSize { get; set; } = 20;
    }
}
