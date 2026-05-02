using System;
using System.IO;
using System.Text.Json;

namespace Comic.WinUI.Services;

public sealed class BackendRuntimeSettings
{
    public string BackendBaseUrl { get; set; } = "http://127.0.0.1:18765/";
    public string PythonExecutablePath { get; set; } = "python";
    public string PythonArguments { get; set; } = string.Empty;
    public string WorkingDirectory { get; set; } = string.Empty;
}

public sealed class BackendSettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _settingsFilePath;
    private readonly BackendRuntimeSettings _defaults;
    private BackendRuntimeSettings _current;

    public BackendSettingsService()
    {
        var repoRoot = FindRepositoryRoot(AppContext.BaseDirectory);
        var backendScriptPath = repoRoot is null ? string.Empty : Path.Combine(repoRoot, "app", "backend", "run_backend.py");
        var venvPythonPath = repoRoot is null ? string.Empty : Path.Combine(repoRoot, ".venv", "Scripts", "python.exe");
        var defaultPython = File.Exists(venvPythonPath) ? venvPythonPath : "python";
        var defaultWorkingDirectory = repoRoot is null ? Environment.CurrentDirectory : Path.Combine(repoRoot, "app");

        _defaults = new BackendRuntimeSettings
        {
            BackendBaseUrl = "http://127.0.0.1:18765/",
            PythonExecutablePath = defaultPython,
            PythonArguments = string.IsNullOrWhiteSpace(backendScriptPath) ? string.Empty : $"\"{backendScriptPath}\"",
            WorkingDirectory = defaultWorkingDirectory,
        };

        var settingsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Comic.WinUI");
        Directory.CreateDirectory(settingsDir);
        _settingsFilePath = Path.Combine(settingsDir, "backend-settings.json");
        _current = LoadFromDisk();
    }

    public BackendRuntimeSettings GetSettings()
    {
        return Clone(_current);
    }

    public void SaveSettings(BackendRuntimeSettings settings)
    {
        _current = MergeWithDefaults(settings);
        var json = JsonSerializer.Serialize(_current, JsonOptions);
        File.WriteAllText(_settingsFilePath, json);
    }

    private BackendRuntimeSettings LoadFromDisk()
    {
        try
        {
            if (!File.Exists(_settingsFilePath))
            {
                return Clone(_defaults);
            }

            var json = File.ReadAllText(_settingsFilePath);
            var loaded = JsonSerializer.Deserialize<BackendRuntimeSettings>(json);
            return MergeWithDefaults(loaded);
        }
        catch
        {
            return Clone(_defaults);
        }
    }

    private BackendRuntimeSettings MergeWithDefaults(BackendRuntimeSettings? settings)
    {
        return new BackendRuntimeSettings
        {
            BackendBaseUrl = NormalizeBaseUrl(settings?.BackendBaseUrl) ?? _defaults.BackendBaseUrl,
            PythonExecutablePath = string.IsNullOrWhiteSpace(settings?.PythonExecutablePath)
                ? _defaults.PythonExecutablePath
                : settings!.PythonExecutablePath.Trim(),
            PythonArguments = string.IsNullOrWhiteSpace(settings?.PythonArguments)
                ? _defaults.PythonArguments
                : settings!.PythonArguments.Trim(),
            WorkingDirectory = string.IsNullOrWhiteSpace(settings?.WorkingDirectory)
                ? _defaults.WorkingDirectory
                : settings!.WorkingDirectory.Trim(),
        };
    }

    private static BackendRuntimeSettings Clone(BackendRuntimeSettings settings)
    {
        return new BackendRuntimeSettings
        {
            BackendBaseUrl = settings.BackendBaseUrl,
            PythonExecutablePath = settings.PythonExecutablePath,
            PythonArguments = settings.PythonArguments,
            WorkingDirectory = settings.WorkingDirectory,
        };
    }

    private static string? NormalizeBaseUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim();
        if (!normalized.EndsWith("/", StringComparison.Ordinal))
        {
            normalized += "/";
        }

        return normalized;
    }

    private static string? FindRepositoryRoot(string startDirectory)
    {
        var current = new DirectoryInfo(startDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, "app", "backend", "run_backend.py");
            if (File.Exists(candidate))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        return null;
    }
}
