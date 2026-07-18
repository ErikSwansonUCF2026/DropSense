// DropSense — ViewModels/AlertsViewModel.cs
// ══════════════════════════════════════════════════════════════════════════════
// AlertsViewModel is owned by AlertsPanel (has-a relationship).
// It mediates between IAlertService and the XAML bindings.
// The modal is driven by SelectedAlert — when set, the panel XAML shows
// the AlertDetailModal popup.

using DropSense.Models;
using DropSense.Services;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows.Input;

namespace DropSense.ViewModels;

public class AlertsViewModel : BaseViewModel
{
    private readonly IAlertService _alertService;
    private readonly INavigationService _nav;
    private readonly IAlertPersistenceService _storage;

    public AlertsViewModel(IAlertService alertService, INavigationService nav, IAlertPersistenceService storage)
    {
        _alertService = alertService;
        _nav = nav;
        _storage = storage;

        // Subscribe to service changes so badge and counts update
        _alertService.AlertsChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(UnacknowledgedCount));
            OnPropertyChanged(nameof(HasAlerts));
            OnPropertyChanged(nameof(HasUnacknowledged));
            OnPropertyChanged(nameof(BadgeText));
        };

        // Commands — panel level
        ClearAllCommand =
            new Command(async () => await OnClearAllAsync());
        OpenAlertCommand = new Command<AlertEvent>(OnOpenAlert);
        ClearSingleCommand = new Command<AlertEvent>(OnClearSingle);
        ToggleAutoSaveCommand = new Command(OnToggleAutoSave);
        SaveAllUnsavedCommand = new Command(async () => await OnSaveAllUnsavedAsync());
        RestoreAlertsCommand = new Command(async () => await OnRestoreAlertsAsync());
        OpenLogsFolderCommand = new Command(OpenLogsFolder);

        // Commands — modal level (operate on SelectedAlert)
        ModalSaveCommand = new Command(async () => await OnModalSaveAsync(), () => SelectedAlert is not null && !SelectedAlert.IsSaved);
        ModalDismissCommand = new Command(OnModalDismiss, () => SelectedAlert is not null && SelectedAlert.IsActive);
        ModalClearCommand = new Command(OnModalClear, () => SelectedAlert is not null);
        CloseModalCommand = new Command(() => SelectedAlert = null);
    }

    // ── Panel-level properties ────────────────────────────────────────────────

    /// <summary>The live alert collection from IAlertService — bind to CollectionView.</summary>
    public ObservableCollection<AlertEvent> Alerts => _alertService.Alerts;

    public int UnacknowledgedCount => _alertService.UnacknowledgedCount;
    public bool HasAlerts => _alertService.Alerts.Count > 0;
    public bool HasUnacknowledged => _alertService.UnacknowledgedCount > 0;
    public string BadgeText => UnacknowledgedCount > 99 ? "99+" : UnacknowledgedCount.ToString();

    private bool _autoSave;
    public bool AutoSave
    {
        get => _autoSave;
        set
        {
            if (SetProperty(ref _autoSave, value))
            {
                _alertService.AutoSave = value;
                OnPropertyChanged(nameof(AutoSaveLabel));
            }
        }
    }
    public string AutoSaveLabel => AutoSave ? "Auto-save On" : "Auto-save Off";

    // ── Modal state ───────────────────────────────────────────────────────────

    private AlertEvent? _selectedAlert;
    public AlertEvent? SelectedAlert
    {
        get => _selectedAlert;
        set
        {
            if (SetProperty(ref _selectedAlert, value))
            {
                OnPropertyChanged(nameof(IsModalOpen));
                // Re-evaluate modal command availability
                ((Command)ModalSaveCommand).ChangeCanExecute();
                ((Command)ModalDismissCommand).ChangeCanExecute();
                ((Command)ModalClearCommand).ChangeCanExecute();
            }
        }
    }

    public bool IsModalOpen => SelectedAlert is not null;

    // ── Status ────────────────────────────────────────────────────────────────

    private string _statusMessage = string.Empty;
    public string StatusMessage
    {
        get => _statusMessage;
        private set { SetProperty(ref _statusMessage, value); OnPropertyChanged(nameof(HasStatus)); }
    }
    public bool HasStatus => !string.IsNullOrEmpty(StatusMessage);

    // ── Persistence ───────────────────────────────────────────────────────────
    private bool _initialized;
    private bool _isRestoring;
    private bool _collectionSubscribed;
    private CancellationTokenSource? _saveDebounce;

    private static readonly HashSet<string> PersistedProperties =
    [
        nameof(AlertEvent.IsSaved),
        nameof(AlertEvent.IsDismissed),
        nameof(AlertEvent.IsActive)
    ];

    public async Task InitializeAsync()
    {
        if (_initialized)
            return;

        Debug.WriteLine("AlertsViewModel InitializeAsync called");
        _isRestoring = true;

        var alerts = new List<AlertEvent>();
        try
        {
            alerts = await _storage.LoadAlertsAsync();

            // FIX: was Alerts.Clear() — must go through the service so
            // UnacknowledgedCount and AlertsChanged are kept consistent.
            await _alertService.ClearAllAsync();

            // FIX: wire up the collection subscription BEFORE the restore loop
            // so that the CollectionChanged handler (and its DebouncedPersistAsync
            // call) is in place for any adds that happen during restore. The
            // _isRestoring guard prevents spurious saves during the loop.
            SubscribeToCollectionChanges();

            foreach (var alert in alerts)
            {
                _alertService.AddRestoredAlert(alert);
                SubscribeToAlert(alert);
            }

            // Mark initialized before clearing _isRestoring so that any
            // CollectionChanged events arriving on the UI thread while we are
            // still in this method see _initialized = true and do not attempt
            // a redundant debounced save.
            _initialized = true;
        }
        finally
        {
            _isRestoring = false;
        }

        // Write the restored batch to disk in one pass now that _isRestoring
        // is clear. AddRestoredAlert uses BeginInvokeOnMainThread, so its
        // CollectionChanged events may still be in flight here; the
        // _initialized guard in DebouncedPersistAsync prevents them from
        // queuing a second redundant save over the top of this one.
        if (alerts.Count > 0)
            await PersistAsync();
    }

    private readonly SemaphoreSlim _saveLock = new(1, 1);

    public async Task PersistAsync()
    {
        // FIX: do NOT cancel _saveDebounce here. PersistAsync is also called
        // directly (e.g. from FlushAsync paths). Cancelling the debounce token
        // from inside PersistAsync would cancel an unrelated in-flight debounce
        // triggered by a concurrent collection change.
        await _saveLock.WaitAsync();
        try
        {
            await _storage.SaveAlertsAsync(Alerts.ToList());
        }
        finally
        {
            _saveLock.Release();
        }
    }

    private void SubscribeToCollectionChanges()
    {
        if (_collectionSubscribed)
            return;

        _collectionSubscribed = true;

        Alerts.CollectionChanged += async (_, e) =>
        {
            if (_isRestoring)
                return;

            if (e.NewItems is not null)
            {
                foreach (AlertEvent alert in e.NewItems)
                    SubscribeToAlert(alert);
            }

            if (e.OldItems is not null)
            {
                foreach (AlertEvent alert in e.OldItems)
                    alert.PropertyChanged -= Alert_PropertyChanged;
            }

            await DebouncedPersistAsync();
        };
    }

    private void SubscribeToAlert(AlertEvent alert)
    {
        alert.PropertyChanged -= Alert_PropertyChanged;
        alert.PropertyChanged += Alert_PropertyChanged;
    }

    private void Alert_PropertyChanged(
        object? sender,
        PropertyChangedEventArgs e)
    {
        if (e.PropertyName is null)
            return;

        if (!PersistedProperties.Contains(e.PropertyName))
            return;

        _ = DebouncedPersistAsync();
    }

    private async Task DebouncedPersistAsync()
    {
        // Do not queue saves before InitializeAsync has completed. Restored
        // alerts arrive via BeginInvokeOnMainThread and can trigger
        // CollectionChanged after _isRestoring clears but before _initialized
        // is set; without this guard they would race PersistAsync and either
        // write an incomplete list or generate spurious TaskCanceledExceptions.
        if (!_initialized)
            return;

        // FIX: capture and cancel the *previous* CTS before replacing it.
        // The old code cancelled and disposed _saveDebounce, then awaited
        // Task.Delay using the new token — but a concurrent caller could
        // dispose the new CTS between its creation and the await, causing
        // an ObjectDisposedException on the token. Using a local capture
        // ensures each caller owns its own CTS reference.
        var previous = _saveDebounce;
        previous?.Cancel();
        previous?.Dispose();

        var cts = new CancellationTokenSource();
        _saveDebounce = cts;

        try
        {
            await Task.Delay(750, cts.Token);
            await PersistAsync();
        }
        catch (OperationCanceledException)
        {
            // Expected — a newer debounce superseded this one.
        }
        finally
        {
            // Only dispose if this CTS is still the current one; another
            // caller may have already replaced it.
            if (ReferenceEquals(_saveDebounce, cts))
            {
                _saveDebounce = null;
                cts.Dispose();
            }
        }
    }

    // ── Commands ──────────────────────────────────────────────────────────────

    public ICommand ClearAllCommand { get; }
    public ICommand OpenAlertCommand { get; }
    public ICommand ClearSingleCommand { get; }
    public ICommand ToggleAutoSaveCommand { get; }
    public ICommand SaveAllUnsavedCommand { get; }
    public ICommand RestoreAlertsCommand { get; }
    public ICommand OpenLogsFolderCommand { get; }

    // Modal commands
    public ICommand ModalSaveCommand { get; }
    public ICommand ModalDismissCommand { get; }
    public ICommand ModalClearCommand { get; }
    public ICommand CloseModalCommand { get; }

    // ── Panel command implementations ─────────────────────────────────────────
    private async Task OnClearAllAsync()
    {
        await _alertService.ClearAllAsync();

        SelectedAlert = null;
        StatusMessage = string.Empty;

        ((Command)ClearAllCommand).ChangeCanExecute();
    }

    private void OnOpenAlert(AlertEvent? alert)
    {
        if (alert is null) return;
        SelectedAlert = alert;
    }

    private void OnClearSingle(AlertEvent? alert)
    {
        if (alert is null) return;

        if (SelectedAlert == alert)
            SelectedAlert = null;

        _alertService.ClearAlert(alert);

        ((Command)ClearAllCommand).ChangeCanExecute();
    }

    private void OnToggleAutoSave()
        => AutoSave = !AutoSave;

    private async Task OnSaveAllUnsavedAsync()
    {
        var unsaved = _alertService.Alerts.Where(a => !a.IsSaved).ToList();
        if (!unsaved.Any())
        {
            StatusMessage = "All alerts already saved.";
            return;
        }
        foreach (var a in unsaved)
            await _alertService.SaveAlertAsync(a);
        StatusMessage = $"{unsaved.Count} alert(s) saved to CSV.";
    }

    // Uses a CSV to return alerts to panel
    public async Task RestoreAlertsFromCsvAsync(string filePath)
    {
        if (!File.Exists(filePath))
        {
            StatusMessage = "No alert log file found.";
            return;
        }

        string[] lines;
        try
        {
            lines = await File.ReadAllLinesAsync(filePath);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Could not read alert log: {ex.Message}";
            return;
        }

        // Skip blank lines; FromCsvRow silently returns null for the header row
        // ("Id,Channel,…") and any genuinely malformed lines.
        int totalLines = 0;
        int parsed = 0;
        int skipped = 0;
        int duplicates = 0;

        // Build a set of existing IDs so re-running restore doesn't duplicate rows.
        var existingIds = _alertService.Alerts.Select(a => a.Id).ToHashSet();

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];

            if (string.IsNullOrWhiteSpace(line))
                continue;

            totalLines++;

            var alert = AlertEvent.FromCsvRow(line, i + 1);

            if (alert is null)
            {
                skipped++;      // header row or malformed — FromCsvRow swallows the exception
                continue;
            }

            if (existingIds.Contains(alert.Id))
            {
                duplicates++;
                continue;
            }

            _alertService.AddRestoredAlert(alert);
            existingIds.Add(alert.Id);
            parsed++;
        }

        // Build a meaningful status message covering all outcomes.
        if (parsed == 0 && skipped > 0 && duplicates == 0)
        {
            // Every data line failed — most likely an enum mismatch or wrong file format.
            StatusMessage =
                $"Restore failed: {skipped} of {totalLines} line(s) could not be parsed. " +
                "Check that the file was saved by this application " +
                "(Severity must be Info/Warning/Critical or High/Medium/Low; " +
                "Condition must be AboveMaximum/BelowMinimum or GreaterThan/LessThan).";
        }
        else if (parsed == 0 && duplicates > 0)
        {
            StatusMessage = $"Nothing new to restore — {duplicates} alert(s) already in panel.";
        }
        else
        {
            var parts = new List<string> { $"Restored {parsed} alert(s)" };
            if (skipped > 0) parts.Add($"{skipped} line(s) skipped");
            if (duplicates > 0) parts.Add($"{duplicates} duplicate(s) skipped");

            // FIX: removed the redundant OnPropertyChanged(nameof(StatusMessage)) call
            // here — the StatusMessage setter already raises it. The extra call on
            // HasAlerts is also redundant since AlertsChanged fires from the service.
            StatusMessage = string.Join(", ", parts) + ".";
        }
    }

    // UI-facing wrapper for RestoreAlertsFromCsvAsync
    private async Task OnRestoreAlertsAsync()
    {
        try
        {
            var path = Path.Combine(
                FileSystem.AppDataDirectory,
                "DropSense",
                "AlertLogs",
                "alerts_data.csv");

            await RestoreAlertsFromCsvAsync(path);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Restore failed: {ex.Message}";
        }
    }

    private void OpenLogsFolder()
    {
#if WINDOWS
        try
        {
            var path = Path.Combine(
                FileSystem.AppDataDirectory,
                "DropSense",
                "AlertLogs");

            Directory.CreateDirectory(path);

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true,
                Verb = "open"
            });
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to open logs folder: {ex.Message}";
        }
#else
        StatusMessage = "Opening logs folder is only supported on Windows.";
#endif
    }

    // ── Modal command implementations ─────────────────────────────────────────

    private async Task OnModalSaveAsync()
    {
        if (SelectedAlert is null) return;
        await _alertService.SaveAlertAsync(SelectedAlert);
        ((Command)ModalSaveCommand).ChangeCanExecute();
        StatusMessage = $"Alert #{SelectedAlert.Id} saved.";
    }

    private void OnModalDismiss()
    {
        if (SelectedAlert is null) return;
        _alertService.DismissAlert(SelectedAlert);
        ((Command)ModalDismissCommand).ChangeCanExecute();
        // Close modal after dismiss
        SelectedAlert = null;
        OnPropertyChanged(nameof(UnacknowledgedCount));
        OnPropertyChanged(nameof(BadgeText));
    }

    private void OnModalClear()
    {
        if (SelectedAlert is null) return;
        var toRemove = SelectedAlert;
        SelectedAlert = null;
        _alertService.ClearAlert(toRemove);
        ((Command)ClearAllCommand).ChangeCanExecute();
    }

    
}