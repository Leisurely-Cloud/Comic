using System;
using Comic.WinUI.Services;
using Comic.WinUI.ViewModels;
using Comic.WinUI.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;

namespace Comic.WinUI;

public sealed partial class App : Application
{
    private Window? _window;

    public IServiceProvider Services { get; }

    public App()
    {
        UnhandledException += OnUnhandledException;
        Services = ConfigureServices();
        InitializeComponent();
    }

    private static IServiceProvider ConfigureServices()
    {
        var services = new ServiceCollection();

        services.AddSingleton<BackendSettingsService>();
        services.AddHttpClient("backend")
            .ConfigurePrimaryHttpMessageHandler(() => new System.Net.Http.SocketsHttpHandler
            {
                ConnectTimeout = TimeSpan.FromSeconds(5),
            })
            .ConfigureHttpClient(client => client.Timeout = TimeSpan.FromSeconds(30));
        services.AddSingleton<BackendClient>(provider =>
        {
            var settings = provider.GetRequiredService<BackendSettingsService>().GetSettings();
            var factory = provider.GetRequiredService<System.Net.Http.IHttpClientFactory>();
            var httpClient = factory.CreateClient("backend");
            return new BackendClient(httpClient, settings.BackendBaseUrl);
        });
        services.AddSingleton<BackendProcessService>();
        services.AddSingleton<DownloadEventStream>();
        services.AddSingleton<ShellViewModel>();
        services.AddTransient<DownloadPageViewModel>();
        services.AddTransient<LibraryPageViewModel>();
        services.AddTransient<SettingsPageViewModel>();

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
        var shellViewModel = Services.GetRequiredService<ShellViewModel>();
        shellViewModel.StopBackendCommand.Execute(null);
    }

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        System.Diagnostics.Debug.WriteLine($"Unhandled exception: {e.Exception}");
    }
}
