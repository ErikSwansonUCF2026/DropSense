// DropSense — Views/PlantLibraryView.xaml.cs
// ══════════════════════════════════════════════════════════════════════════════
// Code-behind for the Plant Library ContentView.
//
// Responsibilities:
//   • Resolve PlantLibraryViewModel from DI (mirrors SettingsView pattern)
//   • Trigger lazy initialisation on first Appearing
//   • Drive the two-panel responsive layout (≥700 px → side-by-side; narrow → stacked)
//   • Hook Windows AppWindow.Changed for maximize / restore sizing (same as SettingsView)

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
    // ── Layout breakpoints (logical pixels) ──────────────────────────────────
    private const double SideBySideBreakpoint = 700;

    // ── Windows app-window hook ───────────────────────────────────────────────
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

            Loaded      += OnLoaded;
            SizeChanged += (_, _) => ApplyLayout();
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
        ApplyLayout();

        // Trigger lazy data load (no-op on subsequent navigations)
        if (BindingContext is PlantLibraryViewModel vm)
            _ = vm.InitializeAsync();

#if WINDOWS
        HookWindowsWindowEvents();

        if (Window is not null)
        {
            Window.SizeChanged += (_, _) =>
                Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(25), ApplyLayout);
        }
#endif
    }

#if WINDOWS
    private void HookWindowsWindowEvents()
    {
        try
        {
            if (Window?.Handler?.PlatformView is not Microsoft.UI.Xaml.Window nativeWindow)
                return;

            IntPtr hwnd     = WindowNative.GetWindowHandle(nativeWindow);
            WindowId wid    = Win32Interop.GetWindowIdFromWindow(hwnd);
            _appWindow      = AppWindow.GetFromWindowId(wid);

            _appWindow.Changed += (_, _) =>
                Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(50), ApplyLayout);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[PlantLibraryView] Window hook failed: {ex}");
        }
    }
#endif

    // ── Responsive two-panel layout ───────────────────────────────────────────
    // Wide  (≥700 px): left list (320) + right detail (*)  — Grid column 1 visible
    // Narrow (<700 px): single full-width column, right panel hidden until a
    //                   plant is selected (the XAML IsVisible binding handles the
    //                   empty-state vs detail swap; we just collapse the column).
    private void ApplyLayout()
    {
        double w = RootGrid.Width;
        if (w <= 0) return;

        bool sideBySide = w >= SideBySideBreakpoint;

        var col0 = RootGrid.ColumnDefinitions[0];
        var col1 = RootGrid.ColumnDefinitions[1];

        if (sideBySide)
        {
            col0.Width = new Microsoft.Maui.GridLength(320, Microsoft.Maui.GridUnitType.Absolute);
            col1.Width = new Microsoft.Maui.GridLength(1, Microsoft.Maui.GridUnitType.Star);
        }
        else
        {
            // On narrow screens collapse the right column; the parent page's
            // navigation can push a separate detail page if desired.
            col0.Width = new Microsoft.Maui.GridLength(1, Microsoft.Maui.GridUnitType.Star);
            col1.Width = new Microsoft.Maui.GridLength(0, Microsoft.Maui.GridUnitType.Absolute);
        }
    }
}
