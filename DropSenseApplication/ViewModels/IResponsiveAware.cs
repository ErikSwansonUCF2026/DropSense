using System;
using System.Collections.Generic;
using System.Text;

// DropSense — ViewModels/IResponsiveAware.cs
namespace DropSense.ViewModels;

/// <summary>
/// Implement on a ViewModel that needs to change behavior based on the
/// page's current width (e.g. SettingsViewModel.ThresholdColumns).
/// ResponsiveShellPage calls this automatically on every layout change,
/// including rotation — no per-page wiring needed.
/// </summary>
public interface IResponsiveAware
{
    void OnWidthChanged(double width);
}