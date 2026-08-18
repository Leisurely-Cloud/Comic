using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Comic.WinUI.Services;
using Comic.WinUI.ViewModels;
using Comic.WinUI.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinRT.Interop;

namespace Comic.WinUI;

public sealed partial class MainWindow : Window
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaUseImmersiveDarkModeBefore20H1 = 19;
    private readonly ApplicationSettingsService _applicationSettings;

    public MainWindow()
    {
        InitializeComponent();
        ApplyWindowIcon();
        ApplyInitialWindowBounds();
        _applicationSettings = ((App)Application.Current).Services.GetRequiredService<ApplicationSettingsService>();
        _applicationSettings.ThemeChanged += OnThemeChanged;
        ApplyApplicationTheme();
        Title = "漫画下载器";
        RootGrid.Loaded += (_, _) => ApplyTitleBarTheme();
        RootGrid.ActualThemeChanged += (_, _) => ApplyTitleBarTheme();
        ApplyTitleBarTheme();

        // 延迟加载主内容
        _ = InitializeAsync();
    }

    private void ApplyWindowIcon()
    {
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
        if (File.Exists(iconPath))
        {
            AppWindow.SetIcon(iconPath);
        }
    }

    private void ApplyInitialWindowBounds()
    {
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.Maximize();
        }
    }

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        DispatcherQueue.TryEnqueue(ApplyApplicationTheme);
    }

    private void ApplyApplicationTheme()
    {
        RootGrid.RequestedTheme = _applicationSettings.Theme switch
        {
            ApplicationSettingsService.LightTheme => ElementTheme.Light,
            ApplicationSettingsService.DarkTheme => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };
    }

    private void ApplyTitleBarTheme()
    {
        var windowHandle = WindowNative.GetWindowHandle(this);
        var useDarkMode = RootGrid.ActualTheme == ElementTheme.Dark ? 1 : 0;
        var result = DwmSetWindowAttribute(
            windowHandle,
            DwmwaUseImmersiveDarkMode,
            ref useDarkMode,
            sizeof(int));

        if (result != 0)
        {
            DwmSetWindowAttribute(
                windowHandle,
                DwmwaUseImmersiveDarkModeBefore20H1,
                ref useDarkMode,
                sizeof(int));
        }
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

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr windowHandle,
        int attribute,
        ref int attributeValue,
        int attributeSize);
}
