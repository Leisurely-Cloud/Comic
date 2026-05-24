using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Comic.WinUI.Services;

public sealed class BackendProcessService
{
    private readonly BackendSettingsService _settingsService;
    private readonly object _syncRoot = new();
    private Process? _ownedProcess;

    public BackendProcessService(BackendSettingsService settingsService)
    {
        _settingsService = settingsService;
    }

    public Task<bool> StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_syncRoot)
        {
            if (_ownedProcess is not null && !_ownedProcess.HasExited)
            {
                return Task.FromResult(false);
            }

            var settings = _settingsService.GetSettings();

            var startInfo = new ProcessStartInfo
            {
                FileName = settings.PythonExecutablePath,
                Arguments = settings.PythonArguments,
                WorkingDirectory = settings.WorkingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            };

            // 设置 PYTHONPATH 让嵌入式 Python 能找到依赖包
            var pythonDir = Path.GetDirectoryName(settings.PythonExecutablePath);
            if (!string.IsNullOrEmpty(pythonDir))
            {
                var sitePackages = Path.Combine(pythonDir, "Lib", "site-packages");
                if (Directory.Exists(sitePackages))
                {
                    startInfo.EnvironmentVariables["PYTHONPATH"] = sitePackages;
                }
            }

            // 调试信息仅输出到调试窗口
            var debugInfo = $"FileName: {startInfo.FileName}\nArguments: {startInfo.Arguments}\nWorkingDirectory: {startInfo.WorkingDirectory}\nPYTHONPATH: {startInfo.EnvironmentVariables["PYTHONPATH"] ?? "not set"}\nTime: {DateTime.Now}\n";
            System.Diagnostics.Debug.WriteLine(debugInfo);

            var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("无法启动后端进程。");
            process.EnableRaisingEvents = true;

            // 捕获 stderr 输出用于调试
            var stderrTask = process.StandardError.ReadToEndAsync();
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            process.Exited += async (_, _) =>
            {
                try
                {
                    var stderr = await stderrTask;
                    var stdout = await stdoutTask;
                    if (!string.IsNullOrEmpty(stderr) || !string.IsNullOrEmpty(stdout))
                    {
                        var logPath = Path.Combine(settings.WorkingDirectory, "backend-startup.log");
                        await File.WriteAllTextAsync(logPath, $"STDOUT:\n{stdout}\n\nSTDERR:\n{stderr}");
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Failed to log backend output: {ex.Message}");
                }
            };
            process.Exited += (_, _) =>
            {
                lock (_syncRoot)
                {
                    if (ReferenceEquals(_ownedProcess, process))
                    {
                        _ownedProcess = null;
                    }
                }
            };
            _ownedProcess = process;
            return Task.FromResult(true);
        }
    }

    public async Task StopAsync(int? pid = null, CancellationToken cancellationToken = default)
    {
        Process? target = null;

        lock (_syncRoot)
        {
            if (pid.HasValue)
            {
                try
                {
                    target = Process.GetProcessById(pid.Value);
                }
                catch
                {
                    target = null;
                }
            }

            if (target is null && _ownedProcess is not null && !_ownedProcess.HasExited)
            {
                target = _ownedProcess;
            }
        }

        if (target is null || target.HasExited)
        {
            return;
        }

        target.Kill(entireProcessTree: true);
        await target.WaitForExitAsync(cancellationToken);

        lock (_syncRoot)
        {
            if (ReferenceEquals(_ownedProcess, target))
            {
                _ownedProcess = null;
            }
        }
    }
}
