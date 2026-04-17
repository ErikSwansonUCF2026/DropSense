using DropSense.ViewModels;
using Microsoft.Maui.Controls;

namespace DropSense.Views
{
    public partial class SidebarView : ContentView
    {
        public SidebarView()
        {
            InitializeComponent();
            BindingContext = App.Current.Handler.MauiContext.Services.GetService<SidebarViewModel>();
        }
    }
}