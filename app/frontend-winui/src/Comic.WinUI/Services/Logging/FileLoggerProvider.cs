using Microsoft.Extensions.Logging;

namespace Comic.WinUI.Services.Logging;

/// <summary>
/// 把日志写入本地文件的轻量 <see cref="ILoggerProvider"/>。
/// 日志按天分文件，保留 <paramref name="retentionDays"/> 天，写入失败不影响主流程。
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _directory;
    private readonly int _retentionDays;
    private readonly object _gate = new();

    public FileLoggerProvider(string directory, int retentionDays = 7)
    {
        _directory = directory;
        _retentionDays = Math.Max(1, retentionDays);
        Directory.CreateDirectory(directory);
        CleanupExpiredLogs();
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(categoryName, _directory, _gate);

    public void Dispose()
    {
    }

    /// <summary>删除超过保留天数的历史日志文件。</summary>
    private void CleanupExpiredLogs()
    {
        try
        {
            var cutoff = DateTime.Now.AddDays(-_retentionDays);
            foreach (var file in Directory.EnumerateFiles(_directory, "app-*.log"))
            {
                if (File.GetLastWriteTime(file) < cutoff)
                {
                    File.Delete(file);
                }
            }
        }
        catch
        {
            // 清理失败不影响主流程。
        }
    }

    private sealed class FileLogger : ILogger
    {
        private readonly string _category;
        private readonly string _directory;
        private readonly object _gate;

        public FileLogger(string category, string directory, object gate)
        {
            _category = category;
            _directory = directory;
            _gate = gate;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        // 过滤交给 ILoggingBuilder 的规则(默认下限是 Information)。这里再写死一层
        // >= Information 的下限,会让 SetMinimumLevel(Debug) 之类的配置完全失效,
        // 想临时开 Debug 排查都开不出来。
        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{logLevel}] [{_category}] {formatter(state, exception)}";
            if (exception is not null)
            {
                line += Environment.NewLine + exception;
            }

            var path = Path.Combine(_directory, $"app-{DateTime.Now:yyyyMMdd}.log");
            lock (_gate)
            {
                try
                {
                    File.AppendAllText(path, line + Environment.NewLine);
                }
                catch
                {
                    // 日志失败不应影响主流程。
                }
            }
        }
    }
}
