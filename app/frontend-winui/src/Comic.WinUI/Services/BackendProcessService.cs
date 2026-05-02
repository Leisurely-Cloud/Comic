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
            };

            var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("无法启动后端进程。");
            process.EnableRaisingEvents = true;
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
