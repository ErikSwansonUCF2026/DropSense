// DropSense — MauiProgram.cs
// ══════════════════════════════════════════════════════════════════════════════
// ADD TO PROJECT: Step 1 (replaces the default MauiProgram.cs in a new MAUI project)
// ══════════════════════════════════════════════════════════════════════════════
// Each service, ViewModel, and Page registration below is labelled with the
// step at which it is uncommented and added to the build.
//
// Step 1 — Dashboard UI shell compiles and launches
// Step 2 — Bluetooth send/receive
// Step 3 — Device configuration
// Step 4 — CSV open / BT download
// Step 5 — CSV export with derived statistics
// Step 6 — Alert subsystem
// Step 7 — Full data analysis (stats, anomaly, charts)
// Step 8 — Plant library + recommendations

using DropSense;
using DropSense.Views;
using DropSense.Services;
using DropSense.ViewModels;
using Microsoft.Extensions.Logging;

namespace DropSenseApplication;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();

        builder
            .UseMauiApp<App>()
            .ConfigureFonts(fonts =>
            {
                // TODO: Add IBM Plex Sans / Mono .ttf files to the Resources/Fonts folder
                // and uncomment when font files are present.
                 fonts.AddFont("IBMPlexSans-Regular.ttf", "IBMPlexSans");
                 fonts.AddFont("IBMPlexMono-Regular.ttf",  "IBMPlexMono");
            });


        // ── Core Services ─────────────────────────────────────────────
        builder.Services.AddSingleton<ISettingsService, SettingsService>();
        builder.Services.AddSingleton<INavigationService, NavigationService>();
        builder.Services.AddSingleton<IDeviceConnectionService, DeviceConnectionService>();
        builder.Services.AddSingleton<IFileSessionService, FileSessionService>();
        builder.Services.AddSingleton<IFileSelectorService, FileSelectorService>();
        builder.Services.AddSingleton<IAlertService, AlertService>();

        // ── Shell ──────────────────────────────────────────────────────────────
        builder.Services.AddTransient<AppShell>();


        // ── ViewModels ────────────────────────────────────────────────
        builder.Services.AddTransient<DashboardViewModel>();
        builder.Services.AddTransient<SidebarViewModel>();
        builder.Services.AddTransient<SettingsViewModel>();
        builder.Services.AddTransient<AlertsViewModel>();

        // ── Views / Pages ─────────────────────────────────────────────────────
        builder.Services.AddTransient<DashboardView>();
        builder.Services.AddTransient<SidebarView>();
        builder.Services.AddTransient<SettingsView>();
        builder.Services.AddTransient<DashboardPage>();
        builder.Services.AddTransient<SettingsPage>();
        builder.Services.AddTransient<AlertsPage>();
        builder.Services.AddTransient<AlertsPanel>();

        // ── Step 7: Connection detail modal ────────────────────────────────────────
        // Uncomment when ConnectionViewModel.cs and ConnectionPage.xaml.cs are added.
        // builder.Services.AddTransient<ConnectionViewModel>();
        // builder.Services.AddTransient<ConnectionPage>();

        // ── Step 8: Plant Library ──────────────────────────────────────────────────
        // Uncomment when IPlantLibraryService.cs is added to the project.
        // builder.Services.AddSingleton<IPlantLibraryService, PlantLibraryService>();
        // builder.Services.AddTransient<PlantLibraryViewModel>();
        // builder.Services.AddTransient<PlantLibraryPage>();
        // builder.Services.AddTransient<PlantEntryEditViewModel>();
        // builder.Services.AddTransient<PlantEntryEditPage>();
        // IExportService (Excel) is also activated here:
        // builder.Services.AddSingleton<IExportService, ExportService>();

#if DEBUG
        builder.Logging.AddDebug();
#endif

        return builder.Build();
    }
}