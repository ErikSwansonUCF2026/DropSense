using DropSense.Services;
using DropSense.ViewModels;
using System;
using System.Collections.Generic;
using System.Text;

namespace DropSense.Services
{
    public interface IAppInitializer
    {
        Task InitializeAsync();
    }

    public class AppInitializer : IAppInitializer
    {
        private readonly AlertsViewModel _alertsViewModel;
        private readonly IDeviceConnectionService _deviceConnectionService;
        public AppInitializer(  
            AlertsViewModel alertsViewModel, IDeviceConnectionService deviceConnectionService)
        {
            _alertsViewModel = alertsViewModel;
            _deviceConnectionService = deviceConnectionService;
        }

        public async Task InitializeAsync()
        {
            // 2. Restore alerts (state)
            await _alertsViewModel.InitializeAsync();
            await _deviceConnectionService.InitializeAsync();
            // 3. Reconnect devices (runtime services)
        }
    }
}
