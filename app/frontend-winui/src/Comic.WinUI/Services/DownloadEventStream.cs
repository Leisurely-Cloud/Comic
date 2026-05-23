using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Comic.WinUI.Models;

namespace Comic.WinUI.Services;

public sealed class DownloadEventStream
{
    private readonly BackendClient _backendClient;
    private readonly HttpClient _httpClient;

    public DownloadEventStream(BackendClient backendClient, HttpClient httpClient)
    {
        _backendClient = backendClient;
        _httpClient = httpClient;
    }

    public async IAsyncEnumerable<SseDownloadEvent> SubscribeAsync(
        string taskId,
        int lastEventId = 0,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var uri = _backendClient.GetSseUri(taskId, lastEventId);
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        // Use CancellationToken.None to bypass HttpClient.Timeout (30s default) for long-lived SSE connections.
        // Cancellation is handled externally via the caller's cancellationToken in the read loop.
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, CancellationToken.None);
        response.EnsureSuccessStatusCode();

        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        var currentEvent = new SseDownloadEvent();
        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null)
            {
                break;
            }

            if (line.StartsWith("id: ", StringComparison.Ordinal))
            {
                if (int.TryParse(line.AsSpan(4), out var id))
                {
                    currentEvent.EventId = id;
                }
            }
            else if (line.StartsWith("event: ", StringComparison.Ordinal))
            {
                currentEvent.EventName = line[7..];
            }
            else if (line.StartsWith("data: ", StringComparison.Ordinal))
            {
                currentEvent.JsonPayload = line[6..];
            }
            else if (string.IsNullOrEmpty(line) && !string.IsNullOrEmpty(currentEvent.JsonPayload))
            {
                yield return currentEvent;
                currentEvent = new SseDownloadEvent();
            }
        }
    }

    public async IAsyncEnumerable<SseDownloadEvent> SubscribeExportAsync(
        string taskId,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var uri = _backendClient.GetExportSseUri(taskId);
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, CancellationToken.None);
        response.EnsureSuccessStatusCode();

        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        var currentEvent = new SseDownloadEvent();
        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null)
            {
                break;
            }

            if (line.StartsWith("id: ", StringComparison.Ordinal))
            {
                if (int.TryParse(line.AsSpan(4), out var id))
                {
                    currentEvent.EventId = id;
                }
            }
            else if (line.StartsWith("event: ", StringComparison.Ordinal))
            {
                currentEvent.EventName = line[7..];
            }
            else if (line.StartsWith("data: ", StringComparison.Ordinal))
            {
                currentEvent.JsonPayload = line[6..];
            }
            else if (string.IsNullOrEmpty(line) && !string.IsNullOrEmpty(currentEvent.JsonPayload))
            {
                yield return currentEvent;
                currentEvent = new SseDownloadEvent();
            }
        }
    }
}
