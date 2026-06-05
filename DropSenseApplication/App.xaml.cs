// DropSense — App.xaml.cs
// ══════════════════════════════════════════════════════════════════════════════
// ADD TO PROJECT: Step 1 (replaces the default App.xaml.cs in a new MAUI project)
// ══════════════════════════════════════════════════════════════════════════════
// Services are injected here only once their implementations exist.
// Commented-out constructor parameters and bodies are re-enabled in the step shown.

using DropSense.Services;
using DropSense.ViewModels;
using Microsoft.Maui.Controls;
using System.Diagnostics;

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
        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
        {
            var ex = e.ExceptionObject as Exception;
            Debug.WriteLine($"[FATAL] UnhandledException: {ex?.GetType().Name} — {ex?.Message}\n{ex?.StackTrace}");
            System.Diagnostics.Debugger.Break(); // halts here in debug mode
        };

        InitializeComponent();

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
                System.Diagnostics.Debug.WriteLine(
                    $"App initialization failed: {task.Exception}");
            }
        }, TaskScheduler.FromCurrentSynchronizationContext());
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
                System.Diagnostics.Debug.WriteLine(
                    $"Sleep persistence failed: {ex}");
            }
        });
    }

    protected override void OnResume()
    {
        base.OnResume();
    }
}
