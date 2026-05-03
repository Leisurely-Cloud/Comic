using System.Collections.ObjectModel;

namespace Comic.WinUI.ViewModels;

public sealed class ShellNavigationItem
{
    public string Tag { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string Glyph { get; set; } = string.Empty;
}

public sealed class ShellNavigationViewModel
{
    public ObservableCollection<ShellNavigationItem> Items { get; } =
    [
        new() { Tag = "download", Label = "搜索下载", Glyph = "" },
        new() { Tag = "library", Label = "本地漫画库", Glyph = "" },
        new() { Tag = "settings", Label = "应用设置", Glyph = "" },
    ];
}
