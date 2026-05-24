using System;
using System.Threading.Tasks;
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

        // 延迟加载主内容
        _ = InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        // 短暂延迟让启动画面显示
        await Task.Delay(800);

        // 加载主内容
        var shellViewModel = ((App)Application.Current).Services.GetRequiredService<ShellViewModel>();
        MainFrame.Navigate(typeof(ShellPage), shellViewModel);

        // 隐藏启动画面，显示主内容
        SplashScreen.Visibility = Visibility.Collapsed;
        MainFrame.Visibility = Visibility.Visible;
    }
}
