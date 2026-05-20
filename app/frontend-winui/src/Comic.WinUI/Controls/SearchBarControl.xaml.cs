using Comic.WinUI.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Comic.WinUI.Controls;

public sealed partial class SearchBarControl : UserControl
{
    public static readonly DependencyProperty ViewModelProperty =
        DependencyProperty.Register(nameof(ViewModel), typeof(DownloadPageViewModel), typeof(SearchBarControl), new PropertyMetadata(null, OnViewModelChanged));

    public DownloadPageViewModel ViewModel
    {
        get => (DownloadPageViewModel)GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }

    public SearchBarControl()
    {
        InitializeComponent();
    }

    private static void OnViewModelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is SearchBarControl control && e.NewValue is DownloadPageViewModel vm)
        {
            control.SearchButton.Command = vm.SearchCommand;
            control.BackendButton.Command = vm.EnsureBackendRunningCommand;
            control.RefreshButton.Command = vm.RefreshBackendCommand;
        }
    }

    private void OnSearchKeywordChanged(object sender, TextChangedEventArgs e)
    {
        if (ViewModel is not null)
        {
            ViewModel.SearchKeyword = SearchKeywordBox.Text;
        }
    }
}
