using System.Text.Json.Serialization;

namespace Comic.WinUI.Models;

public sealed class HealthResponse
{
    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("storage_root")]
    public string StorageRoot { get; set; } = string.Empty;

    [JsonPropertyName("pid")]
    public int Pid { get; set; }
}
