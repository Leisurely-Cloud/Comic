using CommunityToolkit.Mvvm.ComponentModel;

namespace Comic.WinUI.ViewModels;

public partial class ChapterItemViewModel : ObservableObject
{
    [ObservableProperty]
    public partial string Title { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsSelected { get; set; }
}
