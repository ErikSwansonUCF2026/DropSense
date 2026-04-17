using System;
using System.Collections.Generic;
using System.Text;

namespace DropSense.Services
{
    public class NavigationService : INavigationService
    {
        public Task NavigateToAsync(string route)
        {
            return Shell.Current.GoToAsync(route);
        }
    }
}
