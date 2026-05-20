using Comic.WinUI.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Comic.WinUI.Controls;

public sealed partial class MangaDetailCard : UserControl
{
    public static readonly DependencyProperty ViewModelProperty =
        DependencyProperty.Register(nameof(ViewModel), typeof(DownloadPageViewModel), typeof(MangaDetailCard), new PropertyMetadata(null));

    public DownloadPageViewModel ViewModel
    {
        get => (DownloadPageViewModel)GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }

    public MangaDetailCard()
    {
        InitializeComponent();
    }
}
