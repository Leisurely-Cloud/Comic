using System;
using System.IO;
using Comic.WinUI.Services;
using Comic.WinUI.Services.Logging;
using Comic.WinUI.Services.Native;
using Comic.WinUI.ViewModels;
using Comic.WinUI.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;

namespace Comic.WinUI;

public sealed partial class App : Application
{
    private Window? _window;

    public IServiceProvider Services { get; }
    public Window? MainWindow => _window;

    public App()
    {
        UnhandledException += OnUnhandledException;
        Services = ConfigureServices();
        InitializeComponent();
    }

    private static IServiceProvider ConfigureServices()
    {
        var services = new ServiceCollection();

        var logDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Comic.WinUI",
            "logs");
        services.AddLogging(builder => builder.AddProvider(new FileLoggerProvider(logDirectory)));

        services.AddSingleton(new JmSiteOptions());
        // ConfigureServices 由 App 构造函数在 UI 线程上调用,所以这里能安全捕获
        // DispatcherQueue。ViewModel 不再各自去 GetForCurrentThread(),否则它们
        // 在非 UI 线程(单元测试)里就构造不出来。
        services.AddSingleton<IDispatcher>(UiThreadDispatcher.CreateForCurrentThread());
        // JmComicService 必须是单例:它持有专辑缓存和“当前可用 API 域名”的学习结果。
        // 注册成 AddHttpClient 的 typed client 会让它变成 transient,于是 BackendClient 和
        // DownloadSchedulerService 各捕获一份,缓存与域名故障转移的成果互相看不到。
        // 单例长期持有 HttpClient,用 PooledConnectionLifetime 定期回收连接以避免 DNS 固化。
        services.AddSingleton(provider => new JmComicService(
            new System.Net.Http.HttpClient(new System.Net.Http.SocketsHttpHandler
            {
                ConnectTimeout = TimeSpan.FromSeconds(5),
                AutomaticDecompression = System.Net.DecompressionMethods.All,
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                // JM 登录会话由 JmComicService 仅在内存中管理，退出时可立即彻底清除。
                UseCookies = false,
            }),
            provider.GetRequiredService<JmSiteOptions>(),
            provider.GetRequiredService<ILogger<JmComicService>>()));
        services.AddSingleton<LibraryStorageService>();
        services.AddSingleton<DownloadSchedulerService>();
        services.AddSingleton<CbzExportService>();
        services.AddSingleton<ReaderService>();
        services.AddSingleton<BackendClient>();
        services.AddSingleton<DownloadEventStream>();
        services.AddSingleton<SearchHistoryService>();
        services.AddSingleton<ApplicationSettingsService>();
        services.AddSingleton<IJmCredentialStore, WindowsJmCredentialStore>();
        services.AddSingleton<ReadingProgressService>();
        services.AddSingleton<ShellViewModel>();
        services.AddTransient<DownloadPageViewModel>();
        services.AddTransient<LibraryPageViewModel>();
        services.AddTransient<SettingsPageViewModel>();
        services.AddSingleton<FavoritesPageViewModel>();
        services.AddSingleton<UpdateCenterPageViewModel>();
        services.AddTransient<ReaderPageViewModel>();
        services.AddSingleton<RankingPageViewModel>();
        services.AddSingleton<WeeklyPicksPageViewModel>();

        return services.BuildServiceProvider();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        _window.Closed += OnWindowClosed;
        _window.Activate();
    }

    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        if (Services is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        try
        {
            var logger = Services.GetService<ILoggerFactory>()?.CreateLogger("App");
            logger?.LogCritical(e.Exception, "未处理的界面异常");
        }
        catch
        {
            // 日志本身失败时不再抛出，避免二次崩溃。
        }
    }
}
