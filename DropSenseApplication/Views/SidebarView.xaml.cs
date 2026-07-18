// DropSense — Views/SidebarView.xaml.cs
using DropSense.Services;
using DropSense.ViewModels;

namespace DropSense.Views;

public partial class SidebarView : ContentView
{
    public static readonly BindableProperty IsCompactProperty =
        BindableProperty.Create(nameof(IsCompact), typeof(bool), typeof(SidebarView), false,
            propertyChanged: OnIsCompactChanged);

    public bool IsCompact
    {
        get => (bool)GetValue(IsCompactProperty);
        set => SetValue(IsCompactProperty, value);
    }

    public SidebarView()
    {
        InitializeComponent();

        // Explicit resolve — do NOT rely on inherited BindingContext here.
        // SidebarViewModel is registered as a singleton (below) so the
        // desktop rail and the mobile slide-over share the same state
        // (badge count, debug log, active file).
        BindingContext = ServiceHelper.GetRequiredService<SidebarViewModel>();
    }

    static void OnIsCompactChanged(BindableObject bindable, object oldValue, object newValue)
    {
        if (bindable is SidebarView view)
            view.WidthRequest = (bool)newValue ? 64 : 260;
    }
}