using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SpacePlusPlus.Infrastructure;
using SpacePlusPlus.Services;
using SpacePlusPlus.ViewModels;
using SpacePlusPlus.Views;

namespace SpacePlusPlus;

public partial class App : Application
{
    private static IServiceProvider? _services;
    public static IServiceProvider Services => _services ?? throw new InvalidOperationException("Services not initialized");

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _services = BuildServices();

        // Elevated cleanup job mode: run headless and exit.
        var args = e.Args;
        string? cleanupJobArg = args.Select(a => a.TrimStart('-')).FirstOrDefault(a => a.StartsWith("cleanup-job:"));
        if (cleanupJobArg is not null)
        {
            string jobPath = cleanupJobArg["cleanup-job:".Length..];
            int exit = CleanupJobRunner.RunAsync(jobPath, _services).GetAwaiter().GetResult();
            Shutdown(exit);
            return;
        }

        bool smokeTest = args.Contains("--smoke");
        if (smokeTest) SetupSmokeTest();

        DispatcherUnhandledException += OnDispatcherUnhandledException;

        ThemeManager.Apply(_services.GetRequiredService<SettingsService>().Current.Theme);

        var mainWindow = _services.GetRequiredService<MainWindow>();
        MainWindow = mainWindow;
        mainWindow.Show();
    }

    private static IServiceProvider BuildServices()
    {
        var settingsService = new SettingsService(new ServiceLoggerBootstrap());
        var settings = settingsService.Current;

        LogLevel minLevel = settings.LogLevel switch
        {
            "Debug" => LogLevel.Debug,
            "Warning" => LogLevel.Warning,
            "Error" => LogLevel.Error,
            _ => LogLevel.Information
        };
        if (!settings.EnableFileLogging) minLevel = LogLevel.None;

        var factory = LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(minLevel);
            builder.AddProvider(new FileLoggerProvider(settingsService.LogDirectory, () =>
                settingsService.Current.EnableFileLogging
                    ? settingsService.Current.LogLevel switch
                    {
                        "Debug" => LogLevel.Debug,
                        "Warning" => LogLevel.Warning,
                        "Error" => LogLevel.Error,
                        _ => LogLevel.Information
                    }
                    : LogLevel.None));
        });

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ILoggerFactory>(factory);
        services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));
        services.AddSingleton(settingsService);
        services.AddSingleton<DriveService>();
        services.AddSingleton<IDriveService>(sp => sp.GetRequiredService<DriveService>());
        services.AddSingleton<FileClassificationService>();
        services.AddSingleton<SafetyService>();
        services.AddSingleton<StorageScannerService>();
        services.AddSingleton<ScanHistoryService>();
        services.AddSingleton<ScanManager>();
        services.AddSingleton<WindowsUpdateService>();
        services.AddSingleton<OneDriveService>();
        services.AddSingleton<CleanupService>();
        services.AddSingleton<ApplicationInventoryService>();
        services.AddSingleton<DebloatService>();
        services.AddSingleton<SearchService>();
        services.AddSingleton<DialogService>();

        // ViewModels
        services.AddSingleton<OverviewViewModel>();
        services.AddSingleton<StorageMapViewModel>();
        services.AddSingleton<LargeFilesViewModel>();
        services.AddSingleton<ApplicationsViewModel>();
        services.AddSingleton<SmartCleanupViewModel>();
        services.AddSingleton<OneDriveViewModel>();
        services.AddSingleton<ScanHistoryViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<DebloatViewModel>();
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<MainWindow>();

        return services.BuildServiceProvider();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        try
        {
            var logger = _services?.GetService(typeof(ILogger<App>)) as ILogger<App>;
            logger?.LogError(e.Exception, "Unhandled UI exception");
            MessageBox.Show(e.Exception.Message, "Space++ — unexpected error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch
        {
            // Nothing more we can do.
        }
        e.Handled = true;
    }

    private void SetupSmokeTest()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            Shutdown(0);
        };
        timer.Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_services is IDisposable d) d.Dispose();
        base.OnExit(e);
    }

    /// <summary>Silent logger used only to load settings before the real logger exists.</summary>
    private sealed class LoggerFactoryMock : Microsoft.Extensions.Logging.ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => false;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId,
            TState state, Exception? exception, Func<TState, Exception?, string> formatter) { }
    }

    private sealed class ServiceLoggerBootstrap : Microsoft.Extensions.Logging.ILogger<SettingsService>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => false;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId,
            TState state, Exception? exception, Func<TState, Exception?, string> formatter) { }
    }
}
