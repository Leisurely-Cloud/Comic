using Comic.WinUI.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Comic.WinUI.Controls;

public sealed partial class BatchImportPanel : UserControl
{
    public event EventHandler? OpenTasksRequested;

    public BatchImportPanel(BatchImportViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    private void OnOpenTasksClick(object sender, RoutedEventArgs e) => OpenTasksRequested?.Invoke(this, EventArgs.Empty);
}
