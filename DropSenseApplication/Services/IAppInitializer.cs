using DropSense.Services;
using DropSense.ViewModels;
using System;
using System.Collections.Generic;
using System.Text;

namespace DropSenseApplication.Services
{
    public interface IAppInitializer
    {
        Task InitializeAsync();
    }

    public class AppInitializer : IAppInitializer
    {
        private readonly AlertsViewModel _alertsViewModel;
        

        public AppInitializer(
            AlertsViewModel alertsViewModel)
        {
            _alertsViewModel = alertsViewModel;
        }

        public async Task InitializeAsync()
        {
            // 2. Restore alerts (state)
            await _alertsViewModel.InitializeAsync();

            // 3. Reconnect devices (runtime services)
            // await _connectionService.TryReconnectAsync();
        }
    }
}
