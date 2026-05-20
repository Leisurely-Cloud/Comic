using Comic.WinUI.ViewModels;
using Comic.WinUI.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;

namespace Comic.WinUI;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Title = "漫画下载器";

        var shellViewModel = ((App)Application.Current).Services.GetRequiredService<ShellViewModel>();
        Content = new ShellPage(shellViewModel);
    }
}
