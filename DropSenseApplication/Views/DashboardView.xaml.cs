// DropSense — Views/DashboardView.xaml.cs
// ══════════════════════════════════════════════════════════════════════════════

using DropSense.ViewModels;

namespace DropSense.Views
{
    public partial class DashboardView : ResponsiveContentView
    {
        public DashboardView()
        {
            InitializeComponent();
            BindingContext = App.Current.Handler.MauiContext.Services.GetService<DashboardViewModel>();

#if !DEBUG
        // Runtime exception log is a developer diagnostic tool — never
        // shown to end users in Release builds. Collapsed rather than just
        // hidden so it doesn't reserve blank space in the layout.
            ExceptionLogCard.IsVisible = false;
#endif

        }


        protected override void OnWidthChanged(double width)
        {
            double contentWidth = width - RootGrid.Padding.Left - RootGrid.Padding.Right;
            if (contentWidth <= 0) return;

            ToolbarFlex.WidthRequest = contentWidth;
            MetricsFlex.WidthRequest = contentWidth;
            AlertsCard.WidthRequest = contentWidth;
            ExceptionLogCard.WidthRequest = contentWidth;

            LayoutMetricsGrid(contentWidth);

           
        }

        void LayoutMetricsGrid(double contentWidth)
        {
            const double cardMarginPerCard = 8; // Margin="4" on each card, both sides
            const double safetyPx = 1;          // absorb float rounding so 4-up never tips into 3+1

            int cols = contentWidth < 700 ? 3 : 5;
            double cardWidth = Math.Floor((contentWidth - cardMarginPerCard * cols) / cols) - safetyPx;

            foreach (var card in new[] { MetricSynced, MetricDeviceName, MetricDeviceId, MetricBluetooth })
                card.WidthRequest = cardWidth;
        }
    }
}