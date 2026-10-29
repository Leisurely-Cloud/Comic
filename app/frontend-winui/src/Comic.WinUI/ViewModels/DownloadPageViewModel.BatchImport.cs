using Comic.WinUI.Models;

namespace Comic.WinUI.ViewModels;

public partial class DownloadPageViewModel
{
    private BatchImportViewModel? _batchImport;
    private bool _downloadPageDisposed;

    public BatchImportViewModel BatchImport => _batchImport ??= new(
        _backendClient.ResolveMangaAsync,
        SubmitBatchDownloadAsync,
        OnBatchTaskAccepted);

    private async Task<BatchImportSubmissionResult> SubmitBatchDownloadAsync(
        DownloadCreateRequest request, CancellationToken cancellationToken)
    {
        // The scheduler compares both manga ID and requested chapter selections.
        // Matching only a manga ID would silently discard the remaining chapters
        // when an existing task handles only part of this manga.
        var downloads = await _backendClient.GetDownloadsAsync(cancellationToken);
        var created = await _backendClient.CreateDownloadAsync(request, cancellationToken);
        return new(created, downloads.Items.Any(task => task.Id == created.Id));
    }

    private void OnBatchTaskAccepted(DownloadTaskDto task)
    {
        if (_downloadPageDisposed) return;
        UpsertTask(task);
        ApplyDownloadNotice(task);
        CurrentTask = Tasks.FirstOrDefault(item => item.Id == task.Id) ?? SelectPreferredTask();
        CurrentTaskId = task.Id;
        OnPropertyChanged(nameof(TaskCountSummary));
        StartTaskSubscription(task.Id);
    }
}
