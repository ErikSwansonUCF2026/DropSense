// DropSense — Views/PlantLibraryPage.xaml.cs
// Thin Shell page — mirrors SettingsPage.xaml.cs.
// All logic lives in PlantLibraryView / PlantLibraryViewModel.

namespace DropSense.Views;

public partial class PlantLibraryPage : ResponsiveShellPage
{
    public PlantLibraryPage()
    {
        InitializeComponent();
        // BindingContext intentionally not set here.
        // PlantLibraryView resolves PlantLibraryViewModel from DI directly.
    }
}