// DropSense — Services/ISettingsService.cs
// ══════════════════════════════════════════════════════════════════════════════
// ADD TO PROJECT: Step 1
// ══════════════════════════════════════════════════════════════════════════════
// Persists user preferences using MAUI's Preferences API.
// All properties used only in later steps are commented out until needed.

using Microsoft.Maui.Storage;

namespace DropSense.Services;

// ─────────────────────────────────────────────────────────────────────────────
// Interface
// ─────────────────────────────────────────────────────────────────────────────

public interface ISettingsService
{

    // ── Step 2: Device / Connection ────────────────────────────────────────────────
    string? LastConnectedDeviceId { get; set; }
    string? LastConnectedDeviceName { get; set; }

    // ── Step 4: Export defaults ────────────────────────────────────────────────────
    // Uncomment when ICsvService.cs is added:
    // string DefaultExportDirectory { get; set; }

    // ── Step 6: Alert logging ──────────────────────────────────────────────────────
    // Uncomment when IAlertService.cs is added:
    // bool   AlertLoggingEnabled { get; set; }
    // string AlertLogFilePath    { get; }

    // ── Step 8: Plant library ──────────────────────────────────────────────────────
    // Uncomment when IPlantLibraryService.cs is added:
    // string PlantLibraryFilePath { get; }
}

// ─────────────────────────────────────────────────────────────────────────────
// Implementation
// ─────────────────────────────────────────────────────────────────────────────

public class SettingsService : ISettingsService
{
    private const string KeyAlertLogging = "alert_logging_enabled";
    private const string KeyExportDirectory = "default_export_directory";
    private const string KeyLastDeviceId = "last_device_id";
    private const string KeyLastDeviceName = "last_device_name";

    // ── Step 2 — uncomment when IDeviceConnectionService.cs is added: ────────────
    public string? LastConnectedDeviceId
    {
        get => Preferences.Default.Get<string?>(KeyLastDeviceId, null);
        set => Preferences.Default.Set(KeyLastDeviceId, value ?? string.Empty);

    }
    public string? LastConnectedDeviceName
    {
        get => Preferences.Get(nameof(LastConnectedDeviceName), null);
        set => Preferences.Set(nameof(LastConnectedDeviceName), value);
    }

    // ── Step 4 — uncomment when ICsvService.cs is added: ─────────────────────────
    // public string DefaultExportDirectory
    // {
    //     get => Preferences.Default.Get(KeyExportDirectory, FileSystem.Current.AppDataDirectory);
    //     set => Preferences.Default.Set(KeyExportDirectory, value);
    // }

    // ── Step 6 — uncomment when IAlertService.cs is added: ───────────────────────
    // public bool AlertLoggingEnabled
    // {
    //     get => Preferences.Default.Get(KeyAlertLogging, false);
    //     set => Preferences.Default.Set(KeyAlertLogging, value);
    // }
    // public string AlertLogFilePath
    //     => Path.Combine(FileSystem.AppDataDirectory, "alert_log.json");

    // ── Step 8 — uncomment when IPlantLibraryService.cs is added: ────────────────
    // public string PlantLibraryFilePath
    //     => Path.Combine(FileSystem.AppDataDirectory, "plant_library.json");
}
