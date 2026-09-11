using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Comic.WinUI.Models;
using Comic.WinUI.Services;
using Comic.WinUI.Services.Native;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Comic.WinUI.ViewModels;

public partial class ReaderPageViewModel : ObservableObject
{
    private const int StripZoomMinimum = ApplicationSettingsService.StripZoomMinimum;
    private const int StripZoomMaximum = ApplicationSettingsService.StripZoomMaximum;
    private const int PagedZoomMinimum = 50;
    private const int PagedZoomMaximum = 300;

    private readonly BackendClient _backendClient;
    private readonly IDispatcher _dispatcherQueue;
    private readonly ReadingProgressService _readingProgressService;
    private readonly ReaderPreferenceService _readerPreferences;
    private readonly string _defaultReaderMode;
    private readonly int _defaultStripZoom;
    private readonly Func<byte[], CancellationToken, Task<BitmapImage>> _decodePagedImage;
    private CancellationTokenSource? _pagedImageCts;
    private int _pagedImageRequest;
    private int _failedPagedImageIndex = -1;
    internal Task PagedImageApplication { get; private set; } = Task.CompletedTask;

    private CancellationTokenSource? _imageCts;
    private CancellationTokenSource? _preloadCts;
    private byte[]? _nextImageCache;
    private int _nextImageCacheIndex = -1;
    private string _rootDir = string.Empty;
    private bool _isOnlineMode;
    private List<string> _currentImagePaths = [];
    private List<JmImageSource> _currentImageSources = [];
    private int _pendingImageIndex = -1;

    public ReaderPageViewModel(
        BackendClient backendClient,
        ApplicationSettingsService applicationSettings,
        ReadingProgressService readingProgressService,
        ReaderPreferenceService readerPreferences,
        IDispatcher dispatcher)
        : this(backendClient, applicationSettings, readingProgressService, readerPreferences, dispatcher, DecodePagedImageAsync)
    {
    }

    internal ReaderPageViewModel(
        BackendClient backendClient,
        ApplicationSettingsService applicationSettings,
        ReadingProgressService readingProgressService,
        ReaderPreferenceService readerPreferences,
        IDispatcher dispatcher,
        Func<byte[], CancellationToken, Task<BitmapImage>> decodePagedImage)
    {
        _decodePagedImage = decodePagedImage;
        _backendClient = backendClient;
        _dispatcherQueue = dispatcher;
        _readingProgressService = readingProgressService;
        _readerPreferences = readerPreferences;
        _defaultReaderMode = applicationSettings.DefaultReaderMode;
        _defaultStripZoom = applicationSettings.DefaultStripZoomPercent;
        IsStripMode = applicationSettings.DefaultReaderMode == ApplicationSettingsService.ReaderStrip;
        StripZoomPercent = applicationSettings.DefaultStripZoomPercent;
    }

    public event Action<int>? StripPositionRestoreRequested;

    public ObservableCollection<ReaderChapterDto> Chapters { get; } = [];

    public ObservableCollection<ReaderStripImageItemViewModel> StripImages { get; } = [];

    [ObservableProperty]
    public partial string MangaTitle { get; set; } = string.Empty;

    [ObservableProperty]
    public partial ReaderChapterDto? SelectedChapter { get; set; }

    [ObservableProperty]
    public partial int CurrentImageIndex { get; set; }

    [ObservableProperty]
    public partial int TotalImages { get; set; }

    [ObservableProperty]
    public partial BitmapImage? CurrentImage { get; set; }

    [ObservableProperty]
    public partial BitmapImage? SecondaryImage { get; set; }

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    public partial string PageError { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool HasFailedPagedImage { get; set; }

    [ObservableProperty]
    public partial bool IsStripMode { get; set; }

    [ObservableProperty]
    public partial bool IsDoublePage { get; set; }

    [ObservableProperty]
    public partial bool IsRightToLeft { get; set; }

    public int ReaderModeIndex
    {
        get => IsStripMode ? 2 : IsDoublePage ? 1 : 0;
        set
        {
            IsStripMode = value == 2;
            IsDoublePage = value == 1;
            OnPropertyChanged();
            SaveReaderPreference();
        }
    }

    public ReaderShortcutPreference Shortcuts => _readerPreferences.Shortcuts;
    public FlowDirection PageFlowDirection => IsRightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;

    [ObservableProperty]
    public partial int StripZoomPercent { get; set; } = ApplicationSettingsService.DefaultStripZoom;

    [ObservableProperty]
    public partial int PagedZoomPercent { get; set; } = 100;

    public string StripZoomText => $"{StripZoomPercent}%";

    public string PagedZoomText => $"{PagedZoomPercent}%";

    public bool CanZoomStripOut => StripZoomPercent > StripZoomMinimum;

    public bool CanZoomStripIn => StripZoomPercent < StripZoomMaximum;

    public bool CanZoomPagedOut => PagedZoomPercent > PagedZoomMinimum;

    public bool CanZoomPagedIn => PagedZoomPercent < PagedZoomMaximum;

    public bool HasPreviousImage => CurrentImageIndex > 0;

    public bool HasNextImage => CurrentImageIndex < TotalImages - 1;

    public string PageIndicator => TotalImages > 0
        ? IsDoublePage && CurrentImageIndex + 1 < TotalImages
            ? $"{CurrentImageIndex + 1}-{CurrentImageIndex + 2} / {TotalImages}"
            : $"{CurrentImageIndex + 1} / {TotalImages}"
        : "";

    public bool CanToggleReaderMode => true;

    public bool HasChapters => Chapters.Count > 0;

    public bool HasPreviousChapter
    {
        get
        {
            if (SelectedChapter is null) return false;
            return Chapters.IndexOf(SelectedChapter) > 0;
        }
    }

    public bool HasNextChapter
    {
        get
        {
            if (SelectedChapter is null) return false;
            return Chapters.IndexOf(SelectedChapter) < Chapters.Count - 1;
        }
    }

    public string ChapterProgress
    {
        get
        {
            if (SelectedChapter is null || Chapters.Count == 0) return "";
            return $"{Chapters.IndexOf(SelectedChapter) + 1}/{Chapters.Count}";
        }
    }

    public bool HasPreviousImageOrChapter => CanGoPrevious();

    public bool HasNextImageOrChapter => CanGoNext();

    [RelayCommand]
    public async Task LoadAsync(string rootDir, CancellationToken cancellationToken = default)
    {
        _rootDir = rootDir;
        _isOnlineMode = false;
        ApplyReaderPreference(rootDir);
        OnPropertyChanged(nameof(CanToggleReaderMode));
        IsLoading = true;
        PageError = string.Empty;

        try
        {
            var result = await _backendClient.GetReaderChaptersAsync(rootDir, cancellationToken);
            MangaTitle = result.MangaTitle;

            Chapters.Clear();
            foreach (var ch in result.Chapters)
            {
                Chapters.Add(ch);
            }
            OnPropertyChanged(nameof(HasChapters));

            if (Chapters.Count > 0)
            {
                var selectedChapter = Chapters[0];
                var progress = _readingProgressService.Get(rootDir);
                if (progress is not null)
                {
                    foreach (var chapter in Chapters)
                    {
                        if (!string.Equals(
                                chapter.DirName,
                                progress.ChapterDirectoryName,
                                StringComparison.Ordinal))
                        {
                            continue;
                        }

                        selectedChapter = chapter;
                        _pendingImageIndex = progress.PageIndex;
                        break;
                    }
                }

                SelectedChapter = selectedChapter;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (BackendApiException ex)
        {
            PageError = ex.Error.Message;
        }
        catch (Exception ex)
        {
            PageError = $"加载章节异常: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>在线模式:按漫画链接加载章节列表,不依赖本地书库。</summary>
    [RelayCommand]
    public async Task LoadOnlineAsync(string mangaUrl, CancellationToken cancellationToken = default)
    {
        _rootDir = mangaUrl;
        _isOnlineMode = true;
        ApplyReaderPreference(mangaUrl);
        OnPropertyChanged(nameof(CanToggleReaderMode));
        IsLoading = true;
        PageError = string.Empty;

        try
        {
            var detail = await _backendClient.ResolveMangaAsync(
                new MangaResolveRequest
                {
                    Url = mangaUrl,
                    SiteKey = SiteCatalog.Key,
                },
                cancellationToken);
            MangaTitle = detail.Title;

            Chapters.Clear();
            var order = 0;
            foreach (var chapter in detail.Chapters)
            {
                order++;
                Chapters.Add(new ReaderChapterDto
                {
                    DirName = ExtractChapterId(chapter.Url),
                    Title = chapter.Title,
                    Order = order,
                    ImageCount = 0,
                });
            }
            OnPropertyChanged(nameof(HasChapters));

            if (Chapters.Count > 0)
            {
                var selectedChapter = Chapters[0];
                var progress = _readingProgressService.Get(mangaUrl);
                if (progress is not null)
                {
                    foreach (var chapter in Chapters)
                    {
                        if (!string.Equals(
                                chapter.DirName,
                                progress.ChapterDirectoryName,
                                StringComparison.Ordinal))
                        {
                            continue;
                        }

                        selectedChapter = chapter;
                        _pendingImageIndex = progress.PageIndex;
                        break;
                    }
                }

                SelectedChapter = selectedChapter;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (BackendApiException ex)
        {
            PageError = ex.Error.Message;
        }
        catch (Exception ex)
        {
            PageError = $"加载在线章节异常: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private static string ExtractChapterId(string url)
    {
        var parsed = JmComicService.ParseMangaId(url);
        return parsed.StartChapterId ?? parsed.MangaId ?? string.Empty;
    }

    partial void OnSelectedChapterChanged(ReaderChapterDto? value)
    {
        OnPropertyChanged(nameof(HasPreviousChapter));
        OnPropertyChanged(nameof(HasNextChapter));
        OnPropertyChanged(nameof(ChapterProgress));
        OnPropertyChanged(nameof(HasPreviousImageOrChapter));
        OnPropertyChanged(nameof(HasNextImageOrChapter));
        if (value is not null)
        {
            _ = LoadChapterImagesAsync(value);
        }
    }

    partial void OnIsStripModeChanged(bool value)
    {
        OnPropertyChanged(nameof(ReaderModeIndex));
        SaveReaderPreference();
        if (CurrentImageCount == 0) return;

        if (value)
        {
            Interlocked.Increment(ref _pagedImageRequest);
            _pagedImageCts?.Cancel();
            HasFailedPagedImage = false;
            PageError = string.Empty;
            IsLoading = false;
            CurrentImage = null;
            SecondaryImage = null;
            RebuildStripImages();
            StripPositionRestoreRequested?.Invoke(CurrentImageIndex);
        }
        else
        {
            ClearStripImages();
            _ = ShowImageAsync(
                Math.Clamp(CurrentImageIndex, 0, CurrentImageCount - 1),
                _imageCts?.Token ?? CancellationToken.None);
        }
    }

    partial void OnIsDoublePageChanged(bool value)
    {
        OnPropertyChanged(nameof(ReaderModeIndex));
        SaveReaderPreference();
        if (!IsStripMode && CurrentImageCount > 0)
            _ = ShowImageAsync(CurrentImageIndex, _imageCts?.Token ?? CancellationToken.None);
    }

    partial void OnIsRightToLeftChanged(bool value)
    {
        OnPropertyChanged(nameof(PageFlowDirection));
        SaveReaderPreference();
    }

    partial void OnStripZoomPercentChanged(int value)
    {
        OnPropertyChanged(nameof(StripZoomText));
        OnPropertyChanged(nameof(CanZoomStripOut));
        OnPropertyChanged(nameof(CanZoomStripIn));
        SaveReaderPreference();
    }

    partial void OnPagedZoomPercentChanged(int value)
    {
        OnPropertyChanged(nameof(PagedZoomText));
        OnPropertyChanged(nameof(CanZoomPagedOut));
        OnPropertyChanged(nameof(CanZoomPagedIn));
        SaveReaderPreference();
    }

    public void ChangeStripZoom(int delta)
    {
        StripZoomPercent = Math.Clamp(
            StripZoomPercent + delta,
            StripZoomMinimum,
            StripZoomMaximum);
    }

    public void ResetStripZoom() => StripZoomPercent = 100;

    /// <summary>分页模式缩放(50%–300%),100% 表示图片适应阅读区。</summary>
    public void ChangePagedZoom(int delta)
    {
        PagedZoomPercent = Math.Clamp(
            PagedZoomPercent + delta,
            PagedZoomMinimum,
            PagedZoomMaximum);
    }

    public void ResetPagedZoom() => PagedZoomPercent = 100;

    private async Task LoadChapterImagesAsync(ReaderChapterDto chapter, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _pagedImageRequest);
        _pagedImageCts?.Cancel();
        _imageCts?.Cancel();
        _imageCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = _imageCts.Token;
        ClearImageCache();

        IsLoading = true;
        HasFailedPagedImage = false;
        _failedPagedImageIndex = -1;
        PageError = string.Empty;
        _currentImagePaths = [];
        _currentImageSources = [];
        ClearStripImages();
        TotalImages = 0;
        CurrentImageIndex = 0;
        CurrentImage = null;
        SecondaryImage = null;

        try
        {
            if (_isOnlineMode)
            {
                var sources = await _backendClient.GetOnlineChapterImageSourcesAsync(chapter.DirName, token);
                _currentImageSources = sources.ToList();
            }
            else
            {
                var result = await _backendClient.GetChapterImagesAsync(_rootDir, chapter.DirName, token);
                _currentImagePaths = result.Images;
            }

            var totalCount = CurrentImageCount;
            chapter.ImageCount = totalCount;
            TotalImages = totalCount;
            OnPropertyChanged(nameof(PageIndicator));

            if (totalCount > 0)
            {
                var startIndex = _pendingImageIndex >= 0 && _pendingImageIndex < totalCount
                    ? _pendingImageIndex : 0;
                _pendingImageIndex = -1;
                CurrentImageIndex = startIndex;
                if (IsStripMode)
                {
                    RebuildStripImages();
                    NotifyImageNavigationChanged();
                    SaveReadingProgress(startIndex);
                    StripPositionRestoreRequested?.Invoke(startIndex);
                }
                else
                {
                    await ShowImageAsync(startIndex, token);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (BackendApiException ex)
        {
            PageError = ex.Error.Message;
        }
        catch (Exception ex)
        {
            PageError = $"加载图片异常: {ex.Message}";
        }
        finally
        {
            if (!token.IsCancellationRequested && (IsStripMode || _pagedImageCts is null)) IsLoading = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanGoPrevious))]
    public async Task PreviousImageAsync(CancellationToken cancellationToken = default)
    {
        if (CurrentImageIndex > 0)
        {
            await ShowImageAsync(Math.Max(0, CurrentImageIndex - (IsDoublePage ? 2 : 1)), cancellationToken);
        }
        else if (SelectedChapter is not null)
        {
            var idx = Chapters.IndexOf(SelectedChapter);
            if (idx > 0)
            {
                _pendingImageIndex = Chapters[idx - 1].ImageCount - 1;
                SelectedChapter = Chapters[idx - 1];
            }
        }
    }

    [RelayCommand(CanExecute = nameof(CanGoNext))]
    public async Task NextImageAsync(CancellationToken cancellationToken = default)
    {
        if (CurrentImageIndex < TotalImages - 1)
        {
            await ShowImageAsync(Math.Min(TotalImages - 1, CurrentImageIndex + (IsDoublePage ? 2 : 1)), cancellationToken);
        }
        else if (SelectedChapter is not null)
        {
            var idx = Chapters.IndexOf(SelectedChapter);
            if (idx < Chapters.Count - 1)
            {
                _pendingImageIndex = 0;
                SelectedChapter = Chapters[idx + 1];
            }
        }
    }

    private bool CanGoPrevious()
    {
        if (CurrentImageIndex > 0) return true;
        if (SelectedChapter is not null)
        {
            var idx = Chapters.IndexOf(SelectedChapter);
            return idx > 0;
        }
        return false;
    }

    private bool CanGoNext()
    {
        if (CurrentImageIndex < TotalImages - 1) return true;
        if (SelectedChapter is not null)
        {
            var idx = Chapters.IndexOf(SelectedChapter);
            return idx < Chapters.Count - 1;
        }
        return false;
    }

    [RelayCommand]
    public async Task GoToImageAsync(int index, CancellationToken cancellationToken = default)
    {
        if (index < 0 || index >= TotalImages) return;
        await ShowImageAsync(index, cancellationToken);
    }

    [RelayCommand]
    public void PreviousChapter()
    {
        var idx = Chapters.IndexOf(SelectedChapter!);
        if (idx > 0)
        {
            SelectedChapter = Chapters[idx - 1];
        }
    }

    [RelayCommand]
    public void NextChapter()
    {
        var idx = Chapters.IndexOf(SelectedChapter!);
        if (idx < Chapters.Count - 1)
        {
            SelectedChapter = Chapters[idx + 1];
        }
    }

    private async Task ShowImageAsync(int index, CancellationToken cancellationToken)
    {
        var totalCount = CurrentImageCount;
        if (index < 0 || index >= totalCount) return;

        _pagedImageCts?.Cancel();
        var request = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _imageCts?.Token ?? CancellationToken.None);
        _pagedImageCts = request;
        var token = request.Token;
        var requestId = Interlocked.Increment(ref _pagedImageRequest);
        var doublePage = IsDoublePage;
        var enqueued = false;
        IsLoading = true;
        PageError = string.Empty;
        HasFailedPagedImage = false;
        try
        {
            token.ThrowIfCancellationRequested();
            // 优先使用预加载缓存,否则实时拉取。
            byte[] bytes;
            if (index == _nextImageCacheIndex && _nextImageCache is not null)
            {
                bytes = _nextImageCache;
                _nextImageCache = null;
                _nextImageCacheIndex = -1;
            }
            else
            {
                bytes = await GetImageBytesAtAsync(index, token);
            }

            token.ThrowIfCancellationRequested();
            var secondaryBytes = doublePage && index + 1 < totalCount
                ? await GetImageBytesAtAsync(index + 1, token)
                : null;
            enqueued = _dispatcherQueue.TryEnqueue(() =>
                PagedImageApplication = ApplyPagedImageAsync(bytes, secondaryBytes, index, requestId, request, token));
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            enqueued = _dispatcherQueue.TryEnqueue(() =>
            {
                try { if (IsCurrentPagedRequest(requestId, token)) ReportPagedImageFailure(index, ex); }
                finally { FinishPagedRequest(requestId, request, token); }
            });
        }
        finally
        {
            if (!enqueued) FinishPagedRequest(requestId, request, token);
        }
    }

    private async Task ApplyPagedImageAsync(byte[] bytes, byte[]? secondaryBytes, int index,
        int requestId, CancellationTokenSource request, CancellationToken token)
    {
        try
        {
            if (!IsCurrentPagedRequest(requestId, token)) return;
            // 同时覆盖同步异常和异步解码失败；两页都解码成功后才更新显示与阅读进度。
            var primary = await _decodePagedImage(bytes, token);
            if (!IsCurrentPagedRequest(requestId, token)) return;
            var secondary = secondaryBytes is null ? null : await _decodePagedImage(secondaryBytes, token);
            if (!IsCurrentPagedRequest(requestId, token)) return;
            CurrentImage = primary;
            SecondaryImage = secondary;
            CurrentImageIndex = index;
            NotifyImageNavigationChanged();
            PageError = string.Empty;
            HasFailedPagedImage = false;
            _failedPagedImageIndex = -1;
            IsLoading = false;
            SaveReadingProgress(index);
            var next = index + (secondaryBytes is null ? 1 : 2);
            if (next < CurrentImageCount) _ = PreloadImageAsync(next);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (IsCurrentPagedRequest(requestId, token)) ReportPagedImageFailure(index, ex);
        }
        finally { FinishPagedRequest(requestId, request, token); }
    }

    private static async Task<BitmapImage> DecodePagedImageAsync(byte[] bytes, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var bitmap = new BitmapImage();
        using var stream = new MemoryStream(bytes);
        await bitmap.SetSourceAsync(stream.AsRandomAccessStream()).AsTask(token);
        return bitmap;
    }

    private bool IsCurrentPagedRequest(int requestId, CancellationToken token) =>
        requestId == _pagedImageRequest && !token.IsCancellationRequested;

    private void ReportPagedImageFailure(int index, Exception error)
    {
        CurrentImage = null;
        SecondaryImage = null;
        CurrentImageIndex = index;
        NotifyImageNavigationChanged();
        _failedPagedImageIndex = index;
        HasFailedPagedImage = true;
        PageError = $"第 {index + 1} 页加载失败，可重试或翻到下一页：{error.Message}";
    }

    private void FinishPagedRequest(int requestId, CancellationTokenSource request, CancellationToken token)
    {
        if (requestId == _pagedImageRequest) IsLoading = false;
        if (ReferenceEquals(_pagedImageCts, request)) _pagedImageCts = null;
        request.Dispose();
    }

    [RelayCommand]
    public Task RetryPagedImageAsync(CancellationToken cancellationToken = default) =>
        _failedPagedImageIndex < 0 ? Task.CompletedTask : ShowImageAsync(_failedPagedImageIndex, cancellationToken);

    /// <summary>按当前模式(在线/本地)读取指定索引的图片字节。</summary>
    private async Task<byte[]> GetImageBytesAtAsync(int index, CancellationToken cancellationToken)
    {
        if (_isOnlineMode)
        {
            return await _backendClient.GetOnlineImageBytesAsync(_currentImageSources[index], cancellationToken);
        }
        return await _backendClient.GetImageBytesAsync(_currentImagePaths[index], cancellationToken);
    }

    /// <summary>预取指定图片到内存缓存;新的预载会取消上一次,避免快速翻页时的竞态。</summary>
    private async Task PreloadImageAsync(int index)
    {
        _preloadCts?.Cancel();
        _preloadCts = new CancellationTokenSource();
        var token = _preloadCts.Token;
        try
        {
            var bytes = await GetImageBytesAtAsync(index, token);
            if (token.IsCancellationRequested) return;
            _nextImageCache = bytes;
            _nextImageCacheIndex = index;
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            _nextImageCache = null;
            _nextImageCacheIndex = -1;
        }
    }

    private void ClearImageCache()
    {
        _preloadCts?.Cancel();
        _preloadCts = null;
        _nextImageCache = null;
        _nextImageCacheIndex = -1;
    }

    public async Task LoadStripImageAsync(ReaderStripImageItemViewModel item)
    {
        var token = item.BeginLoad(_imageCts?.Token ?? CancellationToken.None);
        if (token is null) return;

        try
        {
            // 条漫元素只保存索引；真正读取时按当前章节模式选择本地文件或在线图片源。
            // 元素离开虚拟化窗口会取消 token，避免继续下载已经看不到的在线图片。
            var bytes = await GetImageBytesAtAsync(item.Index, token.Value);
            _dispatcherQueue.TryEnqueue(() =>
            {
                if (!item.CanComplete(token.Value)) return;
                try
                {
                    var bitmap = new BitmapImage { DecodePixelWidth = 1200 };
                    using var stream = new MemoryStream(bytes);
                    stream.Position = 0;
                    bitmap.SetSource(stream.AsRandomAccessStream());
                    item.Complete(token.Value, bitmap);
                }
                catch (Exception ex)
                {
                    item.Fail(token.Value, ex.Message);
                }
            });
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _dispatcherQueue.TryEnqueue(() => item.Fail(token.Value, ex.Message));
        }
    }

    public void UnloadStripImage(ReaderStripImageItemViewModel item) => item.Unload();

    public void SaveReadingProgress(int? pageIndex = null)
    {
        if (!IsStripMode && (HasFailedPagedImage || IsLoading)) return;
        if (SelectedChapter is null ||
            string.IsNullOrWhiteSpace(_rootDir) ||
            TotalImages <= 0)
        {
            return;
        }

        var normalizedIndex = Math.Clamp(pageIndex ?? CurrentImageIndex, 0, TotalImages - 1);
        if (CurrentImageIndex != normalizedIndex)
        {
            CurrentImageIndex = normalizedIndex;
            NotifyImageNavigationChanged();
        }

        _readingProgressService.Save(_rootDir, SelectedChapter.DirName, normalizedIndex);
    }

    private void RebuildStripImages()
    {
        ClearStripImages();
        for (var index = 0; index < CurrentImageCount; index++)
        {
            var sourceHint = _isOnlineMode
                ? _currentImageSources[index].Url
                : _currentImagePaths[index];
            StripImages.Add(new ReaderStripImageItemViewModel(index, sourceHint));
        }
    }

    private int CurrentImageCount =>
        _isOnlineMode ? _currentImageSources.Count : _currentImagePaths.Count;

    private void ClearStripImages()
    {
        foreach (var item in StripImages)
        {
            item.Unload();
        }
        StripImages.Clear();
    }

    private void NotifyImageNavigationChanged()
    {
        OnPropertyChanged(nameof(PageIndicator));
        OnPropertyChanged(nameof(HasPreviousImage));
        OnPropertyChanged(nameof(HasNextImage));
        OnPropertyChanged(nameof(HasPreviousImageOrChapter));
        OnPropertyChanged(nameof(HasNextImageOrChapter));
        PreviousImageCommand.NotifyCanExecuteChanged();
        NextImageCommand.NotifyCanExecuteChanged();
    }

    private void ApplyReaderPreference(string mangaKey)
    {
        var preference = _readerPreferences.Get(mangaKey, _defaultReaderMode, _defaultStripZoom);
        IsStripMode = preference.Mode == "strip";
        IsDoublePage = preference.Mode == "spread";
        IsRightToLeft = preference.RightToLeft;
        StripZoomPercent = Math.Clamp(preference.StripZoomPercent, StripZoomMinimum, StripZoomMaximum);
        PagedZoomPercent = Math.Clamp(preference.PagedZoomPercent, PagedZoomMinimum, PagedZoomMaximum);
    }

    private void SaveReaderPreference()
    {
        if (string.IsNullOrWhiteSpace(_rootDir)) return;
        var mode = IsStripMode ? "strip" : IsDoublePage ? "spread" : "paged";
        _readerPreferences.Save(_rootDir, new ReaderPreference(mode, IsRightToLeft, StripZoomPercent, PagedZoomPercent));
    }

    public void SaveShortcuts(ReaderShortcutPreference shortcuts)
    {
        _readerPreferences.SaveShortcuts(shortcuts);
        OnPropertyChanged(nameof(Shortcuts));
    }
}

public partial class ReaderStripImageItemViewModel : ObservableObject
{
    private CancellationTokenSource? _loadCts;

    public ReaderStripImageItemViewModel(int index, string path)
    {
        Index = index;
        Path = path;
    }

    public int Index { get; }

    public string Path { get; }

    public string PageLabel => $"第 {Index + 1} 页";

    [ObservableProperty]
    public partial BitmapImage? Image { get; set; }

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    public partial string Error { get; set; } = string.Empty;

    public bool HasError => !string.IsNullOrWhiteSpace(Error);

    internal CancellationToken? BeginLoad(CancellationToken chapterToken)
    {
        if (Image is not null || IsLoading) return null;

        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = CancellationTokenSource.CreateLinkedTokenSource(chapterToken);
        IsLoading = true;
        Error = string.Empty;
        OnPropertyChanged(nameof(HasError));
        return _loadCts.Token;
    }

    internal bool CanComplete(CancellationToken token) =>
        _loadCts is not null && _loadCts.Token == token && !token.IsCancellationRequested;

    internal void Complete(CancellationToken token, BitmapImage bitmap)
    {
        if (!CanComplete(token)) return;
        Image = bitmap;
        IsLoading = false;
        ReleaseLoadSource();
    }

    internal void Fail(CancellationToken token, string error)
    {
        if (!CanComplete(token)) return;
        Error = $"第 {Index + 1} 页加载失败：{error}";
        IsLoading = false;
        OnPropertyChanged(nameof(HasError));
        ReleaseLoadSource();
    }

    internal void Unload()
    {
        _loadCts?.Cancel();
        ReleaseLoadSource();
        Image = null;
        IsLoading = false;
    }

    private void ReleaseLoadSource()
    {
        _loadCts?.Dispose();
        _loadCts = null;
    }
}

/// <summary>阅读器导航参数:本地书库目录与在线漫画链接二选一。</summary>
public sealed record ReaderNavigationArgs(string? LocalRootDir, string? OnlineMangaUrl);
