using Comic.WinUI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Comic.WinUI.Views;

public sealed partial class DownloadPage
{
    private ContentDialog? _batchImportDialog;

    private async void OnBatchImportClick(object sender, RoutedEventArgs e)
    {
        if (_batchImportDialog is not null) return;
        var viewModel = ViewModel.BatchImport;
        var panel = new BatchImportPanel(viewModel);
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "批量导入漫画",
            Content = panel,
            CloseButtonText = "关闭",
            DefaultButton = ContentDialogButton.Close,
        };
        dialog.Resources["ContentDialogMaxWidth"] = 720d;
        _batchImportDialog = dialog;
        panel.OpenTasksRequested += (_, _) =>
        {
            viewModel.CancelOperation();
            dialog.Hide();
            SetWorkspace(showTasks: true);
        };
        dialog.Closing += (_, _) => viewModel.CancelOperation();
        try
        {
            await dialog.ShowAsync();
        }
        finally
        {
            viewModel.CancelOperation();
            _batchImportDialog = null;
        }
    }
}
