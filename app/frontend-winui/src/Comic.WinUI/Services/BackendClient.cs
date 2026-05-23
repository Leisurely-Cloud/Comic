using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Comic.WinUI.Models;

namespace Comic.WinUI.Services;

public sealed class BackendClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = SnakeCaseNamingPolicy.Instance,
    };

    private readonly HttpClient _httpClient;
    private Uri _baseUri = new("http://127.0.0.1:18765/", UriKind.Absolute);

    public BackendClient(HttpClient httpClient, string baseAddress = "http://127.0.0.1:18765/")
    {
        _httpClient = httpClient;
        SetBaseAddress(baseAddress);
    }

    public void SetBaseAddress(string baseAddress)
    {
        var normalized = string.IsNullOrWhiteSpace(baseAddress)
            ? "http://127.0.0.1:18765/"
            : baseAddress.Trim();
        if (!normalized.EndsWith("/", StringComparison.Ordinal))
        {
            normalized += "/";
        }

        _baseUri = new Uri(normalized, UriKind.Absolute);
    }

    public Task<HealthResponse> GetHealthAsync(CancellationToken cancellationToken = default)
    {
        return ReadFromJsonAsync<HealthResponse>("api/health", cancellationToken);
    }

    public Task<MangaResolveResponse> ResolveMangaAsync(MangaResolveRequest request, CancellationToken cancellationToken = default)
    {
        return PostJsonAsync<MangaResolveRequest, MangaResolveResponse>("api/resolve", request, cancellationToken);
    }

    public Task<DownloadTaskDto> CreateDownloadAsync(DownloadCreateRequest request, CancellationToken cancellationToken = default)
    {
        return PostJsonAsync<DownloadCreateRequest, DownloadTaskDto>("api/downloads", request, cancellationToken);
    }

    public Task<DownloadListResponse> GetDownloadsAsync(CancellationToken cancellationToken = default)
    {
        return ReadFromJsonAsync<DownloadListResponse>("api/downloads", cancellationToken);
    }

    public Task<DownloadTaskDto> GetDownloadAsync(string taskId, CancellationToken cancellationToken = default)
    {
        return ReadFromJsonAsync<DownloadTaskDto>($"api/downloads/{taskId}", cancellationToken);
    }

    public Task<DownloadActionResponse> PauseDownloadAsync(string taskId, CancellationToken cancellationToken = default)
    {
        return PostJsonAsync<object, DownloadActionResponse>($"api/downloads/{taskId}/pause", new { }, cancellationToken);
    }

    public Task<DownloadActionResponse> ResumeDownloadAsync(string taskId, CancellationToken cancellationToken = default)
    {
        return PostJsonAsync<object, DownloadActionResponse>($"api/downloads/{taskId}/resume", new { }, cancellationToken);
    }

    public Task<DownloadActionResponse> StopDownloadAsync(string taskId, CancellationToken cancellationToken = default)
    {
        return PostJsonAsync<object, DownloadActionResponse>($"api/downloads/{taskId}/stop", new { }, cancellationToken);
    }

    public Task<LibraryListResponse> GetLibraryAsync(string siteKey = "", string keyword = "", int page = 1, int pageSize = 20, CancellationToken cancellationToken = default)
    {
        var query = $"api/library?site_key={Uri.EscapeDataString(siteKey)}&keyword={Uri.EscapeDataString(keyword)}&page={page}&page_size={pageSize}";
        return ReadFromJsonAsync<LibraryListResponse>(query, cancellationToken);
    }

    public Task<SettingsResponse> GetSettingsAsync(CancellationToken cancellationToken = default)
    {
        return ReadFromJsonAsync<SettingsResponse>("api/settings", cancellationToken);
    }

    public Task<SettingsResponse> UpdateSettingsAsync(object settings, CancellationToken cancellationToken = default)
    {
        return PostJsonAsync<object, SettingsResponse>("api/settings", settings, cancellationToken);
    }

    public Task<LibraryCheckUpdatesResponse> CheckLibraryUpdatesAsync(CancellationToken cancellationToken = default)
    {
        return ReadFromJsonAsync<LibraryCheckUpdatesResponse>("api/library/check-updates", cancellationToken);
    }

    public Task<ExportCbzResponse> ExportCbzAsync(string rootDir, CancellationToken cancellationToken = default)
    {
        return PostJsonAsync<object, ExportCbzResponse>("api/library/export-cbz", new { root_dir = rootDir }, cancellationToken);
    }

    public Task<RankingResponse> GetRankingAsync(string site = "baozimh", string section = "", int page = 1, CancellationToken cancellationToken = default)
    {
        var query = $"api/ranking?site={Uri.EscapeDataString(site)}&section={Uri.EscapeDataString(section)}&page={page}";
        return ReadFromJsonAsync<RankingResponse>(query, cancellationToken);
    }

    public Task<RankingSectionsResponse> GetRankingSectionsAsync(string site = "baozimh", CancellationToken cancellationToken = default)
    {
        return ReadFromJsonAsync<RankingSectionsResponse>($"api/ranking/sections?site={Uri.EscapeDataString(site)}", cancellationToken);
    }

    public Task<ReaderChaptersResponse> GetReaderChaptersAsync(string rootDir, CancellationToken cancellationToken = default)
    {
        return ReadFromJsonAsync<ReaderChaptersResponse>($"api/library/reader?root_dir={Uri.EscapeDataString(rootDir)}", cancellationToken);
    }

    public Task<ReaderImagesResponse> GetChapterImagesAsync(string rootDir, string chapterDirName, CancellationToken cancellationToken = default)
    {
        return ReadFromJsonAsync<ReaderImagesResponse>($"api/library/reader/images?root_dir={Uri.EscapeDataString(rootDir)}&chapter={Uri.EscapeDataString(chapterDirName)}", cancellationToken);
    }

    public async Task<byte[]> GetImageBytesAsync(string imagePath, CancellationToken cancellationToken = default)
    {
        var uri = new Uri(_baseUri, $"api/library/reader/image?path={Uri.EscapeDataString(imagePath)}");
        using var response = await _httpClient.GetAsync(uri, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync(cancellationToken);
    }

    public Task<SearchResponse> SearchAsync(string query, string site = "baozimh", int page = 1, CancellationToken cancellationToken = default)
    {
        return PostJsonAsync<object, SearchResponse>("api/search", new { query, site, page }, cancellationToken);
    }

    public Task<DownloadHistoryResponse> GetDownloadHistoryAsync(int page = 1, int pageSize = 20, CancellationToken cancellationToken = default)
    {
        return ReadFromJsonAsync<DownloadHistoryResponse>($"api/downloads/history?page={page}&page_size={pageSize}", cancellationToken);
    }

    public Task<BatchActionResponse> BatchStopDownloadsAsync(List<string> taskIds, CancellationToken cancellationToken = default)
    {
        return PostJsonAsync<object, BatchActionResponse>("api/downloads/batch-stop", new { task_ids = taskIds }, cancellationToken);
    }

    public Task<BatchActionResponse> BatchDeleteDownloadsAsync(List<string> taskIds, CancellationToken cancellationToken = default)
    {
        return PostJsonAsync<object, BatchActionResponse>("api/downloads/batch-delete", new { task_ids = taskIds }, cancellationToken);
    }

    public Task<object> ClearDownloadHistoryAsync(CancellationToken cancellationToken = default)
    {
        return PostJsonAsync<object, object>("api/downloads/clear-history", new { }, cancellationToken);
    }

    public Uri GetSseUri(string taskId, int lastEventId = 0)
    {
        return new Uri(_baseUri, $"api/downloads/{taskId}/events?last_event_id={lastEventId}");
    }

    public Uri GetExportSseUri(string taskId)
    {
        return new Uri(_baseUri, $"api/library/export-cbz/events?task_id={taskId}");
    }

    private async Task<T> ReadFromJsonAsync<T>(string relativeUri, CancellationToken cancellationToken)
    {
        var uri = new Uri(_baseUri, relativeUri);
        using var response = await _httpClient.GetAsync(uri, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            await ThrowApiErrorAsync(response);
        }

        return await response.Content.ReadFromJsonAsync<T>(JsonOptions, cancellationToken)
               ?? throw new InvalidOperationException("Response deserialized to null");
    }

    private async Task<TResponse> PostJsonAsync<TRequest, TResponse>(string relativeUri, TRequest request, CancellationToken cancellationToken)
    {
        var uri = new Uri(_baseUri, relativeUri);
        var json = JsonSerializer.Serialize(request, JsonOptions);
        using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        using var response = await _httpClient.PostAsync(uri, content, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            await ThrowApiErrorAsync(response);
        }

        return await response.Content.ReadFromJsonAsync<TResponse>(JsonOptions, cancellationToken)
               ?? throw new InvalidOperationException("Response deserialized to null");
    }

    private static async Task ThrowApiErrorAsync(HttpResponseMessage response)
    {
        ApiError? error = null;
        try
        {
            var raw = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.TryGetProperty("error", out var errorElement))
            {
                error = errorElement.Deserialize<ApiError>(JsonOptions);
            }
            else
            {
                error = new ApiError { Code = "http_error", Message = raw };
            }
        }
        catch
        {
            var body = await response.Content.ReadAsStringAsync();
            error = new ApiError { Code = "http_error", Message = body };
        }

        throw new BackendApiException(error ?? new ApiError { Code = "unknown", Message = response.ReasonPhrase ?? "Request failed" });
    }
}

