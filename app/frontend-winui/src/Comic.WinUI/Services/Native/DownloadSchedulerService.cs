using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Comic.WinUI.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Comic.WinUI.Services.Native;

/// <summary>
/// 下载调度服务:管理下载任务的生命周期(创建/暂停/继续/停止/删除)、
/// 章节下载执行、进度统计与下载历史持久化。
/// </summary>
public sealed class DownloadSchedulerService : IDisposable
{
    private const string TemporaryChapterPrefix = ".下载中_";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = SnakeCaseNamingPolicy.Instance,
        WriteIndented = true,
    };

    private readonly JmComicService _jmComic;
    private readonly LibraryStorageService _library;
    private readonly ApplicationSettingsService? _applicationSettings;
    private readonly ILogger<DownloadSchedulerService> _logger;
    private readonly ConcurrentDictionary<string, NativeDownloadTask> _downloads = [];
    // 任务号是 Guid 前 8 位,没有时间序,不能拿来排序。这里单独记创建次序。
    private readonly ConcurrentDictionary<string, long> _creationOrder = [];
    private long _creationSequence;
    private readonly object _historyLock = new();
    private readonly List<DownloadHistoryItem> _history;

    public DownloadSchedulerService(
        JmComicService jmComic,
        LibraryStorageService library,
        ApplicationSettingsService? applicationSettings = null,
        ILogger<DownloadSchedulerService>? logger = null)
    {
        _jmComic = jmComic;
        _library = library;
        _applicationSettings = applicationSettings;
        _logger = logger ?? NullLogger<DownloadSchedulerService>.Instance;
        _history = LoadHistory();
    }

    private string StateDirectory => Path.Combine(_library.StorageRoot, ".comic_state");
    private string HistoryFile => Path.Combine(StateDirectory, "task_history.json");

    public static bool IsTerminal(string status) => status is "completed" or "failed" or "partial" or "stopped";

    public bool HasActiveTasks() => _downloads.Values.Any(state => !IsTerminal(CloneTask(state).Status));

    /// <summary>存储根目录切换后,把历史归档到旧位置并从新位置重新加载。</summary>
    public void ReloadHistoryForStorageChange()
    {
        SaveHistory();
        Directory.CreateDirectory(StateDirectory);
        lock (_historyLock)
        {
            _history.Clear();
            _history.AddRange(LoadHistory());
        }
    }

    public void Dispose()
    {
        // 必须先取消、等 worker 退出,再释放 CTS。直接 Dispose 会在仍在运行的 worker 底下
        // 释放它正在使用的 token,后台线程随即抛 ObjectDisposedException,并留下 .part 残件。
        // 这里用完成回调而不是阻塞等待:Dispose 由窗口关闭触发,不能卡住 UI 线程。
        foreach (var state in _downloads.Values.ToList())
        {
            state.RequestStop();
            var worker = state.Worker;
            if (worker is null || worker.IsCompleted)
            {
                state.Dispose();
                continue;
            }

            worker.ContinueWith(
                _ => state.Dispose(),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    // ---- 任务控制 ----

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
        _creationOrder[id] = Interlocked.Increment(ref _creationSequence);
        state.Worker = Task.Run(() => ProcessDownloadAsync(state), CancellationToken.None);
        return Task.FromResult(CloneTask(state));
    }

    public Task<DownloadListResponse> GetDownloadsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new DownloadListResponse
        {
            Items = _downloads.Values.Select(CloneTask)
                .OrderByDescending(task => _creationOrder.TryGetValue(task.Id, out var order) ? order : 0)
                .ToList(),
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
                _creationOrder.TryRemove(id, out _);
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

    // ---- 下载历史 ----

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

    // ---- 下载执行 ----

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
            var rootDirectory = Path.Combine(_library.StorageRoot, JmComicService.SanitizeFileName(manga.Title));
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
            var concurrency = _applicationSettings?.DownloadConcurrency ?? 3;
            AppendLog(state, "info", $"图片并发: {concurrency}");

            var completed = 0;
            var chapterMaxAttempts = Math.Max(1, _applicationSettings?.ChapterRetryCount ?? 3);
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
                    for (var attempt = 0; attempt < chapterMaxAttempts; attempt++)
                    {
                        try
                        {
                            result = await _jmComic.DownloadChapterAsync(
                                chapter,
                                rootDirectory,
                                concurrency,
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
                            if (attempt < chapterMaxAttempts - 1)
                            {
                                AppendLog(state, "warn", $"章节失败，准备重试 ({attempt + 1}/{chapterMaxAttempts - 1})：{chapter.Title} - {ex.Message}");
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
            _logger.LogError(ex, "下载任务 {TaskId} 执行失败", state.Dto.Id);
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

    public static void DeleteChapterDirectories(
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

    public static List<JmChapter> SelectChapters(JmMangaInfo manga, IReadOnlyCollection<string>? requested)
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
        _library.SaveLibraryMetadata(state.Manga, state.Request.Url, state.RootDirectory, completed, failures);
    }

    private List<DownloadHistoryItem> LoadHistory()
    {
        try
        {
            var items = File.Exists(HistoryFile)
                ? JsonSerializer.Deserialize<List<DownloadHistoryItem>>(File.ReadAllText(HistoryFile), JsonOptions) ?? []
                : [];
            var changed = false;
            foreach (var item in items)
            {
                changed |= EnrichHistoryMetadata(item);
            }
            if (changed)
            {
                _library.WriteJsonAtomically(HistoryFile, items);
            }
            return items;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "读取下载历史失败: {Path}", HistoryFile);
            return [];
        }
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

        var metadata = _library.LoadLibraryMetadata(item.RootDir);
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
            var chapters = _library.OrderChapterDirectories(_library.EnumerateChapterDirectories(item.RootDir)).ToList();
            item.CoverUrl = metadata?.CoverUrl is { Length: > 0 } coverUrl
                ? coverUrl
                : _library.EnumerateImages(item.RootDir).FirstOrDefault()
                    ?? (chapters.Count > 0 ? _library.EnumerateImages(chapters[0].FullName).FirstOrDefault() : null)
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
        _library.WriteJsonAtomically(HistoryFile, snapshot);
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

        /// <summary>只请求停止,不释放。关闭流程需要先取消、等 worker 退出,再 Dispose。</summary>
        public void RequestStop()
        {
            try
            {
                StopSource.Cancel();
                CurrentChapterStopSource?.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // 已经释放过,无需再取消。
            }
        }

        public void Dispose()
        {
            RequestStop();
            StopSource.Dispose();
        }
    }
}
