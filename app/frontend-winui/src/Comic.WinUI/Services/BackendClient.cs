using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Comic.WinUI.Models;
using Comic.WinUI.Services.Native;

namespace Comic.WinUI.Services;

/// <summary>
/// 界面层调用契约。内部把请求编排到同进程的职责化服务:
/// 站点协议(<see cref="JmComicService"/>)、下载调度(<see cref="DownloadSchedulerService"/>)、
/// 书库(<see cref="LibraryStorageService"/>)、CBZ 导出(<see cref="CbzExportService"/>)与阅读(<see cref="ReaderService"/>)。
/// </summary>
public sealed class BackendClient
{
    private readonly JmComicService _jmComic;
    private readonly DownloadSchedulerService _downloads;
    private readonly LibraryStorageService _library;
    private readonly CbzExportService _export;
    private readonly ReaderService _reader;
    private readonly ApplicationSettingsService _applicationSettings;
    private readonly IJmCredentialStore _jmCredentials;
    private readonly SemaphoreSlim _restoreLoginGate = new(1, 1);
    private int _loginGeneration;

    public BackendClient(
        JmComicService jmComic,
        DownloadSchedulerService downloads,
        LibraryStorageService library,
        CbzExportService export,
        ReaderService reader,
        ApplicationSettingsService applicationSettings,
        IJmCredentialStore? jmCredentials = null)
    {
        _jmComic = jmComic;
        _downloads = downloads;
        _library = library;
        _export = export;
        _reader = reader;
        _applicationSettings = applicationSettings;
        _jmCredentials = jmCredentials ?? new VolatileJmCredentialStore();
    }

    public Task<MangaResolveResponse> ResolveMangaAsync(MangaResolveRequest request, CancellationToken cancellationToken = default) =>
        InvokeAsync(() => _jmComic.ResolveAsync(request.Url, cancellationToken), "resolve_failed");

    public JmAccountState GetJmAccountState() => _jmComic.GetAccountState();

    public async Task<JmAccountInfo> LoginJmAsync(
        string username,
        string password,
        bool rememberLogin = false,
        CancellationToken cancellationToken = default)
    {
        var account = await InvokeAsync(
            () => _jmComic.LoginAsync(username, password, cancellationToken),
            "jm_login_failed");
        if (rememberLogin) _jmCredentials.TrySave(username, password);
        else _jmCredentials.Clear();
        return account;
    }

    public bool HasSavedJmLogin => _jmCredentials.HasCredential;

    public async Task<JmAccountState> RestoreJmLoginAsync(CancellationToken cancellationToken = default)
    {
        var current = _jmComic.GetAccountState();
        if (current.IsLoggedIn) return current;
        if (!_jmCredentials.TryLoad(out var username, out var password)) return current;

        await _restoreLoginGate.WaitAsync(cancellationToken);
        try
        {
            current = _jmComic.GetAccountState();
            if (current.IsLoggedIn) return current;
            var generation = Volatile.Read(ref _loginGeneration);
            var account = await InvokeAsync(
                () => _jmComic.LoginAsync(username, password, cancellationToken),
                "jm_login_restore_failed");
            if (generation != Volatile.Read(ref _loginGeneration))
            {
                _jmComic.Logout();
                return _jmComic.GetAccountState();
            }
            return new JmAccountState { IsLoggedIn = true, Account = account };
        }
        finally
        {
            password = string.Empty;
            _restoreLoginGate.Release();
        }
    }

    public void LogoutJm()
    {
        Interlocked.Increment(ref _loginGeneration);
        _jmComic.Logout();
        _jmCredentials.Clear();
    }

    public Task<JmFavoriteResponse> GetJmFavoritesAsync(
        int page = 1,
        string folderId = "0",
        CancellationToken cancellationToken = default) =>
        InvokeAsync(() => _jmComic.GetFavoritesAsync(page, folderId, cancellationToken: cancellationToken), "jm_favorites_failed");

    public Task<JmFavoriteMutationResult> SetJmFavoriteAsync(
        string albumId,
        bool isFavorite,
        CancellationToken cancellationToken = default) =>
        InvokeAsync(() => _jmComic.SetJmFavoriteAsync(albumId, isFavorite, cancellationToken), "jm_favorite_update_failed");

    public Task<DownloadTaskDto> CreateDownloadAsync(DownloadCreateRequest request, CancellationToken cancellationToken = default) =>
        InvokeAsync(() => _downloads.CreateDownloadAsync(request, cancellationToken), "download_create_failed");

    public Task<DownloadListResponse> GetDownloadsAsync(CancellationToken cancellationToken = default) =>
        InvokeAsync(() => _downloads.GetDownloadsAsync(cancellationToken), "download_list_failed");

    public Task<DownloadTaskDto> GetDownloadAsync(string taskId, CancellationToken cancellationToken = default) =>
        InvokeAsync(() => _downloads.GetDownloadAsync(taskId, cancellationToken), "download_not_found");

    public Task<DownloadActionResponse> PauseDownloadAsync(string taskId, CancellationToken cancellationToken = default) =>
        InvokeAsync(() => _downloads.PauseDownloadAsync(taskId, cancellationToken), "download_pause_failed");

    public Task<DownloadActionResponse> ResumeDownloadAsync(string taskId, CancellationToken cancellationToken = default) =>
        InvokeAsync(() => _downloads.ResumeDownloadAsync(taskId, cancellationToken), "download_resume_failed");

    public Task<DownloadActionResponse> StopDownloadAsync(string taskId, CancellationToken cancellationToken = default) =>
        InvokeAsync(() => _downloads.StopDownloadAsync(taskId, cancellationToken), "download_stop_failed");

    public Task<LibraryListResponse> GetLibraryAsync(
        string siteKey = "",
        string keyword = "",
        int page = 1,
        int pageSize = 20,
        CancellationToken cancellationToken = default) =>
        InvokeAsync(() => Task.Run(() =>
        {
            // 书库枚举要逐部漫画遍历章节目录并读取元数据,是重量级同步 IO。
            // 必须离开 UI 线程,否则大书库会直接卡住界面。
            // 调用方在 UI 线程 await,续体仍会回到 UI 线程,集合更新的线程性不变。
            cancellationToken.ThrowIfCancellationRequested();
            var entries = _library.EnumerateLibraryEntries()
                .Where(entry => string.IsNullOrWhiteSpace(keyword) ||
                    entry.Title.Contains(keyword.Trim(), StringComparison.OrdinalIgnoreCase) ||
                    entry.Author.Contains(keyword.Trim(), StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(entry => entry.IsFavorite)
                .ThenByDescending(entry => entry.SavedAt)
                .ToList();
            var safePageSize = Math.Clamp(pageSize, 1, 100);
            var totalPages = Math.Max((int)Math.Ceiling(entries.Count / (double)safePageSize), 1);
            var safePage = Math.Clamp(page, 1, totalPages);
            return new LibraryListResponse
            {
                Items = entries.Skip((safePage - 1) * safePageSize).Take(safePageSize).Select(entry => new LibraryItemDto
                {
                    Title = entry.Title,
                    SiteName = entry.SiteName,
                    Author = entry.Author,
                    RootDir = entry.RootDirectory,
                    MangaUrl = entry.MangaUrl,
                    CoverUrl = entry.CoverUrl,
                    DownloadedChapterCount = entry.DownloadedChapterCount,
                    LastDownloadedChapterTitle = entry.LastDownloadedChapterTitle,
                    IsFavorite = entry.IsFavorite,
                    DuplicateDirectoryCount = entry.DuplicateDirectoryCount,
                }).ToList(),
                Total = entries.Count,
                Page = safePage,
                PageSize = safePageSize,
            };
        }, cancellationToken), "library_failed");

    public Task<bool> ToggleFavoriteAsync(string rootDir, CancellationToken cancellationToken = default) =>
        InvokeAsync(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_library.ToggleFavorite(rootDir));
        }, "favorite_toggle_failed");

    public Task<int> DeleteLibraryMangaAsync(string rootDir, CancellationToken cancellationToken = default) =>
        InvokeAsync(() => Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_downloads.HasActiveTaskForManga(rootDir))
            {
                throw new InvalidOperationException("这部漫画仍有未结束的下载任务，请先停止任务再删除。");
            }

            return _library.DeleteManga(rootDir);
        }, cancellationToken), "library_delete_failed");

    public Task<JmLibraryImportPreview> ScanJmLibraryImportAsync(
        string sourceRoot,
        CancellationToken cancellationToken = default) =>
        InvokeAsync(() => Task.Run(
            () => _library.ScanJmImportDirectory(sourceRoot, cancellationToken),
            cancellationToken), "library_import_scan_failed");

    public Task<JmLibraryImportResult> ImportJmLibraryAsync(
        string sourceRoot,
        CancellationToken cancellationToken = default) =>
        InvokeAsync(() => Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_downloads.HasActiveTasks())
                throw new InvalidOperationException("存在未结束的下载任务，请停止或等待任务完成后再导入。");
            return _library.ImportJmDirectory(sourceRoot, cancellationToken);
        }, cancellationToken), "library_import_failed");

    public Task<SettingsResponse> GetSettingsAsync(CancellationToken cancellationToken = default) =>
        InvokeAsync(() => _library.GetSettingsAsync(cancellationToken), "settings_failed");

    public Task<SettingsResponse> UpdateSettingsAsync(SettingsUpdateRequest settings, CancellationToken cancellationToken = default) =>
        InvokeAsync(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(settings.StorageRoot))
                throw new ArgumentException("下载目录不能为空。");
            if (_downloads.HasActiveTasks())
                throw new InvalidOperationException("存在未结束的下载任务，请停止或等待任务完成后再更改目录。");

            var previousRoot = _library.StorageRoot;
            var resolvedRoot = _library.SwitchStorageRoot(settings.StorageRoot);
            if (!LibraryStorageService.SamePath(previousRoot, resolvedRoot))
            {
                _downloads.ReloadHistoryForStorageChange();
            }
            _applicationSettings.UpdateStorageRoot(resolvedRoot);
            return await _library.GetSettingsAsync(cancellationToken);
        }, "settings_update_failed");

    public async Task<LibraryCheckUpdatesResponse> CheckLibraryUpdatesAsync(CancellationToken cancellationToken = default)
    {
        var response = new LibraryCheckUpdatesResponse();
        foreach (var entry in _library.EnumerateLibraryEntries().Where(entry => !string.IsNullOrWhiteSpace(entry.MangaUrl)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var detail = await _jmComic.ResolveAsync(entry.MangaUrl, cancellationToken);
                response.Items.Add(new LibraryUpdateItem
                {
                    Title = entry.Title,
                    LocalChapterCount = entry.DownloadedChapterCount,
                    RemoteChapterCount = detail.Chapters.Count,
                    HasUpdate = detail.Chapters.Count > entry.DownloadedChapterCount,
                });
            }
            catch
            {
                response.Items.Add(new LibraryUpdateItem { Title = entry.Title, LocalChapterCount = entry.DownloadedChapterCount });
            }
        }
        return response;
    }

    public Task<ExportCbzResponse> ExportCbzAsync(string rootDir, CancellationToken cancellationToken = default) =>
        InvokeAsync(() => _export.ExportCbzAsync(rootDir, cancellationToken), "export_failed");

    public Task<ExportCbzProgress> GetExportProgressAsync(string taskId, CancellationToken cancellationToken = default) =>
        InvokeAsync(() => _export.GetExportProgressAsync(taskId, cancellationToken), "export_not_found");

    public Task<bool> CancelExportAsync(string taskId, CancellationToken cancellationToken = default) =>
        InvokeAsync(() => _export.CancelExportAsync(taskId, cancellationToken), "export_cancel_failed");

    public Task<RankingResponse> GetRankingAsync(
        string site = "jmcomic",
        string section = "",
        int page = 1,
        CancellationToken cancellationToken = default) =>
        InvokeAsync(() => _jmComic.GetRankingAsync(section, page, cancellationToken), "ranking_failed");

    public Task<WeeklyPicksIndexResponse> GetWeeklyPicksIndexAsync(
        CancellationToken cancellationToken = default) =>
        InvokeAsync(() => _jmComic.GetWeeklyPicksIndexAsync(cancellationToken), "weekly_picks_index_failed");

    public Task<WeeklyPicksResponse> GetWeeklyPicksAsync(
        string issueId,
        CancellationToken cancellationToken = default) =>
        InvokeAsync(() => _jmComic.GetWeeklyPicksAsync(issueId, cancellationToken), "weekly_picks_failed");

    public Task<WeeklyPicksResponse> GetWeeklyPicksAsync(
        string issueId,
        string typeId,
        CancellationToken cancellationToken = default) =>
        InvokeAsync(() => _jmComic.GetWeeklyPicksAsync(issueId, typeId, cancellationToken), "weekly_picks_failed");

    public Task<RankingSectionsResponse> GetRankingSectionsAsync(string site = "jmcomic", CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new RankingSectionsResponse
        {
            Site = SiteCatalog.Key,
            SiteName = SiteCatalog.DisplayName,
            Sections = new Dictionary<string, string>(_jmComic.GetRankingSections()),
        });
    }

    public Task<ReaderChaptersResponse> GetReaderChaptersAsync(string rootDir, CancellationToken cancellationToken = default) =>
        InvokeAsync(() => _reader.GetReaderChaptersAsync(rootDir, cancellationToken), "reader_failed");

    public Task<ReaderImagesResponse> GetChapterImagesAsync(
        string rootDir,
        string chapterDirName,
        CancellationToken cancellationToken = default) =>
        InvokeAsync(() => _reader.GetChapterImagesAsync(rootDir, chapterDirName, cancellationToken), "reader_failed");

    public Task<byte[]> GetImageBytesAsync(string imagePath, CancellationToken cancellationToken = default) =>
        InvokeAsync(() => _reader.GetImageBytesAsync(imagePath, cancellationToken), "reader_image_failed");

    public Task<IReadOnlyList<JmImageSource>> GetOnlineChapterImageSourcesAsync(
        string chapterId,
        CancellationToken cancellationToken = default) =>
        InvokeAsync(() =>
        {
            if (!int.TryParse(chapterId, out var id) || id <= 0)
                throw new ArgumentException("章节编号无效");
            return _jmComic.GetChapterImageSourcesAsync(id, cancellationToken);
        }, "online_chapter_images_failed");

    public Task<byte[]> GetOnlineImageBytesAsync(
        JmImageSource source,
        CancellationToken cancellationToken = default) =>
        InvokeAsync(() => _jmComic.FetchChapterImageAsync(source, cancellationToken), "online_image_failed");

    public Task<SearchResponse> SearchAsync(
        string query,
        string site = "jmcomic",
        int page = 1,
        CancellationToken cancellationToken = default) =>
        InvokeAsync(() => _jmComic.SearchAsync(query, page, cancellationToken: cancellationToken), "search_failed");

    public Task<MangaCommentsResponse> GetMangaCommentsAsync(
        string mangaUrl,
        int page = 1,
        CancellationToken cancellationToken = default) =>
        InvokeAsync(() => _jmComic.GetAlbumCommentsAsync(mangaUrl, page, cancellationToken), "comments_failed");

    public Task<DownloadHistoryResponse> GetDownloadHistoryAsync(
        int page = 1,
        int pageSize = 20,
        CancellationToken cancellationToken = default) =>
        InvokeAsync(() => _downloads.GetDownloadHistoryAsync(page, pageSize, cancellationToken), "history_failed");

    public Task<BatchActionResponse> BatchStopDownloadsAsync(List<string> taskIds, CancellationToken cancellationToken = default) =>
        InvokeAsync(() => _downloads.BatchStopDownloadsAsync(taskIds, cancellationToken), "batch_stop_failed");

    public Task<BatchActionResponse> BatchDeleteDownloadsAsync(List<string> taskIds, CancellationToken cancellationToken = default) =>
        InvokeAsync(() => _downloads.BatchDeleteDownloadsAsync(taskIds, cancellationToken), "batch_delete_failed");

    public Task<BatchActionResponse> BatchStopChaptersAsync(
        string taskId,
        List<string> chapterIds,
        CancellationToken cancellationToken = default) =>
        InvokeAsync(
            () => _downloads.BatchStopChaptersAsync(taskId, chapterIds, cancellationToken),
            "batch_chapter_stop_failed");

    public Task<BatchActionResponse> BatchDeleteChaptersAsync(
        string taskId,
        List<string> chapterIds,
        CancellationToken cancellationToken = default) =>
        InvokeAsync(
            () => _downloads.BatchDeleteChaptersAsync(taskId, chapterIds, cancellationToken),
            "batch_chapter_delete_failed");

    public async Task<object> ClearDownloadHistoryAsync(CancellationToken cancellationToken = default)
    {
        await InvokeAsync(async () =>
        {
            await _downloads.ClearDownloadHistoryAsync(cancellationToken);
            return true;
        }, "history_clear_failed");
        return new { status = "ok" };
    }

    public Task<int> DeleteDownloadHistoryAsync(
        IReadOnlyCollection<string> historyIds,
        CancellationToken cancellationToken = default) =>
        InvokeAsync(
            () => _downloads.DeleteDownloadHistoryAsync(historyIds, cancellationToken),
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
