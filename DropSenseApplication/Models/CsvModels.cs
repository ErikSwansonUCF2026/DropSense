// DropSense — Models/CsvModels.cs
namespace DropSense.Models;

// ── Parsed sensor row ──────────────────────────────────────────────────────────

/// <summary>
/// One row of sensor data after parsing and validation.
/// Any field that could not be parsed / validated is null (or the sentinel
/// string <see cref="SensorRow.PARSE_FAILURE"/> for the raw CSV output).
/// </summary>
public sealed class SensorRow
{
    /// <summary>Written into the computed CSV cell when a field failed validation.</summary>
    public const string PARSE_FAILURE = "#ERR";

    /// <summary>Original or converted timestamp string.</summary>
    public string Timestamp { get; init; } = string.Empty;

    public double? TempC         { get; init; }
    public double? HumidityPct   { get; init; }
    public double? PressureHpa   { get; init; }
    public double? IrradianceWm2 { get; init; }

    // ── Computed fields (populated by the analysis pass) ──────────────────────

    /// <summary>Vapor Pressure Deficit in kPa. Null when inputs are unavailable.</summary>
    public double? Vpd { get; set; }

    /// <summary>Dew-point temperature in °C (Magnus approximation).</summary>
    public double? DewPointC { get; set; }

    /// <summary>Absolute humidity in g/m³.</summary>
    public double? AbsHumidityGm3 { get; set; }

    /// <summary>Accumulated solar irradiance up to and including this row in kWh/m².</summary>
    public double? AccumulatedIrradianceKwhM2 { get; set; }

    // ── Validation flags (for display / badge colouring) ──────────────────────

    public bool TempWarn       { get; init; }
    public bool HumidityWarn   { get; init; }
    public bool PressureWarn   { get; init; }
    public bool IrradianceWarn { get; init; }
}

// ── Parse result ───────────────────────────────────────────────────────────────

public sealed class CsvParseResult
{
    public IReadOnlyList<SensorRow> Rows       { get; init; } = [];
    public int                      ErrorCount  { get; init; }
    public int                      WarningCount { get; init; }

    /// <summary>Full path of the error-log CSV written during parsing.</summary>
    public string ErrorLogPath { get; init; } = string.Empty;

    /// <summary>Full path of the computed-data CSV written during parsing.</summary>
    public string ComputedCsvPath { get; init; } = string.Empty;
}

// ── Per-cell parse-log entry ───────────────────────────────────────────────────

public sealed class ParseLogEntry
{
    public int    RowIndex  { get; init; }
    public int    ColIndex  { get; init; }
    public string Level     { get; init; } = "INFO";   // INFO | WARN | ERROR
    public string Field     { get; init; } = string.Empty;
    public string RawValue  { get; init; } = string.Empty;
    public string Message   { get; init; } = string.Empty;
}
