using DropSense.ViewModels;

namespace DropSense.Views;

public partial class AlertListView : ContentView
{
    public AlertListView()
    {
        InitializeComponent();

        Loaded += OnLoaded;
    }

    private void OnLoaded(object? sender, EventArgs e)
    {
        // Only resolve from DI if the host hasn't already set a BindingContext
        // (e.g. a parent page that sets it explicitly via x:DataType binding).
        if (BindingContext is AlertsViewModel)
            return;

        var vm = Application.Current?
            .Handler?
            .MauiContext?
            .Services?
            .GetService<AlertsViewModel>();

        if (vm is null)
        {
            System.Diagnostics.Debug.WriteLine(
                "[AlertListView] AlertsViewModel not found in DI container.");
            return;
        }

        BindingContext = vm;

       
        _ = vm.InitializeAsync().ContinueWith(
            t =>
            {
                if (t.IsFaulted)
                    System.Diagnostics.Debug.WriteLine(
                        $"[AlertListView] InitializeAsync failed: {t.Exception}");
            },
            TaskScheduler.FromCurrentSynchronizationContext());

        // Ensure the modal starts closed regardless of restored state.
        vm.SelectedAlert = null;
    }
}













