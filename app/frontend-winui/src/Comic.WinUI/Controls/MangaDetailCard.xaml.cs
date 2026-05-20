using Comic.WinUI.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Comic.WinUI.Controls;

public sealed partial class MangaDetailCard : UserControl
{
    public static readonly DependencyProperty ViewModelProperty =
        DependencyProperty.Register(nameof(ViewModel), typeof(DownloadPageViewModel), typeof(MangaDetailCard), new PropertyMetadata(null, OnViewModelChanged));

    public DownloadPageViewModel ViewModel
    {
        get => (DownloadPageViewModel)GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }

    public MangaDetailCard()
    {
        InitializeComponent();
    }

    private static void OnViewModelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is MangaDetailCard control && e.NewValue is DownloadPageViewModel vm)
        {
            control.StartDownloadBtn.Command = vm.StartDownloadCommand;
            control.PauseBtn.Command = vm.PauseCommand;
            control.ResumeBtn.Command = vm.ResumeCommand;
            control.StopBtn.Command = vm.StopCommand;
            control.SelectAllBtn.Command = vm.SelectAllChaptersCommand;
            control.DeselectAllBtn.Command = vm.DeselectAllChaptersCommand;
        }
    }
}
