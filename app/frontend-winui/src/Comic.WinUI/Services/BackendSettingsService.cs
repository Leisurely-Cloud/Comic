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
        var isPackaged = IsPackagedInstallation();

        string backendScriptPath;
        string defaultPython;
        string defaultWorkingDirectory;

        if (isPackaged)
        {
            // 打包后的布局：python 和 backend 在应用目录的上级
            var appDir = Path.GetDirectoryName(AppContext.BaseDirectory) ?? AppContext.BaseDirectory;
            var installDir = Path.GetDirectoryName(appDir) ?? appDir;
            backendScriptPath = Path.Combine(installDir, "backend", "run_backend.py");
            defaultPython = Path.Combine(installDir, "python", "python.exe");
            defaultWorkingDirectory = Path.Combine(installDir, "backend");
        }
        else
        {
            // 开发环境布局
            backendScriptPath = repoRoot is null ? string.Empty : Path.Combine(repoRoot, "app", "backend", "run_backend.py");
            var venvPythonPath = repoRoot is null ? string.Empty : Path.Combine(repoRoot, ".venv", "Scripts", "python.exe");
            defaultPython = File.Exists(venvPythonPath) ? venvPythonPath : "python";
            defaultWorkingDirectory = repoRoot is null ? Environment.CurrentDirectory : Path.Combine(repoRoot, "app");
        }

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

    private static bool IsPackagedInstallation()
    {
        // 检查是否是打包后的安装版本
        // 打包后，exe 在 frontend 子目录，python 和 backend 在上级目录
        var exeDir = AppContext.BaseDirectory;
        var frontendDir = Path.GetDirectoryName(exeDir);
        if (frontendDir is null) return false;

        var installDir = Path.GetDirectoryName(frontendDir);
        if (installDir is null) return false;

        var pythonExe = Path.Combine(installDir, "python", "python.exe");
        var backendScript = Path.Combine(installDir, "backend", "run_backend.py");

        return File.Exists(pythonExe) && File.Exists(backendScript);
    }
}
