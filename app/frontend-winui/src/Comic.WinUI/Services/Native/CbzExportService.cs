using System.Collections.Concurrent;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using Comic.WinUI.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Comic.WinUI.Services.Native;

/// <summary>CBZ 导出服务:把书库中已完成的章节打包为 CBZ 压缩档并写入 ComicInfo.xml。</summary>
public sealed class CbzExportService : IDisposable
{
    private readonly LibraryStorageService _library;
    private readonly ILogger<CbzExportService> _logger;
    private readonly ConcurrentDictionary<string, ExportTaskState> _exports = [];

    public CbzExportService(LibraryStorageService library, ILogger<CbzExportService>? logger = null)
    {
        _library = library;
        _logger = logger ?? NullLogger<CbzExportService>.Instance;
    }

    public void Dispose()
    {
        // 与下载调度一致:先取消,等 worker 真正结束后再释放 CTS,且不阻塞关窗的 UI 线程。
        foreach (var state in _exports.Values.ToArray())
        {
            try
            {
                state.Cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
                continue;
            }

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

    /// <summary>清掉已结束的导出任务,避免导出表随会话无界增长。</summary>
    private void PruneFinishedExports()
    {
        foreach (var pair in _exports.ToArray())
        {
            string status;
            lock (pair.Value.Gate) status = pair.Value.Progress.Status;
            if (status is not ("completed" or "failed" or "cancelled")) continue;
            if (_exports.TryRemove(pair.Key, out var removed)) removed.Dispose();
        }
    }

    /// <summary>
    /// 请求取消导出任务。导出线程会在最近的章节边界退出,已生成的 CBZ 文件保留。
    /// 返回是否成功发出取消请求(任务不存在或已结束时为 false)。
    /// </summary>
    public Task<bool> CancelExportAsync(string taskId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_exports.TryGetValue(taskId, out var state)) return Task.FromResult(false);
        lock (state.Gate)
        {
            if (state.Progress.Status != "running") return Task.FromResult(false);
        }

        try
        {
            state.Cancellation.Cancel();
            return Task.FromResult(true);
        }
        catch (ObjectDisposedException)
        {
            return Task.FromResult(false);
        }
    }

    public Task<ExportCbzResponse> ExportCbzAsync(string rootDir, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var resolvedRoot = _library.ResolveLibraryRoot(rootDir);
        var metadata = _library.LoadLibraryMetadata(resolvedRoot);
        PruneFinishedExports();
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
        state.Worker = Task.Run(
            () => ProcessExportAsync(state, resolvedRoot, metadata, state.Cancellation.Token),
            CancellationToken.None);
        return Task.FromResult(new ExportCbzResponse { Status = "ok", TaskId = taskId, Message = "导出任务已创建" });
    }

    public Task<ExportCbzProgress> GetExportProgressAsync(string taskId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_exports.TryGetValue(taskId, out var state)) throw new KeyNotFoundException("导出任务不存在");
        lock (state.Gate) return Task.FromResult(CloneExportProgress(state.Progress));
    }

    private async Task ProcessExportAsync(
        ExportTaskState state,
        string rootDirectory,
        LibraryMetadata? metadata,
        CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var chapters = _library.OrderChapterDirectories(_library.EnumerateChapterDirectories(rootDirectory)).ToList();
            if (chapters.Count == 0) throw new InvalidOperationException("当前漫画目录里没有可导出的已完成章节");
            var simplifiedMangaTitle = ChineseTextConverter.ToSimplified(state.Progress.MangaTitle);
            var mangaUrl = metadata?.MangaUrl ?? string.Empty;
            var exportDirectory = Path.Combine(Directory.GetParent(rootDirectory)!.FullName, simplifiedMangaTitle + "_CBZ");
            Directory.CreateDirectory(exportDirectory);
            var exported = 0;
            var skipped = new List<string>();

            lock (state.Gate)
            {
                state.Progress.TotalChapters = chapters.Count;
                state.Progress.ExportDir = exportDirectory;
                state.Progress.CurrentChapter = LibraryStorageService.ChapterTitle(chapters[0].Name, metadata);
            }

            for (var index = 0; index < chapters.Count; index++)
            {
                var chapter = chapters[index];
                var chapterTitle = LibraryStorageService.ChapterTitle(chapter.Name, metadata);
                cancellationToken.ThrowIfCancellationRequested();
                var images = _library.EnumerateImages(chapter.FullName);
                lock (state.Gate) state.Progress.CurrentChapter = chapterTitle;
                if (images.Count == 0) skipped.Add(chapter.Name);
                else
                {
                    await CreateCbzAsync(
                        Path.Combine(exportDirectory, chapter.Name + ".cbz"),
                        images,
                        simplifiedMangaTitle,
                        chapterTitle,
                        index + 1,
                        chapters.Count,
                        mangaUrl,
                        cancellationToken);
                    exported++;
                }
                lock (state.Gate)
                {
                    state.Progress.CurrentChapter = chapterTitle;
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
        catch (OperationCanceledException)
        {
            // 已生成的 CBZ 文件保留,只标记任务被取消。
            _logger.LogInformation("CBZ 导出任务已取消: {RootDirectory}", rootDirectory);
            lock (state.Gate)
            {
                state.Progress.Status = "cancelled";
                state.Progress.Error = "导出已取消";
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CBZ 导出任务失败: {RootDirectory}", rootDirectory);
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
        string mangaUrl,
        CancellationToken cancellationToken)
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
                    cancellationToken.ThrowIfCancellationRequested();
                    var entry = archive.CreateEntry(Path.GetFileName(imagePath), CompressionLevel.Optimal);
                    await using var target = entry.Open();
                    await using var source = File.OpenRead(imagePath);
                    await source.CopyToAsync(target, cancellationToken);
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

    private sealed class ExportTaskState : IDisposable
    {
        public object Gate { get; } = new();
        public required ExportCbzProgress Progress { get; init; }
        public Task? Worker { get; set; }
        public CancellationTokenSource Cancellation { get; } = new();

        public void Dispose()
        {
            try
            {
                Cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // 已释放,无需再取消。
            }

            Cancellation.Dispose();
        }
    }
}
