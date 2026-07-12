// DropSense — Views/SettingsView.xaml.cs
//
// ══════════════════════════════════════════════════════════════════════════════

using DropSense.ViewModels;
using System.Diagnostics;


#if WINDOWS
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using WinRT.Interop;
using Microsoft.UI;

#endif

namespace DropSense.Views;

public partial class SettingsView : ContentView
{
    private const double TwoColumnBreakpoint = 700;
    private const double ThreeColumnBreakpoint = 1100;

    private const double FlexPaddingH = 16;   // Padding="8" × 2 sides
    private const double CardMarginH = 16;   // Margin="8"  × 2 sides

    // Keep references to the delegates we subscribe with so we can
    // unhook them again. Without this, Android's aggressive view
    // recycling (page re-navigation, CollectionView reuse, etc.)
    // stacks up subscriptions and leaks the old ViewModel/View.
    private EventHandler? _sizeChangedHandler;
    private EventHandler? _windowSizeChangedHandler;

#if WINDOWS
    private AppWindow? _appWindow;
    private Windows.Foundation.TypedEventHandler<AppWindow, AppWindowChangedEventArgs>? _appWindowChangedHandler;
#endif

    public SettingsView()
    {
        try
        {
            InitializeComponent();

            // Resolve the ViewModel from the MAUI DI container.
            BindingContext = App.Current!.Handler!.MauiContext!
                .Services.GetRequiredService<SettingsViewModel>();

            // Listen for layout size changes.
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;

            _sizeChangedHandler = (_, _) => SafeApplyLayout();
            SizeChanged += _sizeChangedHandler;
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
        SafeApplyLayout();

#if WINDOWS
        HookWindowsWindowEvents();

        if (Window is not null)
        {
            _windowSizeChangedHandler = (_, _) =>
            {
                Dispatcher.DispatchDelayed(
                    TimeSpan.FromMilliseconds(25),
                    SafeApplyLayout);
            };
            Window.SizeChanged += _windowSizeChangedHandler;
        }
#endif
    }

    private void OnUnloaded(object? sender, EventArgs e)
    {
        // Detach everything so nothing keeps firing (or keeps this
        // view/ViewModel alive) once it's off screen. This matters much
        // more on Android, where ContentView instances get torn down
        // and recreated far more often than on Windows.
#if WINDOWS
        if (Window is not null && _windowSizeChangedHandler is not null)
        {
            Window.SizeChanged -= _windowSizeChangedHandler;
            _windowSizeChangedHandler = null;
        }

        if (_appWindow is not null && _appWindowChangedHandler is not null)
        {
            _appWindow.Changed -= _appWindowChangedHandler;
            _appWindowChangedHandler = null;
        }
        _appWindow = null;
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

            _appWindowChangedHandler = (_, _) =>
            {
                // Run AFTER maximize/fullscreen sizing finishes
                Dispatcher.DispatchDelayed(
                    TimeSpan.FromMilliseconds(50),
                    SafeApplyLayout);
            };
            _appWindow.Changed += _appWindowChangedHandler;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SettingsView] Window hook failed: {ex}");
        }
    }
#endif

    /// <summary>
    /// Wraps ApplyLayout so a stray platform-specific measure/layout
    /// timing issue (far more common on Android than Windows) can't
    /// crash the app — it just skips that layout pass and logs it.
    /// </summary>
    private void SafeApplyLayout()
    {
        try
        {
            ApplyLayout();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SettingsView] ApplyLayout failed: {ex}");
        }
    }

    private void ApplyLayout()
    {
        if (BindingContext is not SettingsViewModel vm)
            return;

        if (ThresholdsLayout is null)
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