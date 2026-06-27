using DropSense.Services;
using DropSense.Views;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows.Input;


namespace DropSense.ViewModels;

public class SidebarViewModel : BaseViewModel
{
    private readonly INavigationService _nav;
    private readonly IFileSessionService _fileSession;
    private readonly IAlertService _alertService;
    private readonly IDebugLogService _debugLogService;


    public ICommand GoDashboardCommand { get; }
    public ICommand GoAlertsCommand { get; }
    public ICommand GoSettingsCommand { get; }
    public ICommand GoExportCommand { get; }
    public ICommand GoPlantLibraryCommand { get; }

    public ICommand OpenFileCommand { get; }
    public ICommand ToggleLogCommand { get; }
    public ICommand ClearLogCommand { get; }

    public SidebarViewModel(
    INavigationService nav,
    IFileSessionService fileSession,
    IAlertService alertService,
    IDebugLogService debugLogService)
    {
        _nav = nav;
        _fileSession = fileSession;
        _alertService = alertService;
        _debugLogService = debugLogService;

        _fileSession.FileChanged += (_, __) =>
        {
            OnPropertyChanged(nameof(ActiveFileName));
            OnPropertyChanged(nameof(ActiveFilePath));
        };

        // ── ALERT BADGE UPDATES ───────────────────────────
        _alertService.AlertsChanged += (_, __) =>
        {
            OnPropertyChanged(nameof(AlertBadgeText));
            OnPropertyChanged(nameof(HasAlerts));
        };
        
        ToggleLogCommand = new Command(ToggleLog);
        ClearLogCommand = new Command(ClearLog);

        GoDashboardCommand = new Command(async () =>
            await _nav.NavigateToAsync("DashboardPage"));

        GoAlertsCommand = new Command(async () =>
            await _nav.NavigateToAsync("AlertsPage"));

        GoSettingsCommand = new Command(async () =>
            await _nav.NavigateToAsync("SettingsPage"));

        GoExportCommand = new Command(async () =>
             await _nav.NavigateToAsync($"/{nameof(AnalysisExportPage)}"));

        GoPlantLibraryCommand = new Command(async () =>
            await _nav.NavigateToAsync("PlantLibraryPage"));

        OpenFileCommand = new Command(OpenFile);

    }

    // ── GLOBAL FILE DISPLAY ───────────────────────────────
    private void OpenFile()
    {
        if (string.IsNullOrWhiteSpace(ActiveFilePath))
            return;

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = ActiveFilePath,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }
    }
    public bool HasAlerts =>
        _alertService.UnacknowledgedCount > 0;

    public string AlertBadgeText =>
        _alertService.UnacknowledgedCount > 99
            ? "99+"
            : _alertService.UnacknowledgedCount.ToString();

    public string ActiveFileName =>
        string.IsNullOrWhiteSpace(_fileSession.ActiveFileName)
        ? "No File Selected."
        : _fileSession.ActiveFileName;

    public string ActiveFilePath =>
    string.IsNullOrWhiteSpace(_fileSession.ActiveFilePath)
        ? string.Empty
        : _fileSession.ActiveFilePath;

    public ObservableCollection<LogEntry> LogEntries => _debugLogService.Entries;

    private bool _isLogExpanded = false;

    public bool IsLogExpanded
    {
        get => _isLogExpanded;
        private set
        {
            _isLogExpanded = value;
            OnPropertyChanged(nameof(IsLogExpanded));
        }
    }
    private void ToggleLog() => IsLogExpanded = !IsLogExpanded; 
    private void ClearLog() => _debugLogService.Clear();
}