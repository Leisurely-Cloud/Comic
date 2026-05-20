namespace Comic.WinUI.Models;

public sealed class SseDownloadEvent
{
    public int EventId { get; set; }
    public string EventName { get; set; } = string.Empty;
    public string JsonPayload { get; set; } = string.Empty;
}
