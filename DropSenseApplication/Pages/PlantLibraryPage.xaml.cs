// DropSense — Views/PlantLibraryPage.xaml.cs
// Dumb host page — all logic lives in PlantLibraryView / PlantLibraryViewModel.

namespace DropSense.Views;

public partial class PlantLibraryPage : ContentPage
{
    public PlantLibraryPage()
    {
        InitializeComponent();
        // BindingContext intentionally not set here.
        // PlantLibraryView resolves PlantLibraryViewModel from DI directly.
    }
}
