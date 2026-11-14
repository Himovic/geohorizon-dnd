using System.IO;
using System.Windows;
using System.Windows.Threading;
using GeoVisionPro.Application.Abstractions;
using GeoVisionPro.Desktop.Services;
using GeoVisionPro.Desktop.ViewModels;
using GeoVisionPro.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GeoVisionPro.Desktop;

/// <summary>Application entry: builds the generic host (DI, logging, settings) and shows the main window.</summary>
public partial class App : System.Windows.Application
{
    private IHost? _host;
    public static IServiceProvider Services { get; private set; } = null!;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var settings = AppSettings.Load();
        _host = Host.CreateDefaultBuilder(e.Args)
            .ConfigureLogging(l =>
            {
                l.ClearProviders();
                l.AddProvider(new FileLoggerProvider(Path.Combine(AppSettings.Directory, "logs")));
                l.SetMinimumLevel(LogLevel.Information);
            })
            .ConfigureServices(s =>
            {
                s.AddGeoVisionInfrastructure(settings);
                s.AddSingleton<DialogService>();
                s.AddSingleton<ThemeService>();
                s.AddSingleton<CursorBus>();
                s.AddSingleton<MainViewModel>();
                s.AddSingleton<MainWindow>();
            })
            .Build();
        Services = _host.Services;
        await _host.StartAsync();

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, a) => LogFatal(a.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, a) => { LogError(a.Exception); a.SetObserved(); };

        var theme = Services.GetRequiredService<ThemeService>();
        theme.Apply(settings.Theme == "Light" ? AppTheme.Light : AppTheme.Dark);
        var window = Services.GetRequiredService<MainWindow>();
        MainWindow = window;
        window.Show();
        var vm = Services.GetRequiredService<MainViewModel>();
        var projectArg = e.Args.FirstOrDefault(a => a.EndsWith(".gvp", StringComparison.OrdinalIgnoreCase));
        if (projectArg is not null && File.Exists(projectArg)) await vm.OpenProjectAsync(projectArg);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LogError(e.Exception);
        MessageBox.Show($"An unexpected error occurred:\n\n{e.Exception.Message}\n\nThe error was logged; the application will continue. Save your work if the problem persists.",
            "GeoVision Pro", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }

    private static void LogError(Exception? ex)
    {
        if (ex is null) return;
        try { Services?.GetService<IMessageLog>()?.Error("Application", ex.Message); } catch (Exception) { }
        try { File.AppendAllText(Path.Combine(AppSettings.Directory, "errors.log"), $"{DateTime.Now:u} {ex}{Environment.NewLine}"); } catch (IOException) { }
    }

    private static void LogFatal(Exception? ex) => LogError(ex);

    protected override async void OnExit(ExitEventArgs e)
    {
        try
        {
            Services.GetService<AppSettings>()?.Save();
            Services.GetService<GeoVisionPro.Application.Services.ProjectSession>()?.Close();
        }
        catch (Exception ex) { LogError(ex); }
        if (_host is not null) { await _host.StopAsync(TimeSpan.FromSeconds(2)); _host.Dispose(); }
        base.OnExit(e);
    }
}
