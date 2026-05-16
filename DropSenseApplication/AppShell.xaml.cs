// DropSense — AppShell.xaml.cs
// ══════════════════════════════════════════════════════════════════════════════
// ADD TO PROJECT: Step 1 (replaces the default AppShell.xaml.cs)
// ══════════════════════════════════════════════════════════════════════════════
// The shell hosts all pages and maintains the persistent sidebar navigation,
// notification badge, and connection chip.
//
// Route registrations and service subscriptions are uncommented as their
// target pages/services are added in subsequent steps.

using DropSense.Views;
using DropSense.Services;
namespace DropSense;

public partial class AppShell : Shell
{
    // Step 2 — inject once IDeviceConnectionService exists:
    private readonly IDeviceConnectionService _connectionService;

    // Step 6 — inject once IAlertService exists:
    // private readonly IAlertService _alertService;

    // Step 2+: Replace the constructor above with the version below
    // (adding injected services as each step introduces them).
    
    public AppShell(IDeviceConnectionService connectionService)  // Step 2
    {
        InitializeComponent();
        _connectionService = connectionService;

        // Step 2: subscribe to connection state changes
        _connectionService.ConnectionStateChanged += (_, state) => OnConnectionStateChanged(state);
    //
    //     // Step 6: subscribe to alert count changes
    //     _alertService.AlertsChanged += (_, _) => OnAlertCountChanged(_alertService.UnacknowledgedCount);
    //
        RegisterRoutes();
     }

    private void RegisterRoutes()
    {
        Routing.RegisterRoute(nameof(DashboardPage), typeof(DashboardPage));
        Routing.RegisterRoute(nameof(SettingsPage), typeof(SettingsPage));

        // Step 5 — uncomment when ExportWizardPage.xaml.cs is added:
        // Routing.RegisterRoute(nameof(ExportWizardPage), typeof(ExportWizardPage));

        // Step 6 — uncomment when AlertsPage.xaml.cs and AlertDetailPage are added:
        //Routing.RegisterRoute(nameof(AlertsPage),       typeof(AlertsPage));
        // Routing.RegisterRoute(nameof(AlertDetailPage),  typeof(AlertDetailPage));

        // Step 7 — uncomment when ConnectionPage.xaml.cs is added:
        // Routing.RegisterRoute(nameof(ConnectionPage), typeof(ConnectionPage));

        // Step 8 — uncomment when PlantLibraryPage and PlantEntryEditPage are added:
        // Routing.RegisterRoute(nameof(PlantLibraryPage),   typeof(PlantLibraryPage));
        // Routing.RegisterRoute(nameof(PlantEntryEditPage), typeof(PlantEntryEditPage));
    }

    // ── Alert Badge ────────────────────────────────────────────────────────────────
    // Step 6 — uncomment this entire method:
    // private void OnAlertCountChanged(int count)
    // {
    //     // TODO: Show/hide badge Label in AppShell.xaml based on count > 0
    //     // TODO: Update badge text to the count number
    //     // TODO: Apply "active" visual state to the Alerts nav item
    // }

    // ── Connection Chip ────────────────────────────────────────────────────────────
    //Step 2 — uncomment this entire method:
    private void OnConnectionStateChanged(ConnectionState state)
    {
    //     // TODO: Map ConnectionState enum to a dot colour and label text
    //     // TODO: Animate the dot (pulse/blink) for the Connecting/Transferring states
    //Display device name when connected; "Not connected" when disconnected
    }

    // ── Shell-level Navigation Handlers (wired from AppShell.xaml menu items) ─────

    // Step 4 — uncomment when ICsvService exists:
    // private async void OnOpenCsvClicked(object sender, EventArgs e)
    // {
    //     // TODO: Show FilePicker filtered to .csv; pass path to ICsvService; navigate to Dashboard
    // }

    // Step 2 — uncomment when IDeviceConnectionService exists:
    private async void OnDownloadFromDeviceClicked(object sender, EventArgs e)
    {
    //     // TODO: Guard: device must be connected
    //     // TODO: Call _connectionService.RequestDataDownloadAsync() with progress indicator
    }

    // Step 5 — uncomment when ExportWizardPage route is registered:
    // private async void OnExportCsvClicked(object sender, EventArgs e)
    // {
    //     // TODO: Guard: a file must be loaded; navigate to ExportWizardPage?format=csv
    // }

    // Step 7 — uncomment when ExportWizardPage XLSX path is implemented:
    // private async void OnExportExcelClicked(object sender, EventArgs e)
    // {
    //     // TODO: Guard: a file must be loaded; navigate to ExportWizardPage?format=xlsx
    // }
}