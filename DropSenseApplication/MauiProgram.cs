// DropSense — MauiProgram.cs
// ══════════════════════════════════════════════════════════════════════════════
// Each service, ViewModel, and Page registration below is labelled with the
// step at which it is uncommented and added to the build.
//


using DropSense;
using DropSense.Services;
using DropSense.ViewModels;
using DropSense.Views;
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

        // ── Start-Up Services ─────────────────────────────────────────────
        builder.Services.AddSingleton<IAppInitializer, AppInitializer>();
        builder.Services.AddSingleton<ISettingsService, SettingsService>();


        // ── Core Services ─────────────────────────────────────────────
        builder.Services.AddSingleton<INavigationService, NavigationService>();
        builder.Services.AddSingleton<IDeviceConnectionService, DeviceConnectionService>();
        builder.Services.AddSingleton<IFileSessionService, FileSessionService>();
        builder.Services.AddSingleton<IFileSelectorService, FileSelectorService>();
        builder.Services.AddSingleton<IAlertService, AlertService>();
        builder.Services.AddSingleton<IAlertPersistenceService, AlertPersistenceService>();
        builder.Services.AddSingleton<ICsvService, CsvService>();
        builder.Services.AddSingleton<IDebugLogService, DebugLogService>();
        builder.Services.AddSingleton<IExportXlsxService, ExportXlsxService>();
        builder.Services.AddSingleton<IPlantLibraryService, PlantLibraryService>();




        // ── Shell ──────────────────────────────────────────────────────────────
        builder.Services.AddTransient<AppShell>();


        // ── ViewModels ────────────────────────────────────────────────
        builder.Services.AddTransient<DashboardViewModel>();
        builder.Services.AddSingleton<SidebarViewModel>();
        builder.Services.AddTransient<SettingsViewModel>();
        builder.Services.AddSingleton<AlertsViewModel>();
        builder.Services.AddTransient<AnalysisExportViewModel>();
        builder.Services.AddSingleton<PlantLibraryViewModel>();


        // ── Views / Pages ─────────────────────────────────────────────────────
        builder.Services.AddTransient<DashboardView>();
        builder.Services.AddSingleton<SidebarView>();
        builder.Services.AddTransient<SettingsView>();
        builder.Services.AddTransient<DashboardPage>();
        builder.Services.AddTransient<SettingsPage>();
        builder.Services.AddTransient<AnalysisExportPage>();
        builder.Services.AddTransient<AnalysisExportView>();
        builder.Services.AddTransient<PlantLibraryPage>();
        builder.Services.AddTransient<PlantLibraryView>();

        // ── A&E Subviews ─────────────────────────────────────────────────────
        builder.Services.AddTransient<ChipToggle>();
        builder.Services.AddTransient<ThresholdRow>();
        builder.Services.AddTransient<ToggleRow>();
        builder.Services.AddTransient<ZScoreThresholdRow>();


        // ── Alerts ─────────────────────────────────────────────────────
        builder.Services.AddTransient<AlertsPage>();
        builder.Services.AddTransient<AlertsPanel>();
        builder.Services.AddTransient<AlertListView>();
        builder.Services.AddTransient<AlertToolbarView>();



#if DEBUG
        builder.Logging.AddDebug();
#endif

        return builder.Build();
    }
}