// DropSense — ViewModels/DashboardViewModel.cs
// ══════════════════════════════════════════════════════════════════════════════
// ADD TO PROJECT: Step 1
// ══════════════════════════════════════════════════════════════════════════════
// The Dashboard is the first fully functional page. Only the ISettingsService
// dependency is live at Step 1. All other injected services are introduced and
// uncommented as their implementations are added in subsequent steps.

//using DropSense.Models;
using DropSense.Services;
using Microsoft.Maui.Animations;
using Plugin.BLE.Abstractions.Contracts;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace DropSense.ViewModels;

public class DashboardViewModel : BaseViewModel
{
    // ── Step 1: only settings needed to construct without errors ──────────────────
    private readonly ISettingsService _settings;

    // Step 2 — add field when IDeviceConnectionService.cs is added:
    private readonly IDeviceConnectionService _connectionService;

    private readonly IFileSelectorService _fileSelector;
    private readonly IFileSessionService _fileSession;

    // Step 4 — add field when ICsvService.cs is added:
    // private readonly ICsvService _csvService;

    // Step 5 — add field when IDataAnalysisService.cs is added:
    // private readonly IDataAnalysisService _analysisService;

    // Step 6 — add field when IAlertService.cs is added:
    // private readonly IAlertService _alertService;

    // Step 8 — add field when IPlantLibraryService.cs is added:
    // private readonly IPlantLibraryService _plantLibrary;

    // ── Step 1 constructor ────────────────────────────────────────────────────────
    public DashboardViewModel(ISettingsService settings, IDeviceConnectionService connectionService, IFileSelectorService fileSelector, IFileSessionService fileSession)
    // Steps 2-8: progressively expand the constructor signature:
    //   Step 2: add IDeviceConnectionService connectionService
    //   Step 4: add ICsvService csvService
    //   Step 5: add IDataAnalysisService analysisService
    //   Step 6: add IAlertService alertService
    //   Step 8: add IPlantLibraryService plantLibrary
    {
        _settings = settings;

        // Step 2 — assign and subscribe:
        _connectionService = connectionService;
        _connectionService.ConnectionStateChanged += (_, state) => OnConnectionStateChanged(state);
        _state = ConnectionState.Disconnected;

        _fileSelector = fileSelector;
        _fileSession = fileSession;


        // Step 6 — assign and subscribe:
        // _alertService = alertService;
        // _alertService.AlertsChanged += (_, _) => RefreshAlerts();

        // Step 8 — assign:
        // _plantLibrary = plantLibrary;

        // ── Commands ───────────────────────────────────────────────────────────────
        // Step 1: placeholder commands (no-ops until backing services exist)
        OpenCsvCommand = new Command(async () => await OnOpenCsvAsync());
        RequestDownloadCommand = new Command(async () => await OnRequestDownloadAsync(), () => !IsBusy);
        ExportCsvCommand = new Command(async () => await OnExportCsvAsync(), () => IsFileLoaded);
        ExportXlsxCommand = new Command(async () => await OnExportXlsxAsync(), () => IsFileLoaded);
        TestConnectionCommand = new Command(async () => await TestConnectionAsync());
        LoadCsvCommand = new Command(async () => await LoadCsvAsync());


    }


    // ── Observable Properties ──────────────────────────────────────────────────────

    // Step 1: file state (UI elements may be bound to these even before CSV is implemented)
    private string? _activeFileName;
    public string? ActiveFileName
    {
        get => _activeFileName;
        set { _activeFileName = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsFileLoaded)); }
    }
    public bool IsFileLoaded => !string.IsNullOrEmpty(ActiveFileName);

    // Step 2 — connection display properties (bind in XAML now; values populate at Step 2):
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

    private List<IDevice> _devices = new();

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
        set
        {
            _isBusy = value;
            OnPropertyChanged();
        }
    }

    private string? _lastDownloadedFile;
    public string? LastDownloadedFile
    {
        get => _lastDownloadedFile;
        set
        {
            _lastDownloadedFile = value;
            OnPropertyChanged();
        }
    }

    // Step 4 — sensor metric card properties (bind in XAML; values populate at Step 4):
    // TODO: Add LatestTemperature, LatestHumidity, LatestPressure, LatestIrradiance (double?)
    // TODO: Add TemperatureStatus, HumidityStatus, PressureStatus, IrradianceStatus (string)
    //        each returning "Ok", "Warn", or "Alert" for VisualState binding

    // Step 5 — derived statistics chips (bind in XAML; values populate at Step 5):
    // TODO: Add LatestVpd, LatestHeatIndex, LatestDewPoint, LatestDli (string — formatted with units)

    // Step 5 — chart sparkline data (bind in XAML; values populate at Step 5):
    // TODO: Add TemperaturePoints, HumidityPoints, PressurePoints, IrradiancePoints (IList<Point>)

    // Step 6 — active alerts summary for the dashboard card:
    // public ObservableCollection<Alert> ActiveAlerts { get; } = new();

    private int _badgeCount;
    public int BadgeCount
    {
        get => _badgeCount;
        set { _badgeCount = value; OnPropertyChanged(); }
    }

    // ── Commands ───────────────────────────────────────────────────────────────────
    public ICommand OpenCsvCommand { get; }
    public ICommand RequestDownloadCommand { get; }
    public ICommand ExportCsvCommand { get; }
    public ICommand ExportXlsxCommand { get; }
    public ICommand TestConnectionCommand { get; }

    public ICommand LoadCsvCommand { get; }

    public int DownloadProgress { get; private set; }


    // ── Command Implementations ────────────────────────────────────────────────────

    private async Task TestConnectionAsync()
    {
       
        try
        {
            await _connectionService.ExecuteWithConnectionAsync(async device =>
            {
                // Example operation (replace with real BLE logic)
                await Task.Delay(500);

                ConnectedDeviceName = device.Name;
                State = ConnectionState.Connected;
            });
        }
        catch
        {
            State = ConnectionState.Disconnected;
            ConnectedDeviceName = null;
        }
    }


    private async Task OnOpenCsvAsync()
    {
        // Step 4 — implement:
        // TODO: Show MAUI FilePicker filtered to .csv
        // TODO: Pass selected path to _csvService.ParseAsync()
        // TODO: Set ActiveFileName; update metric card properties with latest reading values
        // TODO: Step 5+: Run _analysisService.AnalyzeAsync() to compute derived stats
        await Task.CompletedTask; // remove at Step 4
    }

    private async Task OnRequestDownloadAsync()
    {
        if (IsBusy)
            return;

        IsBusy = true;

        try
        {
            var progress = new Progress<int>(value =>
            {
                DownloadProgress = value;
            });

            var filePath = await _connectionService.RequestDataDownloadAsync(progress);

            LastDownloadedFile = filePath;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(ex);
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
        // Step 5 — implement:
        // TODO: Guard: IsFileLoaded must be true
        // TODO: Shell.Current.GoToAsync(nameof(ExportWizardPage) + "?format=csv")
        await Task.CompletedTask; // remove at Step 5
    }

    private async Task OnExportXlsxAsync()
    {
        // Step 7 — implement (XLSX export requires full analysis pipeline):
        // TODO: Guard: IsFileLoaded must be true
        // TODO: Shell.Current.GoToAsync(nameof(ExportWizardPage) + "?format=xlsx")
        await Task.CompletedTask; // remove at Step 7
    }

    // ── Step 2: Connection state handler ──────────────────────────────────────────
    private void OnConnectionStateChanged(ConnectionState state)
    {
        IsConnected     = state == ConnectionState.Connected;
        ConnectionLabel = state switch
        {
            ConnectionState.Connected    => $"● {_connectionService.ConnectedDeviceName}",
            ConnectionState.Connecting   => "Connecting…",
            ConnectionState.Transferring => "Transferring…",
            ConnectionState.Error        => "Connection error",
            _                            => "Not connected"
        };
        ((Command)RequestDownloadCommand).ChangeCanExecute();
    }

    // ── Step 6: Alert refresh helper ──────────────────────────────────────────────
    // Uncomment at Step 6:
    // private void RefreshAlerts()
    // {
    //     ActiveAlerts.Clear();
    //     foreach (var a in _alertService.Alerts.Where(a => a.State == AlertState.Active).Take(3))
    //         ActiveAlerts.Add(a);
    //     BadgeCount = _alertService.UnacknowledgedCount;
    // }

    // ── INotifyPropertyChanged ─────────────────────────────────────────────────────
}