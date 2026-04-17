using DropSense.Services;
using System.ComponentModel;
using System.Windows.Input;

namespace DropSense.ViewModels;

public class SidebarViewModel : BaseViewModel
{
    private readonly INavigationService _nav;
    private readonly IFileSessionService _fileSession;

    public ICommand GoDashboardCommand { get; }
    public ICommand GoAlertsCommand { get; }
    public ICommand GoSettingsCommand { get; }

    public SidebarViewModel(INavigationService nav, IFileSessionService fileSession)
    {
        _nav = nav;
        _fileSession = fileSession;

        _fileSession.FileChanged += (_, __) =>
        {
            OnPropertyChanged(nameof(ActiveFileName));
            OnPropertyChanged(nameof(ActiveFilePath));
        };

        GoDashboardCommand = new Command(async () =>
            await _nav.NavigateToAsync("//DashboardPage"));

        GoAlertsCommand = new Command(async () =>
            await _nav.NavigateToAsync("//AlertsPage"));

        GoSettingsCommand = new Command(async () =>
            await _nav.NavigateToAsync("//DeviceSettingsPage"));
    }

    // ── GLOBAL FILE DISPLAY ───────────────────────────────

    public string ActiveFileName =>
        string.IsNullOrWhiteSpace(_fileSession.ActiveFileName)
        ? "No File Selected."
        : _fileSession.ActiveFileName;

    public string ActiveFilePath =>
    string.IsNullOrWhiteSpace(_fileSession.ActiveFilePath)
        ? string.Empty
        : _fileSession.ActiveFilePath;

    public event PropertyChangedEventHandler? PropertyChanged;

    public void RefreshFileState()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ActiveFileName)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ActiveFilePath)));
    }

}