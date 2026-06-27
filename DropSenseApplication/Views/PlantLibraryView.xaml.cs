// DropSense — Views/PlantLibraryView.xaml.cs
// ══════════════════════════════════════════════════════════════════════════════
// Code-behind for PlantLibraryView.
//
// Responsibilities:
//   • Resolve PlantLibraryViewModel from DI  (mirrors SettingsView pattern)
//   • Trigger lazy initialisation on Loaded
//   • Drive the Add-Plant accordion open/close (code-behind is fine here —
//     it is pure view animation state, not business logic)
//   • Hook Windows AppWindow.Changed for maximize/restore sizing

using System.Diagnostics;
using DropSense.ViewModels;

#if WINDOWS
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using WinRT.Interop;
using Microsoft.UI;
#endif

namespace DropSense.Views;

public partial class PlantLibraryView : ContentView
{
    private bool _addPlantOpen;

#if WINDOWS
    private AppWindow? _appWindow;
#endif

    public PlantLibraryView()
    {
        try
        {
            InitializeComponent();

            // Resolve VM from MAUI DI — identical pattern to SettingsView
            BindingContext = App.Current!.Handler!.MauiContext!
                .Services.GetRequiredService<PlantLibraryViewModel>();

            Loaded += OnLoaded;
        }
        catch (Exception ex)
        {
            Debug.WriteLine("PlantLibraryView XAML ERROR:");
            Debug.WriteLine(ex);
            Debug.WriteLine(ex.InnerException);
            throw;
        }
    }

    // ── First load ────────────────────────────────────────────────────────────
    private void OnLoaded(object? sender, EventArgs e)
    {
        if (BindingContext is PlantLibraryViewModel vm)
            _ = vm.InitializeAsync();

#if WINDOWS
        HookWindowsWindowEvents();
#endif
    }

#if WINDOWS
    private void HookWindowsWindowEvents()
    {
        try
        {
            if (Window?.Handler?.PlatformView is not Microsoft.UI.Xaml.Window nativeWindow)
                return;

            IntPtr hwnd = WindowNative.GetWindowHandle(nativeWindow);
            WindowId windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
            _appWindow = AppWindow.GetFromWindowId(windowId);

            _appWindow.Changed += (_, _) =>
                Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(50), () => { });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[PlantLibraryView] Window hook failed: {ex}");
        }
    }
#endif

    // ── Add-Plant accordion toggle ────────────────────────────────────────────
    // This is intentionally in code-behind: it is pure view animation state
    // (show/hide a VerticalStackLayout) with no business logic involved.
    private void OnAddPlantToggled(object sender, EventArgs e)
    {
        _addPlantOpen = !_addPlantOpen;

        AddPlantForm.IsVisible = _addPlantOpen;
        AddPlantChevron.Text = _addPlantOpen ? "✕" : "＋";

        // Also clear the form fields when closing via Cancel
        if (!_addPlantOpen && BindingContext is PlantLibraryViewModel vm)
            vm.ClearNewPlantForm();
    }
}