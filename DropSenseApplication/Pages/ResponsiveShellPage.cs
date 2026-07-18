using System;
using System.Collections.Generic;
using System.Text;


// DropSense — Views/ResponsiveShellPage.cs
using DropSense.ViewModels;

namespace DropSense.Views;

/// <summary>
/// Base class for any page using the Sidebar + Header + Content layout.
/// Handles the phone/tablet/desktop breakpoints and the slide-over sidebar,
/// so derived pages only need to InitializeComponent().
///
/// Each derived page's XAML MUST use these exact x:Name values:
///   SidebarLayout   — Grid shown on tablet + desktop
///   MobileLayoutRoot— Grid shown on phone
///   DesktopSidebar  — SidebarView inside SidebarLayout
///   MobileSidebar   — SidebarView inside MobileLayoutRoot (slide-over)
///   Overlay         — BoxView dimmer behind the slide-over sidebar
/// A hamburger button's Clicked handler should be MenuButton_Clicked,
/// and the overlay's Tapped handler should be OverlayTapped — both
/// inherited from here, don't redeclare them per page.
/// </summary>
public abstract class ResponsiveShellPage : ContentPage
{
    protected const double MobileBreakpoint = 700;   // below → phone layout

    bool _mobileSidebarOpen;
    double _lastWidth = -1;
    protected ResponsiveShellPage()
    {
    }

    protected override void OnSizeAllocated(double width, double height)
    {
        base.OnSizeAllocated(width, height);
        if (width > 0 && Math.Abs(width - _lastWidth) > 0.5)
        {
            _lastWidth = width;
            ApplyLayout(width);
        }
    }

    // Implement layout logic here (allow overrides in derived pages)
    protected virtual void ApplyLayout(double width)
    {
        if (width <= 0) return;

        bool isMobile = width < MobileBreakpoint;
        if (this.FindByName<VisualElement>("SidebarLayout") is { } sidebarLayout)
            sidebarLayout.IsVisible = !isMobile;

        if (this.FindByName<VisualElement>("MobileLayoutRoot") is { } mobileLayout)
            mobileLayout.IsVisible = isMobile;

        if (this.FindByName<SidebarView>("DesktopSidebar") is { } desktopSidebar)
            desktopSidebar.IsCompact = false;

        // Window grew back past the mobile breakpoint (rotation, or a
        // resizable desktop window) — reset the slide-over state so it's
        // not left "open" underneath the now-visible desktop sidebar.
        if (!isMobile && _mobileSidebarOpen)
            CloseMobileSidebar(animate: false);

        if (BindingContext is IResponsiveAware aware)
            aware.OnWidthChanged(width);
    }

    // ── Hamburger / overlay — wire these up by name in each page's XAML ──
    protected void MenuButton_Clicked(object? sender, EventArgs e) => ToggleMobileSidebar();
    protected void OverlayTapped(object? sender, TappedEventArgs e) => CloseMobileSidebar();

    void ToggleMobileSidebar()
    {
        if (_mobileSidebarOpen) CloseMobileSidebar();
        else OpenMobileSidebar();
    }

    void OpenMobileSidebar()
    {
        var overlay = this.FindByName<VisualElement>("Overlay");
        var sidebar = this.FindByName<VisualElement>("MobileSidebar");
        if (overlay is null || sidebar is null) return;

        _mobileSidebarOpen = true;
        overlay.IsVisible = true;
        _ = overlay.FadeTo(1, 150);
        _ = sidebar.TranslateTo(0, 0, 200, Easing.CubicOut);
    }

    void CloseMobileSidebar(bool animate = true)
    {
        var overlay = this.FindByName<VisualElement>("Overlay");
        var sidebar = this.FindByName<VisualElement>("MobileSidebar");
        if (overlay is null || sidebar is null) return;

        _mobileSidebarOpen = false;
        double hideOffset = sidebar.Width > 0 ? sidebar.Width : 260;

        if (animate)
        {
            _ = overlay.FadeTo(0, 150).ContinueWith(_ =>
                MainThread.BeginInvokeOnMainThread(() => overlay.IsVisible = false));
            _ = sidebar.TranslateTo(-hideOffset, 0, 200, Easing.CubicIn);
        }
        else
        {
            overlay.IsVisible = false;
            overlay.Opacity = 0;
            sidebar.TranslationX = -hideOffset;
        }
    }
}