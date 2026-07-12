// DropSense — App.xaml.cs
// ══════════════════════════════════════════════════════════════════════════════
// ADD TO PROJECT: Step 1 (replaces the default App.xaml.cs in a new MAUI project)
// ══════════════════════════════════════════════════════════════════════════════
// Services are injected here only once their implementations exist.
// Commented-out constructor parameters and bodies are re-enabled in the step shown.

using DropSense.Services;
using DropSense.ViewModels;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Storage;
using OfficeOpenXml;
using System.Diagnostics;
using System.IO;

namespace DropSense;

public partial class App : Application
{
    private readonly IAppInitializer _appInitializer;
    private readonly ISettingsService _settingsService;
    private readonly IDeviceConnectionService _connectionService;
    private readonly IAlertService _alertService;
    private readonly IAlertPersistenceService _alertPersistenceService;

    private readonly AlertsViewModel _alertsViewModel;

    private readonly AppShell _appShell;

    public App(
        ISettingsService settingsService,
        IDeviceConnectionService connectionService,
        IAlertService alertService,
        AlertsViewModel alertsViewModel,
        AppShell appShell,
        IAppInitializer appInitializer,
        IAlertPersistenceService alertPersistenceService)
    {
        // Cross-platform fatal exception logging.
        // AppDomain.UnhandledException fires on every platform MAUI supports
        // (Android, iOS, Windows, Mac Catalyst), so this alone is enough to
        // catch crashes without any Windows-specific APIs.
        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
        {
            var ex = e.ExceptionObject as Exception;
            LogFatal("AppDomain.UnhandledException", ex);
        };

        // Catch unobserved exceptions from fire-and-forget Tasks too
        // (e.g. the Task.Run in OnSleep below), which otherwise fail silently.
        TaskScheduler.UnobservedTaskException += (s, e) =>
        {
            LogFatal("TaskScheduler.UnobservedTaskException", e.Exception);
            e.SetObserved();
        };

#if WINDOWS
        // WinUI-specific handler: only compiled in on Windows targets.
        // Microsoft.UI.Xaml types don't exist in the Android build at all,
        // so this must be behind a platform conditional rather than a runtime check.
        Microsoft.UI.Xaml.Application.Current.UnhandledException += (s, e) =>
        {
            e.Handled = true;
            LogFatal("WinUI.UnhandledException", e.Exception);
        };
#endif

        InitializeComponent();

        ExcelPackage.License.SetNonCommercialPersonal("Erik Swanson");

        _settingsService = settingsService;
        _connectionService = connectionService;
        _alertService = alertService;
        _alertsViewModel = alertsViewModel;
        _appShell = appShell;
        _appInitializer = appInitializer;
        _alertPersistenceService = alertPersistenceService;

        // Start initialization without blocking UI startup
        _appInitializer.InitializeAsync().ContinueWith(task =>
        {
            if (task.IsFaulted)
            {
                Debug.WriteLine($"App initialization failed: {task.Exception}");
            }
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>
    /// Writes fatal-exception info to Debug output and to a crash file inside
    /// the app's own sandboxed storage. FileSystem.AppDataDirectory resolves to
    /// a writable, app-private folder on every platform (on Android this is
    /// something like /data/user/0/{package}/files), unlike a hardcoded
    /// "C:\temp" path which only exists on a Windows dev machine and would
    /// throw a DirectoryNotFoundException/UnauthorizedAccessException on device.
    /// </summary>
    private static void LogFatal(string source, Exception? ex)
    {
        Debug.WriteLine($"[FATAL] {source}: {ex?.GetType().Name} — {ex?.Message}\n{ex?.StackTrace}");

        try
        {
            var path = Path.Combine(FileSystem.AppDataDirectory, "dropsense_crash.txt");
            File.WriteAllText(path, ex?.ToString() ?? "null");
        }
        catch (Exception writeEx)
        {
            // Never let crash-logging itself throw during an unhandled-exception
            // handler — that can crash the process a second time / mask the
            // original exception, and is especially unforgiving on Android.
            Debug.WriteLine($"[FATAL] Failed to write crash log: {writeEx}");
        }

        // Debugger.Break() only matters when a debugger is actually attached;
        // it's a no-op otherwise on every platform, so it's safe to leave in,
        // but it's still gated here so it never runs in a Release build.
#if DEBUG
        Debugger.Break();
#endif
    }

    protected override Window CreateWindow(
        IActivationState? activationState)
    {
        return new Window(_appShell);
    }

    protected override void OnStart()
    {
        base.OnStart();

        // Auto reconnect logic
        // _ = _connectionService.TryReconnectAsync();
    }

    protected override void OnSleep()
    {
        base.OnSleep();

        // FIX: FlushAsync() is now defined on IAlertService. It acquires and
        // releases the file lock, ensuring any in-flight SaveAlertAsync has
        // finished writing before the OS may suspend the process.
        _ = Task.Run(async () =>
        {
            try
            {
                await _alertService.FlushAsync();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Sleep persistence failed: {ex}");
            }
        });
    }

    protected override void OnResume()
    {
        base.OnResume();
    }
}