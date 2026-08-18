using Comic.WinUI.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Comic.WinUI.Services.Native;

/// <summary>阅读访问服务:提供受管书库内的章节列表、图片列表与图片读取。</summary>
public sealed class ReaderService
{
    private readonly LibraryStorageService _library;
    private readonly ILogger<ReaderService> _logger;

    public ReaderService(LibraryStorageService library, ILogger<ReaderService>? logger = null)
    {
        _library = library;
        _logger = logger ?? NullLogger<ReaderService>.Instance;
    }

    public Task<ReaderChaptersResponse> GetReaderChaptersAsync(string rootDir, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var resolvedRoot = _library.ResolveLibraryRoot(rootDir);
        var metadata = _library.LoadLibraryMetadata(resolvedRoot);
        var chapters = _library.OrderChapterDirectories(_library.EnumerateChapterDirectories(resolvedRoot)).Select(item => new ReaderChapterDto
        {
            DirName = item.Name,
            Title = LibraryStorageService.ChapterTitle(item.Name),
            Order = LibraryStorageService.ChapterOrder(item.Name),
            ImageCount = _library.EnumerateImages(item.FullName).Count,
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
        var chapterDirectory = _library.ResolveChapterDirectory(rootDir, chapterDirectoryName);
        return Task.FromResult(new ReaderImagesResponse { Images = _library.EnumerateImages(chapterDirectory) });
    }

    public Task<byte[]> GetImageBytesAsync(string imagePath, CancellationToken cancellationToken = default) =>
        File.ReadAllBytesAsync(_library.ResolveReaderImage(imagePath), cancellationToken);
}
