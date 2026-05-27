// DropSense — Models/AlertEvent.cs
//
// ══════════════════════════════════════════════════════════════════════════════
// AlertEvent is the model for the alert object. It contains:
//   • All parsed alert fields (Channel, Severity, Value, Timestamp, Condition)
//   • Static TryParse() for decoding the 9-byte BLE wire packet
//   • Instance methods for CSV serialisation, display formatting, and copying
//   • IsDismissed flag — set when the user dismisses from the modal
//     (clears the badge count but does not remove the alert from the panel)
//   • IsSaved flag — set after the alert has been written to the CSV log
//
// AlertsPanel has-a collection of AlertEvents.
// AlertEvent does NOT reference any service or ViewModel — it is a pure model.
// ══════════════════════════════════════════════════════════════════════════════

using DropSense.Services;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace DropSense.Models;

// ─────────────────────────────────────────────────────────────────────────────
// Supporting enums
// ─────────────────────────────────────────────────────────────────────────────

public enum AlertSeverity : byte
{
    Info = 0x01,
    Warning = 0x02,
    Critical = 0x03,
}

public enum AlertCondition
{
    BelowMinimum,   // value < SafeMin  (flags bit 0 = 1)
    AboveMaximum,   // value > SafeMax  (flags bit 0 = 0)
    ProximityRisk,  // dew-point proximity (binary channel)
    Unknown,
}

// ─────────────────────────────────────────────────────────────────────────────
// AlertEvent
// ─────────────────────────────────────────────────────────────────────────────

public sealed class AlertEvent : INotifyPropertyChanged
{
    // ── Wire protocol ─────────────────────────────────────────────────────────
    // Packet layout (9 bytes, packet type already consumed by caller):
    //   Byte 0  : MeasurementChannel  (uint8)
    //   Byte 1  : AlertSeverity       (uint8)
    //   Bytes 2–5 : sensor value      (float32 IEEE 754 LE)
    //   Byte 6  : condition flags
    //               bit 0 = 1 → BelowMinimum, 0 → AboveMaximum
    //               bit 1 = 1 → ProximityRisk (dew-point binary channel)
    //   Byte 7  : reserved
    // Total payload after packet-type byte: 8 bytes. Minimum check: 8 bytes.

    public const int PayloadSize = 8;    // bytes following the 0x03 packet-type byte
    private static long _nextId = 0;

    // ── Identity ──────────────────────────────────────────────────────────────
    public long Id { get; }
    public MeasurementChannel Channel { get; }
    public AlertSeverity Severity { get; }
    public float Value { get; }
    public AlertCondition Condition { get; }
    public DateTime ActualTime { get; }
    public DateTime ReceivedTime { get; }
    public string DeviceName { get; }

    // ── UI state (observable, not persisted to CSV) ───────────────────────────
    private bool _isDismissed;
    public bool IsDismissed
    {
        get => _isDismissed;
        set { _isDismissed = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsActive)); }
    }

    private bool _isSaved;
    public bool IsSaved
    {
        get => _isSaved;
        set { _isSaved = value; OnPropertyChanged(); }
    }

    // Alert counts toward the badge only while undismissed
    public bool IsActive => !IsDismissed;

    // ── Constructor (private — use TryParse or Create) ────────────────────────
    private AlertEvent(
        MeasurementChannel channel,
        AlertSeverity severity,
        float value,
        AlertCondition condition,
        DateTime actualTime,
        DateTime receivedTime,
        string deviceName)
    {
        Id = System.Threading.Interlocked.Increment(ref _nextId);
        Channel = channel;
        Severity = severity;
        Value = value;
        Condition = condition;
        ActualTime = actualTime;  
        ReceivedTime = receivedTime; 
        DeviceName = deviceName;
    }

    // ── Static factory — wire packet parse ───────────────────────────────────
    /// <summary>
    /// Attempts to decode an alert payload from the DATA_CHAR notification.
    /// <paramref name="payload"/> should be the bytes AFTER the 0x03 packet-type byte.
    /// Returns false and an error message when the payload is malformed.
    /// </summary>
    public static bool TryParse(
    byte[] payload,
    string deviceName,
    out AlertEvent? result,
    out string error)
    {
        result = null;
        error = string.Empty;

        const int MinSize = 7; // up to flags byte
        if (payload == null || payload.Length < MinSize)
        {
            error = $"Alert payload too short: {payload?.Length ?? 0} bytes.";
            return false;
        }

        // ── Channel ─────────────────────────────────────────────
        byte chanByte = payload[0];
        if (!Enum.IsDefined(typeof(MeasurementChannel), chanByte))
        {
            error = $"Unknown MeasurementChannel byte: 0x{chanByte:X2}.";
            return false;
        }
        var channel = (MeasurementChannel)chanByte;

        // ── Severity ────────────────────────────────────────────
        byte sevByte = payload[1];
        if (!Enum.IsDefined(typeof(AlertSeverity), sevByte))
        {
            error = $"Unknown AlertSeverity byte: 0x{sevByte:X2}.";
            return false;
        }
        var severity = (AlertSeverity)sevByte;

        // ── Float value (bytes 2–5, little-endian) ──────────────
        if (payload.Length < 6)
        {
            error = "Payload too short for float value.";
            return false;
        }

        float value = BitConverter.ToSingle(payload, 2);

        if (float.IsNaN(value) || float.IsInfinity(value))
        {
            error = $"Invalid float value: {value}.";
            return false;
        }

        // ── Unix timestamp (bytes 4–7) ──────────────────────────
        if (payload.Length < 8)
        {
            error = "Payload too short for timestamp.";
            return false;
        }

        uint unixSeconds = BitConverter.ToUInt32(payload, 4);
        DateTime actualTime = DateTimeOffset
            .FromUnixTimeSeconds(unixSeconds)
            .UtcDateTime;

        // ── Flags (byte 8) ──────────────────────────────────────
        if (payload.Length < 9)
        {
            error = "Payload too short for flags.";
            return false;
        }

        byte flags = payload[8];

        AlertCondition condition;
        if ((flags & 0x02) != 0)
            condition = AlertCondition.ProximityRisk;
        else if ((flags & 0x01) != 0)
            condition = AlertCondition.BelowMinimum;
        else
            condition = AlertCondition.AboveMaximum;

        // ── Device-side vs host-side time ───────────────────────
        DateTime receivedTime = DateTime.UtcNow;

        result = new AlertEvent(
            channel,
            severity,
            value,
            condition,
            actualTime,
            receivedTime,
            deviceName);

        return true;
    }

    // ── Static factory — restore from saved CSV row ───────────────────────────
    /// <summary>
    /// Creates an AlertEvent from a persisted CSV row.
    /// Tolerates:
    ///   • UTF-8 BOM on the first field (EF BB BF prefix — produced by Excel/Notepad).
    ///   • CRLF line endings (trailing \r stripped by Trim()).
    ///   • Severity aliases: "High" → Critical, "Medium" → Warning, "Low" → Info.
    ///     These appear when the CSV was created by an external tool or an older
    ///     version of the app that used display labels instead of enum names.
    ///   • Condition aliases: "GreaterThan" → AboveMaximum, "LessThan" → BelowMinimum.
    ///   • Both ISO-8601 ("2026-05-26T10:15:00Z") and display ("2026-05-26 10:15:00")
    ///     timestamp formats — DateTime.Parse with RoundtripKind handles both.
    /// Returns null (and is filtered out by the caller) for header rows and
    /// genuinely malformed lines rather than throwing.
    /// </summary>
    public static AlertEvent? FromCsvRow(
    string csvLine,
    int lineNumber = -1)
    {
        if (string.IsNullOrWhiteSpace(csvLine))
        {
            System.Diagnostics.Debug.WriteLine(
                $"[AlertRestore] Line {lineNumber}: skipped empty row.");
            return null;
        }

        try
        {
            // Strip UTF-8 BOM (Excel / Notepad issue)
            var line = csvLine.TrimStart('\uFEFF');

            var parts = line.Split(',');

            if (parts.Length < 7)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[AlertRestore] Line {lineNumber}: invalid column count. " +
                    $"Expected >= 7, got {parts.Length}. " +
                    $"Row='{line}'");

                return null;
            }

            // Header row
            if (parts[1].Trim().Equals(
                    "Channel",
                    StringComparison.OrdinalIgnoreCase))
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[AlertRestore] Line {lineNumber}: skipped CSV header.");

                return null;
            }

            // ─────────────────────────────────────────────────────────
            // Channel
            // ─────────────────────────────────────────────────────────
            MeasurementChannel channel;

            try
            {
                channel = Enum.Parse<MeasurementChannel>(
                    parts[1].Trim(),
                    ignoreCase: true);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[AlertRestore] Line {lineNumber}: failed Channel parse. " +
                    $"Value='{parts[1]}' | {ex.Message}");

                return null;
            }

            // ─────────────────────────────────────────────────────────
            // Severity
            // ─────────────────────────────────────────────────────────
            AlertSeverity severity;

            try
            {
                var severityRaw = parts[2].Trim();

                severity = severityRaw.ToLowerInvariant() switch
                {
                    "critical" or "high"
                        => AlertSeverity.Critical,

                    "warning" or "medium"
                        => AlertSeverity.Warning,

                    "info" or "low"
                        => AlertSeverity.Info,

                    _ => Enum.Parse<AlertSeverity>(
                        severityRaw,
                        ignoreCase: true)
                };
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[AlertRestore] Line {lineNumber}: failed Severity parse. " +
                    $"Value='{parts[2]}' | {ex.Message}");

                return null;
            }

            // ─────────────────────────────────────────────────────────
            // Value
            // ─────────────────────────────────────────────────────────
            float value;

            try
            {
                value = float.Parse(
                    parts[3].Trim(),
                    CultureInfo.InvariantCulture);

                if (float.IsNaN(value) || float.IsInfinity(value))
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[AlertRestore] Line {lineNumber}: invalid float value '{parts[3]}'.");

                    return null;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[AlertRestore] Line {lineNumber}: failed Value parse. " +
                    $"Value='{parts[3]}' | {ex.Message}");

                return null;
            }

            // ─────────────────────────────────────────────────────────
            // Condition
            // ─────────────────────────────────────────────────────────
            AlertCondition condition;

            try
            {
                var conditionRaw = parts[4].Trim();

                condition = conditionRaw.ToLowerInvariant() switch
                {
                    "abovemaximum"
                    or "greaterthan"
                    or "above"
                        => AlertCondition.AboveMaximum,

                    "belowminimum"
                    or "lessthan"
                    or "below"
                        => AlertCondition.BelowMinimum,

                    "proximityrisk"
                        => AlertCondition.ProximityRisk,

                    _ => AlertCondition.Unknown
                };
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[AlertRestore] Line {lineNumber}: failed Condition parse. " +
                    $"Value='{parts[4]}' | {ex.Message}");

                return null;
            }

            // ─────────────────────────────────────────────────────────
            // Actual Recorded Time
            // ─────────────────────────────────────────────────────────
            DateTime actualTime;

            try
            {
                actualTime = DateTime.Parse(
                    parts[5].Trim(),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[AlertRestore] Line {lineNumber}: failed Recording Timestamp parse. " +
                    $"Value='{parts[5]}' | {ex.Message}");

                return null;
            }

            // ─────────────────────────────────────────────────────────
            // Recieved  Time
            // ─────────────────────────────────────────────────────────
            DateTime recievedTime;

            try
            {
                recievedTime = DateTime.Parse(
                    parts[6].Trim(),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[AlertRestore] Line {lineNumber}: failed Recieved Timestamp parse. " +
                    $"Value='{parts[6]}' | {ex.Message}");

                return null;
            }

            // ─────────────────────────────────────────────────────────
            // Device
            // ─────────────────────────────────────────────────────────
            var device = parts[7].Trim();

            if (string.IsNullOrWhiteSpace(device))
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[AlertRestore] Line {lineNumber}: missing device name.");

                return null;
            }

            var alert = new AlertEvent(
                channel,
                severity,
                value,
                condition,
                actualTime,
                recievedTime,
                device)
            {
                _isSaved = true
            };

            System.Diagnostics.Debug.WriteLine(
                $"[AlertRestore] Line {lineNumber}: restored alert. " +
                $"Channel={channel}, Severity={severity}, " +
                $"Value={value}, Condition={condition}, Device={device}");

            return alert;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[AlertRestore] Line {lineNumber}: unexpected failure. " +
                $"{ex.GetType().Name}: {ex.Message}\n" +
                $"Row='{csvLine}'");

            return null;
        }
    }

    // ── Display helpers ───────────────────────────────────────────────────────

    /// <summary>Human-readable channel name shown in the panel and modal.</summary>
    public string ChannelDisplay => Channel switch
    {
        MeasurementChannel.Temperature => "Temperature",
        MeasurementChannel.Humidity => "Humidity",
        MeasurementChannel.Pressure => "Pressure",
        MeasurementChannel.Irradiance => "Light Stress",
        MeasurementChannel.VPD => "VPD",
        MeasurementChannel.DewPoint => "Condensation Risk",
        _ => $"Channel 0x{(byte)Channel:X2}",
    };

    /// <summary>Physical unit string for the channel.</summary>
    public string UnitDisplay => Channel switch
    {
        MeasurementChannel.Temperature => "°C",
        MeasurementChannel.Humidity => "%",
        MeasurementChannel.Pressure => "kPa",
        MeasurementChannel.Irradiance => "W/m²",
        MeasurementChannel.VPD => "hPa",
        MeasurementChannel.DewPoint => "",
        _ => "",
    };

    /// <summary>Formatted value + unit for compact panel display.</summary>
    public string ValueDisplay => Condition == AlertCondition.ProximityRisk
        ? "Within 2 °C of dew point"
        : $"{Value:F1} {UnitDisplay}".Trim();

    /// <summary>Short condition label for the panel row.</summary>
    public string ConditionDisplay => Condition switch
    {
        AlertCondition.BelowMinimum => "Below minimum",
        AlertCondition.AboveMaximum => "Above maximum",
        AlertCondition.ProximityRisk => "Condensation risk",
        _ => "Unknown",
    };

    /// <summary>Severity badge text.</summary>
    public string SeverityDisplay => Severity.ToString();

    /// <summary>Recording Timestamp formatted for panel rows.</summary>
    public string aTimestampShort => ActualTime.ToString("HH:mm:ss");

    /// <summary>Recording Timestamp formatted for modal detail view.</summary>
    public string aTimestampFull => ActualTime.ToString("yyyy-MM-dd HH:mm:ss");

    /// <summary>Recieved Timestamp formatted for panel rows.</summary>
    public string rTimestampShort => ReceivedTime.ToString("HH:mm:ss");

    /// <summary>Recieved Timestamp formatted for modal detail view.</summary>
    public string rTimestampFull => ReceivedTime.ToString("yyyy-MM-dd HH:mm:ss");

    /// <summary>Single-line summary for the panel list row.</summary>
    public string Summary =>
        $"{ChannelDisplay}: {ValueDisplay} — {ConditionDisplay}";

    /// <summary>Colour token name for severity (resolved in XAML via converter or AppTheme).</summary>
    public Color SeverityColor => Severity switch
    {
        AlertSeverity.Critical => Color.FromArgb("#B83030"),
        AlertSeverity.Warning => Color.FromArgb("#D4A010"),
        AlertSeverity.Info => Color.FromArgb("#4A90D9"),
        _ => Colors.Gray,
    };

    // ── CSV serialisation ─────────────────────────────────────────────────────
    // CSV header: Id,Channel,Severity,Value,Condition,Timestamp,DeviceName
    public static string CsvHeader =>
        "Id,Channel,Severity,Value,Condition,ActualTime,ReceivedTime,DeviceName";

    public string ToCsvRow() =>
    $"{Id},{Channel},{Severity}," +
    $"{Value.ToString("F4", System.Globalization.CultureInfo.InvariantCulture)}," +
    $"{Condition},{ActualTime:O},{ReceivedTime:O},{DeviceName}";

    // ── INotifyPropertyChanged ────────────────────────────────────────────────
    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string name = "")
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}