using System;
using System.Collections.Generic;
using System.Text;

public interface INavigationService
{
    Task NavigateToAsync(string route);
}