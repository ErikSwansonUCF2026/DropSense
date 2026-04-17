// DropSense — App.xaml.cs
// ══════════════════════════════════════════════════════════════════════════════
// ADD TO PROJECT: Step 1 (replaces the default App.xaml.cs in a new MAUI project)
// ══════════════════════════════════════════════════════════════════════════════
// Services are injected here only once their implementations exist.
// Commented-out constructor parameters and bodies are re-enabled in the step shown.

//using Android.Telecom;
using DropSense.Services;
using Microsoft.Maui.Controls;

namespace DropSense;

public partial class App : Application
{
    // ── Step 1: Settings service is the only dependency at launch ─────────────────
    private readonly ISettingsService _settingsService;

    // Step 2 — uncomment when IDeviceConnectionService.cs is added:
    private readonly IDeviceConnectionService _connectionService;

    // Step 6 — uncomment when IAlertService.cs is added:
    // private readonly IAlertService _alert_service;

    // Step 8 — uncomment when IPlantLibraryService.cs is added:
    // private readonly IPlantLibraryService _plant_library_service;

    public App(ISettingsService settingsService,
        IDeviceConnectionService connectionService)   // Step 2
    //            IAlertService alertService,                   // Step 6
    //            IPlantLibraryService plantLibraryService)     // Step 8
    {
        InitializeComponent();

        _settingsService = settingsService;

        // Step 2 — uncomment:
        _connectionService   = connectionService;

        // Step 6 — uncomment:
        // _alertService = alertService;

        // Step 8 — uncomment:
        // _plantLibraryService = plantLibraryService;

        // TODO (Step 1): Load persisted user preferences via _settingsService on startup

        // Set the application shell as the root page.
        // AppShell must exist before this line; its XAML declares the initial route.
        
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        return new Window(new AppShell(_connectionService));
    }

    protected override void OnStart()
    {
        base.OnStart();

        // Step 8 — restore plant library from JSON:
        // _ = _plantLibraryService.LoadAsync();

        // Step 6 — restore alert log if logging is enabled:
        // if (_settingsService.AlertLoggingEnabled)
        //     _ = _alertService.LoadPersistedLogAsync();

        // Step 2 — attempt auto-reconnect to last known device:
        
    }

    protected override void OnSleep()
    {
        base.OnSleep();

        // Step 6 — persist alert log on suspend:
        // (no explicit call needed; AlertService persists on each clear if logging enabled)
    }

    protected override void OnResume()
    {
        base.OnResume();
    }
}