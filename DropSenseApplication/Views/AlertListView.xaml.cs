using DropSense.ViewModels;

namespace DropSense.Views;

public partial class AlertListView : ContentView
{
    public AlertListView()
    {
        InitializeComponent();

        Loaded += (_, _) =>
        {
            if (BindingContext != null)
                return;

            BindingContext =
                Application.Current?
                .Handler?
                .MauiContext?
                .Services?
                .GetService<AlertsViewModel>();

            // ensure modal starts closed
            if (BindingContext is AlertsViewModel vm)
                vm.SelectedAlert = null;
        };
    }
}