// DropSense — Services/IAlertService.cs
//
// ══════════════════════════════════════════════════════════════════════════════
// IAlertService is a singleton that:
//   • Receives raw alert byte payloads from the BLE layer via AddRawAlert()
//   • Parses them into AlertEvent objects using AlertEvent.TryParse()
//   • Maintains the live ObservableCollection<AlertEvent> for UI binding
//   • Tracks UnacknowledgedCount for the badge (undismissed alerts)
//   • Writes parse/IO errors to Documents/DropSense/alert_errors.txt (append)
//   • Optionally auto-saves each AlertEvent to Documents/DropSense/alerts_data.csv
//   • Exposes SaveAlertAsync() for manual single-alert saves from the modal
// ══════════════════════════════════════════════════════════════════════════════

using DropSense.Models;
using System.Collections.ObjectModel;

namespace DropSense.Services;

// ─────────────────────────────────────────────────────────────────────────────
// Interface
// ─────────────────────────────────────────────────────────────────────────────

public interface IAlertService
{
    /// <summary>Live alert collection — bind directly in the panel.</summary>
    ObservableCollection<AlertEvent> Alerts { get; }

    /// <summary>Count of undismissed alerts — drives the Shell badge.</summary>
    int UnacknowledgedCount { get; }

    /// <summary>Raised when Alerts or UnacknowledgedCount changes.</summary>
    event EventHandler AlertsChanged;

    /// <summary>When true, every new alert is automatically written to the CSV log.</summary>
    bool AutoSave { get; set; }

    /// <summary>
    /// Parses a raw BLE notification payload and, if valid, adds an AlertEvent
    /// to the collection. Errors are written to the error log file.
    /// <paramref name="payload"/> is the bytes AFTER the 0x03 packet-type byte.
    /// </summary>
    Task AddRawAlertAsync(byte[] payload, string deviceName);

    /// <summary>Writes a single AlertEvent to the CSV log file.</summary>
    Task SaveAlertAsync(AlertEvent alert);

    /// <summary>Dismisses an alert (clears badge contribution, does not remove from panel).</summary>
    void DismissAlert(AlertEvent alert);

    /// <summary>Removes a single alert from the panel collection.</summary>
    void ClearAlert(AlertEvent alert);

    /// <summary>Removes all alerts from the panel collection.</summary>
    void ClearAll();
    
    /// 
    void AddRestoredAlert(AlertEvent alert);
}

// ─────────────────────────────────────────────────────────────────────────────
// Implementation
// ─────────────────────────────────────────────────────────────────────────────

public class AlertService : IAlertService
{
    // ── File paths ────────────────────────────────────────────────────────────
    private static string BaseDir =>
    FileSystem.AppDataDirectory;

    private static string DocsDir =>
        Path.Combine(BaseDir, "DropSense");

    private static string LogsDir =>
        Path.Combine(DocsDir, "AlertLogs");

    private static string CsvPath   => Path.Combine(LogsDir, "alerts_data.csv");
    private static string ErrorPath => Path.Combine(LogsDir, "alert_errors.txt");

    private static void EnsureLogDirectories()
    {
        Directory.CreateDirectory(LogsDir);
    }

    // ── State ─────────────────────────────────────────────────────────────────
    public ObservableCollection<AlertEvent> Alerts { get; } = new();

    private int _unacknowledgedCount;
    public int UnacknowledgedCount
    {
        get => _unacknowledgedCount;
        private set
        {
            _unacknowledgedCount = value;
            AlertsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public event EventHandler? AlertsChanged;

    private bool _autoSave;
    public bool AutoSave
    {
        get => _autoSave;
        set => _autoSave = value;
    }

    // File I/O is serialised to avoid concurrent writes
    private readonly SemaphoreSlim _fileLock = new(1, 1);
    private readonly object _sync = new();

    // ── AddRawAlertAsync ──────────────────────────────────────────────────────
    public async Task AddRawAlertAsync(byte[] payload, string deviceName)
    {
        if (!AlertEvent.TryParse(payload, deviceName, out var alert, out var error))
        {
            // Parse failure → write to error log, do NOT add to collection
            await WriteErrorLogAsync(
                $"[PARSE ERROR] {DateTime.Now:yyyy-MM-dd HH:mm:ss} | " +
                $"Device: {deviceName} | " +
                $"Payload: {BitConverter.ToString(payload ?? Array.Empty<byte>())} | " +
                $"Error: {error}");
            return;
        }

        // Always marshal collection changes onto the UI thread
        MainThread.BeginInvokeOnMainThread(() =>
        {
            Alerts.Insert(0, alert!);   // newest first
            UnacknowledgedCount = Alerts.Count(a => a.IsActive);
        });

        if (_autoSave)
            await SaveAlertAsync(alert!);
    }

    // ── SaveAlertAsync ────────────────────────────────────────────────────────
    public async Task SaveAlertAsync(AlertEvent alert)
    {
        if (alert.IsSaved) return;   // idempotent

        await _fileLock.WaitAsync();
        try
        {
            EnsureLogDirectories();

            bool csvExists = File.Exists(CsvPath);

            // Append mode — create only if file does not exist, then add header
            await using var writer = new StreamWriter(CsvPath, append: true);

            if (!csvExists)
                await writer.WriteLineAsync(AlertEvent.CsvHeader);

            await writer.WriteLineAsync(alert.ToCsvRow());
            await writer.FlushAsync();

            // Mark saved on UI thread so binding updates
            MainThread.BeginInvokeOnMainThread(() => alert.IsSaved = true);
        }
        catch (Exception ex)
        {
            await WriteErrorLogAsync(
                $"[SAVE ERROR] {DateTime.Now:yyyy-MM-dd HH:mm:ss} | " +
                $"Alert ID: {alert.Id} | Error: {ex.Message}",
                acquireLock: false);   // lock already held
        }
        finally
        {
            _fileLock.Release();
        }
    }

    // ── DismissAlert ──────────────────────────────────────────────────────────
    public void DismissAlert(AlertEvent alert)
    {
        alert.IsDismissed = true;
        UnacknowledgedCount = Alerts.Count(a => a.IsActive);
    }

    // ── ClearAlert ────────────────────────────────────────────────────────────
    public void ClearAlert(AlertEvent alert)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            Alerts.Remove(alert);
            UnacknowledgedCount = Alerts.Count(a => a.IsActive);

            AlertsChanged?.Invoke(this, EventArgs.Empty);
        });
    }
    // ── ClearAll ──────────────────────────────────────────────────────────────
    public void ClearAll()
    {
        lock (_sync)
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                Alerts.Clear();
                UnacknowledgedCount = 0;
                AlertsChanged?.Invoke(this, EventArgs.Empty);
            });
        }
    }

    public void AddRestoredAlert(AlertEvent alert)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            Alerts.Add(alert);
            UnacknowledgedCount = Alerts.Count(a => a.IsActive);
        });
    }

    // ── Error log ─────────────────────────────────────────────────────────────
    private async Task WriteErrorLogAsync(string message, bool acquireLock = true)
    {
        if (acquireLock) await _fileLock.WaitAsync();
        try
        {
            EnsureLogDirectories();
            await using var writer = new StreamWriter(ErrorPath, append: true);
            await writer.WriteLineAsync(message);
            await writer.FlushAsync();
        }
        catch
        {
            // Swallow — we cannot recurse into the error log from the error log handler
            System.Diagnostics.Debug.WriteLine($"[AlertService] Could not write to error log: {message}");
        }
        finally
        {
            if (acquireLock) _fileLock.Release();
        }
    }
}
