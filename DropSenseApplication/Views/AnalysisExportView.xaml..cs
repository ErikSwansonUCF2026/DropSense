// DropSense — Views/DashboardView.xaml.cs
// ══════════════════════════════════════════════════════════════════════════════

using DropSense.ViewModels;

namespace DropSense.Views
{
    public partial class AnalysisExportView : ContentView
    {
        public AnalysisExportView()
        {
            InitializeComponent();
            BindingContext = App.Current.Handler.MauiContext.Services.GetService<AnalysisExportViewModel>();

        }
    }
}