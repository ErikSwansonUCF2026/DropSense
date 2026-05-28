using DropSense.ViewModels;

namespace DropSense.Views;

public partial class AlertToolbarView : ContentView
{
    public AlertToolbarView()
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
        };
    }
}