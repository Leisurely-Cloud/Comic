using System.Collections.ObjectModel;
using System.ComponentModel;
using Comic.WinUI.Models;
using Comic.WinUI.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Comic.WinUI.ViewModels;

public sealed class BatchImportItemViewModel : ObservableObject
{
    private bool _isSelected;
    private bool _isResolved;
    private bool _isSubmitted;
    private string _title = string.Empty;
    private string _statusText;
    private int _chapterCount;

    public BatchImportItemViewModel(BatchImportInput input)
    {
        Input = input.Input;
        MangaId = input.MangaId;
        _statusText = input.Error.Length > 0 ? input.Error : "待解析";
    }

    public string Input { get; }
    public string? MangaId { get; }
    public MangaResolveResponse? Manga { get; internal set; }
    public bool IsSelected { get => _isSelected; set => SetProperty(ref _isSelected, value); }
    public bool IsResolved
    {
        get => _isResolved;
        internal set { if (SetProperty(ref _isResolved, value)) OnPropertyChanged(nameof(CanSelect)); }
    }
    public bool IsSubmitted
    {
        get => _isSubmitted;
        internal set { if (SetProperty(ref _isSubmitted, value)) OnPropertyChanged(nameof(CanSelect)); }
    }
    public bool CanSelect => IsResolved && !IsSubmitted;
    public string Title
    {
        get => _title;
        internal set { if (SetProperty(ref _title, value)) OnPropertyChanged(nameof(DisplayTitle)); }
    }
    public string DisplayTitle => Title.Length > 0 ? Title : MangaId is null ? Input : $"JM {MangaId}";
    public string IdentityText => MangaId is null ? Input : $"JM {MangaId} · {ChapterCount} 章";
    public int ChapterCount
    {
        get => _chapterCount;
        internal set { if (SetProperty(ref _chapterCount, value)) OnPropertyChanged(nameof(IdentityText)); }
    }
    public string StatusText { get => _statusText; internal set => SetProperty(ref _statusText, value); }
}

/// <summary>
/// A two-phase import: preview resolves metadata; only SubmitSelectedAsync creates tasks.
/// Each operation has a generation so an ignored cancellation cannot publish old metadata.
/// Successful task receipts are retained independently of the editable preview.
/// </summary>
public sealed partial class BatchImportViewModel : ObservableObject, IDisposable
{
    private readonly Func<MangaResolveRequest, CancellationToken, Task<MangaResolveResponse>> _resolve;
    private readonly Func<DownloadCreateRequest, CancellationToken, Task<BatchImportSubmissionResult>> _submit;
    private readonly Action<DownloadTaskDto>? _taskAccepted;
    private readonly Dictionary<string, BatchImportReceipt> _accepted = new(StringComparer.Ordinal);
    private readonly HashSet<string> _inFlight = new(StringComparer.Ordinal);
    private CancellationTokenSource? _operationCts;
    private long _operationVersion;
    private bool _disposed;
    private bool _isPreviewing;
    private bool _isSubmitting;
    private bool _previewValid;
    private string _inputText = string.Empty;
    private string _statusText = "粘贴后先解析预览，确认后再加入下载队列。";
    private BatchImportParseResult _parseResult = BatchImportParser.Parse(string.Empty);

    public BatchImportViewModel(
        Func<MangaResolveRequest, CancellationToken, Task<MangaResolveResponse>> resolve,
        Func<DownloadCreateRequest, CancellationToken, Task<BatchImportSubmissionResult>> submit,
        Action<DownloadTaskDto>? taskAccepted = null)
    {
        _resolve = resolve;
        _submit = submit;
        _taskAccepted = taskAccepted;
    }

    public ObservableCollection<BatchImportItemViewModel> Items { get; } = [];
    public ObservableCollection<BatchImportReceipt> SubmittedItems { get; } = [];

    public string InputText
    {
        get => _inputText;
        set
        {
            if (_disposed || !SetProperty(ref _inputText, value ?? string.Empty)) return;
            CancelOperation();
            _previewValid = false;
            foreach (var item in Items) item.PropertyChanged -= OnItemChanged;
            Items.Clear();
            _parseResult = BatchImportParser.Parse(_inputText);
            foreach (var input in _parseResult.Items)
            {
                var item = new BatchImportItemViewModel(input);
                if (item.MangaId is not null && _accepted.TryGetValue(item.MangaId, out var receipt))
                {
                    item.Title = receipt.Title;
                    item.IsSubmitted = true;
                    item.StatusText = receipt.Message;
                }
                item.PropertyChanged += OnItemChanged;
                Items.Add(item);
            }
            StatusText = _parseResult.ExceedsLimit
                ? "输入超出限制，请减少到 100 条以内后再解析。"
                : "输入已更新，请重新解析预览。无效条目不会提交。";
            NotifyState();
        }
    }

    public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }
    public string InputSummary => _parseResult.ExceedsLimit
        ? $"已输入 {_parseResult.InputCount} 条，最多 {BatchImportParser.MaximumItems} 条；文本最多 {BatchImportParser.MaximumInputLength:N0} 字符。"
        : $"输入 {_parseResult.InputCount} 条 · 有效 {Items.Count(item => item.MangaId is not null)} 部 · 重复 {_parseResult.DuplicateCount} 条 · 无效 {Items.Count(item => item.MangaId is null)} 条";
    public bool IsBusy => _isPreviewing || _isSubmitting;
    public bool IsIdle => !IsBusy;
    public bool CanPreview => !_disposed && !IsBusy && !_parseResult.ExceedsLimit
        && Items.Any(item => item.MangaId is not null && !item.IsSubmitted);
    public bool CanSubmit => !_disposed && !IsBusy && _previewValid && Items.Any(CanSubmitItem);
    public bool CanCancel => IsBusy;
    public bool HasSubmittedItems => SubmittedItems.Count > 0;
    public string SelectionSummary => $"已勾选 {Items.Count(CanSubmitItem)} 部 · 本次会话已处理 {SubmittedItems.Count} 部";

    [RelayCommand(CanExecute = nameof(CanPreview), AllowConcurrentExecutions = true)]
    public async Task PreviewAsync(CancellationToken cancellationToken = default)
    {
        if (!CanPreview) return;
        var (version, source) = StartOperation(cancellationToken);
        var token = source.Token;
        _previewValid = false;
        _isPreviewing = true;
        NotifyState();
        var candidates = Items.Where(item => item.MangaId is not null && !item.IsSubmitted).ToArray();
        try
        {
            for (var index = 0; index < candidates.Length; index++)
            {
                var item = candidates[index];
                if (!IsCurrent(version, token)) return;
                item.IsResolved = false;
                item.IsSelected = false;
                item.StatusText = "正在解析…";
                StatusText = $"正在解析 {index + 1} / {candidates.Length}，尚未创建下载任务。";
                try
                {
                    var manga = await _resolve(new() { Url = item.MangaId!, SiteKey = SiteCatalog.Key }, token);
                    if (!IsCurrent(version, token)) return;
                    if (!string.Equals(manga.MangaId.TrimStart('0'), item.MangaId, StringComparison.Ordinal))
                        throw new InvalidOperationException("站点返回的漫画编号与输入不一致，请检查输入后重试。");
                    item.Manga = manga;
                    item.Title = manga.Title;
                    item.ChapterCount = manga.Chapters.Count;
                    item.IsResolved = manga.Chapters.Count > 0;
                    item.IsSelected = item.IsResolved;
                    item.StatusText = item.IsResolved ? "解析成功，等待确认入队" : "没有可下载章节，请稍后重新解析。";
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
                catch (Exception ex)
                {
                    if (!IsCurrent(version, token)) return;
                    item.StatusText = $"解析失败：{GetError(ex)}";
                }
            }
            if (!IsCurrent(version, token)) return;
            _previewValid = true;
            StatusText = $"预览完成：成功 {Items.Count(item => item.IsResolved)} 部；确认勾选后加入下载队列。";
        }
        finally
        {
            FinishOperation(version, source);
        }
    }

    [RelayCommand(CanExecute = nameof(CanSubmit), AllowConcurrentExecutions = true)]
    public async Task SubmitSelectedAsync(CancellationToken cancellationToken = default)
    {
        if (!CanSubmit) return;
        var candidates = Items.Where(CanSubmitItem).ToArray();
        var (version, source) = StartOperation(cancellationToken);
        var token = source.Token;
        _isSubmitting = true;
        NotifyState();
        try
        {
            for (var index = 0; index < candidates.Length; index++)
            {
                if (!IsCurrent(version, token)) return;
                var item = candidates[index];
                if (_accepted.ContainsKey(item.MangaId!) || !_inFlight.Add(item.MangaId!)) continue;
                item.StatusText = "正在加入下载队列…";
                StatusText = $"正在处理 {index + 1} / {candidates.Length}；取消会保留已创建的任务。";
                try
                {
                    var manga = item.Manga!;
                    var result = await _submit(new()
                    {
                        Url = string.IsNullOrWhiteSpace(manga.MangaUrl) ? item.MangaId! : manga.MangaUrl,
                        SiteKey = SiteCatalog.Key,
                        Source = "winui",
                        Chapters = manga.Chapters.Select(chapter =>
                            string.IsNullOrWhiteSpace(chapter.Url) ? chapter.Title : chapter.Url).ToList(),
                    }, token);

                    // A returned task is an actual accepted operation, even if cancellation raced
                    // with the response. Keep its receipt so retry never creates it a second time.
                    if (_disposed) return;
                    var message = result.AlreadyQueued
                        ? "已在任务列表中，沿用现有任务，无需重复入队。"
                        : result.Task.Status == "completed" && result.Task.LocalSkippedChapterCount > 0
                            ? "本地已完整下载，已跳过重复下载。"
                            : result.Task.LocalSkippedChapterCount > 0
                                ? $"已加入队列；本地已有 {result.Task.LocalSkippedChapterCount} 章，仅补下载缺失章节。"
                                : "已加入下载队列；本地已有章节会自动跳过。";
                    var receipt = new BatchImportReceipt(item.MangaId!, item.Title, result.Task.Id, message);
                    _accepted[item.MangaId!] = receipt;
                    SubmittedItems.Add(receipt);
                    foreach (var current in Items.Where(current => current.MangaId == item.MangaId))
                    {
                        current.IsSelected = false;
                        current.IsSubmitted = true;
                        current.StatusText = message;
                    }
                    _taskAccepted?.Invoke(result.Task);
                    NotifyState();
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    if (IsCurrent(version, token, allowCancellation: true))
                        item.StatusText = "提交已取消；已创建的任务仍保留在任务列表。";
                    return;
                }
                catch (Exception ex)
                {
                    if (!IsCurrent(version, token)) return;
                    item.StatusText = $"入队失败：{GetError(ex)}；可保留勾选后重试。";
                }
                finally
                {
                    _inFlight.Remove(item.MangaId!);
                    if (!_disposed) NotifyState();
                }
            }
            if (IsCurrent(version, token))
                StatusText = $"处理完成：本次会话已处理 {SubmittedItems.Count} 部。失败项可再次加入队列，成功项不会重复提交。";
        }
        finally
        {
            FinishOperation(version, source);
        }
    }

    [RelayCommand(CanExecute = nameof(CanCancel))]
    public void CancelOperation()
    {
        _operationVersion++;
        _operationCts?.Cancel();
        _operationCts = null;
        if (_isPreviewing)
        {
            _previewValid = Items.Any(item => item.IsResolved);
            foreach (var item in Items.Where(item => item.MangaId is not null && !item.IsResolved && !item.IsSubmitted))
                item.StatusText = "解析已取消，可重新解析。";
        }
        else if (_isSubmitting)
        {
            foreach (var item in Items.Where(item => item.StatusText == "正在加入下载队列…"))
                item.StatusText = "提交已取消；已创建的任务仍保留在任务列表。";
        }
        if (IsBusy) StatusText = "已取消。已解析结果和已创建任务已保留。";
        _isPreviewing = false;
        _isSubmitting = false;
        if (!_disposed) NotifyState();
    }

    public void Dispose()
    {
        if (_disposed) return;
        CancelOperation();
        _disposed = true;
        foreach (var item in Items) item.PropertyChanged -= OnItemChanged;
        NotifyState();
    }

    private bool CanSubmitItem(BatchImportItemViewModel item) => item.IsSelected && item.IsResolved
        && !item.IsSubmitted && item.MangaId is not null && !_inFlight.Contains(item.MangaId);

    private (long Version, CancellationTokenSource Source) StartOperation(CancellationToken cancellationToken)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _operationCts = source;
        return (++_operationVersion, source);
    }

    private bool IsCurrent(long version, CancellationToken token, bool allowCancellation = false) =>
        !_disposed && version == _operationVersion && (allowCancellation || !token.IsCancellationRequested);

    private void FinishOperation(long version, CancellationTokenSource source)
    {
        if (!_disposed && version == _operationVersion && source.IsCancellationRequested)
            CancelOperation();
        source.Dispose();
        if (_disposed || version != _operationVersion) return;
        _operationCts = null;
        _isPreviewing = false;
        _isSubmitting = false;
        NotifyState();
    }

    private void OnItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(BatchImportItemViewModel.IsSelected)
            or nameof(BatchImportItemViewModel.IsResolved) or nameof(BatchImportItemViewModel.IsSubmitted))
            NotifyState();
    }

    private void NotifyState()
    {
        OnPropertyChanged(nameof(InputSummary));
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(IsIdle));
        OnPropertyChanged(nameof(CanPreview));
        OnPropertyChanged(nameof(CanSubmit));
        OnPropertyChanged(nameof(CanCancel));
        OnPropertyChanged(nameof(HasSubmittedItems));
        OnPropertyChanged(nameof(SelectionSummary));
        PreviewCommand.NotifyCanExecuteChanged();
        SubmitSelectedCommand.NotifyCanExecuteChanged();
        CancelOperationCommand.NotifyCanExecuteChanged();
    }

    private static string GetError(Exception exception) => exception is BackendApiException backend
        ? backend.Error.Message : exception.Message;
}
