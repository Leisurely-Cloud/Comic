namespace Comic.WinUI.Models;

public sealed record BatchImportInput(string Input, string? MangaId, string Error);

public sealed record BatchImportParseResult(
    IReadOnlyList<BatchImportInput> Items,
    int InputCount,
    int DuplicateCount,
    bool ExceedsLimit);

public sealed record BatchImportSubmissionResult(DownloadTaskDto Task, bool AlreadyQueued = false);

public sealed record BatchImportReceipt(string MangaId, string Title, string TaskId, string Message);
