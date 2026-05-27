// DropSense — Views/SettingsPage.xaml.cs
//
// SettingsPage is a thin Shell page. All real logic lives in DeviceSettingsViewModel
// which is owned by the SettingsView inside this page.

namespace DropSense.Views;

public partial class SettingsPage : ContentPage
{
    public SettingsPage()
    {
        InitializeComponent();
        // BindingContext is NOT set here — SettingsView resolves its own
        // DeviceSettingsViewModel from DI. SettingsPage stays a dumb host.
    }
}