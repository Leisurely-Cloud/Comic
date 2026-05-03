using System;
using System.Net.Http;
using Comic.WinUI.Services;
using Comic.WinUI.ViewModels;
using Comic.WinUI.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;

namespace Comic.WinUI;

public partial class App : Application
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
        services.AddSingleton<HttpClient>(new HttpClient(new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(5),
        })
        {
            Timeout = TimeSpan.FromSeconds(30),
        });
        services.AddSingleton<BackendClient>(provider =>
        {
            var settings = provider.GetRequiredService<BackendSettingsService>().GetSettings();
            var httpClient = provider.GetRequiredService<HttpClient>();
            return new BackendClient(httpClient, settings.BackendBaseUrl);
        });
        services.AddSingleton<BackendProcessService>();
        services.AddSingleton<NavigationService>();
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
        if (_window?.Content is ShellPage shellPage)
        {
            shellPage.ViewModel.StopBackendCommand.Execute(null);
        }
    }

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        System.Diagnostics.Debug.WriteLine($"Unhandled exception: {e.Exception}");
    }
}
