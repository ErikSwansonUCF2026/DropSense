using DropSense.Services;
using System.ComponentModel;
using System.Windows.Input;
using System.Diagnostics;


namespace DropSense.ViewModels;

public class SidebarViewModel : BaseViewModel
{
    private readonly INavigationService _nav;
    private readonly IFileSessionService _fileSession;

    public ICommand GoDashboardCommand { get; }
    public ICommand GoAlertsCommand { get; }
    public ICommand GoSettingsCommand { get; }
    public ICommand OpenFileCommand { get; }

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
            await _nav.NavigateToAsync("DashboardPage"));

        GoAlertsCommand = new Command(async () =>
            await _nav.NavigateToAsync("//AlertsPage"));

        GoSettingsCommand = new Command(async () =>
            await _nav.NavigateToAsync("SettingsPage"));
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


    public string ActiveFileName =>
        string.IsNullOrWhiteSpace(_fileSession.ActiveFileName)
        ? "No File Selected."
        : _fileSession.ActiveFileName;

    public string ActiveFilePath =>
    string.IsNullOrWhiteSpace(_fileSession.ActiveFilePath)
        ? string.Empty
        : _fileSession.ActiveFilePath;
}