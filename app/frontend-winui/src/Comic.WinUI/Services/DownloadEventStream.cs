using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Comic.WinUI.Models;
using Comic.WinUI.Services.Native;

namespace Comic.WinUI.Services;

/// <summary>把进程内任务快照适配为现有页面使用的异步事件流。</summary>
public sealed class DownloadEventStream
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = SnakeCaseNamingPolicy.Instance,
    };

    private readonly BackendClient _backendClient;

    public DownloadEventStream(BackendClient backendClient)
    {
        _backendClient = backendClient;
    }

    public async IAsyncEnumerable<DownloadStateEvent> SubscribeAsync(
        string taskId,
        int lastEventId = 0,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var eventId = lastEventId;
        string? previousPayload = null;
        while (!cancellationToken.IsCancellationRequested)
        {
            var task = await _backendClient.GetDownloadAsync(taskId, cancellationToken);
            var payload = JsonSerializer.Serialize(task, JsonOptions);
            if (!string.Equals(payload, previousPayload, StringComparison.Ordinal))
            {
                previousPayload = payload;
                yield return new DownloadStateEvent
                {
                    EventId = ++eventId,
                    EventName = "snapshot",
                    JsonPayload = payload,
                };
            }
            if (DownloadSchedulerService.IsTerminal(task.Status)) yield break;
            await Task.Delay(150, cancellationToken);
        }
    }

    public async IAsyncEnumerable<DownloadStateEvent> SubscribeExportAsync(
        string taskId,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var eventId = 0;
        string? previousPayload = null;
        while (!cancellationToken.IsCancellationRequested)
        {
            var progress = await _backendClient.GetExportProgressAsync(taskId, cancellationToken);
            var payload = JsonSerializer.Serialize(progress, JsonOptions);
            if (!string.Equals(payload, previousPayload, StringComparison.Ordinal))
            {
                previousPayload = payload;
                yield return new DownloadStateEvent
                {
                    EventId = ++eventId,
                    EventName = "export",
                    JsonPayload = payload,
                };
            }
            if (progress.Status is "completed" or "failed" or "cancelled") yield break;
            await Task.Delay(150, cancellationToken);
        }
    }
}
