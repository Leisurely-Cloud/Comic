using System;
using Comic.WinUI.Services;
using Comic.WinUI.Services.Native;
using Comic.WinUI.ViewModels;
using Comic.WinUI.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;

namespace Comic.WinUI;

public sealed partial class App : Application
{
    private Window? _window;

    public IServiceProvider Services { get; }
    public Window? MainWindow => _window;

    public static T GetService<T>() where T : class
    {
        if (Current is App app)
        {
            return app.Services.GetRequiredService<T>();
        }
        throw new InvalidOperationException("App is not initialized");
    }

    public App()
    {
        UnhandledException += OnUnhandledException;
        Services = ConfigureServices();
        InitializeComponent();
    }

    private static IServiceProvider ConfigureServices()
    {
        var services = new ServiceCollection();

        services.AddHttpClient<JmComicService>()
            .ConfigurePrimaryHttpMessageHandler(() => new System.Net.Http.SocketsHttpHandler
            {
                ConnectTimeout = TimeSpan.FromSeconds(5),
                AutomaticDecompression = System.Net.DecompressionMethods.All,
            });
        services.AddSingleton<NativeBackendService>();
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
        services.AddTransient<RankingPageViewModel>();

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
        System.Diagnostics.Debug.WriteLine($"Unhandled exception: {e.Exception}");
    }
}
