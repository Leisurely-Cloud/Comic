using System.Collections.ObjectModel;
using System.Linq;
using Comic.WinUI.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Comic.WinUI.ViewModels;

public partial class DownloadTaskItemViewModel : ObservableObject
{
    [ObservableProperty]
    public partial string Id { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Url { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string SiteKey { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string MangaTitle { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Status { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string StatusText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial double Progress { get; set; }

    [ObservableProperty]
    public partial double DownloadSpeedBytesPerSecond { get; set; }

    [ObservableProperty]
    public partial ApiError? TaskError { get; set; }

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    [ObservableProperty]
    public partial bool IsBatchMode { get; set; }

    public string ProgressText => $"{Progress:0}%";

    public string DownloadSpeedText =>
        $"{Math.Max(DownloadSpeedBytesPerSecond, 0) / 1024d / 1024d:0.00} MB/s";

    public string DisplayName => string.IsNullOrWhiteSpace(MangaTitle) ? Url : MangaTitle;

    public string StatusLabel => Status switch
    {
        "pending" => "等待中",
        "running" => "下载中",
        "paused" => "已暂停",
        "pausing" => "暂停中",
        "stopping" => "停止中",
        "stopped" => "已停止",
        "completed" => "已完成",
        "partial" => "部分完成",
        "failed" => "失败",
        _ => Status,
    };

    public bool HasTaskError => TaskError is not null && !string.IsNullOrWhiteSpace(TaskError.Message);

    public string ErrorSummary => TaskError?.Message ?? string.Empty;

    public bool CanRetryFailures => Status is "failed" or "partial" &&
                                    (HasTaskError || Chapters.Any(chapter => chapter.Status == "failed"));

    public string FailureReasonSummary
    {
        get
        {
            var chapterReasons = string.Join("；", Chapters
                .Where(chapter => chapter.Status == "failed" && chapter.HasError)
                .Take(3)
                .Select(chapter => $"{chapter.Title}：{chapter.Error}"));
            return string.IsNullOrWhiteSpace(chapterReasons) ? ErrorSummary : chapterReasons;
        }
    }

    public bool HasFailureReasonSummary => !string.IsNullOrWhiteSpace(FailureReasonSummary);

    public string SiteLabel => string.IsNullOrWhiteSpace(SiteKey) ? "-" : SiteCatalog.GetDisplayName(SiteKey);

    public string LatestLogMessage => Logs.LastOrDefault()?.Message ?? "暂无日志";

    public ObservableCollection<DownloadLogEntry> Logs { get; } = [];

    public ObservableCollection<DownloadChapterProgressItemViewModel> Chapters { get; } = [];

    public bool HasChapters => Chapters.Count > 0;

    public int SelectedChapterCount => Chapters.Count(chapter => chapter.IsSelected);

    public bool HasSelectedChapters => SelectedChapterCount > 0;

    public static DownloadTaskItemViewModel FromDto(DownloadTaskDto dto)
    {
        var vm = new DownloadTaskItemViewModel();
        vm.UpdateFrom(dto);
        return vm;
    }

    public void UpdateFrom(DownloadTaskDto dto)
    {
        Id = dto.Id;
        Url = dto.Url;
        SiteKey = dto.SiteKey;
        MangaTitle = dto.MangaTitle;
        Status = dto.Status;
        StatusText = dto.StatusText;
        Progress = dto.Progress;
        DownloadSpeedBytesPerSecond = dto.DownloadSpeedBytesPerSecond;
        TaskError = dto.TaskError;

        // 日志是追加型的。整表 Clear + 重填会让绑定的 ListView 每秒重建 6~7 次,
        // 所以只追加新增的尾部;仅在日志被截断(变短)时才整体重建。
        var logEntries = dto.Logs ?? [];
        if (Logs.Count > logEntries.Count)
        {
            Logs.Clear();
            foreach (var entry in logEntries) Logs.Add(entry);
        }
        else
        {
            for (var index = Logs.Count; index < logEntries.Count; index++)
            {
                Logs.Add(logEntries[index]);
            }
        }

        var chapterDtos = dto.Chapters ?? [];
        var liveChapterIds = chapterDtos.Select(chapter => chapter.Id).ToHashSet(StringComparer.Ordinal);
        for (var index = Chapters.Count - 1; index >= 0; index--)
        {
            if (!liveChapterIds.Contains(Chapters[index].Id))
            {
                Chapters[index].PropertyChanged -= OnChapterPropertyChanged;
                Chapters.RemoveAt(index);
            }
        }

        // 建一次索引再查。原来在 chapterDtos 循环里对 Chapters 做 FirstOrDefault,
        // 是 O(n²):400 章的漫画每次轮询要做十几万次字符串比较,而轮询是 150ms 一次。
        var existingById = Chapters.ToDictionary(item => item.Id, StringComparer.Ordinal);
        foreach (var chapter in chapterDtos)
        {
            if (existingById.TryGetValue(chapter.Id, out var existing))
            {
                existing.UpdateFrom(chapter);
            }
            else
            {
                var item = DownloadChapterProgressItemViewModel.FromDto(chapter);
                item.IsBatchMode = IsBatchMode;
                item.PropertyChanged += OnChapterPropertyChanged;
                Chapters.Add(item);
            }
        }

        OnPropertyChanged(nameof(ProgressText));
        OnPropertyChanged(nameof(DownloadSpeedText));
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(StatusLabel));
        OnPropertyChanged(nameof(HasTaskError));
        OnPropertyChanged(nameof(ErrorSummary));
        OnPropertyChanged(nameof(CanRetryFailures));
        OnPropertyChanged(nameof(FailureReasonSummary));
        OnPropertyChanged(nameof(HasFailureReasonSummary));
        OnPropertyChanged(nameof(SiteLabel));
        OnPropertyChanged(nameof(LatestLogMessage));
        OnPropertyChanged(nameof(HasChapters));
        OnPropertyChanged(nameof(SelectedChapterCount));
        OnPropertyChanged(nameof(HasSelectedChapters));
    }

    partial void OnIsBatchModeChanged(bool value)
    {
        foreach (var chapter in Chapters)
        {
            chapter.IsBatchMode = value;
            if (!value)
            {
                chapter.IsSelected = false;
            }
        }
    }

    private void OnChapterPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DownloadChapterProgressItemViewModel.IsSelected))
        {
            OnPropertyChanged(nameof(SelectedChapterCount));
            OnPropertyChanged(nameof(HasSelectedChapters));
        }
    }
}

public partial class DownloadChapterProgressItemViewModel : ObservableObject
{
    [ObservableProperty]
    public partial string Id { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Title { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Status { get; set; } = "pending";

    [ObservableProperty]
    public partial int CompletedImages { get; set; }

    [ObservableProperty]
    public partial int TotalImages { get; set; }

    [ObservableProperty]
    public partial double Progress { get; set; }

    [ObservableProperty]
    public partial string Error { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string DirectoryName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    [ObservableProperty]
    public partial bool IsBatchMode { get; set; }

    public string StatusLabel => Status switch
    {
        "pending" => "等待",
        "running" => "下载中",
        "completed" => "已完成",
        "failed" => "失败",
        "stopped" => "已停止",
        _ => Status,
    };

    public string ImageProgressText => TotalImages > 0
        ? $"{CompletedImages}/{TotalImages} 页"
        : Status switch
        {
            "pending" => "等待下载",
            "failed" => "下载失败",
            "stopped" => "已停止",
            _ => "正在获取页数",
        };

    public string ProgressText => $"{Progress:0}%";

    public bool HasError => !string.IsNullOrWhiteSpace(Error);

    public static DownloadChapterProgressItemViewModel FromDto(DownloadChapterProgressDto dto)
    {
        var vm = new DownloadChapterProgressItemViewModel();
        vm.UpdateFrom(dto);
        return vm;
    }

    public void UpdateFrom(DownloadChapterProgressDto dto)
    {
        Id = dto.Id;
        Title = dto.Title;
        Status = dto.Status;
        CompletedImages = dto.CompletedImages;
        TotalImages = dto.TotalImages;
        Progress = dto.Progress;
        Error = dto.Error;
        DirectoryName = dto.DirectoryName;

        OnPropertyChanged(nameof(StatusLabel));
        OnPropertyChanged(nameof(ImageProgressText));
        OnPropertyChanged(nameof(ProgressText));
        OnPropertyChanged(nameof(HasError));
    }
}
