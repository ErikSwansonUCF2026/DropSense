// DropSense — Views/SettingsView.xaml.cs
//
// ══════════════════════════════════════════════════════════════════════════════

using DropSense.ViewModels;
using System.Diagnostics;
using Microsoft.UI;


#if WINDOWS
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using WinRT.Interop;
#endif

namespace DropSense.Views;

public partial class SettingsView : ContentView
{
    private const double TwoColumnBreakpoint = 700;
    private const double ThreeColumnBreakpoint = 1100;

    private const double FlexPaddingH = 16;   // Padding="8" × 2 sides
    private const double CardMarginH = 16;   // Margin="8"  × 2 sides

    #if WINDOWS
        private AppWindow? _appWindow;
    #endif

    public SettingsView()
    {
        try
        {
            InitializeComponent();

            // Resolve the ViewModel from the MAUI DI container.
            BindingContext = App.Current!.Handler!.MauiContext!
                .Services.GetRequiredService<SettingsViewModel>();

            // Listen for layout size changes
            Loaded += OnLoaded;
            SizeChanged += (_, _) => ApplyLayout();
        }
        catch (Exception ex)
        {
            Debug.WriteLine("XAML ERROR:");
            Debug.WriteLine(ex);
            Debug.WriteLine(ex.InnerException);
            throw;
        }
    }


    private void OnLoaded(object? sender, EventArgs e)
    {
        ApplyLayout();

#if WINDOWS
        HookWindowsWindowEvents();

        if (Window is not null)
        {
            Window.SizeChanged += (_, _) =>
            {
                Dispatcher.DispatchDelayed(
                    TimeSpan.FromMilliseconds(25),
                    ApplyLayout);
            };
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

            IntPtr hwnd = WindowNative.GetWindowHandle(nativeWindow);
            WindowId windowId = Win32Interop.GetWindowIdFromWindow(hwnd);

            _appWindow = AppWindow.GetFromWindowId(windowId);

            _appWindow.Changed += (_, _) =>
            {
                // Run AFTER maximize/fullscreen sizing finishes
                Dispatcher.DispatchDelayed(
                    TimeSpan.FromMilliseconds(50),
                    () =>
                    {
                        ApplyLayout();
                    });
            };
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SettingsView] Window hook failed: {ex}");
        }
    }
#endif

    private void ApplyLayout()
    {
        if (BindingContext is not SettingsViewModel vm)
            return;

        double layoutWidth = ThresholdsLayout.Width;
        if (layoutWidth <= 0)
            return;

        // Decide max columns
        int columns = layoutWidth switch
        {
            >= ThreeColumnBreakpoint => 3,
            >= TwoColumnBreakpoint => 2,
            _ => 1
        };

        vm.ThresholdColumns = columns;

        // Remove layout padding
        double innerWidth = layoutWidth - FlexPaddingH;

        // Total gutter between cards
        double totalMargins = columns * CardMarginH;

        // Exact slot width
        double cardWidth =
            (innerWidth - totalMargins) / columns;

        cardWidth = Math.Max(280, cardWidth); // optional minimum

        foreach (var child in ThresholdsLayout.Children)
        {
            if (child is View view)
            {
                // HARD enforce width
                view.WidthRequest = cardWidth;
                view.MinimumWidthRequest = cardWidth;
                view.MaximumWidthRequest = cardWidth;

                // Prevent FlexLayout from stretching/shrinking
                FlexLayout.SetGrow(view, 0);
                FlexLayout.SetShrink(view, 0);
            }
        }
    }
}