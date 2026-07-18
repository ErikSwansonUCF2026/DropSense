// DropSense — ViewModels/DashboardViewModel.cs
// ══════════════════════════════════════════════════════════════════════════════

//using DropSense.Models;
using DropSense.Services;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace DropSense.ViewModels;

public class DashboardViewModel : BaseViewModel
{
    // ── Service fields ─────────────────────────────────────────────────────────
    private readonly ISettingsService _settings;
    private readonly IDeviceConnectionService _connectionService;
    private readonly IFileSelectorService _fileSelector;
    private readonly IFileSessionService _fileSession;
    private readonly ICsvService _csvService;
    private readonly IAlertService _alertService;
    // Step 6 — add field when IAlertService.cs is added:
    // private readonly IAlertService _alertService;

    // Step 8 — add field when IPlantLibraryService.cs is added:
    // private readonly IPlantLibraryService _plantLibrary;

    // ── Constructor ────────────────────────────────────────────────────────────
    public DashboardViewModel(
        ISettingsService settings,
        IDeviceConnectionService connectionService,
        IFileSelectorService fileSelector,
        IFileSessionService fileSession,
        ICsvService csvService, IAlertService alertService)

    //   Step 5: add IDataAnalysisService analysisService
    //   Step 6: add IAlertService alertService
    //   Step 8: add IPlantLibraryService plantLibrary
    {
        _settings = settings;

        _connectionService = connectionService;
        _connectionService.ConnectionStateChanged += (_, state) => OnConnectionStateChanged(state);
        _state = ConnectionState.Disconnected;

        _fileSelector = fileSelector;
        _fileSession = fileSession;
        _csvService = csvService;
        _alertService = alertService;

        // Step 8 — assign:
        // _plantLibrary = plantLibrary;

        // ── Restore persisted alert polling preference ─────────────────────────
        // Read the preference that StopAlertPolling/StartAlertPollingAsync persist
        // so the chip reflects the correct state immediately on first load.
        _isAlertPollingEnabled = Preferences.Get("alert_polling_enabled", false);

        // ── Commands ───────────────────────────────────────────────────────────
        RequestDownloadCommand = new Command(async () => await OnRequestDownloadAsync(), () => !IsBusy);
        ExportCsvCommand = new Command(async () => await OnExportCsvAsync());
        TestConnectionCommand = new Command(async () => await TestConnectionAsync());
        LoadCsvCommand = new Command(async () => await LoadCsvAsync());
        ToggleAlertPollingCommand = new Command(async () => await OnToggleAlertPollingAsync());
    }

    // ── Observable Properties ──────────────────────────────────────────────────

    public string ActiveFileName =>
        string.IsNullOrWhiteSpace(_fileSession.ActiveFileName)
            ? "No File Selected."
            : _fileSession.ActiveFileName;

    public string ActiveFilePath =>
        string.IsNullOrWhiteSpace(_fileSession.ActiveFilePath)
            ? string.Empty
            : _fileSession.ActiveFilePath;

    public bool IsFileLoaded => !string.IsNullOrEmpty(ActiveFilePath);

    // ── Connection ─────────────────────────────────────────────────────────────

    private string _connectionLabel = "Not connected";
    public string ConnectionLabel
    {
        get => _connectionLabel;
        set { _connectionLabel = value; OnPropertyChanged(); }
    }

    private ConnectionState _state;
    public ConnectionState State
    {
        get => _state;
        set => SetProperty(ref _state, value);
    }

    private string? _connectedDeviceName;
    public string? ConnectedDeviceName
    {
        get => _connectedDeviceName;
        set => SetProperty(ref _connectedDeviceName, value);
    }

    public string BluetoothStatusText =>
        _connectionService.IsBluetoothOn ? "Bluetooth Enabled" : "Bluetooth Disabled";

    public Color BluetoothStatusColor =>
        _connectionService.IsBluetoothOn ? Colors.LimeGreen : Colors.Red;

    private bool _isConnected;
    public bool IsConnected
    {
        get => _isConnected;
        set { _isConnected = value; OnPropertyChanged(); }
    }

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        set { _isBusy = value; OnPropertyChanged(); }
    }

    private string? _lastDownloadedFile;
    public string? LastDownloadedFile
    {
        get => _lastDownloadedFile;
        set { _lastDownloadedFile = value; OnPropertyChanged(); }
    }

    private DateTime? _lastSyncTime;
    public DateTime? LastSyncTime
    {
        get => _lastSyncTime;
        set
        {
            _lastSyncTime = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(LastSyncDisplay));
        }
    }
    private DateTime? _lastAlertPoll;
    public DateTime? LastAlertPoll
    {
        get => _lastAlertPoll;
        set
        {
            _lastAlertPoll = value;
            OnPropertyChanged();
            OnPropertyChanged("Last Alert");
        }
    }


    public string? LastDeviceName => _settings.LastConnectedDeviceName;
    public string? LastDeviceID => _settings.LastConnectedDeviceId;

    private int _downloadProgress;
    public int DownloadProgress
    {
        get => _downloadProgress;
        set
        {
            if (SetProperty(ref _downloadProgress, value))
            {
                OnPropertyChanged(nameof(DownloadProgressNormalized));
                OnPropertyChanged(nameof(DownloadProgressText));
            }
        }
    }

    public double DownloadProgressNormalized => DownloadProgress / 100.0;
    public string DownloadProgressText => $"{DownloadProgress}%";

    // ── Alert Polling ──────────────────────────────────────────────────────────

    /// <summary>
    /// Reflects whether background alert polling is currently running.
    /// The XAML VisualStateManager switches the chip between "PollingOn"
    /// and "PollingOff" by watching this property.
    /// </summary>
    private bool _isAlertPollingEnabled;
    public bool IsAlertPollingEnabled
    {
        get => _isAlertPollingEnabled;
        set
        {
            if (SetProperty(ref _isAlertPollingEnabled, value))
                // Drive the VSM state so the chip re-colours itself
                OnPropertyChanged(nameof(AlertPollingVisualState));
        }
    }

    /// <summary>
    /// Convenience string consumed by a VisualStateManager trigger in XAML
    /// (if using a behaviour) or directly as a trigger value.
    /// Returns "PollingOn" or "PollingOff".
    /// </summary>
    public string AlertPollingVisualState =>
        _isAlertPollingEnabled ? "PollingOn" : "PollingOff";

    // Step 6 — alert badge
    private int _badgeCount;
    public int BadgeCount
    {
        get => _badgeCount;
        set { _badgeCount = value; OnPropertyChanged(); }
    }

    public ObservableCollection<string> ExceptionLog { get; } = new();

    // ── Commands ───────────────────────────────────────────────────────────────
    public ICommand RequestDownloadCommand { get; }
    public ICommand ExportCsvCommand { get; }
    public ICommand ExportXlsxCommand { get; }
    public ICommand TestConnectionCommand { get; }
    public ICommand LoadCsvCommand { get; }

    /// <summary>
    /// Bound to the Alert Polling chip in the toolbar.
    /// Calls StartAlertPollingAsync or StopAlertPolling on the connection
    /// service depending on the current state, then flips IsAlertPollingEnabled.
    /// </summary>
    public ICommand ToggleAlertPollingCommand { get; }

    // ── Command Implementations ────────────────────────────────────────────────

    /// <summary>
    /// Starts or stops background alert polling and updates the UI chip.
    /// The interval (60 s default) can be surfaced as a settings property later.
    /// </summary>
    private async Task OnToggleAlertPollingAsync()
    {
        try
        {
            if (IsAlertPollingEnabled)
            {
                _connectionService.StopAlertPolling();
                IsAlertPollingEnabled = false;
            }
            else
            {
                int interval = Preferences.Get("settings_alert_interval", defaultValue: 300);
                _connectionService.StartAlertPollingAsync(interval, _alertService);
                IsAlertPollingEnabled = true;
            }
        }
        catch (Exception ex)
        {
            LogException(ex);
        }

        await Task.CompletedTask;
    }

    private async Task TestConnectionAsync()
    {
        try
        {
            await _connectionService.ExecuteWithConnectionAsync(
                async (device, ct) =>
                {
                    State = ConnectionState.Connected;
                    await Task.Delay(500, ct);
                    ConnectedDeviceName = device.Name;
                    LastSyncTime = DateTime.UtcNow;
                },
                stayConnected: false);
        }
        catch (Exception ex)
        {
            ConnectedDeviceName = null;
            LogException(ex);
        }
    }

    private async Task OnOpenCsvAsync(string targetFilePath)
    {
        if (string.IsNullOrWhiteSpace(targetFilePath))
            return;

        try
        {
            if (!File.Exists(targetFilePath))
            {
                LogException(new FileNotFoundException("File not found.", targetFilePath));
                return;
            }

            await Launcher.Default.OpenAsync(new OpenFileRequest
            {
                File = new ReadOnlyFile(targetFilePath)
            });
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            LogException(ex);
        }
    }

    private async Task OnRequestDownloadAsync()
    {
        if (IsBusy)
            return;

        IsBusy = true;
        ((Command)RequestDownloadCommand).ChangeCanExecute();

        try
        {
            var progress = new Progress<int>(pct =>
                MainThread.BeginInvokeOnMainThread(() => DownloadProgress = pct));

            var filePath = await _connectionService.RequestDataDownloadAsync(progress, stayConnected: false);

            if (!string.IsNullOrWhiteSpace(filePath))
            {
                LastDownloadedFile = filePath;
                LastSyncTime = DateTime.UtcNow;
                _fileSession.SetActiveFile(filePath);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            LogException(ex);
        }
        finally
        {
            IsBusy = false;
            ((Command)RequestDownloadCommand).ChangeCanExecute();
        }
    }

    private async Task LoadCsvAsync()
    {
        var filePath = await _fileSelector.PickCsvFileAsync();

        if (string.IsNullOrWhiteSpace(filePath))
            return;

        _fileSession.SetActiveFile(filePath);
    }

    private async Task OnExportCsvAsync()
    {
        var filePath = IsFileLoaded
            ? ActiveFilePath
            : await _fileSelector.PickCsvFileAsync();

        try
        {
            if (filePath is null)
                return;

            var result = await _csvService.ParseAsync(filePath);
            ExceptionLog.Insert(0,
                $"Parsed {result.Rows.Count} rows with {result.ErrorCount} errors " +
                $"and {result.WarningCount} warnings. Computed CSV at: {result.ComputedCsvPath}");
            await OnOpenCsvAsync(result.ComputedCsvPath);
        }
        catch (Exception ex)
        {
            LogException(ex);
        }
    }

   

    // ── Connection state handler ───────────────────────────────────────────────

    private void OnConnectionStateChanged(ConnectionState state)
    {
        IsConnected = state == ConnectionState.Connected;

        ConnectionLabel = state switch
        {
            ConnectionState.Connected => $"● {_connectionService.ConnectedDeviceName}",
            ConnectionState.Connecting => "Connecting…",
            ConnectionState.Transferring => "Transferring…",
            ConnectionState.Error => "Connection error",
            _ => "Not connected"
        };

        OnPropertyChanged(nameof(LastDeviceName));
        OnPropertyChanged(nameof(LastDeviceID));

        ((Command)RequestDownloadCommand).ChangeCanExecute();
    }

    public string LastSyncDisplay
    {
        get
        {
            if (LastSyncTime is null)
                return "Never";

            var elapsed = DateTime.UtcNow - LastSyncTime.Value;

            string relative = elapsed.TotalMinutes switch
            {
                < 1 => "just now",
                < 60 => $"{(int)elapsed.TotalMinutes} min ago",
                < 1440 => $"{(int)elapsed.TotalHours} hr ago",
                _ => $"{(int)elapsed.TotalDays} days ago"
            };

            return $"{LastSyncTime:yyyy-MM-dd HH:mm:ss} ({relative})";
        }
    }

    public string LastAlertPollDisplay
    {
        get
        {
            if (LastAlertPoll is null)
                return "Never";

            var elapsed = DateTime.UtcNow - LastAlertPoll.Value;

            string relative = elapsed.TotalMinutes switch
            {
                < 1 => "just now",
                < 60 => $"{(int)elapsed.TotalMinutes} min ago",
                < 1440 => $"{(int)elapsed.TotalHours} hr ago",
                _ => $"{(int)elapsed.TotalDays} days ago"
            };

            return $"{LastAlertPoll:yyyy-MM-dd HH:mm:ss} ({relative})";
        }
    }

    private void LogException(Exception ex)
    {
        MainThread.BeginInvokeOnMainThread(() =>
            ExceptionLog.Insert(0, $"{DateTime.Now:HH:mm:ss} — {ex.Message}"));
    }

 
}