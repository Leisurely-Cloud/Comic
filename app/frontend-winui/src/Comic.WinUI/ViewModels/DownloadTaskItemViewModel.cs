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
    public partial string Status { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string StatusText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial double Progress { get; set; }

    [ObservableProperty]
    public partial ApiError? TaskError { get; set; }

    public string ProgressText => $"{Progress:0}%";

    public bool HasTaskError => TaskError is not null && !string.IsNullOrWhiteSpace(TaskError.Message);

    public string ErrorSummary => TaskError?.Message ?? string.Empty;

    public string SiteLabel => string.IsNullOrWhiteSpace(SiteKey) ? "-" : SiteCatalog.GetDisplayName(SiteKey);

    public string LatestLogMessage => Logs.LastOrDefault()?.Message ?? "暂无日志";

    public ObservableCollection<DownloadLogEntry> Logs { get; } = [];

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
        Status = dto.Status;
        StatusText = dto.StatusText;
        Progress = dto.Progress;
        TaskError = dto.TaskError;

        Logs.Clear();
        if (dto.Logs is not null)
        {
            foreach (var entry in dto.Logs)
            {
                Logs.Add(entry);
            }
        }

        OnPropertyChanged(nameof(ProgressText));
        OnPropertyChanged(nameof(HasTaskError));
        OnPropertyChanged(nameof(ErrorSummary));
        OnPropertyChanged(nameof(SiteLabel));
        OnPropertyChanged(nameof(LatestLogMessage));
    }
}
