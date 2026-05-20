using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Comic.WinUI.Models;

public sealed class SettingsResponse
{
    [JsonPropertyName("storage_root")]
    public string StorageRoot { get; set; } = string.Empty;

    [JsonPropertyName("legacy_root")]
    public string LegacyRoot { get; set; } = string.Empty;

    [JsonPropertyName("download_runner_configured")]
    public bool DownloadRunnerConfigured { get; set; }

    [JsonPropertyName("supported_sites")]
    public List<string> SupportedSites { get; set; } = [];
}
