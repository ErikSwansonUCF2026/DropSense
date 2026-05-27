// DropSense — Views/AlertsPanel.xaml.cs
// ══════════════════════════════════════════════════════════════════════════════
// ADD TO PROJECT: Step 6
// ══════════════════════════════════════════════════════════════════════════════

using DropSense.ViewModels;
using System.Diagnostics;

namespace DropSense.Views;

public partial class AlertsPanel : ContentView
{
    public AlertsPanel()
    {
        InitializeComponent();

        Loaded += (_, _) =>
        {
            if (BindingContext != null)
                return;

            BindingContext =
                Application.Current?.Handler?.MauiContext?.Services?
                    .GetService<AlertsViewModel>();
        };
    }
}
