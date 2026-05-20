using Comic.WinUI.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Comic.WinUI.Controls;

public sealed partial class SearchResultsList : UserControl
{
    public static readonly DependencyProperty ViewModelProperty =
        DependencyProperty.Register(nameof(ViewModel), typeof(DownloadPageViewModel), typeof(SearchResultsList), new PropertyMetadata(null));

    public DownloadPageViewModel ViewModel
    {
        get => (DownloadPageViewModel)GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }

    public SearchResultsList()
    {
        InitializeComponent();
    }
}
