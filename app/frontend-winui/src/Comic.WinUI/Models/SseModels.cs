using System.Text.Json.Serialization;

namespace Comic.WinUI.Models;

public sealed class SseDownloadEvent
{
    [JsonPropertyName("event_id")]
    public int EventId { get; set; }

    [JsonPropertyName("event_name")]
    public string EventName { get; set; } = string.Empty;

    [JsonPropertyName("json_payload")]
    public string JsonPayload { get; set; } = string.Empty;
}
