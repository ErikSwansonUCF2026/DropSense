// DropSense — Converters/Converters.cs
// ══════════════════════════════════════════════════════════════════════════════
// ADD TO PROJECT: Step 1 (register all converters in App.xaml ResourceDictionary)
// Individual converters activate when their dependent types/enums exist:
//   BoolToVisibilityConverter      — Step 1 (general utility)
//   NullableDoubleToStringConverter — Step 3 (threshold input binding)
//   ConnectionStateToColorConverter — Step 2 (connection chip colour)
//   AlertSeverityToColorConverter   — Step 6 (alert severity bar colour)
//   SensorValueStatusHelper         — Step 4 (metric card status)
// ══════════════════════════════════════════════════════════════════════════════

using System.Globalization;
using DropSense.Services;

namespace DropSense.Converters;

// ─────────────────────────────────────────────────────────────────────────────
// Step 1 — BoolToVisibilityConverter
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Maps bool → bool (IsVisible). Pass ConverterParameter="invert" to flip.
/// Register in App.xaml ResourceDictionary as <converters:BoolToVisibilityConverter x:Key="BoolToVis"/>
/// </summary>
public class BoolToVisibilityConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool visible = value is bool b && b;
        if (parameter is string s && s.Equals("invert", StringComparison.OrdinalIgnoreCase))
            visible = !visible;
        return visible;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b && b;
}

// ─────────────────────────────────────────────────────────────────────────────
// Step 3 — NullableDoubleToStringConverter
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Two-way converter for nullable double threshold Entry bindings.
/// Empty string ↔ null; valid numeric string ↔ double value.
/// </summary>
public class NullableDoubleToStringConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is double d ? d.ToString("G", CultureInfo.InvariantCulture) : string.Empty;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string s || string.IsNullOrWhiteSpace(s)) return null;
        return double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var d)
            ? d : null;
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Step 2 — ConnectionStateToColorConverter
// (Requires ConnectionState enum from IDeviceConnectionService.cs)
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>Maps ConnectionState → Color for the titlebar connection dot.</summary>
public class ConnectionStateToColorConverter : IValueConverter
{
    private static readonly Color ConnectedColor    = Color.FromArgb("#4AD98A");
    private static readonly Color ConnectingColor   = Color.FromArgb("#F0B440");
    private static readonly Color DisconnectedColor = Color.FromArgb("#E05555");

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is ConnectionState state ? state switch
        {
            ConnectionState.Connected    => ConnectedColor,
            ConnectionState.Connecting   => ConnectingColor,
            ConnectionState.Transferring => ConnectingColor,
            _                            => DisconnectedColor
        } : DisconnectedColor;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public class InverseBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b ? !b : false;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b ? !b : false;
}

// ─────────────────────────────────────────────────────────────────────────────
// Step 6 — AlertSeverityToColorConverter
// (Requires AlertSeverity enum from Alert.cs)
// ─────────────────────────────────────────────────────────────────────────────

// Uncomment at Step 6 when Alert.cs is added:

// using DropSense.Models;
//
// /// <summary>Maps AlertSeverity → Color for the severity bar and badge elements.</summary>
// public class AlertSeverityToColorConverter : IValueConverter
// {
//     private static readonly Color HighColor   = Color.FromArgb("#B83030");
//     private static readonly Color MediumColor = Color.FromArgb("#D4A010");
//     private static readonly Color LowColor    = Color.FromArgb("#4A90D9");
//
//     public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
//         => value is AlertSeverity s ? s switch
//         {
//             AlertSeverity.High   => HighColor,
//             AlertSeverity.Medium => MediumColor,
//             AlertSeverity.Low    => LowColor,
//             _                   => Colors.Gray
//         } : Colors.Gray;
//
//     public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
//         => throw new NotSupportedException();
// }

// ─────────────────────────────────────────────────────────────────────────────
// Step 4 — SensorValueStatusHelper
// (Requires DeviceSettings / AlertThresholdConfig from DeviceSettings.cs)
// ─────────────────────────────────────────────────────────────────────────────

// Uncomment at Step 4 when DeviceSettings.cs is added and metric cards need status:

// using DropSense.Models;
//
// namespace DropSense.Helpers;
//
// public static class SensorValueStatusHelper
// {
//     public enum ValueStatus { Ok, Warning, Alert }
//
//     /// <summary>
//     /// Returns the status of a sensor value against a threshold configuration.
//     /// Returns Ok if the threshold is disabled or has no limits defined.
//     /// </summary>
//     public static ValueStatus GetStatus(double value, AlertThresholdConfig config)
//     {
//         if (!config.Enabled) return ValueStatus.Ok;
//         // TODO: Return Alert if value < config.LowerLimit || value > config.UpperLimit
//         // TODO: Optionally return Warning when value is within a configurable margin of the limit
//         throw new NotImplementedException();
//     }
//
//     /// <summary>Returns the XAML VisualState name for the metric card based on status.</summary>
//     public static string GetStyleName(ValueStatus status) => status switch
//     {
//         ValueStatus.Alert   => "MetricCardAlert",
//         ValueStatus.Warning => "MetricCardWarn",
//         _                   => "MetricCard"
//     };
// }
