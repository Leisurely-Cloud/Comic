using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Comic.WinUI.Services;

public sealed class AppUpdateService
{
    private const string LatestReleaseUrl = "https://api.github.com/repos/Leisurely-Cloud/Comic/releases/latest";
    private readonly HttpClient _httpClient;
    private readonly string _downloadDirectory;
    private readonly TimeSpan _downloadReadTimeout;

    public AppUpdateService() : this(new HttpClient { Timeout = TimeSpan.FromSeconds(20) }) { }

    internal AppUpdateService(HttpClient httpClient, string? downloadDirectory = null, TimeSpan? downloadReadTimeout = null)
    {
        _httpClient = httpClient;
        _downloadDirectory = downloadDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "ComicUpdates");
        _downloadReadTimeout = downloadReadTimeout ?? TimeSpan.FromSeconds(30);
        if (_downloadReadTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(downloadReadTimeout));
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
            $"Comic-WinUI-Updater/{Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0"}");
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
            asset?.BrowserDownloadUrl ?? string.Empty, asset?.Size ?? 0, asset?.Digest ?? string.Empty);
    }

    public async Task<string> DownloadInstallerAsync(AppUpdateInfo update, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(update.AssetDownloadUrl))
            throw new InvalidOperationException("此版本没有可用的 Windows 安装包。");
        var expectedHash = ParseDigest(update.AssetDigest);
        var fileName = Path.GetFileName(update.AssetName);
        if (string.IsNullOrWhiteSpace(fileName))
            throw new InvalidDataException("安装包文件名无效，请重新检查更新。");
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(_downloadDirectory);
        var destination = Path.Combine(_downloadDirectory, fileName);
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".download";
        try
        {
            using var response = await WaitForNetworkAsync(token => _httpClient.GetAsync(
                update.AssetDownloadUrl, HttpCompletionOption.ResponseHeadersRead, token), cancellationToken);
            response.EnsureSuccessStatusCode();
            var contentLength = response.Content.Headers.ContentLength;
            if (update.AssetSize > 0 && contentLength.HasValue && contentLength.Value != update.AssetSize)
                throw new InvalidDataException("安装包大小与发布信息不一致，请重新检查更新。");
            var total = update.AssetSize > 0 ? update.AssetSize : contentLength ?? 0;
            await using var input = await WaitForNetworkAsync(response.Content.ReadAsStreamAsync, cancellationToken);
            using var hasher = expectedHash is null ? null : IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long written = 0;
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var buffer = new byte[81920];
                while (true)
                {
                    var read = await WaitForNetworkAsync(token => input.ReadAsync(buffer.AsMemory(), token).AsTask(), cancellationToken);
                    if (read == 0) break;
                    if (total > 0 && read > total - written)
                        throw new InvalidDataException("收到的安装包超出预期大小，请重试。");
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    hasher?.AppendData(buffer, 0, read);
                    written += read;
                    if (total > 0) progress?.Report(Math.Min(99.9, written * 100d / total));
                }
                if (written == 0 || (total > 0 && written != total))
                    throw new InvalidDataException("安装包下载不完整，请重试。");
                if (expectedHash is not null && !CryptographicOperations.FixedTimeEquals(hasher!.GetHashAndReset(), expectedHash))
                    throw new InvalidDataException("安装包 SHA-256 校验失败，请重新下载。");
                await output.FlushAsync(cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, destination, true);
            progress?.Report(100);
            return destination;
        }
        finally
        {
            if (File.Exists(temporary)) try { File.Delete(temporary); } catch { }
        }
    }

    private async Task<T> WaitForNetworkAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_downloadReadTimeout);
        Task<T>? pending = null;
        try
        {
            pending = operation(timeout.Token);
            return await pending.WaitAsync(_downloadReadTimeout, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            timeout.Cancel();
            if (pending is not null) _ = DisposeLateResultAsync(pending);
            cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException("更新下载长时间无响应，请重试。");
        }
        catch (TimeoutException)
        {
            timeout.Cancel();
            if (pending is not null) _ = DisposeLateResultAsync(pending);
            cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException("更新下载长时间无响应，请重试。");
        }
    }

    private static async Task DisposeLateResultAsync<T>(Task<T> pending)
    {
        try
        {
            if (await pending.ConfigureAwait(false) is IDisposable result) result.Dispose();
        }
        catch { }
    }

    private static byte[]? ParseDigest(string digest)
    {
        if (string.IsNullOrWhiteSpace(digest)) return null;
        const string prefix = "sha256:";
        if (!digest.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
            digest.Length != prefix.Length + 64 || digest.Skip(prefix.Length).Any(character => !Uri.IsHexDigit(character)))
            throw new InvalidDataException("安装包校验信息无效，请重新检查更新。");
        return Convert.FromHexString(digest[prefix.Length..]);
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
        public string? Digest { get; set; }
    }
}

public sealed record AppUpdateInfo(
    string CurrentVersion, string LatestVersion, bool IsUpdateAvailable, string ReleaseName,
    string ReleaseNotes, string ReleasePageUrl, string AssetName, string AssetDownloadUrl, long AssetSize,
    string AssetDigest = "");
