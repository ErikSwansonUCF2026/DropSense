using DropSense.Services;
using DropSense.Views;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Input;


namespace DropSense.ViewModels;

public class SidebarViewModel : BaseViewModel
{
    private readonly INavigationService _nav;
    private readonly IFileSessionService _fileSession;
    private readonly IAlertService _alertService;
    private readonly IDebugLogService _debugLogService;
    private readonly IFileSelectorService _fileSelector;

    private bool isMobile;

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
        IDebugLogService debugLogService,
        IFileSelectorService fileSelector)
    {
        _nav = nav;
        _fileSession = fileSession;
        _alertService = alertService;
        _debugLogService = debugLogService;
        _fileSelector = fileSelector;

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

        isMobile = (DeviceInfo.Idiom == DeviceIdiom.Phone) || (DeviceInfo.Idiom == DeviceIdiom.Tablet);

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

        OpenFileCommand = new Command(async () => await OpenFileAsync());
    }

    // ── GLOBAL FILE DISPLAY ───────────────────────────────
    private async Task OpenFileAsync()
    {
        try
        {
            // No active file — behave like Dashboard's LoadCsvAsync: prompt the user to pick one
            if (string.IsNullOrWhiteSpace(ActiveFilePath))
            {
                var pickedPath = await _fileSelector.PickCsvFileAsync();
                if (string.IsNullOrWhiteSpace(pickedPath))
                    return;

                _fileSession.SetActiveFile(pickedPath);
                return;
            }

            if (!File.Exists(ActiveFilePath))
            {
                Debug.WriteLine($"Active file no longer exists: {ActiveFilePath}");
                return;
            }

            await Launcher.Default.OpenAsync(new OpenFileRequest
            {
                File = new ReadOnlyFile(ActiveFilePath)
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