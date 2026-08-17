using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Comic.WinUI.Models;

namespace Comic.WinUI.Services.Native;

public sealed class NativeBackendService : IDisposable
{
    private const string TemporaryChapterPrefix = ".下载中_";
    private static readonly string[] ImageExtensions = [".jpg", ".jpeg", ".png", ".webp", ".gif", ".bmp"];
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = SnakeCaseNamingPolicy.Instance,
        WriteIndented = true,
    };

    private readonly JmComicService _jmComic;
    private readonly ConcurrentDictionary<string, NativeDownloadTask> _downloads = [];
    private readonly ConcurrentDictionary<string, ExportTaskState> _exports = [];
    private readonly object _historyLock = new();
    private readonly List<DownloadHistoryItem> _history;
    private readonly ApplicationSettingsService? _applicationSettings;
    private string _stateDirectory;
    private string _historyFile;

    public NativeBackendService(JmComicService jmComic, ApplicationSettingsService applicationSettings)
        : this(jmComic, applicationSettings.StorageRoot)
    {
        _applicationSettings = applicationSettings;
    }

    internal NativeBackendService(JmComicService jmComic, string storageRoot)
    {
        _jmComic = jmComic;
        var requestedStorageRoot = Path.GetFullPath(storageRoot);
        Directory.CreateDirectory(requestedStorageRoot);
        StorageRoot = ResolveExistingDirectoryPath(requestedStorageRoot);
        _stateDirectory = Path.Combine(StorageRoot, ".comic_state");
        Directory.CreateDirectory(_stateDirectory);
        _historyFile = Path.Combine(_stateDirectory, "task_history.json");
        _history = LoadHistory();
    }

    public string StorageRoot { get; private set; }

    public Task<HealthResponse> GetHealthAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new HealthResponse
        {
            Status = "native",
            StorageRoot = StorageRoot,
            Pid = Environment.ProcessId,
        });
    }

    public Task<SettingsResponse> GetSettingsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new SettingsResponse
        {
            StorageRoot = StorageRoot,
            LegacyRoot = string.Empty,
            DownloadRunnerConfigured = true,
            SupportedSites = [SiteCatalog.Key],
        });
    }

    public Task<SettingsResponse> UpdateSettingsAsync(SettingsUpdateRequest settings, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(settings.StorageRoot))
            throw new ArgumentException("下载目录不能为空。");
        if (_downloads.Values.Any(state => !IsTerminal(CloneTask(state).Status)))
            throw new InvalidOperationException("存在未结束的下载任务，请停止或等待任务完成后再更改目录。");

        var requestedRoot = Path.GetFullPath(Environment.ExpandEnvironmentVariables(settings.StorageRoot.Trim()));
        Directory.CreateDirectory(requestedRoot);
        var resolvedRoot = ResolveExistingDirectoryPath(requestedRoot);

        if (!SamePath(StorageRoot, resolvedRoot))
        {
            SaveHistory();
            var nextStateDirectory = Path.Combine(resolvedRoot, ".comic_state");
            Directory.CreateDirectory(nextStateDirectory);
            var nextHistoryFile = Path.Combine(nextStateDirectory, "task_history.json");

            lock (_historyLock)
            {
                StorageRoot = resolvedRoot;
                _stateDirectory = nextStateDirectory;
                _historyFile = nextHistoryFile;
                _history.Clear();
                _history.AddRange(LoadHistory());
            }
        }

        _applicationSettings?.UpdateStorageRoot(StorageRoot);
        return GetSettingsAsync(cancellationToken);
    }

    public Task<SearchResponse> SearchAsync(string query, int page, CancellationToken cancellationToken = default) =>
        _jmComic.SearchAsync(query, page, cancellationToken: cancellationToken);

    public Task<MangaResolveResponse> ResolveMangaAsync(MangaResolveRequest request, CancellationToken cancellationToken = default) =>
        _jmComic.ResolveAsync(request.Url, cancellationToken);

    public Task<RankingResponse> GetRankingAsync(string section, int page, CancellationToken cancellationToken = default) =>
        _jmComic.GetRankingAsync(section, page, cancellationToken);

    public Task<RankingSectionsResponse> GetRankingSectionsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new RankingSectionsResponse
        {
            Site = SiteCatalog.Key,
            SiteName = SiteCatalog.DisplayName,
            Sections = new Dictionary<string, string>(_jmComic.GetRankingSections()),
        });
    }

    public Task<DownloadTaskDto> CreateDownloadAsync(DownloadCreateRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var id = Guid.NewGuid().ToString("N")[..8];
        var state = new NativeDownloadTask(
            new DownloadCreateRequest
            {
                Url = request.Url,
                SiteKey = SiteCatalog.Key,
                Source = request.Source,
                Chapters = request.Chapters?.ToList(),
            },
            new DownloadTaskDto
            {
                Id = id,
                Url = request.Url,
                SiteKey = SiteCatalog.Key,
                Status = "pending",
                StatusText = "等待开始",
            });
        _downloads[id] = state;
        state.Worker = Task.Run(() => ProcessDownloadAsync(state), CancellationToken.None);
        return Task.FromResult(CloneTask(state));
    }

    public Task<DownloadListResponse> GetDownloadsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new DownloadListResponse
        {
            Items = _downloads.Values.Select(CloneTask).OrderByDescending(task => task.Id, StringComparer.Ordinal).ToList(),
        });
    }

    public Task<DownloadTaskDto> GetDownloadAsync(string taskId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_downloads.TryGetValue(taskId, out var state)) throw new KeyNotFoundException("任务不存在");
        return Task.FromResult(CloneTask(state));
    }

    public Task<DownloadActionResponse> PauseDownloadAsync(string taskId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var state = RequireTask(taskId);
        lock (state.Gate)
        {
            if (state.Dto.Status == "running")
            {
                state.PauseRequested = true;
                state.Dto.Status = "pausing";
                state.Dto.StatusText = "正在暂停，等待当前章节收尾";
            }
        }
        return Task.FromResult(new DownloadActionResponse { Status = CloneTask(state).Status });
    }

    public Task<DownloadActionResponse> ResumeDownloadAsync(string taskId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var state = RequireTask(taskId);
        lock (state.Gate)
        {
            if (state.Dto.Status is "paused" or "pausing")
            {
                state.PauseRequested = false;
                state.Dto.Status = "running";
                state.Dto.StatusText = "继续下载中";
                ResetDownloadSpeedLocked(state);
            }
        }
        return Task.FromResult(new DownloadActionResponse { Status = CloneTask(state).Status });
    }

    public Task<DownloadActionResponse> StopDownloadAsync(string taskId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var state = RequireTask(taskId);
        lock (state.Gate)
        {
            if (state.Dto.Status is "running" or "paused" or "pausing" or "pending")
            {
                state.PauseRequested = false;
                state.Dto.Status = "stopping";
                state.Dto.StatusText = "正在停止";
                state.StopSource.Cancel();
            }
        }
        return Task.FromResult(new DownloadActionResponse { Status = CloneTask(state).Status });
    }

    public async Task<BatchActionResponse> BatchStopDownloadsAsync(IReadOnlyCollection<string> taskIds, CancellationToken cancellationToken = default)
    {
        var result = new BatchActionResponse();
        foreach (var id in taskIds)
        {
            try
            {
                var response = await StopDownloadAsync(id, cancellationToken);
                if (response.Status == "stopping") result.Stopped.Add(id); else result.Failed.Add(id);
            }
            catch { result.Failed.Add(id); }
        }
        return result;
    }

    public Task<BatchActionResponse> BatchDeleteDownloadsAsync(IReadOnlyCollection<string> taskIds, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = new BatchActionResponse();
        foreach (var id in taskIds)
        {
            if (_downloads.TryGetValue(id, out var state) && IsTerminal(CloneTask(state).Status) && _downloads.TryRemove(id, out _))
            {
                state.Dispose();
                result.Deleted.Add(id);
            }
            else result.Failed.Add(id);
        }
        return Task.FromResult(result);
    }

    public Task<BatchActionResponse> BatchStopChaptersAsync(
        string taskId,
        IReadOnlyCollection<string> chapterIds,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var state = RequireTask(taskId);
        var requested = chapterIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.Ordinal);
        var result = new BatchActionResponse();
        CancellationTokenSource? activeChapterSource = null;

        lock (state.Gate)
        {
            foreach (var chapter in state.Dto.Chapters.Where(chapter => requested.Contains(chapter.Id)))
            {
                if (chapter.Status is not ("pending" or "running"))
                {
                    result.Failed.Add(chapter.Id);
                    continue;
                }

                state.StoppedChapterIds.Add(chapter.Id);
                chapter.Status = "stopped";
                chapter.Error = string.Empty;
                result.Stopped.Add(chapter.Id);
            }

            if (state.CurrentChapterId is { Length: > 0 } currentId && requested.Contains(currentId))
            {
                activeChapterSource = state.CurrentChapterStopSource;
            }
            RecalculateTaskProgressLocked(state);
        }

        activeChapterSource?.Cancel();
        if (result.Stopped.Count > 0)
        {
            AppendLog(state, "info", $"已请求停止 {result.Stopped.Count} 个章节");
        }
        return Task.FromResult(result);
    }

    public async Task<BatchActionResponse> BatchDeleteChaptersAsync(
        string taskId,
        IReadOnlyCollection<string> chapterIds,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var state = RequireTask(taskId);
        var requested = chapterIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.Ordinal);
        var candidates = new List<(string Id, JmChapter Chapter, string DirectoryName)>();
        CancellationTokenSource? activeChapterSource = null;

        lock (state.Gate)
        {
            foreach (var progress in state.Dto.Chapters.Where(chapter => requested.Contains(chapter.Id)).ToList())
            {
                var chapter = state.Manga?.Chapters.FirstOrDefault(item => item.Id == progress.Id);
                if (chapter is null)
                {
                    continue;
                }

                state.StoppedChapterIds.Add(progress.Id);
                state.DeletedChapterIds.Add(progress.Id);
                candidates.Add((progress.Id, chapter, progress.DirectoryName));
            }

            if (state.CurrentChapterId is { Length: > 0 } currentId && requested.Contains(currentId))
            {
                activeChapterSource = state.CurrentChapterStopSource;
            }
        }

        activeChapterSource?.Cancel();
        var result = new BatchActionResponse();
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!await WaitForChapterReleaseAsync(state, candidate.Id, cancellationToken))
            {
                result.Failed.Add(candidate.Id);
                continue;
            }

            try
            {
                DeleteChapterDirectories(state.RootDirectory, candidate.Chapter, candidate.DirectoryName);
                lock (state.Gate)
                {
                    state.Dto.Chapters.RemoveAll(chapter => chapter.Id == candidate.Id);
                    state.TotalChapterCount = state.Dto.Chapters.Count;
                    RecalculateTaskProgressLocked(state);
                }
                result.Deleted.Add(candidate.Id);
            }
            catch
            {
                result.Failed.Add(candidate.Id);
            }
        }

        foreach (var missingId in requested.Except(candidates.Select(candidate => candidate.Id)))
        {
            result.Failed.Add(missingId);
        }

        if (result.Deleted.Count > 0)
        {
            AppendLog(state, "info", $"已删除 {result.Deleted.Count} 个章节及其本地文件");
            if (state.Manga is not null)
            {
                SaveLibraryMetadata(state, false, []);
            }
        }
        return result;
    }

    public Task<DownloadHistoryResponse> GetDownloadHistoryAsync(int page, int pageSize, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_historyLock)
        {
            var safePage = Math.Max(page, 1);
            var safePageSize = Math.Clamp(pageSize, 1, 100);
            return Task.FromResult(new DownloadHistoryResponse
            {
                Items = _history.AsEnumerable().Reverse().Skip((safePage - 1) * safePageSize).Take(safePageSize).Select(CloneHistory).ToList(),
                Total = _history.Count,
                Page = safePage,
                PageSize = safePageSize,
            });
        }
    }

    public Task ClearDownloadHistoryAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_historyLock) _history.Clear();
        SaveHistory();
        return Task.CompletedTask;
    }

    public Task<int> DeleteDownloadHistoryAsync(
        IReadOnlyCollection<string> historyIds,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var requested = historyIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.Ordinal);
        if (requested.Count == 0)
        {
            return Task.FromResult(0);
        }

        int removed;
        lock (_historyLock)
        {
            removed = _history.RemoveAll(item => requested.Contains(item.Id));
        }
        if (removed > 0)
        {
            SaveHistory();
        }
        return Task.FromResult(removed);
    }

    public Task<LibraryListResponse> GetLibraryAsync(string keyword, int page, int pageSize, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var entries = EnumerateLibraryEntries()
            .Where(entry => string.IsNullOrWhiteSpace(keyword) ||
                entry.Title.Contains(keyword.Trim(), StringComparison.OrdinalIgnoreCase) ||
                entry.Author.Contains(keyword.Trim(), StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(entry => entry.SavedAt)
            .ToList();
        var safePageSize = Math.Clamp(pageSize, 1, 100);
        var totalPages = Math.Max((int)Math.Ceiling(entries.Count / (double)safePageSize), 1);
        var safePage = Math.Clamp(page, 1, totalPages);
        return Task.FromResult(new LibraryListResponse
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
            }).ToList(),
            Total = entries.Count,
            Page = safePage,
            PageSize = safePageSize,
        });
    }

    public async Task<LibraryCheckUpdatesResponse> CheckLibraryUpdatesAsync(CancellationToken cancellationToken = default)
    {
        var response = new LibraryCheckUpdatesResponse();
        foreach (var entry in EnumerateLibraryEntries().Where(entry => !string.IsNullOrWhiteSpace(entry.MangaUrl)))
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

    public Task<ExportCbzResponse> ExportCbzAsync(string rootDir, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var resolvedRoot = ResolveLibraryRoot(rootDir);
        var metadata = LoadLibraryMetadata(resolvedRoot);
        var taskId = Guid.NewGuid().ToString("N")[..8];
        var state = new ExportTaskState
        {
            Progress = new ExportCbzProgress
            {
                Id = taskId,
                Status = "running",
                MangaTitle = metadata?.MangaTitle is { Length: > 0 } metadataTitle ? metadataTitle : Path.GetFileName(resolvedRoot),
            },
        };
        _exports[taskId] = state;
        state.Worker = Task.Run(() => ProcessExportAsync(state, resolvedRoot, metadata?.MangaUrl ?? string.Empty), CancellationToken.None);
        return Task.FromResult(new ExportCbzResponse { Status = "ok", TaskId = taskId, Message = "导出任务已创建" });
    }

    public Task<ExportCbzProgress> GetExportProgressAsync(string taskId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_exports.TryGetValue(taskId, out var state)) throw new KeyNotFoundException("导出任务不存在");
        lock (state.Gate) return Task.FromResult(CloneExportProgress(state.Progress));
    }

    public Task<ReaderChaptersResponse> GetReaderChaptersAsync(string rootDir, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var resolvedRoot = ResolveLibraryRoot(rootDir);
        var metadata = LoadLibraryMetadata(resolvedRoot);
        var chapters = OrderChapterDirectories(EnumerateChapterDirectories(resolvedRoot)).Select(item => new ReaderChapterDto
        {
            DirName = item.Name,
            Title = ChapterTitle(item.Name),
            Order = ChapterOrder(item.Name),
            ImageCount = EnumerateImages(item.FullName).Count,
        }).ToList();
        return Task.FromResult(new ReaderChaptersResponse
        {
            MangaTitle = metadata?.MangaTitle is { Length: > 0 } metadataTitle ? metadataTitle : Path.GetFileName(resolvedRoot),
            Chapters = chapters,
        });
    }

    public Task<ReaderImagesResponse> GetChapterImagesAsync(string rootDir, string chapterDirectoryName, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var chapterDirectory = ResolveChapterDirectory(rootDir, chapterDirectoryName);
        return Task.FromResult(new ReaderImagesResponse { Images = EnumerateImages(chapterDirectory) });
    }

    public Task<byte[]> GetImageBytesAsync(string imagePath, CancellationToken cancellationToken = default) =>
        File.ReadAllBytesAsync(ResolveReaderImage(imagePath), cancellationToken);

    public void Dispose()
    {
        foreach (var state in _downloads.Values) state.Dispose();
    }

    public static bool IsTerminal(string status) => status is "completed" or "failed" or "partial" or "stopped";

    private async Task ProcessDownloadAsync(NativeDownloadTask state)
    {
        var token = state.StopSource.Token;
        var failures = new List<FailedChapterRecord>();
        try
        {
            AppendLog(state, "info", "正在解析漫画链接...");
            var manga = await _jmComic.GetMangaInfoFromUrlAsync(state.Request.Url, token);
            AppendLog(state, "info", $"漫画: {manga.Title}，共 {manga.Chapters.Count} 章");
            lock (state.Gate)
            {
                state.Manga = manga;
                state.Dto.MangaTitle = manga.Title;
                state.Dto.SiteKey = SiteCatalog.Key;
            }
            var rootDirectory = Path.Combine(StorageRoot, JmComicService.SanitizeFileName(manga.Title));
            Directory.CreateDirectory(rootDirectory);
            var selected = SelectChapters(manga, state.Request.Chapters);
            if (selected.Count == 0) throw new InvalidOperationException("没有匹配到可下载章节");

            lock (state.Gate)
            {
                state.RootDirectory = rootDirectory;
                state.TotalChapterCount = selected.Count;
                state.Dto.Status = "running";
                state.Dto.StatusText = $"准备下载 0/{selected.Count} 章";
                state.Dto.Chapters = selected.Select(chapter => new DownloadChapterProgressDto
                {
                    Id = chapter.Id,
                    Title = chapter.Title,
                    Status = "pending",
                }).ToList();
            }
            AppendLog(state, "info", $"保存目录: {rootDirectory}");
            AppendLog(state, "info", "图片并发: 3");

            var completed = 0;
            for (var chapterIndex = 0; chapterIndex < selected.Count; chapterIndex++)
            {
                var chapter = selected[chapterIndex];
                var chapterNumber = chapterIndex + 1;
                token.ThrowIfCancellationRequested();
                await WaitWhilePausedAsync(state, completed, selected.Count, token);
                if (IsChapterStopRequested(state, chapter.Id))
                {
                    AppendLog(state, "info", $"跳过章节: {chapter.Title}");
                    continue;
                }

                using var chapterStopSource = CancellationTokenSource.CreateLinkedTokenSource(token);
                lock (state.Gate)
                {
                    state.CurrentChapterId = chapter.Id;
                    state.CurrentChapterStopSource = chapterStopSource;
                }
                try
                {
                    ResetDownloadSpeed(state);
                    UpdateChapterProgress(state, chapter.Id, "running", 0, 0, string.Empty);
                    SetStatus(state, "running", $"第 {chapterNumber}/{selected.Count} 章 · {chapter.Title} · 准备中");
                    AppendLog(state, "info", $"开始章节: {chapter.Title}");

                    Exception? lastError = null;
                    JmChapterDownloadResult? result = null;
                    for (var attempt = 0; attempt < 3; attempt++)
                    {
                        try
                        {
                            result = await _jmComic.DownloadChapterAsync(
                                chapter,
                                rootDirectory,
                                3,
                                chapterStopSource.Token,
                                imageProgress => ReportImageProgress(
                                    state,
                                    chapter,
                                    completed,
                                    selected.Count,
                                    chapterNumber,
                                    imageProgress));
                            break;
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex)
                        {
                            lastError = ex;
                            if (attempt < 2)
                            {
                                AppendLog(state, "warn", $"章节失败，准备重试 ({attempt + 1}/2)：{chapter.Title} - {ex.Message}");
                                await Task.Delay(
                                    TimeSpan.FromSeconds(Math.Min(1.5 * (attempt + 1), 5)),
                                    chapterStopSource.Token);
                            }
                        }
                    }

                    if (result is null)
                    {
                        var failureReason = lastError?.Message ?? "章节下载失败";
                        failures.Add(new FailedChapterRecord
                        {
                            Order = chapter.Order,
                            Slug = chapter.Id,
                            Title = chapter.Title,
                            Reason = failureReason,
                        });
                        MarkChapterFailed(state, chapter.Id, failureReason);
                        AppendLog(state, "error", $"章节失败: {chapter.Title} - {failures[^1].Reason}");
                        continue;
                    }

                    if (IsChapterStopRequested(state, chapter.Id))
                    {
                        UpdateChapterProgress(state, chapter.Id, "stopped", 0, 0, string.Empty);
                        continue;
                    }

                    completed++;
                    lock (state.Gate)
                    {
                        var chapterProgress = state.Dto.Chapters.FirstOrDefault(item => item.Id == chapter.Id);
                        if (chapterProgress is not null)
                        {
                            chapterProgress.Status = "completed";
                            chapterProgress.CompletedImages = result.ImageCount;
                            chapterProgress.TotalImages = result.ImageCount;
                            chapterProgress.Progress = 100;
                            chapterProgress.Error = string.Empty;
                            chapterProgress.DirectoryName = result.DirectoryName;
                        }
                        state.CompletedChapterCount = completed;
                        state.Dto.Progress = completed / (double)selected.Count * 100;
                        state.Dto.Status = "running";
                        state.Dto.StatusText = $"已完成 {completed}/{selected.Count} 章";
                    }
                    AppendLog(state, "progress", $"完成章节: {chapter.Title} ({result.ImageCount} 张图片)");
                    SaveLibraryMetadata(state, false, failures);
                }
                catch (OperationCanceledException) when (
                    !token.IsCancellationRequested &&
                    IsChapterStopRequested(state, chapter.Id))
                {
                    if (!IsChapterDeleteRequested(state, chapter.Id))
                    {
                        UpdateChapterProgress(state, chapter.Id, "stopped", 0, 0, string.Empty);
                        AppendLog(state, "info", $"已停止章节: {chapter.Title}");
                    }
                }
                finally
                {
                    lock (state.Gate)
                    {
                        if (string.Equals(state.CurrentChapterId, chapter.Id, StringComparison.Ordinal))
                        {
                            state.CurrentChapterId = string.Empty;
                            state.CurrentChapterStopSource = null;
                        }
                    }
                }
            }

            int completedCount;
            int stoppedCount;
            int totalCount;
            lock (state.Gate)
            {
                completedCount = state.Dto.Chapters.Count(chapter => chapter.Status == "completed");
                stoppedCount = state.Dto.Chapters.Count(chapter => chapter.Status == "stopped");
                totalCount = state.Dto.Chapters.Count;
                RecalculateTaskProgressLocked(state);
            }

            if (totalCount == 0)
            {
                lock (state.Gate)
                {
                    state.Dto.Status = "stopped";
                    state.Dto.StatusText = "所有章节均已删除";
                    state.Dto.TaskError = null;
                }
                SaveLibraryMetadata(state, false, failures);
            }
            else if (failures.Count > 0 || stoppedCount > 0)
            {
                var status = completedCount > 0 ? "partial" : stoppedCount == totalCount ? "stopped" : "failed";
                var summary = $"完成 {completedCount}/{totalCount} 章";
                lock (state.Gate)
                {
                    state.Dto.Status = status;
                    state.Dto.StatusText = stoppedCount > 0
                        ? $"{summary}，已停止 {stoppedCount} 章"
                        : summary;
                    state.Dto.TaskError = failures.Count > 0
                        ? new ApiError { Code = "download_failed", Message = $"{summary}，失败章节 {failures.Count} 个" }
                        : null;
                }
                SaveLibraryMetadata(state, false, failures);
            }
            else
            {
                lock (state.Gate)
                {
                    state.Dto.Status = "completed";
                    state.Dto.StatusText = $"下载完成 {completedCount}/{totalCount} 章";
                    state.Dto.Progress = 100;
                    state.Dto.TaskError = null;
                }
                AppendLog(state, "info", "下载完成");
                SaveLibraryMetadata(state, true, failures);
            }
        }
        catch (OperationCanceledException)
        {
            lock (state.Gate)
            {
                foreach (var chapter in state.Dto.Chapters.Where(item => item.Status == "running"))
                {
                    chapter.Status = "stopped";
                }
                state.Dto.Status = "stopped";
                state.Dto.StatusText = $"已停止，完成 {state.CompletedChapterCount}/{state.TotalChapterCount} 章";
                state.Dto.TaskError = null;
            }
            AppendLog(state, "info", "下载已停止");
            if (state.Manga is not null) SaveLibraryMetadata(state, false, failures);
        }
        catch (Exception ex)
        {
            lock (state.Gate)
            {
                foreach (var chapter in state.Dto.Chapters.Where(item => item.Status == "running"))
                {
                    chapter.Status = "failed";
                    chapter.Error = ex.Message;
                }
                state.Dto.Status = "failed";
                state.Dto.StatusText = "下载失败";
                state.Dto.TaskError = new ApiError { Code = "download_failed", Message = ex.Message };
            }
            AppendLog(state, "error", ex.Message);
        }
        finally { RecordHistory(state); }
    }

    private static void ReportImageProgress(
        NativeDownloadTask state,
        JmChapter chapter,
        int completedChapters,
        int totalChapters,
        int chapterNumber,
        JmImageProgress imageProgress)
    {
        lock (state.Gate)
        {
            var progress = state.Dto.Chapters.FirstOrDefault(item => item.Id == chapter.Id);
            if (progress is null) return;

            UpdateDownloadSpeedLocked(state, imageProgress);
            progress.Status = "running";
            progress.CompletedImages = imageProgress.CompletedImages;
            progress.TotalImages = imageProgress.TotalImages;
            progress.Progress = imageProgress.TotalImages > 0
                ? imageProgress.CompletedImages / (double)imageProgress.TotalImages * 100
                : 0;
            progress.Error = string.Empty;

            var chapterFraction = imageProgress.TotalImages > 0
                ? imageProgress.CompletedImages / (double)imageProgress.TotalImages
                : 0;
            state.Dto.Progress = totalChapters > 0
                ? (completedChapters + chapterFraction) / totalChapters * 100
                : 0;
            state.Dto.Status = "running";
            state.Dto.StatusText = imageProgress.TotalImages > 0
                ? $"第 {chapterNumber}/{totalChapters} 章 · {chapter.Title} · {imageProgress.CompletedImages}/{imageProgress.TotalImages} 页"
                : $"第 {chapterNumber}/{totalChapters} 章 · {chapter.Title} · 准备中";
        }
    }

    private static void ResetDownloadSpeed(NativeDownloadTask state)
    {
        lock (state.Gate)
        {
            ResetDownloadSpeedLocked(state);
        }
    }

    private static void ResetDownloadSpeedLocked(NativeDownloadTask state)
    {
        state.Dto.DownloadSpeedBytesPerSecond = 0;
        state.SpeedLastReportedBytes = 0;
        state.SpeedWindowBytes = 0;
        state.SpeedWindowStartedTimestamp = Stopwatch.GetTimestamp();
        state.SpeedLastCompletedImages = 0;
        state.SpeedLastActivityAt = null;
    }

    private static void UpdateDownloadSpeedLocked(NativeDownloadTask state, JmImageProgress imageProgress)
    {
        var nowTimestamp = Stopwatch.GetTimestamp();
        if (state.SpeedWindowStartedTimestamp == 0 ||
            imageProgress.DownloadedBytes < state.SpeedLastReportedBytes)
        {
            ResetDownloadSpeedLocked(state);
            nowTimestamp = state.SpeedWindowStartedTimestamp;
        }

        if (imageProgress.DownloadedBytes == 0 && state.SpeedLastReportedBytes == 0)
        {
            state.SpeedWindowStartedTimestamp = nowTimestamp;
            state.SpeedLastCompletedImages = imageProgress.CompletedImages;
            return;
        }

        var delta = Math.Max(imageProgress.DownloadedBytes - state.SpeedLastReportedBytes, 0);
        var imageCompleted = imageProgress.CompletedImages > state.SpeedLastCompletedImages;
        state.SpeedLastReportedBytes = imageProgress.DownloadedBytes;
        state.SpeedLastCompletedImages = imageProgress.CompletedImages;
        if (delta > 0)
        {
            state.SpeedWindowBytes += delta;
            state.SpeedLastActivityAt = DateTimeOffset.UtcNow;
        }

        var elapsed = Stopwatch.GetElapsedTime(state.SpeedWindowStartedTimestamp, nowTimestamp);
        if (state.SpeedWindowBytes == 0 ||
            (!imageCompleted && elapsed < TimeSpan.FromMilliseconds(150) && state.SpeedWindowBytes < 256 * 1024))
        {
            return;
        }

        var currentSpeed = state.SpeedWindowBytes / Math.Max(elapsed.TotalSeconds, 0.001);
        state.Dto.DownloadSpeedBytesPerSecond = state.Dto.DownloadSpeedBytesPerSecond <= 0
            ? currentSpeed
            : state.Dto.DownloadSpeedBytesPerSecond * 0.65 + currentSpeed * 0.35;
        state.SpeedWindowBytes = 0;
        state.SpeedWindowStartedTimestamp = nowTimestamp;
    }

    private static void UpdateChapterProgress(
        NativeDownloadTask state,
        string chapterId,
        string status,
        int completedImages,
        int totalImages,
        string error)
    {
        lock (state.Gate)
        {
            var progress = state.Dto.Chapters.FirstOrDefault(item => item.Id == chapterId);
            if (progress is null) return;
            progress.Status = status;
            progress.CompletedImages = completedImages;
            progress.TotalImages = totalImages;
            progress.Progress = totalImages > 0 ? completedImages / (double)totalImages * 100 : 0;
            progress.Error = error;
        }
    }

    private static void MarkChapterFailed(NativeDownloadTask state, string chapterId, string error)
    {
        lock (state.Gate)
        {
            var progress = state.Dto.Chapters.FirstOrDefault(item => item.Id == chapterId);
            if (progress is null) return;
            progress.Status = "failed";
            progress.Error = error;
        }
    }

    private static bool IsChapterStopRequested(NativeDownloadTask state, string chapterId)
    {
        lock (state.Gate)
        {
            return state.StoppedChapterIds.Contains(chapterId);
        }
    }

    private static bool IsChapterDeleteRequested(NativeDownloadTask state, string chapterId)
    {
        lock (state.Gate)
        {
            return state.DeletedChapterIds.Contains(chapterId);
        }
    }

    private static void RecalculateTaskProgressLocked(NativeDownloadTask state)
    {
        state.CompletedChapterCount = state.Dto.Chapters.Count(chapter => chapter.Status == "completed");
        state.TotalChapterCount = state.Dto.Chapters.Count;
        state.Dto.Progress = state.Dto.Chapters.Count == 0
            ? 0
            : state.Dto.Chapters.Sum(chapter => Math.Clamp(chapter.Progress, 0, 100)) / state.Dto.Chapters.Count;
    }

    private static async Task<bool> WaitForChapterReleaseAsync(
        NativeDownloadTask state,
        string chapterId,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        while (true)
        {
            lock (state.Gate)
            {
                if (!string.Equals(state.CurrentChapterId, chapterId, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            if (Stopwatch.GetElapsedTime(started) >= TimeSpan.FromSeconds(10))
            {
                return false;
            }
            await Task.Delay(50, cancellationToken);
        }
    }

    internal static void DeleteChapterDirectories(
        string rootDirectory,
        JmChapter chapter,
        string directoryName)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory) || !Directory.Exists(rootDirectory))
        {
            return;
        }

        var root = Path.GetFullPath(rootDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var orderPrefix = $"{chapter.Order:000}_";
        var candidates = Directory.EnumerateDirectories(root)
            .Where(path =>
            {
                var name = Path.GetFileName(path);
                return (!string.IsNullOrWhiteSpace(directoryName) &&
                        (string.Equals(name, directoryName, StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(name, TemporaryChapterPrefix + directoryName, StringComparison.OrdinalIgnoreCase))) ||
                       name.StartsWith(orderPrefix, StringComparison.OrdinalIgnoreCase) ||
                       name.StartsWith(TemporaryChapterPrefix + orderPrefix, StringComparison.OrdinalIgnoreCase);
            })
            .ToList();

        foreach (var candidate in candidates)
        {
            var fullPath = Path.GetFullPath(candidate);
            var parent = Directory.GetParent(fullPath)?.FullName;
            if (!string.Equals(
                    Path.GetFullPath(parent ?? string.Empty).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    root,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var attributes = File.GetAttributes(fullPath);
            Directory.Delete(fullPath, (attributes & FileAttributes.ReparsePoint) == 0);
        }
    }

    private async Task WaitWhilePausedAsync(NativeDownloadTask state, int completed, int total, CancellationToken cancellationToken)
    {
        var logged = false;
        while (true)
        {
            bool paused;
            lock (state.Gate)
            {
                paused = state.PauseRequested;
                if (paused)
                {
                    state.Dto.Status = "paused";
                    state.Dto.StatusText = $"已暂停，完成 {completed}/{total} 章";
                }
            }
            if (!paused) break;
            if (!logged)
            {
                AppendLog(state, "info", "下载已暂停");
                logged = true;
            }
            await Task.Delay(200, cancellationToken);
        }
        if (logged) AppendLog(state, "info", "下载已继续");
    }

    internal static List<JmChapter> SelectChapters(JmMangaInfo manga, IReadOnlyCollection<string>? requested)
    {
        if (requested is { Count: > 0 })
        {
            var values = requested.Select(value => value.Trim())
                .Where(value => value.Length > 0)
                .ToHashSet(StringComparer.Ordinal);
            var ids = values.Select(value => JmComicService.ParseMangaId(value).MangaId ?? value)
                .ToHashSet(StringComparer.Ordinal);
            return manga.Chapters.Where(chapter =>
                ids.Contains(chapter.Id) ||
                values.Contains(chapter.Title) ||
                values.Contains($"https://18comic.vip/photo/{chapter.Id}")).ToList();
        }
        if (!string.IsNullOrWhiteSpace(manga.StartChapterId))
        {
            var index = manga.Chapters.FindIndex(chapter => chapter.Id == manga.StartChapterId);
            if (index >= 0) return manga.Chapters.Skip(index).ToList();
        }
        return manga.Chapters.ToList();
    }

    private void SaveLibraryMetadata(NativeDownloadTask state, bool completed, IReadOnlyCollection<FailedChapterRecord> failures)
    {
        if (state.Manga is null || string.IsNullOrWhiteSpace(state.RootDirectory)) return;
        var downloaded = EnumerateChapterDirectories(state.RootDirectory).Select(directory => new DownloadedChapterRecord
        {
            Order = ChapterOrder(directory.Name),
            DirName = directory.Name,
            Title = ChapterTitle(directory.Name),
            ImageCount = EnumerateImages(directory.FullName).Count,
        }).OrderBy(record => record.Order).ToList();
        var now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        var existing = LoadLibraryMetadata(state.RootDirectory);
        var metadata = new LibraryMetadata
        {
            SchemaVersion = 1,
            SiteKey = SiteCatalog.Key,
            SiteName = SiteCatalog.DisplayName,
            MangaTitle = state.Manga.Title,
            Authors = state.Manga.Authors
                .Where(author => !string.IsNullOrWhiteSpace(author))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList(),
            MangaUrl = state.Request.Url,
            RootDirectory = state.RootDirectory,
            CoverUrl = state.Manga.CoverUrl,
            TotalChapters = state.Manga.Chapters.Count,
            DownloadedChapterCount = downloaded.Count,
            LastDownloadedChapterTitle = downloaded.LastOrDefault()?.Title ?? string.Empty,
            LastDownloadedChapterOrder = downloaded.LastOrDefault()?.Order,
            DownloadedChapters = downloaded,
            Completed = completed && failures.Count == 0,
            CreatedAt = existing?.CreatedAt is { Length: > 0 } created ? created : now,
            SavedAt = now,
            LastFailedChapterRecords = failures.ToList(),
            LastFailedChapterCount = failures.Count,
        };
        WriteJsonAtomically(Path.Combine(state.RootDirectory, "元数据.json"), metadata);
    }

    private List<LibraryEntry> EnumerateLibraryEntries()
    {
        var entries = new List<LibraryEntry>();
        foreach (var directory in new DirectoryInfo(StorageRoot).EnumerateDirectories())
        {
            if (directory.Name.StartsWith('.') || directory.Name.EndsWith("_CBZ", StringComparison.OrdinalIgnoreCase)) continue;
            var chapters = EnumerateChapterDirectories(directory.FullName);
            if (chapters.Count == 0) continue;
            var metadata = LoadLibraryMetadata(directory.FullName);
            var ordered = OrderChapterDirectories(chapters).ToList();
            var fallbackCover = EnumerateImages(directory.FullName).FirstOrDefault()
                ?? EnumerateImages(ordered[0].FullName).FirstOrDefault()
                ?? string.Empty;
            entries.Add(new LibraryEntry(
                metadata?.MangaTitle is { Length: > 0 } metadataTitle ? metadataTitle : directory.Name,
                metadata is null ? string.Empty : string.Join("、", metadata.Authors
                    .Where(author => !string.IsNullOrWhiteSpace(author))
                    .Distinct(StringComparer.OrdinalIgnoreCase)),
                metadata?.SiteName is { Length: > 0 } siteName ? siteName : "本地漫画",
                directory.FullName,
                metadata?.MangaUrl ?? string.Empty,
                metadata?.CoverUrl is { Length: > 0 } coverUrl ? coverUrl : fallbackCover,
                ordered.Count,
                metadata?.LastDownloadedChapterTitle is { Length: > 0 } lastTitle ? lastTitle : ChapterTitle(ordered[^1].Name),
                ParseDate(metadata?.SavedAt) ?? directory.LastWriteTime));
        }
        return entries;
    }

    private async Task ProcessExportAsync(ExportTaskState state, string rootDirectory, string mangaUrl)
    {
        try
        {
            var chapters = OrderChapterDirectories(EnumerateChapterDirectories(rootDirectory)).ToList();
            if (chapters.Count == 0) throw new InvalidOperationException("当前漫画目录里没有可导出的已完成章节");
            var exportDirectory = Path.Combine(Directory.GetParent(rootDirectory)!.FullName, Path.GetFileName(rootDirectory) + "_CBZ");
            Directory.CreateDirectory(exportDirectory);
            var exported = 0;
            var skipped = new List<string>();

            lock (state.Gate)
            {
                state.Progress.TotalChapters = chapters.Count;
                state.Progress.ExportDir = exportDirectory;
                state.Progress.CurrentChapter = ChapterTitle(chapters[0].Name);
            }

            for (var index = 0; index < chapters.Count; index++)
            {
                var chapter = chapters[index];
                var images = EnumerateImages(chapter.FullName);
                lock (state.Gate) state.Progress.CurrentChapter = ChapterTitle(chapter.Name);
                if (images.Count == 0) skipped.Add(chapter.Name);
                else
                {
                    await CreateCbzAsync(
                        Path.Combine(exportDirectory, chapter.Name + ".cbz"),
                        images,
                        state.Progress.MangaTitle,
                        ChapterTitle(chapter.Name),
                        index + 1,
                        chapters.Count,
                        mangaUrl);
                    exported++;
                }
                lock (state.Gate)
                {
                    state.Progress.CurrentChapter = ChapterTitle(chapter.Name);
                    state.Progress.CurrentIndex = index + 1;
                    state.Progress.TotalChapters = chapters.Count;
                    state.Progress.ExportedCount = exported;
                    state.Progress.ExportDir = exportDirectory;
                    state.Progress.SkippedChapters = skipped.ToList();
                }
            }
            if (exported == 0) throw new InvalidOperationException("没有找到可写入 CBZ 的图片文件");
            lock (state.Gate) state.Progress.Status = "completed";
        }
        catch (Exception ex)
        {
            lock (state.Gate)
            {
                state.Progress.Status = "failed";
                state.Progress.Error = ex.Message;
            }
        }
    }

    private static async Task CreateCbzAsync(
        string archivePath,
        IReadOnlyCollection<string> images,
        string mangaTitle,
        string chapterTitle,
        int chapterNumber,
        int chapterCount,
        string mangaUrl)
    {
        var temporaryPath = archivePath + ".tmp";
        if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        try
        {
            await using (var output = File.Create(temporaryPath))
            using (var archive = new ZipArchive(output, ZipArchiveMode.Create, false, Encoding.UTF8))
            {
                foreach (var imagePath in images)
                {
                    var entry = archive.CreateEntry(Path.GetFileName(imagePath), CompressionLevel.Optimal);
                    await using var target = entry.Open();
                    await using var source = File.OpenRead(imagePath);
                    await source.CopyToAsync(target);
                }
                var comicInfo = new XDocument(
                    new XDeclaration("1.0", "utf-8", null),
                    new XElement("ComicInfo",
                        new XElement("Series", mangaTitle),
                        new XElement("Title", chapterTitle),
                        new XElement("Number", chapterNumber),
                        new XElement("Count", chapterCount),
                        new XElement("PageCount", images.Count),
                        new XElement("Manga", "YesAndRightToLeft"),
                        string.IsNullOrWhiteSpace(mangaUrl) ? null : new XElement("Web", mangaUrl)));
                var infoEntry = archive.CreateEntry("ComicInfo.xml", CompressionLevel.Optimal);
                await using var infoStream = infoEntry.Open();
                await using var writer = new StreamWriter(infoStream, new UTF8Encoding(false));
                await writer.WriteAsync(comicInfo.ToString(SaveOptions.DisableFormatting));
            }

            File.Move(temporaryPath, archivePath, true);
        }
        catch
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            throw;
        }
    }

    private string ResolveLibraryRoot(string rootDirectory)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory)) throw new ArgumentException("漫画目录不存在");
        if (!Directory.Exists(rootDirectory)) throw new DirectoryNotFoundException("漫画目录不存在");
        var candidate = ResolveExistingDirectoryPath(rootDirectory);
        if (!SamePath(Directory.GetParent(candidate)?.FullName, StorageRoot) || EnumerateChapterDirectories(candidate).Count == 0)
            throw new UnauthorizedAccessException("漫画目录不在受管理的书库中");
        return candidate;
    }

    private string ResolveChapterDirectory(string rootDirectory, string chapterDirectoryName)
    {
        var root = ResolveLibraryRoot(rootDirectory);
        if (string.IsNullOrWhiteSpace(chapterDirectoryName) ||
            Path.GetFileName(chapterDirectoryName) != chapterDirectoryName ||
            !IsPotentialChapterDirectoryName(chapterDirectoryName))
            throw new ArgumentException("章节目录名称无效");
        var chapterPath = Path.GetFullPath(Path.Combine(root, chapterDirectoryName));
        if (!Directory.Exists(chapterPath))
            throw new DirectoryNotFoundException("章节目录不存在");
        var chapter = ResolveExistingDirectoryPath(chapterPath);
        if (!SamePath(Directory.GetParent(chapter)?.FullName, root) || !ContainsImageFile(chapter))
            throw new UnauthorizedAccessException("章节目录不在当前漫画目录中");
        return chapter;
    }

    private string ResolveReaderImage(string imagePath)
    {
        if (string.IsNullOrWhiteSpace(imagePath)) throw new ArgumentException("图片路径无效");
        if (!File.Exists(imagePath) || !ImageExtensions.Contains(Path.GetExtension(imagePath), StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException("图片路径无效");
        var candidate = ResolveExistingFilePath(imagePath);
        var imageDirectory = Directory.GetParent(candidate)?.FullName;
        if (imageDirectory is null)
            throw new UnauthorizedAccessException("图片不在受管理的书库中");

        var imageDirectoryParent = Directory.GetParent(imageDirectory)?.FullName;
        var isMangaCover = SamePath(imageDirectoryParent, StorageRoot) &&
            EnumerateChapterDirectories(imageDirectory).Count > 0;
        if (isMangaCover) return candidate;

        var mangaDirectory = imageDirectoryParent;
        var isChapterImage = mangaDirectory is not null &&
            IsPotentialChapterDirectoryName(Path.GetFileName(imageDirectory)) &&
            SamePath(Directory.GetParent(mangaDirectory)?.FullName, StorageRoot) &&
            ContainsImageFile(imageDirectory);
        if (!isChapterImage)
            throw new UnauthorizedAccessException("图片不在受管理的书库中");
        return candidate;
    }

    private static List<DirectoryInfo> EnumerateChapterDirectories(string rootDirectory)
    {
        try
        {
            return new DirectoryInfo(rootDirectory).EnumerateDirectories()
                .Where(directory => IsPotentialChapterDirectoryName(directory.Name) && ContainsImageFile(directory.FullName))
                .ToList();
        }
        catch { return []; }
    }

    private static List<string> EnumerateImages(string chapterDirectory)
    {
        try
        {
            return Directory.EnumerateFiles(chapterDirectory)
                .Where(file => ImageExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
                .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch { return []; }
    }

    private static bool ContainsImageFile(string directory)
    {
        try
        {
            return Directory.EnumerateFiles(directory)
                .Any(file => ImageExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase));
        }
        catch { return false; }
    }

    private static bool IsPotentialChapterDirectoryName(string name)
    {
        return !string.IsNullOrWhiteSpace(name) &&
            Path.GetFileName(name) == name &&
            !name.StartsWith(".", StringComparison.Ordinal) &&
            !name.EndsWith("_CBZ", StringComparison.OrdinalIgnoreCase);
    }

    private static IOrderedEnumerable<DirectoryInfo> OrderChapterDirectories(IEnumerable<DirectoryInfo> directories) =>
        directories.OrderBy(directory => ChapterOrder(directory.Name))
            .ThenBy(directory => directory.Name, StringComparer.OrdinalIgnoreCase);

    private static int ChapterOrder(string directoryName)
    {
        var match = Regex.Match(directoryName, @"\d+");
        return match.Success && int.TryParse(match.Value, out var order) ? order : int.MaxValue;
    }

    private static string ChapterTitle(string directoryName)
    {
        var separator = directoryName.IndexOf('_');
        return separator > 0 &&
            separator < directoryName.Length - 1 &&
            directoryName[..separator].All(char.IsDigit)
                ? directoryName[(separator + 1)..]
                : directoryName;
    }

    private LibraryMetadata? LoadLibraryMetadata(string rootDirectory)
    {
        try
        {
            var path = Path.Combine(rootDirectory, "元数据.json");
            if (!File.Exists(path)) return null;

            var json = File.ReadAllText(path);
            var metadata = JsonSerializer.Deserialize<LibraryMetadata>(json, JsonOptions) ?? new LibraryMetadata();
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return metadata;

            if (string.IsNullOrWhiteSpace(metadata.MangaTitle))
            {
                metadata.MangaTitle = ReadMetadataString(document.RootElement, "name", "title", "comic_name", "book_name");
            }

            if (metadata.Authors.Count == 0)
            {
                metadata.Authors = ReadMetadataStrings(document.RootElement, "author", "authors", "writer", "writers", "artist", "artists");
            }

            return metadata;
        }
        catch { return null; }
    }

    private static string ReadMetadataString(JsonElement root, params string[] propertyNames)
    {
        foreach (var property in root.EnumerateObject())
        {
            if (!propertyNames.Contains(property.Name, StringComparer.OrdinalIgnoreCase) ||
                property.Value.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var value = property.Value.GetString()?.Trim();
            if (!string.IsNullOrWhiteSpace(value)) return value;
        }

        return string.Empty;
    }

    private static List<string> ReadMetadataStrings(JsonElement root, params string[] propertyNames)
    {
        var values = new List<string>();
        foreach (var property in root.EnumerateObject())
        {
            if (!propertyNames.Contains(property.Name, StringComparer.OrdinalIgnoreCase)) continue;

            if (property.Value.ValueKind == JsonValueKind.String)
            {
                AddMetadataString(values, property.Value.GetString());
            }
            else if (property.Value.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in property.Value.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String) AddMetadataString(values, item.GetString());
                }
            }
        }

        return values.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static void AddMetadataString(List<string> values, string? value)
    {
        var trimmed = value?.Trim();
        if (!string.IsNullOrWhiteSpace(trimmed)) values.Add(trimmed);
    }

    private List<DownloadHistoryItem> LoadHistory()
    {
        try
        {
            var items = File.Exists(_historyFile)
                ? JsonSerializer.Deserialize<List<DownloadHistoryItem>>(File.ReadAllText(_historyFile), JsonOptions) ?? []
                : [];
            var changed = false;
            foreach (var item in items)
            {
                changed |= EnrichHistoryMetadata(item);
            }
            if (changed)
            {
                WriteJsonAtomically(_historyFile, items);
            }
            return items;
        }
        catch { return []; }
    }

    private bool EnrichHistoryMetadata(DownloadHistoryItem item)
    {
        var changed = false;
        if (string.IsNullOrWhiteSpace(item.SiteName))
        {
            item.SiteName = SiteCatalog.GetDisplayName(item.SiteKey);
            changed = true;
        }

        if (string.IsNullOrWhiteSpace(item.RootDir) || !Directory.Exists(item.RootDir))
        {
            return changed;
        }

        var metadata = LoadLibraryMetadata(item.RootDir);
        if (string.IsNullOrWhiteSpace(item.MangaTitle) && !string.IsNullOrWhiteSpace(metadata?.MangaTitle))
        {
            item.MangaTitle = metadata.MangaTitle;
            changed = true;
        }
        if (string.IsNullOrWhiteSpace(item.Author) && metadata?.Authors.Count > 0)
        {
            item.Author = string.Join("、", metadata.Authors
                .Where(author => !string.IsNullOrWhiteSpace(author))
                .Distinct(StringComparer.OrdinalIgnoreCase));
            changed = true;
        }
        if (string.IsNullOrWhiteSpace(item.SiteName) && !string.IsNullOrWhiteSpace(metadata?.SiteName))
        {
            item.SiteName = metadata.SiteName;
            changed = true;
        }
        if (string.IsNullOrWhiteSpace(item.Url) && !string.IsNullOrWhiteSpace(metadata?.MangaUrl))
        {
            item.Url = metadata.MangaUrl;
            changed = true;
        }
        if (string.IsNullOrWhiteSpace(item.CoverUrl))
        {
            var chapters = OrderChapterDirectories(EnumerateChapterDirectories(item.RootDir)).ToList();
            item.CoverUrl = metadata?.CoverUrl is { Length: > 0 } coverUrl
                ? coverUrl
                : EnumerateImages(item.RootDir).FirstOrDefault()
                    ?? (chapters.Count > 0 ? EnumerateImages(chapters[0].FullName).FirstOrDefault() : null)
                    ?? string.Empty;
            changed = !string.IsNullOrWhiteSpace(item.CoverUrl) || changed;
        }

        return changed;
    }

    private void RecordHistory(NativeDownloadTask state)
    {
        DownloadHistoryItem item;
        lock (state.Gate)
        {
            if (state.HistoryRecorded) return;
            state.HistoryRecorded = true;
            item = new DownloadHistoryItem
            {
                Id = state.Dto.Id,
                Url = state.Dto.Url,
                SiteKey = state.Dto.SiteKey,
                MangaTitle = state.Dto.MangaTitle,
                Author = state.Manga is null
                    ? string.Empty
                    : string.Join("、", state.Manga.Authors
                        .Where(author => !string.IsNullOrWhiteSpace(author))
                        .Distinct(StringComparer.OrdinalIgnoreCase)),
                SiteName = SiteCatalog.DisplayName,
                CoverUrl = state.Manga?.CoverUrl ?? string.Empty,
                Status = state.Dto.Status,
                Progress = state.Dto.Progress,
                CompletedChapterCount = state.CompletedChapterCount,
                TotalChapterCount = state.TotalChapterCount,
                RootDir = state.RootDirectory,
                TaskError = state.Dto.TaskError is null ? null : new ApiError { Code = state.Dto.TaskError.Code, Message = state.Dto.TaskError.Message },
                FinishedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            };
        }
        lock (_historyLock)
        {
            _history.Add(item);
            if (_history.Count > 200) _history.RemoveRange(0, _history.Count - 200);
        }
        SaveHistory();
    }

    private void SaveHistory()
    {
        List<DownloadHistoryItem> snapshot;
        lock (_historyLock) snapshot = _history.Select(CloneHistory).ToList();
        WriteJsonAtomically(_historyFile, snapshot);
    }

    private static void WriteJsonAtomically<T>(string path, T value)
    {
        try
        {
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(value, JsonOptions), new UTF8Encoding(false));
            File.Move(temporary, path, true);
        }
        catch (Exception ex) { Debug.WriteLine($"保存状态失败: {ex.Message}"); }
    }

    private static DateTime? ParseDate(string? value) => DateTime.TryParse(value, out var result) ? result : null;

    private static bool SamePath(string? left, string? right) =>
        !string.IsNullOrWhiteSpace(left) && !string.IsNullOrWhiteSpace(right) &&
        string.Equals(Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);

    private static string ResolveExistingDirectoryPath(string path)
    {
        var directory = new DirectoryInfo(Path.GetFullPath(path));
        return Path.GetFullPath(directory.ResolveLinkTarget(true)?.FullName ?? directory.FullName);
    }

    private static string ResolveExistingFilePath(string path)
    {
        var file = new FileInfo(Path.GetFullPath(path));
        return Path.GetFullPath(file.ResolveLinkTarget(true)?.FullName ?? file.FullName);
    }

    private NativeDownloadTask RequireTask(string id) =>
        _downloads.TryGetValue(id, out var state) ? state : throw new KeyNotFoundException("任务不存在");

    private static void SetStatus(NativeDownloadTask state, string status, string text)
    {
        lock (state.Gate)
        {
            state.Dto.Status = status;
            state.Dto.StatusText = text;
            if (status is "running" or "completed" or "paused" or "pausing" or "stopping" or "stopped") state.Dto.TaskError = null;
        }
    }

    private static void AppendLog(NativeDownloadTask state, string tag, string message)
    {
        lock (state.Gate)
        {
            state.Dto.Logs.Add(new DownloadLogEntry { Time = DateTime.Now.ToString("HH:mm:ss"), Tag = tag, Message = message });
            if (state.Dto.Logs.Count > 200) state.Dto.Logs.RemoveRange(0, state.Dto.Logs.Count - 200);
        }
    }

    private static DownloadTaskDto CloneTask(NativeDownloadTask state)
    {
        lock (state.Gate)
        {
            var speedIsFresh = (state.Dto.Status is "running" or "pausing") &&
                state.SpeedLastActivityAt is { } lastActivity &&
                DateTimeOffset.UtcNow - lastActivity < TimeSpan.FromSeconds(2);
            return new DownloadTaskDto
            {
                Id = state.Dto.Id,
                Url = state.Dto.Url,
                SiteKey = state.Dto.SiteKey,
                MangaTitle = state.Dto.MangaTitle,
                Status = state.Dto.Status,
                StatusText = state.Dto.StatusText,
                Progress = state.Dto.Progress,
                DownloadSpeedBytesPerSecond = speedIsFresh ? state.Dto.DownloadSpeedBytesPerSecond : 0,
                TaskError = state.Dto.TaskError is null ? null : new ApiError { Code = state.Dto.TaskError.Code, Message = state.Dto.TaskError.Message },
                Logs = state.Dto.Logs.Select(log => new DownloadLogEntry { Time = log.Time, Tag = log.Tag, Message = log.Message }).ToList(),
                Chapters = state.Dto.Chapters.Select(chapter => new DownloadChapterProgressDto
                {
                    Id = chapter.Id,
                    Title = chapter.Title,
                    Status = chapter.Status,
                    CompletedImages = chapter.CompletedImages,
                    TotalImages = chapter.TotalImages,
                    Progress = chapter.Progress,
                    Error = chapter.Error,
                    DirectoryName = chapter.DirectoryName,
                }).ToList(),
            };
        }
    }

    private static DownloadHistoryItem CloneHistory(DownloadHistoryItem item) => new()
    {
        Id = item.Id,
        Url = item.Url,
        SiteKey = item.SiteKey,
        MangaTitle = item.MangaTitle,
        Author = item.Author,
        SiteName = item.SiteName,
        CoverUrl = item.CoverUrl,
        Status = item.Status,
        Progress = item.Progress,
        CompletedChapterCount = item.CompletedChapterCount,
        TotalChapterCount = item.TotalChapterCount,
        RootDir = item.RootDir,
        TaskError = item.TaskError is null ? null : new ApiError { Code = item.TaskError.Code, Message = item.TaskError.Message },
        FinishedAt = item.FinishedAt,
    };

    private static ExportCbzProgress CloneExportProgress(ExportCbzProgress value) => new()
    {
        Id = value.Id,
        Status = value.Status,
        MangaTitle = value.MangaTitle,
        CurrentChapter = value.CurrentChapter,
        CurrentIndex = value.CurrentIndex,
        TotalChapters = value.TotalChapters,
        ExportedCount = value.ExportedCount,
        ExportDir = value.ExportDir,
        SkippedChapters = value.SkippedChapters.ToList(),
        Error = value.Error,
    };

    private sealed class NativeDownloadTask : IDisposable
    {
        public NativeDownloadTask(DownloadCreateRequest request, DownloadTaskDto dto)
        {
            Request = request;
            Dto = dto;
        }

        public object Gate { get; } = new();
        public DownloadCreateRequest Request { get; }
        public DownloadTaskDto Dto { get; }
        public CancellationTokenSource StopSource { get; } = new();
        public HashSet<string> StoppedChapterIds { get; } = new(StringComparer.Ordinal);
        public HashSet<string> DeletedChapterIds { get; } = new(StringComparer.Ordinal);
        public CancellationTokenSource? CurrentChapterStopSource { get; set; }
        public string CurrentChapterId { get; set; } = string.Empty;
        public Task? Worker { get; set; }
        public bool PauseRequested { get; set; }
        public bool HistoryRecorded { get; set; }
        public string RootDirectory { get; set; } = string.Empty;
        public int CompletedChapterCount { get; set; }
        public int TotalChapterCount { get; set; }
        public JmMangaInfo? Manga { get; set; }
        public long SpeedLastReportedBytes { get; set; }
        public long SpeedWindowBytes { get; set; }
        public long SpeedWindowStartedTimestamp { get; set; }
        public int SpeedLastCompletedImages { get; set; }
        public DateTimeOffset? SpeedLastActivityAt { get; set; }

        public void Dispose()
        {
            StopSource.Cancel();
            StopSource.Dispose();
        }
    }

    private sealed class ExportTaskState
    {
        public object Gate { get; } = new();
        public required ExportCbzProgress Progress { get; init; }
        public Task? Worker { get; set; }
    }

    private sealed record LibraryEntry(
        string Title,
        string Author,
        string SiteName,
        string RootDirectory,
        string MangaUrl,
        string CoverUrl,
        int DownloadedChapterCount,
        string LastDownloadedChapterTitle,
        DateTime SavedAt);

    private sealed class LibraryMetadata
    {
        [JsonPropertyName("schema_version")] public int SchemaVersion { get; set; }
        [JsonPropertyName("site_key")] public string SiteKey { get; set; } = string.Empty;
        [JsonPropertyName("site_name")] public string SiteName { get; set; } = string.Empty;
        [JsonPropertyName("manga_title")] public string MangaTitle { get; set; } = string.Empty;
        [JsonPropertyName("authors")] public List<string> Authors { get; set; } = [];
        [JsonPropertyName("manga_url")] public string MangaUrl { get; set; } = string.Empty;
        [JsonPropertyName("root_dir")] public string RootDirectory { get; set; } = string.Empty;
        [JsonPropertyName("cover_url")] public string CoverUrl { get; set; } = string.Empty;
        [JsonPropertyName("total_chapters")] public int TotalChapters { get; set; }
        [JsonPropertyName("downloaded_chapter_count")] public int DownloadedChapterCount { get; set; }
        [JsonPropertyName("last_downloaded_chapter_title")] public string LastDownloadedChapterTitle { get; set; } = string.Empty;
        [JsonPropertyName("last_downloaded_chapter_order")] public int? LastDownloadedChapterOrder { get; set; }
        [JsonPropertyName("downloaded_chapters")] public List<DownloadedChapterRecord> DownloadedChapters { get; set; } = [];
        [JsonPropertyName("completed")] public bool Completed { get; set; }
        [JsonPropertyName("saved_at")] public string SavedAt { get; set; } = string.Empty;
        [JsonPropertyName("created_at")] public string CreatedAt { get; set; } = string.Empty;
        [JsonPropertyName("last_failed_chapter_records")] public List<FailedChapterRecord> LastFailedChapterRecords { get; set; } = [];
        [JsonPropertyName("last_failed_chapter_count")] public int LastFailedChapterCount { get; set; }
    }

    private sealed class DownloadedChapterRecord
    {
        [JsonPropertyName("order")] public int Order { get; set; }
        [JsonPropertyName("dir_name")] public string DirName { get; set; } = string.Empty;
        [JsonPropertyName("title")] public string Title { get; set; } = string.Empty;
        [JsonPropertyName("image_count")] public int ImageCount { get; set; }
    }

    private sealed class FailedChapterRecord
    {
        [JsonPropertyName("order")] public int Order { get; set; }
        [JsonPropertyName("slug")] public string Slug { get; set; } = string.Empty;
        [JsonPropertyName("title")] public string Title { get; set; } = string.Empty;
        [JsonPropertyName("reason")] public string Reason { get; set; } = string.Empty;
    }
}
