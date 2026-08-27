using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Comic.WinUI.Services;

public sealed class AppUpdateService
{
    private const string LatestReleaseUrl = "https://api.github.com/repos/Leisurely-Cloud/Comic/releases/latest";
    private readonly HttpClient _httpClient;

    public AppUpdateService() : this(new HttpClient { Timeout = TimeSpan.FromSeconds(20) }) { }

    internal AppUpdateService(HttpClient httpClient)
    {
        _httpClient = httpClient;
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Comic-WinUI-Updater/2.7");
        _httpClient.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    public async Task<AppUpdateInfo> CheckAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.GetAsync(LatestReleaseUrl, cancellationToken);
        response.EnsureSuccessStatusCode();
        var release = JsonSerializer.Deserialize<GitHubRelease>(
            await response.Content.ReadAsStringAsync(cancellationToken),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException("GitHub Release 返回内容无效。");
        var current = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0);
        var latestText = release.TagName.TrimStart('v', 'V');
        var latest = Version.TryParse(latestText.Split('-', '+')[0], out var parsed) ? parsed : new Version(0, 0);
        var asset = release.Assets.FirstOrDefault(item =>
            item.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
            item.Name.Contains("Setup", StringComparison.OrdinalIgnoreCase));
        return new AppUpdateInfo(
            current.ToString(3), release.TagName, latest > current, release.Name,
            release.Body, release.HtmlUrl, asset?.Name ?? string.Empty,
            asset?.BrowserDownloadUrl ?? string.Empty, asset?.Size ?? 0);
    }

    public async Task<string> DownloadInstallerAsync(AppUpdateInfo update, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(update.AssetDownloadUrl))
            throw new InvalidOperationException("此版本没有可用的 Windows 安装包。");
        var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "ComicUpdates");
        Directory.CreateDirectory(downloads);
        var destination = Path.Combine(downloads, Path.GetFileName(update.AssetName));
        var temporary = destination + ".download";
        try
        {
            using var response = await _httpClient.GetAsync(update.AssetDownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? update.AssetSize;
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var output = File.Create(temporary);
            var buffer = new byte[81920];
            long written = 0;
            int read;
            while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                written += read;
                if (total > 0) progress?.Report(written * 100d / total);
            }
            await output.FlushAsync(cancellationToken);
            output.Close();
            File.Move(temporary, destination, true);
            progress?.Report(100);
            return destination;
        }
        finally
        {
            if (File.Exists(temporary)) try { File.Delete(temporary); } catch { }
        }
    }

    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")] public string TagName { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Body { get; set; } = string.Empty;
        [JsonPropertyName("html_url")] public string HtmlUrl { get; set; } = string.Empty;
        public List<GitHubAsset> Assets { get; set; } = [];
    }

    private sealed class GitHubAsset
    {
        public string Name { get; set; } = string.Empty;
        [JsonPropertyName("browser_download_url")] public string BrowserDownloadUrl { get; set; } = string.Empty;
        public long Size { get; set; }
    }
}

public sealed record AppUpdateInfo(
    string CurrentVersion, string LatestVersion, bool IsUpdateAvailable, string ReleaseName,
    string ReleaseNotes, string ReleasePageUrl, string AssetName, string AssetDownloadUrl, long AssetSize);
