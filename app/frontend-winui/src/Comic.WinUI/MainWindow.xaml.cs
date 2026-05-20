using Comic.WinUI.ViewModels;
using Comic.WinUI.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Comic.WinUI;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Title = "漫画下载器";

        var frame = new Frame();
        Content = frame;
        var shellViewModel = ((App)Application.Current).Services.GetRequiredService<ShellViewModel>();
        frame.Navigate(typeof(ShellPage), shellViewModel);
    }
}
