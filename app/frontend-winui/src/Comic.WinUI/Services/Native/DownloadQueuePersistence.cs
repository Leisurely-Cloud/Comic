using System.Text.Json;
using Comic.WinUI.Models;
using Microsoft.Extensions.Logging;

namespace Comic.WinUI.Services.Native;

public sealed partial class DownloadSchedulerService
{
    private readonly object _queueLock = new();
    private string? _queueFile;
    private Timer? _queueTimer;
    private volatile bool _shuttingDown;
    private string? _lastQueueJson;

    internal void SaveQueue()
    {
        lock (_queueLock)
        {
            if (_shuttingDown) return;
            try
            {
                var items = _downloads.Values.Select(state =>
                {
                    lock (state.Gate)
                    {
                        return new QueueItem
                        {
                            Request = state.Request,
                            Task = CloneTask(state),
                            Order = _creationOrder.GetValueOrDefault(state.Dto.Id),
                            StoppedChapters = state.StoppedChapterIds.ToList(),
                            DeletedChapters = state.DeletedChapterIds.ToList(),
                        };
                    }
                }).Where(item => item.Task.Status != "completed").OrderBy(item => item.Order).ToList();
                var json = JsonSerializer.Serialize(items, JsonOptions);
                if (json == _lastQueueJson) return;
                AtomicJsonFile.WriteText(_queueFile ??= Path.Combine(StateDirectory, "task_queue.json"), json);
                _lastQueueJson = json;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "保存下载队列失败");
            }
        }
    }

    private void RestoreQueue()
    {
        _lastQueueJson = null;
        var items = AtomicJsonFile.Read<List<QueueItem>>(
            _queueFile ??= Path.Combine(StateDirectory, "task_queue.json"), JsonOptions) ?? [];
        foreach (var item in items)
        {
            if (!IsValidQueueItem(item) || item.Task.Status == "completed") continue;
            item.Task.DownloadSpeedBytesPerSecond = 0;
            var resume = _applicationSettings?.ResumeDownloadsOnStartup == true && item.Task.Status is ("running" or "pending" or "scheduled");
            if (item.Task.Status == "stopping") item.Task.Status = "stopped";
            if (!IsTerminal(item.Task.Status))
            {
                item.Task.Status = resume ? "pending" : "paused";
                item.Task.StatusText = resume ? "已恢复任务，等待检查本地进度与下载计划" : "已恢复任务，点击继续后检查本地进度与下载计划";
            }
            var state = new NativeDownloadTask(item.Request, item.Task);
            state.StoppedChapterIds.UnionWith(item.StoppedChapters ?? []);
            state.DeletedChapterIds.UnionWith(item.DeletedChapters ?? []);
            if (_downloads.TryAdd(item.Task.Id, state))
                _creationOrder[item.Task.Id] = Interlocked.Increment(ref _creationSequence);
            else state.Dispose();
        }
        foreach (var state in _downloads.Values.Where(state => state.Dto.Status == "pending"))
            state.Worker = Task.Run(() => ProcessDownloadAsync(state), CancellationToken.None);
    }

    internal static bool IsValidQueueItem(QueueItem? item) =>
        item is { Request: not null, Task: not null } &&
        !string.IsNullOrWhiteSpace(item.Task.Id) && !string.IsNullOrWhiteSpace(item.Request.Url) &&
        JmComicService.ParseMangaId(item.Request.Url).MangaId is not null &&
        item.Task.Status is ("running" or "pending" or "scheduled" or "pausing" or "paused" or "stopping" or "stopped" or "failed" or "partial" or "completed") &&
        item.Task.Logs is not null && item.Task.Logs.All(log => log is not null) &&
        item.Task.Chapters is not null && item.Task.Chapters.All(chapter => chapter is not null) &&
        (item.Request.Chapters?.All(chapter => !string.IsNullOrWhiteSpace(chapter)) ?? true);

    internal sealed class QueueItem
    {
        public DownloadCreateRequest Request { get; set; } = new();
        public DownloadTaskDto Task { get; set; } = new();
        public long Order { get; set; }
        public List<string>? StoppedChapters { get; set; } = [];
        public List<string>? DeletedChapters { get; set; } = [];
    }
}
