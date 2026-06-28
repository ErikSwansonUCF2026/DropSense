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
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

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
    /// ── Wire protocol ─────────────────────────────────
    /// Total payload AFTER packet-type and serialization byte: 11 bytes
    ///
    /// Byte 0     : MeasurementChannel (uint8)
    /// Byte 1     : AlertSeverity      (uint8)
    /// Bytes 2–5  : sensor value       (float32 IEEE 754 little-endian)
    /// Bytes 6–9  : Unix timestamp     (uint32 seconds since epoch, little-endian)
    /// Byte 10    : condition flags
    ///
    /// Flags:
    ///   bit 0 = 1 → BelowMinimum
    ///   bit 0 = 0 → AboveMaximum
    ///   bit 1 = 1 → ProximityRisk

    public const int PayloadSize = 11;
    private static long _nextId = 0;

    // ── Identity ──────────────────────────────────────────────────────────────

    // FIX: was { get; private set; } — must have an accessible setter so that
    // System.Text.Json can write the value back during deserialization.
    // The [JsonInclude] attribute exposes the private setter to the serializer
    // without making it publicly mutable from other code.
    [JsonInclude]
    public long Id { get; private set; }

    [JsonInclude]
    public MeasurementChannel Channel { get; private set; }

    [JsonInclude]
    public AlertSeverity Severity { get; private set; }

    [JsonInclude]
    public float Value { get; private set; }

    [JsonInclude]
    public AlertCondition Condition { get; private set; }

    [JsonInclude]
    public DateTime ActualTime { get; private set; }

    [JsonInclude]
    public DateTime ReceivedTime { get; private set; }

    [JsonInclude]
    public string DeviceName { get; private set; } = string.Empty;

    // ── UI state (observable) ─────────────────────────────────────────────────

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

    public bool IsActive => !IsDismissed;

    // ── Constructors ──────────────────────────────────────────────────────────

    // FIX: System.Text.Json requires either a parameterless constructor or a
    // constructor annotated with [JsonConstructor]. Using a parameterless one
    // here keeps serialization simple — [JsonInclude] on each property with a
    // private setter lets the deserializer populate them without making the
    // setters publicly accessible.
    //
    // This constructor must NOT assign Id via Interlocked.Increment — the
    // deserializer will overwrite Id with the persisted value immediately after
    // construction, so the increment would waste a counter slot and leave a gap
    // in the live-alert sequence. Id is left at 0 and overwritten by the
    // deserializer (JSON path) or by the object initializer (FromCsvRow path).
    [JsonConstructor]
    public AlertEvent() { }

    // Full constructor used by TryParse (live BLE alerts).
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
    public static bool TryParse(
        byte[] payload,
        string deviceName,
        out AlertEvent? result,
        out string error)
    {
        result = null;
        error = string.Empty;

        const int MinSize = 7;
        if (payload == null || payload.Length < MinSize)
        {
            error = $"Alert payload too short: {payload?.Length ?? 0} bytes.";
            return false;
        }

        if (!BitConverter.IsLittleEndian)
        {
            Array.Reverse(payload, 2, 4);
            Array.Reverse(payload, 6, 4);
        }

        byte chanByte = payload[0];
        if (!Enum.IsDefined(typeof(MeasurementChannel), (int)chanByte))
        {
            error = $"Unknown MeasurementChannel byte: 0x{chanByte:X2}.";
            return false;
        }
        var channel = (MeasurementChannel)chanByte;

        byte sevByte = payload[1];
        if (!Enum.IsDefined(typeof(AlertSeverity), sevByte))
        {
            error = $"Unknown AlertSeverity byte: 0x{sevByte:X2}.";
            return false;
        }
        var severity = (AlertSeverity)sevByte;

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

        if (payload.Length < 10)
        {
            error = "Payload too short for timestamp.";
            return false;
        }

        uint unixSeconds = BitConverter.ToUInt32(payload, 6);
        DateTime actualTime = DateTimeOffset
            .FromUnixTimeSeconds(unixSeconds)
            .UtcDateTime;

        if (payload.Length < 11)
        {
            error = "Payload too short for flags.";
            return false;
        }

        byte flags = payload[10];
        AlertCondition condition;
        if ((flags & 0x02) != 0)
            condition = AlertCondition.ProximityRisk;
        else if ((flags & 0x01) != 0)
            condition = AlertCondition.BelowMinimum;
        else
            condition = AlertCondition.AboveMaximum;

        DateTime receivedTime = DateTime.UtcNow;

        result = new AlertEvent(
            channel, severity, value, condition, actualTime, receivedTime, deviceName);

        return true;
    }

    // ── Static factory — restore from CSV row ────────────────────────────────
    public static AlertEvent? FromCsvRow(string csvLine, int lineNumber = -1)
    {
        if (string.IsNullOrWhiteSpace(csvLine))
        {
            Debug.WriteLine($"[AlertRestore] Line {lineNumber}: skipped empty row.");
            return null;
        }

        try
        {
            var line = csvLine.TrimStart('\uFEFF');
            var parts = line.Split(',');

            if (parts.Length < 7)
            {
                Debug.WriteLine(
                    $"[AlertRestore] Line {lineNumber}: invalid column count. " +
                    $"Expected >= 7, got {parts.Length}. Row='{line}'");
                return null;
            }

            if (parts[1].Trim().Equals("Channel", StringComparison.OrdinalIgnoreCase))
            {
                Debug.WriteLine($"[AlertRestore] Line {lineNumber}: skipped CSV header.");
                return null;
            }

            if (!long.TryParse(parts[0].Trim(), out long restoredId))
            {
                Debug.WriteLine($"[AlertRestore] Line {lineNumber}: invalid ID.");
                return null;
            }

            MeasurementChannel channel;
            try
            {
                channel = Enum.Parse<MeasurementChannel>(parts[1].Trim(), ignoreCase: true);
            }
            catch (Exception ex)
            {
                Debug.WriteLine(
                    $"[AlertRestore] Line {lineNumber}: failed Channel parse. " +
                    $"Value='{parts[1]}' | {ex.Message}");
                return null;
            }

            AlertSeverity severity;
            try
            {
                var raw = parts[2].Trim();
                severity = raw.ToLowerInvariant() switch
                {
                    "critical" or "high" => AlertSeverity.Critical,
                    "warning" or "medium" => AlertSeverity.Warning,
                    "info" or "low" => AlertSeverity.Info,
                    _ => Enum.Parse<AlertSeverity>(raw, ignoreCase: true)
                };
            }
            catch (Exception ex)
            {
                Debug.WriteLine(
                    $"[AlertRestore] Line {lineNumber}: failed Severity parse. " +
                    $"Value='{parts[2]}' | {ex.Message}");
                return null;
            }

            float value;
            try
            {
                value = float.Parse(parts[3].Trim(), CultureInfo.InvariantCulture);
                if (float.IsNaN(value) || float.IsInfinity(value))
                {
                    Debug.WriteLine(
                        $"[AlertRestore] Line {lineNumber}: invalid float value '{parts[3]}'.");
                    return null;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine(
                    $"[AlertRestore] Line {lineNumber}: failed Value parse. " +
                    $"Value='{parts[3]}' | {ex.Message}");
                return null;
            }

            AlertCondition condition;
            try
            {
                condition = parts[4].Trim().ToLowerInvariant() switch
                {
                    "abovemaximum" => AlertCondition.AboveMaximum,
                    "belowminimum" => AlertCondition.BelowMinimum,
                    "proximityrisk" => AlertCondition.ProximityRisk,
                    _ => AlertCondition.Unknown
                };
            }
            catch (Exception ex)
            {
                Debug.WriteLine(
                    $"[AlertRestore] Line {lineNumber}: failed Condition parse. " +
                    $"Value='{parts[4]}' | {ex.Message}");
                return null;
            }

            DateTime actualTime;
            try
            {
                actualTime = DateTime.Parse(
                    parts[5].Trim(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            }
            catch (Exception ex)
            {
                Debug.WriteLine(
                    $"[AlertRestore] Line {lineNumber}: failed ActualTime parse. " +
                    $"Value='{parts[5]}' | {ex.Message}");
                return null;
            }

            DateTime receivedTime;
            try
            {
                receivedTime = DateTime.Parse(
                    parts[6].Trim(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            }
            catch (Exception ex)
            {
                Debug.WriteLine(
                    $"[AlertRestore] Line {lineNumber}: failed ReceivedTime parse. " +
                    $"Value='{parts[6]}' | {ex.Message}");
                return null;
            }

            if (parts.Length < 8 || string.IsNullOrWhiteSpace(parts[7]))
            {
                Debug.WriteLine($"[AlertRestore] Line {lineNumber}: missing device name.");
                return null;
            }
            var device = parts[7].Trim();

            var alert = new AlertEvent(
                channel, severity, value, condition, actualTime, receivedTime, device)
            {
                // Overwrite the auto-incremented Id with the persisted one.
                // Using the private setter via object initializer is valid here
                // because FromCsvRow is a static method on the same type.
                Id = restoredId,
                // Set the backing field directly to avoid firing OnPropertyChanged
                // during construction (no subscribers exist yet).
                _isSaved = true
            };

            // Advance the global counter so live alerts never reuse a restored Id.
            while (true)
            {
                long current = _nextId;
                if (current >= restoredId) break;
                if (System.Threading.Interlocked.CompareExchange(
                        ref _nextId, restoredId, current) == current) break;
            }

            Debug.WriteLine(
                $"[AlertRestore] Line {lineNumber}: restored alert. " +
                $"Id={restoredId}, Channel={channel}, Severity={severity}, " +
                $"Value={value}, Condition={condition}, Device={device}");

            return alert;
        }
        catch (Exception ex)
        {
            Debug.WriteLine(
                $"[AlertRestore] Line {lineNumber}: unexpected failure. " +
                $"{ex.GetType().Name}: {ex.Message}\nRow='{csvLine}'");
            return null;
        }
    }

    // ── Display helpers ───────────────────────────────────────────────────────

    public string ChannelDisplay => Channel switch
    {
        MeasurementChannel.Temperature => "Temperature",
        MeasurementChannel.RelativeHumidity => "Humidity",
        MeasurementChannel.BarometricPressure => "Pressure",
        MeasurementChannel.SolarIrradiance => "Light Stress",
        MeasurementChannel.VaporPressureDeficit => "VPD",
        MeasurementChannel.DewPointTemperature => "Condensation Risk",
        _ => $"Channel 0x{(byte)Channel:X2}",
    };

    public string UnitDisplay => Channel switch
    {
        MeasurementChannel.Temperature => "°C",
        MeasurementChannel.RelativeHumidity => "%",
        MeasurementChannel.BarometricPressure => "kPa",
        MeasurementChannel.SolarIrradiance => "W/m²",
        MeasurementChannel.VaporPressureDeficit => "hPa",
        MeasurementChannel.DewPointTemperature => "",
        _ => "",
    };

    public string ValueDisplay => Condition == AlertCondition.ProximityRisk
        ? "Within 2 °C of dew point"
        : $"{Value:F1} {UnitDisplay}".Trim();

    public string ConditionDisplay => Condition switch
    {
        AlertCondition.BelowMinimum => "Below minimum",
        AlertCondition.AboveMaximum => "Above maximum",
        AlertCondition.ProximityRisk => "Condensation risk",
        _ => "Unknown",
    };

    public string SeverityDisplay => Severity.ToString();

    // FIX: renamed from aTimestampShort / aTimestampFull / rTimestampShort /
    // rTimestampFull to match the binding paths already corrected in the XAML.
    // The old names had a single-letter prefix that was a typo carried from an
    // earlier draft and never corrected in the model.
    public string TimestampShort => ActualTime.ToString("HH:mm:ss");
    public string TimestampFull => ActualTime.ToString("yyyy-MM-dd HH:mm:ss");
    public string ReceivedTimestampShort => ReceivedTime.ToString("HH:mm:ss");
    public string ReceivedTimestampFull => ReceivedTime.ToString("yyyy-MM-dd HH:mm:ss");

    public string Summary =>
        $"{ChannelDisplay}: {ValueDisplay} — {ConditionDisplay}";

    public Color SeverityColor => Severity switch
    {
        AlertSeverity.Critical => Color.FromArgb("#B83030"),
        AlertSeverity.Warning => Color.FromArgb("#D4A010"),
        AlertSeverity.Info => Color.FromArgb("#4A90D9"),
        _ => Colors.Gray,
    };

    // ── CSV serialisation ─────────────────────────────────────────────────────

    public static string CsvHeader =>
        "Id,Channel,Severity,Value,Condition,ActualTime,ReceivedTime,DeviceName";

    public string ToCsvRow() =>
        $"{Id},{Channel},{Severity}," +
        $"{Value.ToString("F4", CultureInfo.InvariantCulture)}," +
        $"{Condition},{ActualTime:O},{ReceivedTime:O},{DeviceName}";

    private static bool TryConvertChannel(
    byte raw,
    out MeasurementChannel channel)
    {
        channel = raw switch
        {
            0 => MeasurementChannel.Temperature,
            1 => MeasurementChannel.RelativeHumidity,
            2 => MeasurementChannel.BarometricPressure,
            3 => MeasurementChannel.SolarIrradiance,
            4 => MeasurementChannel.VaporPressureDeficit,
            5 => MeasurementChannel.DewPointTemperature,
            6 => MeasurementChannel.AbsoluteHumidity,
            7 => MeasurementChannel.AccumulatedSolarRadiation,
            8 => MeasurementChannel.DailyLightIntegral,
            9 => MeasurementChannel.EstimatedPAR,
            _ => default
        };

        return raw <= 9;
    }

    // ── INotifyPropertyChanged ────────────────────────────────────────────────

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string name = "")
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}