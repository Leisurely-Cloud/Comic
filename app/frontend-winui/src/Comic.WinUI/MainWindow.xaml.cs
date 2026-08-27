using System;
using System.Diagnostics;
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
    private const uint WmXButtonDown = 0x020B;
    private const uint WmAppCommand = 0x0319;
    private const ushort XButton1 = 1;
    private const ushort XButton2 = 2;
    private const int AppCommandBrowserBackward = 1;
    private const int AppCommandBrowserForward = 2;
    private static readonly UIntPtr MouseNavigationSubclassId = new(0x434F4D49);
    private readonly ApplicationSettingsService _applicationSettings;
    private readonly AppUpdateService _appUpdateService;
    private readonly SubclassProc _mouseNavigationSubclassProc;
    private IntPtr _windowHandle;
    private bool _mouseNavigationHookInstalled;

    public MainWindow()
    {
        InitializeComponent();
        _mouseNavigationSubclassProc = MouseNavigationWindowProc;
        ApplyWindowIcon();
        ApplyInitialWindowBounds();
        _applicationSettings = ((App)Application.Current).Services.GetRequiredService<ApplicationSettingsService>();
        _appUpdateService = ((App)Application.Current).Services.GetRequiredService<AppUpdateService>();
        _applicationSettings.ThemeChanged += OnThemeChanged;
        ApplyApplicationTheme();
        Title = "漫画下载器";
        RootGrid.Loaded += (_, _) => ApplyTitleBarTheme();
        RootGrid.ActualThemeChanged += (_, _) => ApplyTitleBarTheme();
        ApplyTitleBarTheme();
        Activated += OnWindowActivated;
        Closed += OnMainWindowClosed;

        // 延迟加载主内容
        _ = InitializeAsync();
    }

    private void OnWindowActivated(object sender, WindowActivatedEventArgs args)
    {
        if (_mouseNavigationHookInstalled) return;
        _windowHandle = WindowNative.GetWindowHandle(this);
        _mouseNavigationHookInstalled = SetWindowSubclass(
            _windowHandle,
            _mouseNavigationSubclassProc,
            MouseNavigationSubclassId,
            UIntPtr.Zero);
    }

    private void OnMainWindowClosed(object sender, WindowEventArgs args)
    {
        if (!_mouseNavigationHookInstalled || _windowHandle == IntPtr.Zero) return;
        RemoveWindowSubclass(_windowHandle, _mouseNavigationSubclassProc, MouseNavigationSubclassId);
        _mouseNavigationHookInstalled = false;
    }

    private IntPtr MouseNavigationWindowProc(
        IntPtr windowHandle,
        uint message,
        UIntPtr wParam,
        IntPtr lParam,
        UIntPtr subclassId,
        UIntPtr referenceData)
    {
        if (MainFrame.Content is ShellPage shellPage)
        {
            var handled = message switch
            {
                WmXButtonDown => ((ushort)((wParam.ToUInt64() >> 16) & 0xFFFF)) switch
                {
                    XButton1 => shellPage.TryNavigateBack(),
                    XButton2 => shellPage.TryNavigateForward(),
                    _ => false,
                },
                WmAppCommand => ((int)((lParam.ToInt64() >> 16) & 0x0FFF)) switch
                {
                    AppCommandBrowserBackward => shellPage.TryNavigateBack(),
                    AppCommandBrowserForward => shellPage.TryNavigateForward(),
                    _ => false,
                },
                _ => false,
            };
            if (handled) return IntPtr.Zero;
        }

        return DefSubclassProc(windowHandle, message, wParam, lParam);
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
        if (_applicationSettings.CheckUpdatesOnStartup) _ = CheckForUpdateOnStartupAsync();
    }

    private async Task CheckForUpdateOnStartupAsync()
    {
        try
        {
            var update = await _appUpdateService.CheckAsync();
            if (!update.IsUpdateAvailable) return;
            var notes = new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(update.ReleaseNotes) ? "此版本没有更新说明。" : update.ReleaseNotes,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 620,
            };
            var dialog = new ContentDialog
            {
                XamlRoot = RootGrid.XamlRoot,
                Title = $"发现新版本 {update.LatestVersion}",
                Content = new ScrollViewer { Content = notes, MaxHeight = 420 },
                PrimaryButtonText = string.IsNullOrWhiteSpace(update.AssetDownloadUrl) ? string.Empty : "下载安装包",
                CloseButtonText = "稍后",
                DefaultButton = ContentDialogButton.Close,
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            var path = await _appUpdateService.DownloadInstallerAsync(update);
            Process.Start("explorer.exe", $"/select,\"{path}\"");
            var completed = new ContentDialog
            {
                XamlRoot = RootGrid.XamlRoot,
                Title = "安装包已下载",
                Content = $"已保存到：\n{path}\n\n请手动运行安装包。应用不会自动安装或重启。",
                CloseButtonText = "知道了",
            };
            await completed.ShowAsync();
        }
        catch
        {
            // 启动检查失败不打断应用；用户可在设置页手动检查并查看错误。
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr windowHandle,
        int attribute,
        ref int attributeValue,
        int attributeSize);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr SubclassProc(
        IntPtr windowHandle,
        uint message,
        UIntPtr wParam,
        IntPtr lParam,
        UIntPtr subclassId,
        UIntPtr referenceData);

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(
        IntPtr windowHandle,
        SubclassProc subclassProc,
        UIntPtr subclassId,
        UIntPtr referenceData);

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveWindowSubclass(
        IntPtr windowHandle,
        SubclassProc subclassProc,
        UIntPtr subclassId);

    [DllImport("comctl32.dll")]
    private static extern IntPtr DefSubclassProc(
        IntPtr windowHandle,
        uint message,
        UIntPtr wParam,
        IntPtr lParam);
}
