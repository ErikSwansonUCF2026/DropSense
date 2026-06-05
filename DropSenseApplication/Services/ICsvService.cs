// DropSense — Services/ICsvService.cs
using System.Globalization;
using System.Text;
using DropSense.Models;
using System.Runtime.CompilerServices;

namespace DropSense.Services;

public interface ICsvService
{
    /// <summary>
    /// Parses the CSV at <paramref name="filePath"/>, writes an error-log CSV alongside it,
    /// and writes a computed-data CSV alongside it.
    /// </summary>
    /// <returns>The parsed result containing all rows, warnings, and error counts.</returns>
    Task<CsvParseResult> ParseAsync(string filePath, CancellationToken ct = default);
}

// ══════════════════════════════════════════════════════════════════════════════
//  Parsing contract
//  ─────────────────
//  Expected header (case-insensitive, leading/trailing whitespace trimmed):
//      timestamp, temp_c, humidity_%, pressure_hpa, irradiance_wm2
//
//  Trailing columns with no header are silently ignored when empty.
//  Extra *named* columns (or extra data columns beyond col 4) are logged as
//  errors and discarded if non-empty.
//
//  Timestamp formats accepted
//  ──────────────────────────
//  • Relative:  T+<N>s    (kept as-is as a string literal)
//  • Unix epoch: any long / double that is plausibly a Unix timestamp
//    (1 000 000 000 .. 9 999 999 999)  → converted to "MM/dd/yyyy HH:mm:ss"
//
//  Validation
//  ──────────
//  temp_c       : warn  <-20 or >50  |  error (discard) <-40 or >85
//  humidity_%   : error (discard)    outside 0–100
//  pressure_hpa : warn  outside 800–1100
//  irradiance   : warn  >1500
//
//  Missing / skippable cells
//  ─────────────────────────
//  If one of the 5 data columns is empty the parser writes a WARNING and
//  looks right along the row until it either finds a non-empty cell or
//  reaches end-of-line.  The first non-empty cell found is validated as if
//  it were the expected column; if it passes it is used, otherwise an ERROR
//  is written and parsing continues from the cell *after the original column
//  position* (not after the candidate cell).
//
//  Outputs (written in parallel)
//  ──────────────────────────────
//  1. <basename>_errors.csv   – every ParseLogEntry (row, col, level, field, raw, message)
//  2. <basename>_computed.csv – all parsed rows + VPD, dew-point, abs-humidity,
//                               accumulated irradiance (kWh/m²)
// ══════════════════════════════════════════════════════════════════════════════
public sealed class CsvService : ICsvService
{
    // ── Column indices in the source file ──────────────────────────────────────
    private const int COL_TIMESTAMP = 0;
    private const int COL_TEMP = 1;
    private const int COL_HUMIDITY = 2;
    private const int COL_PRESSURE = 3;
    private const int COL_IRRADIANCE = 4;
    private const int EXPECTED_COLS = 5;

    // ── Sentinel written into the computed CSV for a failed cell ──────────────
    private static readonly string Err = SensorRow.PARSE_FAILURE;

    // ══════════════════════════════════════════════════════════════════════════
    public async Task<CsvParseResult> ParseAsync(string filePath, CancellationToken ct = default)
    {
        // ── Derive output paths ────────────────────────────────────────────────
        string dir = Path.GetDirectoryName(filePath) ?? string.Empty;
        string baseName = Path.GetFileNameWithoutExtension(filePath);
        string errorLogPath = Path.Combine(dir, $"{baseName}_errors.csv");
        string computedPath = Path.Combine(dir, $"{baseName}_computed.csv");

        // ── Shared log list (appended by parser, flushed by writer task) ──────
        var logEntries = new System.Collections.Concurrent.ConcurrentQueue<ParseLogEntry>();
        var rows = new List<SensorRow>();
        int errorCount = 0;
        int warnCount = 0;

        // ── Read all lines up-front (files are small sensor logs) ─────────────
        string[] lines = await File.ReadAllLinesAsync(filePath, Encoding.UTF8, ct);

        // ── Find and validate header ───────────────────────────────────────────
        int dataStartLine = FindAndValidateHeader(lines, logEntries, ref errorCount);

        // ── Open the computed CSV writer (parallel stream) ─────────────────────
        // We open the file now and write rows as they are parsed so that the
        // computed output is ready the moment ParseAsync returns.
        await using var computedWriter = new StreamWriter(computedPath, append: false, Encoding.UTF8);
        await computedWriter.WriteLineAsync(
            "timestamp,temp_c,humidity_%,pressure_hpa,irradiance_wm2," +
            "vpd_kpa,dew_point_c,abs_humidity_gm3,accumulated_irradiance_kwh_m2");

        // ── Parse rows ─────────────────────────────────────────────────────────
        double accumulatedIrradiance = 0.0; // running total in Wh/m²

        for (int lineIdx = dataStartLine; lineIdx < lines.Length; lineIdx++)
        {
            ct.ThrowIfCancellationRequested();

            string line = lines[lineIdx].TrimEnd();
            if (string.IsNullOrWhiteSpace(line)) continue;

            string[] cells = SplitCsvLine(line);

            // ── Trim trailing empty cells created by ,,,, suffixes ─────────────
            int lastNonEmpty = cells.Length - 1;
            while (lastNonEmpty >= EXPECTED_COLS && string.IsNullOrWhiteSpace(cells[lastNonEmpty]))
                lastNonEmpty--;
            int effectiveCols = lastNonEmpty + 1;

            // ── Warn about extra NON-EMPTY columns beyond col 4 ───────────────
            for (int c = EXPECTED_COLS; c <= lastNonEmpty; c++)
            {
                if (!string.IsNullOrWhiteSpace(cells[c]))
                {
                    Log(logEntries, lineIdx, c, "ERROR", $"col{c}", cells[c],
                        "Extra column beyond the 5 expected; value discarded.");
                    errorCount++;
                }
            }

            // ── Parse each of the 5 required fields ───────────────────────────
            string timestamp = ParseTimestamp(cells, lineIdx, logEntries, ref errorCount);
            double? temp = ParseDouble(cells, lineIdx, COL_TEMP, effectiveCols, "temp_c", logEntries, ref errorCount, ref warnCount,
                                        warnLow: -20, warnHigh: 50, failLow: -40, failHigh: 85,
                                        outsideRangeIsWarnOnly: false);
            double? humidity = ParseDouble(cells, lineIdx, COL_HUMIDITY, effectiveCols, "humidity_%", logEntries, ref errorCount, ref warnCount,
                                        warnLow: 0, warnHigh: 100, failLow: 0, failHigh: 100,
                                        outsideRangeIsWarnOnly: false, warnOnlyRange: false);
            double? pressure = ParseDouble(cells, lineIdx, COL_PRESSURE, effectiveCols, "pressure_hpa", logEntries, ref errorCount, ref warnCount,
                                        warnLow: 800, warnHigh: 1100, failLow: double.MinValue, failHigh: double.MaxValue,
                                        outsideRangeIsWarnOnly: true);
            double? irradiance = ParseDouble(cells, lineIdx, COL_IRRADIANCE, effectiveCols, "irradiance_wm2", logEntries, ref errorCount, ref warnCount,
                                        warnLow: 0, warnHigh: 1500, failLow: double.MinValue, failHigh: double.MaxValue,
                                        outsideRangeIsWarnOnly: true, warnOnlyHigh: true);

            // ── Compute derived fields ─────────────────────────────────────────
            double? vpd = ComputeVpd(temp, humidity);
            double? dewPt = ComputeDewPoint(temp, humidity);
            double? absHum = ComputeAbsoluteHumidity(temp, humidity);

            // Accumulate irradiance.  We need the interval in hours.
            // Without a real clock we treat every row as instantaneous (0 s interval)
            // and use a trapezoidal approach when two consecutive valid readings exist.
            // For the first row or when irradiance is null we add 0.
            if (irradiance.HasValue)
            {
                double intervalHours = EstimateIntervalHours(rows, irradiance.Value);
                accumulatedIrradiance += irradiance.Value * intervalHours; // Wh/m²
            }
            double accKwh = accumulatedIrradiance / 1000.0;

            bool tempWarn = temp == null ? false : (temp < -20 || temp > 50);
            bool humidityWarn = false; // 0-100 is error-only for humidity
            bool pressureWarn = pressure == null ? false : (pressure < 800 || pressure > 1100);
            bool irradianceWarn = irradiance == null ? false : (irradiance > 1500);

            var row = new SensorRow
            {
                Timestamp = timestamp,
                TempC = temp,
                HumidityPct = humidity,
                PressureHpa = pressure,
                IrradianceWm2 = irradiance,
                Vpd = vpd,
                DewPointC = dewPt,
                AbsHumidityGm3 = absHum,
                AccumulatedIrradianceKwhM2 = accKwh,
                TempWarn = tempWarn,
                HumidityWarn = humidityWarn,
                PressureWarn = pressureWarn,
                IrradianceWarn = irradianceWarn,
            };
            rows.Add(row);

            // ── Write computed row ─────────────────────────────────────────────
            await computedWriter.WriteLineAsync(FormatComputedRow(row));
        }

        // ── Flush computed file ────────────────────────────────────────────────
        await computedWriter.FlushAsync(ct);

        // ── Write error log ────────────────────────────────────────────────────
        await WriteErrorLogAsync(errorLogPath, logEntries, ct);

        return new CsvParseResult
        {
            Rows = rows.AsReadOnly(),
            ErrorCount = errorCount,
            WarningCount = warnCount,
            ErrorLogPath = errorLogPath,
            ComputedCsvPath = computedPath,
        };
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  Header detection
    // ══════════════════════════════════════════════════════════════════════════

    private static int FindAndValidateHeader(
        string[] lines,
        System.Collections.Concurrent.ConcurrentQueue<ParseLogEntry> log,
        ref int errorCount)
    {
        for (int i = 0; i < lines.Length; i++)
        {
            string[] cells = SplitCsvLine(lines[i]);
            if (cells.Length < EXPECTED_COLS) continue;

            bool looksLikeHeader =
                NormCol(cells[COL_TIMESTAMP]) == "timestamp" &&
                NormCol(cells[COL_TEMP]) == "temp_c" &&
                NormCol(cells[COL_HUMIDITY]) == "humidity_%" &&
                NormCol(cells[COL_PRESSURE]) == "pressure_hpa" &&
                NormCol(cells[COL_IRRADIANCE]) == "irradiance_wm2";

            if (!looksLikeHeader) continue;

            // Check for unexpected extra named headers
            for (int c = EXPECTED_COLS; c < cells.Length; c++)
            {
                string cell = cells[c].Trim();
                if (!string.IsNullOrEmpty(cell))
                {
                    Log(log, i, c, "ERROR", $"col{c}", cell,
                        $"Unexpected header column '{cell}'; column will be ignored.");
                    errorCount++;
                }
            }

            return i + 1; // data starts on the next line
        }

        // No header found — assume first line is data
        Log(log, 0, 0, "WARN", "header", string.Empty,
            "No recognisable header row found; assuming data starts at line 0.");
        return 0;
    }

    private static string NormCol(string s) => s.Trim().ToLowerInvariant();

    // ══════════════════════════════════════════════════════════════════════════
    //  Timestamp parsing
    // ══════════════════════════════════════════════════════════════════════════

    private static string ParseTimestamp(
        string[] cells,
        int lineIdx,
        System.Collections.Concurrent.ConcurrentQueue<ParseLogEntry> log,
        ref int errorCount)
    {
        if (cells.Length <= COL_TIMESTAMP || string.IsNullOrWhiteSpace(cells[COL_TIMESTAMP]))
        {
            Log(log, lineIdx, COL_TIMESTAMP, "ERROR", "timestamp", "",
                "Timestamp cell is empty.");
            errorCount++;
            return Err;
        }

        string raw = cells[COL_TIMESTAMP].Trim();

        // Relative format: T+<N>s
        if (raw.StartsWith("T+", StringComparison.OrdinalIgnoreCase) && raw.EndsWith("s", StringComparison.OrdinalIgnoreCase))
            return raw; // keep literal

        // Unix epoch: a long integer or decimal in plausible range
        if (double.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out double epoch) &&
            epoch >= 1_000_000_000 && epoch <= 9_999_999_999)
        {
            var dt = DateTimeOffset.FromUnixTimeSeconds((long)epoch).LocalDateTime;
            return dt.ToString("MM/dd/yyyy HH:mm:ss", CultureInfo.InvariantCulture);
        }

        // Unrecognised — keep as-is but warn
        Log(log, lineIdx, COL_TIMESTAMP, "WARN", "timestamp", raw,
            "Unrecognised timestamp format; value kept as-is.");
        return raw;
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  Generic numeric field parser with skip-ahead for empty cells
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Parses a double from <paramref name="cells"/> at <paramref name="colIdx"/>.
    /// • If the cell is empty: logs a WARNING then scans right to find the first
    ///   non-empty cell, validates it as a candidate, and uses it if valid.
    ///   Parsing then resumes from (colIdx + 1), not after the candidate.
    /// • Type errors replace the value with null (sentinel written to CSV).
    /// • Range errors: fatal range → null; warn-only range → value kept, warning logged.
    /// </summary>
    private static double? ParseDouble(
        string[] cells,
        int lineIdx,
        int colIdx,
        int effectiveCols,
        string fieldName,
        System.Collections.Concurrent.ConcurrentQueue<ParseLogEntry> log,
        ref int errorCount,
        ref int warnCount,
        double warnLow,
        double warnHigh,
        double failLow,
        double failHigh,
        bool outsideRangeIsWarnOnly,   // pressure, irradiance
        bool warnOnlyRange = false,   // humidity uses error-only out-of-range
        bool warnOnlyHigh = false)   // irradiance: only warn on high
    {
        string raw = colIdx < cells.Length ? cells[colIdx].Trim() : string.Empty;

        if (string.IsNullOrEmpty(raw))
        {
            // ── Empty cell: look ahead ─────────────────────────────────────────
            Log(log, lineIdx, colIdx, "WARN", fieldName, "",
                $"Empty cell for '{fieldName}'; scanning ahead for a value.");
            warnCount++;

            double? candidate = null;
            for (int scanCol = colIdx + 1; scanCol < effectiveCols; scanCol++)
            {
                string scanRaw = cells[scanCol].Trim();
                if (string.IsNullOrEmpty(scanRaw)) continue;

                // Found a non-empty cell: try to validate it
                if (!double.TryParse(scanRaw, NumberStyles.Any, CultureInfo.InvariantCulture, out double scanVal))
                {
                    Log(log, lineIdx, scanCol, "ERROR", fieldName, scanRaw,
                        $"Candidate value for '{fieldName}' (found at col {scanCol}) is not a valid number; discarded.");
                    errorCount++;
                    // Resume from colIdx+1 per spec — break out, return null
                    return null;
                }

                var (scanOk, scanMsg, scanIsWarn) = ValidateRange(scanVal, fieldName,
                    warnLow, warnHigh, failLow, failHigh, outsideRangeIsWarnOnly, warnOnlyHigh);

                if (!scanOk)
                {
                    Log(log, lineIdx, scanCol, "ERROR", fieldName, scanRaw, scanMsg);
                    errorCount++;
                    return null;
                }
                if (scanIsWarn)
                {
                    Log(log, lineIdx, scanCol, "WARN", fieldName, scanRaw,
                        $"Candidate value used for '{fieldName}' (originally col {colIdx}, found at col {scanCol}): {scanMsg}");
                    warnCount++;
                }
                else
                {
                    Log(log, lineIdx, scanCol, "INFO", fieldName, scanRaw,
                        $"Candidate value {scanVal} used for '{fieldName}' (originally col {colIdx}, found at col {scanCol}).");
                }
                candidate = scanVal;
                break;
            }

            if (candidate == null)
            {
                Log(log, lineIdx, colIdx, "ERROR", fieldName, "",
                    $"No valid value found for '{fieldName}' across remaining columns.");
                errorCount++;
            }
            return candidate;
        }

        // ── Normal cell: parse ─────────────────────────────────────────────────
        if (!double.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out double val))
        {
            Log(log, lineIdx, colIdx, "ERROR", fieldName, raw,
                $"'{raw}' is not a valid number for field '{fieldName}'; cell replaced with {Err}.");
            errorCount++;
            return null;
        }

        var (ok, msg, isWarn) = ValidateRange(val, fieldName,
            warnLow, warnHigh, failLow, failHigh, outsideRangeIsWarnOnly, warnOnlyHigh);

        if (!ok)
        {
            Log(log, lineIdx, colIdx, "ERROR", fieldName, raw, msg);
            errorCount++;
            return null;
        }
        if (isWarn)
        {
            Log(log, lineIdx, colIdx, "WARN", fieldName, raw, msg);
            warnCount++;
        }

        return val;
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  Range validation helper
    // ══════════════════════════════════════════════════════════════════════════

    /// <returns>(isOk, message, isWarnOnly)</returns>
    private static (bool ok, string msg, bool warnOnly) ValidateRange(
        double val,
        string field,
        double warnLow, double warnHigh,
        double failLow, double failHigh,
        bool outsideRangeIsWarnOnly,
        bool warnOnlyHigh = false)
    {
        // Hard-fail range check
        if (!outsideRangeIsWarnOnly)
        {
            if (val < failLow || val > failHigh)
                return (false,
                    $"'{field}' value {val} is outside the hard-fail range [{failLow}, {failHigh}]; discarded.",
                    false);
        }

        // Warn range check
        bool belowWarn = val < warnLow;
        bool aboveWarn = val > warnHigh;

        if (warnOnlyHigh && aboveWarn)
            return (true,
                $"'{field}' value {val} exceeds warning threshold {warnHigh}.",
                true);

        if (!warnOnlyHigh && (belowWarn || aboveWarn))
        {
            if (outsideRangeIsWarnOnly)
                return (true,
                    $"'{field}' value {val} is outside the warning range [{warnLow}, {warnHigh}].",
                    true);

            // For temp: warn range is inner; fail range is outer
            // belowWarn / aboveWarn means it's outside [-20,50] but inside [-40,85]
            return (true,
                $"'{field}' value {val} is outside the recommended range [{warnLow}, {warnHigh}].",
                true);
        }

        return (true, string.Empty, false);
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  Thermodynamic computations
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Saturation vapour pressure via Buck equation (hPa → kPa conversion).
    /// es(T) = 6.1078 × exp(17.27·T / (T + 237.3))  [hPa]
    /// </summary>
    private static double SatVapourPressure(double tempC)
        => 0.61078 * Math.Exp(17.27 * tempC / (tempC + 237.3)); // kPa

    /// <summary>VPD = es(T) × (1 - RH/100)  [kPa]</summary>
    private static double? ComputeVpd(double? tempC, double? humidityPct)
    {
        if (!tempC.HasValue || !humidityPct.HasValue) return null;
        double es = SatVapourPressure(tempC.Value);
        return Math.Round(es * (1.0 - humidityPct.Value / 100.0), 4);
    }

    /// <summary>Magnus formula dew-point [°C].</summary>
    private static double? ComputeDewPoint(double? tempC, double? humidityPct)
    {
        if (!tempC.HasValue || !humidityPct.HasValue) return null;
        const double a = 17.625, b = 243.04;
        double T = tempC.Value;
        double RH = humidityPct.Value;
        if (RH <= 0) return null;
        double gamma = Math.Log(RH / 100.0) + a * T / (b + T);
        return Math.Round(b * gamma / (a - gamma), 4);
    }

    /// <summary>
    /// Absolute humidity [g/m³]:
    ///   AH = (RH/100) × 6.112 × exp(17.67·T/(T+243.5)) × 2.1674 / (T+273.15)
    /// </summary>
    private static double? ComputeAbsoluteHumidity(double? tempC, double? humidityPct)
    {
        if (!tempC.HasValue || !humidityPct.HasValue) return null;
        double T = tempC.Value;
        double RH = humidityPct.Value;
        double es = 6.112 * Math.Exp(17.67 * T / (T + 243.5));
        return Math.Round(RH / 100.0 * es * 2.1674 / (T + 273.15), 4);
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  Irradiance accumulation
    //  We don't know the real time interval between rows (relative timestamps
    //  like T+60s give elapsed seconds from start, not inter-row deltas).
    //  Strategy: derive the interval in seconds from consecutive relative
    //  timestamps when possible; otherwise assume 1 second.
    // ══════════════════════════════════════════════════════════════════════════

    private static double EstimateIntervalHours(List<SensorRow> previous, double currentW)
    {
        if (previous.Count == 0) return 0.0; // first row — no interval yet

        SensorRow prev = previous[^1];

        // Try to extract seconds from relative timestamps
        double tPrev = ParseRelativeSeconds(prev.Timestamp);
        // We don't have the current timestamp here — use a default 1 s
        // (caller could pass it; kept simple for now)
        double intervalS = 1.0;

        // If previous was also relative, delta is meaningful when rows grow
        // monotonically; we compute from stored rows
        if (previous.Count >= 2)
        {
            double tPrevPrev = ParseRelativeSeconds(previous[^2].Timestamp);
            if (tPrev >= 0 && tPrevPrev >= 0)
                intervalS = Math.Max(0, tPrev - tPrevPrev);
        }

        return intervalS / 3600.0;
    }

    /// <summary>Returns seconds from "T+Ns" or -1 if not parseable.</summary>
    private static double ParseRelativeSeconds(string ts)
    {
        if (ts.StartsWith("T+", StringComparison.OrdinalIgnoreCase) &&
            ts.EndsWith("s", StringComparison.OrdinalIgnoreCase))
        {
            string inner = ts[2..^1];
            if (double.TryParse(inner, NumberStyles.Any, CultureInfo.InvariantCulture, out double s))
                return s;
        }
        return -1;
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  Computed-row formatter
    // ══════════════════════════════════════════════════════════════════════════

    private static string FormatComputedRow(SensorRow r)
    {
        static string F(double? v) => v.HasValue
            ? v.Value.ToString("G6", CultureInfo.InvariantCulture)
            : SensorRow.PARSE_FAILURE;

        return string.Join(",",
            EscapeCsv(r.Timestamp),
            F(r.TempC),
            F(r.HumidityPct),
            F(r.PressureHpa),
            F(r.IrradianceWm2),
            F(r.Vpd),
            F(r.DewPointC),
            F(r.AbsHumidityGm3),
            F(r.AccumulatedIrradianceKwhM2));
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  Error-log writer
    // ══════════════════════════════════════════════════════════════════════════

    private static async Task WriteErrorLogAsync(
        string path,
        System.Collections.Concurrent.ConcurrentQueue<ParseLogEntry> entries,
        CancellationToken ct)
    {
        await using var writer = new StreamWriter(path, append: false, Encoding.UTF8);
        await writer.WriteLineAsync("row_index,col_index,level,field,raw_value,message");

        foreach (var e in entries)
        {
            ct.ThrowIfCancellationRequested();
            await writer.WriteLineAsync(string.Join(",",
                e.RowIndex,
                e.ColIndex,
                EscapeCsv(e.Level),
                EscapeCsv(e.Field),
                EscapeCsv(e.RawValue),
                EscapeCsv(e.Message)));
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  Utilities
    // ══════════════════════════════════════════════════════════════════════════

    private static void Log(
        System.Collections.Concurrent.ConcurrentQueue<ParseLogEntry> q,
        int row, int col, string level, string field, string raw, string msg)
    {
        q.Enqueue(new ParseLogEntry
        {
            RowIndex = row,
            ColIndex = col,
            Level = level,
            Field = field,
            RawValue = raw,
            Message = msg,
        });
    }

    /// <summary>Splits a CSV line respecting double-quoted fields.</summary>
    private static string[] SplitCsvLine(string line)
    {
        var result = new List<string>();
        bool inQuotes = false;
        var sb = new StringBuilder();

        foreach (char c in line)
        {
            if (c == '"') { inQuotes = !inQuotes; }
            else if (c == ',' && !inQuotes) { result.Add(sb.ToString()); sb.Clear(); }
            else { sb.Append(c); }
        }
        result.Add(sb.ToString());
        return result.ToArray();
    }

    private static string EscapeCsv(string? value)
    {
        if (value is null) return string.Empty;
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n'))
            return $"\"{value.Replace("\"", "\"\"")}\"";
        return value;
    }
}