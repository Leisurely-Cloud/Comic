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
        services.AddHttpClient<JmComicService>()
            .ConfigurePrimaryHttpMessageHandler(() => new System.Net.Http.SocketsHttpHandler
            {
                ConnectTimeout = TimeSpan.FromSeconds(5),
                AutomaticDecompression = System.Net.DecompressionMethods.All,
            });
        services.AddSingleton<LibraryStorageService>();
        services.AddSingleton<DownloadSchedulerService>();
        services.AddSingleton<CbzExportService>();
        services.AddSingleton<ReaderService>();
        services.AddSingleton<BackendClient>();
        services.AddSingleton<DownloadEventStream>();
        services.AddSingleton<SearchHistoryService>();
        services.AddSingleton<ApplicationSettingsService>();
        services.AddSingleton<ReadingProgressService>();
        services.AddSingleton<ShellViewModel>();
        services.AddTransient<DownloadPageViewModel>();
        services.AddTransient<LibraryPageViewModel>();
        services.AddTransient<SettingsPageViewModel>();
        services.AddTransient<ReaderPageViewModel>();
        services.AddSingleton<RankingPageViewModel>();

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
