using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Comic.WinUI.Models;
using Comic.WinUI.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Comic.WinUI.ViewModels;

public partial class ReaderPageViewModel : ObservableObject
{
    private readonly BackendClient _backendClient;
    private readonly DispatcherQueue _dispatcherQueue;

    private CancellationTokenSource? _imageCts;
    private string _rootDir = string.Empty;
    private List<string> _currentImagePaths = [];
    private int _pendingImageIndex = -1;

    public ReaderPageViewModel(BackendClient backendClient)
    {
        _backendClient = backendClient;
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
    }

    public ObservableCollection<ReaderChapterDto> Chapters { get; } = [];

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
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    public partial string PageError { get; set; } = string.Empty;

    public bool HasPreviousImage => CurrentImageIndex > 0;

    public bool HasNextImage => CurrentImageIndex < TotalImages - 1;

    public string PageIndicator => TotalImages > 0 ? $"{CurrentImageIndex + 1} / {TotalImages}" : "";

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
                SelectedChapter = Chapters[0];
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (BackendApiException ex)
        {
            PageError = ex.Error.Message;
        }
        catch (HttpRequestException)
        {
            PageError = "无法连接后端服务。";
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

    private async Task LoadChapterImagesAsync(ReaderChapterDto chapter, CancellationToken cancellationToken = default)
    {
        _imageCts?.Cancel();
        _imageCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = _imageCts.Token;

        IsLoading = true;
        PageError = string.Empty;
        _currentImagePaths = [];
        TotalImages = 0;
        CurrentImageIndex = 0;
        CurrentImage = null;

        try
        {
            var result = await _backendClient.GetChapterImagesAsync(_rootDir, chapter.DirName, token);
            _currentImagePaths = result.Images;
            TotalImages = _currentImagePaths.Count;
            OnPropertyChanged(nameof(PageIndicator));

            if (_currentImagePaths.Count > 0)
            {
                var startIndex = _pendingImageIndex >= 0 && _pendingImageIndex < _currentImagePaths.Count
                    ? _pendingImageIndex : 0;
                _pendingImageIndex = -1;
                await ShowImageAsync(startIndex, token);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (BackendApiException ex)
        {
            PageError = ex.Error.Message;
        }
        catch (HttpRequestException)
        {
            PageError = "无法连接后端服务。";
        }
        catch (Exception ex)
        {
            PageError = $"加载图片异常: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanGoPrevious))]
    public async Task PreviousImageAsync(CancellationToken cancellationToken = default)
    {
        if (CurrentImageIndex > 0)
        {
            await ShowImageAsync(CurrentImageIndex - 1, cancellationToken);
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
            await ShowImageAsync(CurrentImageIndex + 1, cancellationToken);
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
        if (index < 0 || index >= _currentImagePaths.Count) return;

        IsLoading = true;
        try
        {
            var bytes = await _backendClient.GetImageBytesAsync(_currentImagePaths[index], cancellationToken);

            _dispatcherQueue.TryEnqueue(() =>
            {
                var bitmap = new BitmapImage();
                CurrentImage = bitmap;
                CurrentImageIndex = index;
                OnPropertyChanged(nameof(PageIndicator));
                OnPropertyChanged(nameof(HasPreviousImage));
                OnPropertyChanged(nameof(HasNextImage));
                OnPropertyChanged(nameof(HasPreviousImageOrChapter));
                OnPropertyChanged(nameof(HasNextImageOrChapter));
                PreviousImageCommand.NotifyCanExecuteChanged();
                NextImageCommand.NotifyCanExecuteChanged();

                using var stream = new MemoryStream(bytes);
                stream.Position = 0;
                bitmap.SetSource(stream.AsRandomAccessStream());
            });
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            PageError = $"加载图片失败: {ex.Message}";
        }
        finally
        {
            _dispatcherQueue.TryEnqueue(() => IsLoading = false);
        }
    }
}
