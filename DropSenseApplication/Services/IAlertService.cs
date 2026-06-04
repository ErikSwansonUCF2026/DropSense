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
//   • Exposes FlushAsync() for safe persistence on app suspend (OnSleep)
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

    /// <summary>Removes all alerts from the panel collection.</summary>
    Task ClearAllAsync();

    /// <summary>Removes a single alert from the panel collection.</summary>
    void ClearAlert(AlertEvent alert);

    /// <summary>Adds a previously-persisted alert back to the collection without triggering auto-save.</summary>
    void AddRestoredAlert(AlertEvent alert);

    /// <summary>
    /// Flushes any pending state. Called from App.OnSleep() to ensure
    /// in-flight work completes before the process may be suspended.
    /// The base implementation waits for the file lock to drain.
    /// </summary>
    Task FlushAsync();
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

    private static string CsvPath => Path.Combine(LogsDir, "alerts_data.csv");
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

    // ── ClearAllAsync ─────────────────────────────────────────────────────────
    public async Task ClearAllAsync()
    {
        await MainThread.InvokeOnMainThreadAsync(() =>
        {
            Alerts.Clear();
            UnacknowledgedCount = 0;
            AlertsChanged?.Invoke(this, EventArgs.Empty);
        });
    }

    // ── AddRestoredAlert ──────────────────────────────────────────────────────
    // Restored alerts are added synchronously — no BeginInvokeOnMainThread.
    // InitializeAsync is always called from the main thread (via AppInitializer),
    // so the collection can be mutated directly. Using BeginInvokeOnMainThread
    // here deferred the inserts past the end of InitializeAsync, causing
    // CollectionChanged to fire after _isRestoring was cleared and after
    // _initialized was set to true, which triggered DebouncedPersistAsync
    // for every restored alert — producing 4 TaskCanceledExceptions and a
    // redundant second save 750 ms after the explicit PersistAsync call.
    //
    // BUG FIX (ordering): still using Insert(0, ...) so that alerts loaded
    // in chronological order from storage appear newest-first in the panel.
    public void AddRestoredAlert(AlertEvent alert)
    {
        Alerts.Insert(0, alert);
        UnacknowledgedCount = Alerts.Count(a => a.IsActive);
    }

    // ── FlushAsync ────────────────────────────────────────────────────────────
    // FIX: was missing entirely — called by App.OnSleep(). Acquires and
    // immediately releases the file lock so we know any in-flight SaveAlertAsync
    // has finished writing before the process may be suspended by the OS.
    public async Task FlushAsync()
    {
        await _fileLock.WaitAsync();
        _fileLock.Release();
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