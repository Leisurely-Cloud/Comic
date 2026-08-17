using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Comic.WinUI.Models;
using Comic.WinUI.Services.Native;

namespace Comic.WinUI.Services;

/// <summary>
/// 保留界面层调用契约，内部直接调用同进程的 C# 服务。
/// </summary>
public sealed class BackendClient
{
    private readonly NativeBackendService _backend;

    public BackendClient(NativeBackendService backend)
    {
        _backend = backend;
    }

    public Task<HealthResponse> GetHealthAsync(CancellationToken cancellationToken = default) =>
        InvokeAsync(() => _backend.GetHealthAsync(cancellationToken), "health_failed");

    public Task<MangaResolveResponse> ResolveMangaAsync(MangaResolveRequest request, CancellationToken cancellationToken = default) =>
        InvokeAsync(() => _backend.ResolveMangaAsync(request, cancellationToken), "resolve_failed");

    public Task<DownloadTaskDto> CreateDownloadAsync(DownloadCreateRequest request, CancellationToken cancellationToken = default) =>
        InvokeAsync(() => _backend.CreateDownloadAsync(request, cancellationToken), "download_create_failed");

    public Task<DownloadListResponse> GetDownloadsAsync(CancellationToken cancellationToken = default) =>
        InvokeAsync(() => _backend.GetDownloadsAsync(cancellationToken), "download_list_failed");

    public Task<DownloadTaskDto> GetDownloadAsync(string taskId, CancellationToken cancellationToken = default) =>
        InvokeAsync(() => _backend.GetDownloadAsync(taskId, cancellationToken), "download_not_found");

    public Task<DownloadActionResponse> PauseDownloadAsync(string taskId, CancellationToken cancellationToken = default) =>
        InvokeAsync(() => _backend.PauseDownloadAsync(taskId, cancellationToken), "download_pause_failed");

    public Task<DownloadActionResponse> ResumeDownloadAsync(string taskId, CancellationToken cancellationToken = default) =>
        InvokeAsync(() => _backend.ResumeDownloadAsync(taskId, cancellationToken), "download_resume_failed");

    public Task<DownloadActionResponse> StopDownloadAsync(string taskId, CancellationToken cancellationToken = default) =>
        InvokeAsync(() => _backend.StopDownloadAsync(taskId, cancellationToken), "download_stop_failed");

    public Task<LibraryListResponse> GetLibraryAsync(
        string siteKey = "",
        string keyword = "",
        int page = 1,
        int pageSize = 20,
        CancellationToken cancellationToken = default) =>
        InvokeAsync(() => _backend.GetLibraryAsync(keyword, page, pageSize, cancellationToken), "library_failed");

    public Task<SettingsResponse> GetSettingsAsync(CancellationToken cancellationToken = default) =>
        InvokeAsync(() => _backend.GetSettingsAsync(cancellationToken), "settings_failed");

    public Task<SettingsResponse> UpdateSettingsAsync(SettingsUpdateRequest settings, CancellationToken cancellationToken = default) =>
        InvokeAsync(() => _backend.UpdateSettingsAsync(settings, cancellationToken), "settings_update_failed");

    public Task<LibraryCheckUpdatesResponse> CheckLibraryUpdatesAsync(CancellationToken cancellationToken = default) =>
        InvokeAsync(() => _backend.CheckLibraryUpdatesAsync(cancellationToken), "library_update_check_failed");

    public Task<ExportCbzResponse> ExportCbzAsync(string rootDir, CancellationToken cancellationToken = default) =>
        InvokeAsync(() => _backend.ExportCbzAsync(rootDir, cancellationToken), "export_failed");

    public Task<ExportCbzProgress> GetExportProgressAsync(string taskId, CancellationToken cancellationToken = default) =>
        InvokeAsync(() => _backend.GetExportProgressAsync(taskId, cancellationToken), "export_not_found");

    public Task<RankingResponse> GetRankingAsync(
        string site = "jmcomic",
        string section = "",
        int page = 1,
        CancellationToken cancellationToken = default) =>
        InvokeAsync(() => _backend.GetRankingAsync(section, page, cancellationToken), "ranking_failed");

    public Task<RankingSectionsResponse> GetRankingSectionsAsync(string site = "jmcomic", CancellationToken cancellationToken = default) =>
        InvokeAsync(() => _backend.GetRankingSectionsAsync(cancellationToken), "ranking_sections_failed");

    public Task<ReaderChaptersResponse> GetReaderChaptersAsync(string rootDir, CancellationToken cancellationToken = default) =>
        InvokeAsync(() => _backend.GetReaderChaptersAsync(rootDir, cancellationToken), "reader_failed");

    public Task<ReaderImagesResponse> GetChapterImagesAsync(
        string rootDir,
        string chapterDirName,
        CancellationToken cancellationToken = default) =>
        InvokeAsync(() => _backend.GetChapterImagesAsync(rootDir, chapterDirName, cancellationToken), "reader_failed");

    public Task<byte[]> GetImageBytesAsync(string imagePath, CancellationToken cancellationToken = default) =>
        InvokeAsync(() => _backend.GetImageBytesAsync(imagePath, cancellationToken), "reader_image_failed");

    public Task<SearchResponse> SearchAsync(
        string query,
        string site = "jmcomic",
        int page = 1,
        CancellationToken cancellationToken = default) =>
        InvokeAsync(() => _backend.SearchAsync(query, page, cancellationToken), "search_failed");

    public Task<DownloadHistoryResponse> GetDownloadHistoryAsync(
        int page = 1,
        int pageSize = 20,
        CancellationToken cancellationToken = default) =>
        InvokeAsync(() => _backend.GetDownloadHistoryAsync(page, pageSize, cancellationToken), "history_failed");

    public Task<BatchActionResponse> BatchStopDownloadsAsync(List<string> taskIds, CancellationToken cancellationToken = default) =>
        InvokeAsync(() => _backend.BatchStopDownloadsAsync(taskIds, cancellationToken), "batch_stop_failed");

    public Task<BatchActionResponse> BatchDeleteDownloadsAsync(List<string> taskIds, CancellationToken cancellationToken = default) =>
        InvokeAsync(() => _backend.BatchDeleteDownloadsAsync(taskIds, cancellationToken), "batch_delete_failed");

    public Task<BatchActionResponse> BatchStopChaptersAsync(
        string taskId,
        List<string> chapterIds,
        CancellationToken cancellationToken = default) =>
        InvokeAsync(
            () => _backend.BatchStopChaptersAsync(taskId, chapterIds, cancellationToken),
            "batch_chapter_stop_failed");

    public Task<BatchActionResponse> BatchDeleteChaptersAsync(
        string taskId,
        List<string> chapterIds,
        CancellationToken cancellationToken = default) =>
        InvokeAsync(
            () => _backend.BatchDeleteChaptersAsync(taskId, chapterIds, cancellationToken),
            "batch_chapter_delete_failed");

    public async Task<object> ClearDownloadHistoryAsync(CancellationToken cancellationToken = default)
    {
        await InvokeAsync(async () =>
        {
            await _backend.ClearDownloadHistoryAsync(cancellationToken);
            return true;
        }, "history_clear_failed");
        return new { status = "ok" };
    }

    public Task<int> DeleteDownloadHistoryAsync(
        IReadOnlyCollection<string> historyIds,
        CancellationToken cancellationToken = default) =>
        InvokeAsync(
            () => _backend.DeleteDownloadHistoryAsync(historyIds, cancellationToken),
            "history_delete_failed");

    private static async Task<T> InvokeAsync<T>(Func<Task<T>> operation, string code)
    {
        try
        {
            return await operation();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (BackendApiException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new BackendApiException(new ApiError { Code = code, Message = ex.Message });
        }
    }
}
