using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using GoogleDriveWorkSync.Helpers;
using GoogleDriveWorkSync.Services;
using GoogleDriveWorkSync.Services.Interfaces;
using GoogleDriveWorkSync.ViewModels;

namespace GoogleDriveWorkSync;

public partial class App : Application
{
    public static Window Window { get; private set; } = null!;
    public static DispatcherQueue DispatcherQueue { get; private set; } = null!;
    public static nint WindowHandle => WinRT.Interop.WindowNative.GetWindowHandle(Window);

    private static IHost? _host;

    public App()
    {
        // Global exception handlers dumping immediately to disk
        this.UnhandledException += (s, e) =>
        {
            LogCrash("XAML_UnhandledException", e.Exception, e.Message);
        };
        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
        {
            LogCrash("AppDomain_UnhandledException", e.ExceptionObject as Exception, e.ExceptionObject?.ToString());
        };
        TaskScheduler.UnobservedTaskException += (s, e) =>
        {
            LogCrash("TaskScheduler_UnobservedTaskException", e.Exception, e.Exception?.ToString());
        };

        LogTrace("App() constructor started");

        try
        {
            InitializeComponent();
            LogTrace("InitializeComponent() completed");

            _host = Host.CreateDefaultBuilder()
                .ConfigureServices((context, services) =>
                {
                    // Services
                    services.AddSingleton<IDriveSyncService, DriveSyncService>();
                    services.AddSingleton<IClaudeDiscoveryService, ClaudeDiscoveryService>();
                    services.AddSingleton<ISyncScheduleService, SyncScheduleService>();
                    services.AddSingleton<IUpdateService, UpdateService>();

                    // ViewModels
                    services.AddSingleton<WorkSyncViewModel>();
                    services.AddSingleton<ContextDiscoveryViewModel>();
                    services.AddTransient<SettingsViewModel>();
                })
                .Build();
            LogTrace("Host built successfully");
        }
        catch (Exception ex)
        {
            LogCrash("App_Constructor", ex, ex.Message);
            throw;
        }
    }

    public static T GetService<T>() where T : class
    {
        return _host!.Services.GetRequiredService<T>();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        LogTrace("OnLaunched() started");
        try
        {
            var cmdArgs = Environment.GetCommandLineArgs();
            bool isAutostart = AutostartHelper.HasAutostartArgument(cmdArgs);
            LogTrace($"Launch arguments evaluated. IsAutostart: {isAutostart}");

            AutostartHelper.EnsureAutostartSynced();

            // WinUI 3 Invariant: Initialize DispatcherQueue BEFORE creating MainWindow
            DispatcherQueue = DispatcherQueue.GetForCurrentThread();
            DiagnosticLogger.IsUIThreadCheck = () => DispatcherQueue.HasThreadAccess;
            DiagnosticLogger.UIThreadDispatcher = action => DispatcherQueue.TryEnqueue(() => action());
            LogTrace("DispatcherQueue obtained");

            Window = new MainWindow();
            LogTrace("MainWindow instantiated");

            var scheduleService = GetService<ISyncScheduleService>();
            var driveSyncService = GetService<IDriveSyncService>();

            scheduleService.ScheduledSyncTriggered += (s, e) =>
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        LogTrace("Background scheduled auto-sync triggered");
                        await driveSyncService.RunSyncAsync(forceFullSync: false);
                    }
                    catch (Exception ex)
                    {
                        LogCrash("ScheduledSync", ex, ex.Message);
                    }
                });
            };

            if (isAutostart)
            {
                LogTrace("Autostart launch: keeping window hidden in system tray.");
            }
            else
            {
                Window.Activate();
                LogTrace("Window.Activate() executed");
            }
        }
        catch (Exception ex)
        {
            LogCrash("OnLaunched", ex, ex.Message);
            throw;
        }
    }

    public static void LogTrace(string step)
    {
        DiagnosticLogger.LogTrace(step);
    }

    public static void LogCrash(string source, Exception? ex, string? message)
    {
        DiagnosticLogger.LogCrash(source, ex, message);
    }
}
