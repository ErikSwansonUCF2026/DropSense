using DropSense.Models;
using OfficeOpenXml;
using OfficeOpenXml.Drawing;
using OfficeOpenXml.Drawing.Chart;
using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Linq;

namespace DropSense.Services;

// ══════════════════════════════════════════════════════════════════════════
//  GROUP 4 · GRAPHING
//  ─────────────────────────────────────────────────────────────────────────
//  Everything needed to turn the Graphing section of the export form into a
//  worksheet full of charts lives in this file, kept separate from the rest
//  of ExportXlsxService so the two areas can evolve independently.
//
//  Layout of the "Graphs" sheet:
//    • Columns A–J   : one chart per included measurement, stacked vertically.
//    • Column L+     : the resampled (time-bucketed) data table that every
//                       chart's series reference.
//
//  Scatter-only note: Line and Bar chart types have been removed entirely
//  (from this file, the ViewModel, and the View). Both render time as a
//  plain category axis in this charting library, not a true date/time-scale
//  axis — that's what caused the label-skew and "collapses to a day scale"
//  problems investigated previously. Scatter's X-axis is a genuine
//  value/date axis and handles every resolution (Auto/Hourly/Daily/Weekly)
//  correctly, so it's now the only chart type offered.
//
//  "Show Data Point Markers" and "Smooth Curves" are implemented by picking
//  the appropriate eChartType.XYScatter* variant (see DetermineScatterChartType)
//  rather than per-series flags — OOXML encodes a scatter chart's marker/line
//  style at the chart-type level (scatterStyle), not as a boolean on each
//  series.
//  Graphing generates scatter charts from explicitly selected measurements.
//  Charts consume ProcessDataResult outputs and never perform calculations.
//  Derived measurements must already exist before graph export.
//  
//  Timestamp note: SensorRow.Timestamp is a raw CSV string, not a DateTime.
//  ParseTimestamp() below is the single place that parses it; rows that
//  fail to parse are excluded from the time axis (see BuildResampledSeries).
// ══════════════════════════════════════════════════════════════════════════

public partial class ExportXlsxService
{
    /// <summary>One resampled measurement series, ready to be dropped into a chart.</summary>
    private sealed record GraphSeriesData(string ColumnKey, string Label, string Unit, double?[] Values);

    /// <summary>Effective time-bucket granularity used to resample rows for charting.</summary>
    private enum GraphBucketResolution { Raw, Hourly, Daily, Weekly }

    /// <summary>
    /// Label format and tick spacing for a Scatter chart's X-axis, which is a
    /// genuine value/date axis (unlike the category axis Line/Bar used).
    /// </summary>
    /// <param name="Format">.NET date/time format string for axis tick labels (e.g. "HH:mm", "MM/dd").</param>
    /// <param name="MajorUnitDays">
    /// Spacing between major ticks, expressed in days (Excel's date-serial unit — 1.0 = 1 day,
    /// so e.g. 1 hour = 1.0/24). This is a real numeric axis quantity, not a label-skip count.
    /// </param>
    private readonly record struct TimeAxisSettings(string Format, double MajorUnitDays);

    /// <summary>
    /// Measurement catalogue for graphing: column key (matches
    /// <see cref="ProcessDataResult.Columns"/> / <see cref="BuildColumnSelectors"/>),
    /// display label, the config toggle that gates inclusion, and unit for axis labels.
    /// </summary>
    private static readonly (string ColumnKey, string Label, Func<ExportConfiguration, bool> Included, string Unit)[] GraphMeasurementDefs =
    [
        ("temp_c",                        "Temperature",                 c => c.IncludeTemperature,               "°C"),
        ("humidity_%",                    "Relative Humidity",           c => c.IncludeRelativeHumidity,          "%"),
        ("pressure_hpa",                  "Barometric Pressure",         c => c.IncludeBarometricPressure,        "hPa"),
        ("irradiance_wm2",                "Solar Irradiance",            c => c.IncludeSolarIrradiance,           "W/m²"),
        ("vpd_kpa",                       "Vapor Pressure Deficit",      c => c.IncludeVaporPressureDeficit,      "kPa"),
        ("dew_point_c",                   "Dew Point Temperature",       c => c.IncludeDewPoint,                  "°C"),
        ("abs_humidity_gm3",              "Absolute Humidity",           c => c.IncludeAbsoluteHumidity,          "g/m³"),
        ("accumulated_irradiance_kwh_m2", "Accumulated Solar Radiation", c => c.IncludeAccumulatedSolarRadiation, "kWh/m²"),
        ("dli_mol_m2_d",                  "Daily Light Integral",        c => c.IncludeDailyLightIntegral,        "mol/m²/d"),
        ("par_umol_m2_s","Estimated PAR", c => c.IncludePAR, "µmol/m²/s"),    ];

    /// <summary>
    /// Every measurement except Temperature is physically non-negative, so
    /// its chart's vertical axis should never dip below 0 regardless of how
    /// auto-scaling (or the anomaly-band padding) would otherwise round it.
    /// </summary>
    private static bool AllowsNegativeValues(string columnKey) => columnKey == "temp_c";

    /// <summary>
    /// Scatter's marker/line style is chosen via the chart-type enum itself
    /// (OOXML's scatterStyle), not a per-series boolean — so "Show Data Point
    /// Markers" and "Smooth Curves" together select one of four XYScatter*
    /// variants rather than being applied afterward to a fixed chart type.
    /// </summary>
    private static eChartType DetermineScatterChartType(ExportConfiguration config)
    {
        if (config.GraphSmoothCurves)
            return config.GraphShowMarkers ? eChartType.XYScatterSmooth : eChartType.XYScatterSmoothNoMarkers;

        return config.GraphShowMarkers ? eChartType.XYScatterLines : eChartType.XYScatterLinesNoMarkers;
    }

    // ── Entry point ──────────────────────────────────────────────────────────

    private static void WriteGraphsSheet(
        ExcelPackage package,
        ProcessDataResult data,
        IReadOnlyList<SensorRow> rows,
        ExportConfiguration config)
    {
        if (rows.Count == 0) return;

        eChartType chartType = DetermineScatterChartType(config);
        GraphBucketResolution resolution = ResolveBucketResolution(rows, config.GraphTimeResolution);

        var (buckets, seriesList) = BuildResampledSeries(rows, data.Columns, config, resolution);
        if (buckets.Length == 0 || seriesList.Count == 0) return;

        ExcelWorksheet ws = package.Workbook.Worksheets.Add("Graphs");

        const string headerBg = "1F4E79";
        const string headerFg = "FFFFFF";
        const string altRowBg = "D6E4F0";
        const string bodyFont = "Arial";


        string bucketHeader = resolution switch
        {
            GraphBucketResolution.Hourly => "Hour",
            GraphBucketResolution.Daily => "Date",
            GraphBucketResolution.Weekly => "Week Of",
            _ => "Timestamp",
        };
        // NOTE: minutes use the real "mm" placeholder (not a literal "00") —
        // literal digit characters like 0/#/? aren't valid inside a pure
        // date/time format code without quoting, and produced a malformed
        // numFmt that Excel would flag and "repair" out of styles.xml. Since
        // every hourly bucket's DateTime always has Minute == 0, "mm" renders
        // as "00" anyway, with none of the escaping risk.
        string bucketFormat = resolution == GraphBucketResolution.Hourly ? "MM/dd/yy\\, HH:mm" : "MM/dd/yyyy";

        // ── Layout constants — mirrors the Moving Average sheet's convention
        //    of charts stacked in a fixed-width left column, table to the right. ──
        const int ChartCols = 10;
        const int GapCols = 1;
        const int TableStartCol = ChartCols + GapCols + 1; // col L = 12

        SetChartAreaColumnWidths(ws, ChartCols);

        // ── Data table ───────────────────────────────────────────────────────
        ws.Cells[1, TableStartCol].Value = bucketHeader;
        StyleHeader(ws.Cells[1, TableStartCol], headerBg, headerFg, bodyFont);

        for (int r = 0; r < buckets.Length; r++)
        {
            int excelRow = r + 2;
            var cell = ws.Cells[excelRow, TableStartCol];
            cell.Value = buckets[r];
            cell.Style.Numberformat.Format = bucketFormat;
            StyleBodyCell(cell, bodyFont, r % 2 == 1 ? altRowBg : "FFFFFF");
        }

        var valueColOf = new Dictionary<string, int>();
        var flagColOf = new Dictionary<string, int>();

        int col = TableStartCol + 1;
        foreach (GraphSeriesData s in seriesList)
        {
           

            int valueCol = col++;
            ws.Cells[1, valueCol].Value = string.IsNullOrEmpty(s.Unit) ? s.Label : $"{s.Label} ({s.Unit})";
            StyleHeader(ws.Cells[1, valueCol], headerBg, headerFg, bodyFont);
            valueColOf[s.ColumnKey] = valueCol;

            int flagCol = -1;
           

            for (int r = 0; r < s.Values.Length; r++)
            {
                int excelRow = r + 2;
                bool stripe = r % 2 == 1;
                double? v = s.Values[r];

                var vCell = ws.Cells[excelRow, valueCol];
                if (v.HasValue)
                {
                    vCell.Value = Math.Round(v.Value, 4);
                    vCell.Style.Numberformat.Format = "0.0000";
                }
                else
                {
                    vCell.Value = "—";
                }
                StyleBodyCell(vCell, bodyFont, stripe ? altRowBg : "FFFFFF");

               

               
            }

            ws.Column(valueCol).AutoFit();
            if (flagCol > 0) ws.Column(flagCol).AutoFit();
        }

        ws.Column(TableStartCol).Width = 16;

        

        // ── Primary sheet: Option D — reference lines + flagged-point highlighting ──
        WritePerMeasurementCharts(ws, ws, buckets, seriesList, valueColOf, TableStartCol,
        0, chartType, config);
        ws.View.FreezePanes(2, TableStartCol);

    }

    private static void SetChartAreaColumnWidths(ExcelWorksheet ws, int chartCols)
    {
        for (int c = 1; c <= chartCols; c++)
            ws.Column(c).Width = 9.1;
        ws.Column(chartCols + 1).Width = 2;
    }

    // ── Time bucketing ───────────────────────────────────────────────────────

    /// <summary>
    /// <see cref="SensorRow.Timestamp"/> is a string straight out of the CSV,
    /// not a parsed <see cref="DateTime"/>. This is the single place that
    /// turns it into one; every other method in this file works off the
    /// parsed value so a parsing quirk only has to be handled here.
    /// Rows whose timestamp can't be parsed are treated as unusable for
    /// charting (excluded) rather than throwing or silently misordering
    /// the time axis.
    /// </summary>
    private static DateTime? ParseTimestamp(SensorRow row)
    {
        string? raw = row.Timestamp;
        if (string.IsNullOrWhiteSpace(raw)) return null;

        if (DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out DateTime dt))
            return dt;

        // Fall back to the current-culture parser in case the CSV was
        // produced with locale-specific date formatting.
        return DateTime.TryParse(raw, out dt) ? dt : null;
    }

    private static GraphBucketResolution ResolveBucketResolution(IReadOnlyList<SensorRow> rows, GraphTimeResolution requested)
    {
        switch (requested)
        {
            case GraphTimeResolution.Hourly: return GraphBucketResolution.Hourly;
            case GraphTimeResolution.Daily: return GraphBucketResolution.Daily;
            case GraphTimeResolution.Weekly: return GraphBucketResolution.Weekly;
            case GraphTimeResolution.Auto:
            default:
                List<DateTime> parsed = rows
                    .Select(ParseTimestamp)
                    .Where(d => d.HasValue)
                    .Select(d => d!.Value)
                    .ToList();

                if (parsed.Count < 2) return GraphBucketResolution.Raw;

                TimeSpan span = parsed.Max() - parsed.Min();
                if (span <= TimeSpan.FromDays(3)) return GraphBucketResolution.Raw;
                if (span <= TimeSpan.FromDays(14)) return GraphBucketResolution.Hourly;
                if (span <= TimeSpan.FromDays(120)) return GraphBucketResolution.Daily;
                return GraphBucketResolution.Weekly;
        }
    }

    private static DateTime BucketKey(DateTime timestamp, GraphBucketResolution resolution) => resolution switch
    {
        GraphBucketResolution.Hourly => new DateTime(timestamp.Year, timestamp.Month, timestamp.Day, timestamp.Hour, 0, 0),
        GraphBucketResolution.Daily => timestamp.Date,
        GraphBucketResolution.Weekly => timestamp.Date.AddDays(-(int)timestamp.DayOfWeek), // week starting Sunday
        _ => timestamp, // Raw — effectively one bucket per row
    };

    /// <summary>
    /// Parses each row's timestamp, groups the parseable ones into time
    /// buckets per the resolved resolution, and averages each included
    /// measurement within every bucket — producing one aligned array of
    /// bucket keys plus one values-array per measurement, exactly what a
    /// chart series needs. Rows with an unparseable timestamp are dropped
    /// from the graphs (they still appear on the Data sheet untouched).
    /// </summary>
    private static (DateTime[] Buckets, List<GraphSeriesData> Series) BuildResampledSeries(
        IReadOnlyList<SensorRow> rows,
        IEnumerable<string> dataColumns,
        ExportConfiguration config,
        GraphBucketResolution resolution)
    {
        var selectors = BuildColumnSelectors();
        var columnSet = new HashSet<string>(dataColumns);

        var timedRows = rows
            .Select(r => (Row: r, Timestamp: ParseTimestamp(r)))
            .Where(x => x.Timestamp.HasValue)
            .Select(x => (x.Row, Timestamp: x.Timestamp!.Value))
            .ToList();

        var bucketed = timedRows
            .Select(x => (x.Row, Key: BucketKey(x.Timestamp, resolution)))
            .GroupBy(x => x.Key)
            .OrderBy(g => g.Key)
            .ToList();

        DateTime[] buckets = bucketed.Select(g => g.Key).ToArray();

        var seriesList = new List<GraphSeriesData>();

        foreach (var def in GraphMeasurementDefs)
        {
            if (!columnSet.Contains(def.ColumnKey)) continue;
            if (!def.Included(config)) continue;
            if (!selectors.TryGetValue(def.ColumnKey, out var selector)) continue;

            double?[] values = bucketed.Select(g =>
            {
                double[] vals = g
                    .Select(x => selector(x.Row))
                    .Where(v => v.HasValue)
                    .Select(v => v!.Value)
                    .ToArray();
                return vals.Length > 0 ? (double?)vals.Average() : null;
            }).ToArray();

            seriesList.Add(new GraphSeriesData(def.ColumnKey, def.Label, def.Unit, values));
        }

        return (buckets, seriesList);
    }

    // ── Per-measurement charts ───────────────────────────────────────────────

    /// <summary>
    /// Writes one chart per measurement onto <paramref name="chartSheet"/>,
    /// sourcing series data from <paramref name="dataSheet"/> (the same
    /// worksheet for the primary "Graphs" sheet; a different one for the
    /// "Reference Lines Only" comparison sheet, via ordinary cross-sheet
    /// cell references — EPPlus/Excel both support a chart series living on
    /// one sheet while its data range lives on another).
    /// </summary>
    private static void WritePerMeasurementCharts(
        ExcelWorksheet dataSheet,
        ExcelWorksheet chartSheet,
        DateTime[] buckets,
        List<GraphSeriesData> seriesList,
        Dictionary<string, int> valueColOf,
        int tableStartCol,
        int startAnchorRow,
        eChartType chartType,
        ExportConfiguration config)
    {
        const int ChartWidthPx = 640;
        const int ChartHeightPx = 300;
        const int RowHeightPx = 20;
        const int ChartRowSpan = ChartHeightPx / RowHeightPx;
        const int PaddingRows = 1;

        var xRange = dataSheet.Cells[2, tableStartCol, buckets.Length + 1, tableStartCol];

        TimeAxisSettings axis = DetermineTimeAxisSettings(buckets);

        int anchorRow = startAnchorRow;

        foreach (GraphSeriesData s in seriesList)
        {
            string safeName = "G_" + SafeSheetObjectName(chartSheet.Name) + "_" + SafeSheetObjectName(s.ColumnKey);
            ExcelChart chart = chartSheet.Drawings.AddChart(safeName, chartType);

            chart.Title.Text = string.IsNullOrEmpty(s.Unit) ? s.Label : $"{s.Label} ({s.Unit})";
            chart.Title.Font.Size = 10;
            chart.Title.Font.Bold = true;

            if (!config.GraphShowLegend)
                chart.Legend.Remove();

            chart.SetPosition(anchorRow, 0, 0, 5);
            chart.SetSize(ChartWidthPx, ChartHeightPx);

            bool allowNegative = AllowsNegativeValues(s.ColumnKey);
            

            double[] observed = s.Values.Where(v => v.HasValue).Select(v => v!.Value).ToArray();
            

            

            int valueCol = valueColOf[s.ColumnKey];
            var yRange = dataSheet.Cells[2, valueCol, buckets.Length + 1, valueCol];

            var series = chart.Series.Add(yRange, xRange);
            series.Header = s.Label;



            if (observed.Length > 0)
            {
                double dataMin = observed.Min();
                double dataMax = observed.Max();

                // Widen the axis to include the thresholds themselves so the
                // reference line(s) actually land inside the visible range,
                // only when they're going to be drawn at all.

                (double axisMin, double axisMax) = 
                    ComputeAxisBounds(
                        dataMin,
                        dataMax,

                        allowNegative);
                chart.YAxis.MinValue = axisMin;
                chart.YAxis.MaxValue = axisMax;
                chart.YAxis.MajorUnit = DetermineAxisMajorUnit(s.ColumnKey, axisMin, axisMax, dataMin, dataMax);

               
            }

            chart.XAxis.Crosses = eCrosses.Min; 
            chart.XAxis.Format = axis.Format;
            chart.XAxis.MajorUnit = axis.MajorUnitDays;
            chart.XAxis.RemoveGridlines();

            chart.YAxis.Title.Text = s.Unit;
            chart.YAxis.Title.Font.Size = 8;
            if (config.GraphShowGridLines)
                chart.YAxis.MajorGridlines.Width = 0.5;

            anchorRow += ChartRowSpan + PaddingRows;

          
        }
    }

    /// <summary>
    /// Draws a min/max threshold as a dashed, marker-less horizontal line
    /// spanning the full visible time range. Implemented as a genuine 2-point
    /// XY-Scatter series (backed by two hidden helper cells on
    /// <paramref name="dataSheet"/>) rather than an overlaid shape, so the line
    /// is positioned by Excel's own axis math and stays correct regardless of
    /// chart size, plot-area padding, legend, or axis rescaling.
    /// </summary>
    

    /// <summary>
    /// Per-measurement rules for the Y-axis's numbered step (major unit):
    /// MinStep/MaxStep bound how coarse or fine the step is allowed to be,
    /// and Precision is the granularity the computed step gets rounded to
    /// (e.g. Temperature's step is always a multiple of 0.1). Measurements
    /// not listed here get a fully automatic "nice number" step with no
    /// constraints, per "if a bound is not listed it can be adjusted as
    /// needed."
    /// </summary>
    private static readonly Dictionary<string, (double? MinStep, double? MaxStep, double Precision)> AxisStepRules = new()
    {
        ["temp_c"] = (null, null, 0.1),                    // Temperature — tenths place
        ["humidity_%"] = (null, null, 1),                  // rH — 1 percent
        ["pressure_hpa"] = (10, null, 10),                  // Barometric Pressure — increments of 10
        ["irradiance_wm2"] = (1, null, 1),                  // Solar Irradiance (W/m²) — whole number
        ["vpd_kpa"] = (0.001, 1, 0.001),                    // VPD (kPa) — thousandths .. whole number
        ["dew_point_c"] = (0.1, 1, 0.1),                    // Dew Point — tenths of a degree .. 1 degree
        ["abs_humidity_gm3"] = (null, 1, 0.1),              // Absolute Humidity — up to 1 g/m³
        ["accumulated_irradiance_kwh_m2"] = (1, 5, 1),      // Accumulated Solar Radiation — 1 .. 5 kWh/m²
        ["dli_mol_m2_d"] = (1, 5, 1),                       // Daily Light Integral — 1 .. 5 mol/m²/d
    };

    /// <summary>
    /// Classic "nice numbers for graph labels" step: picks 1, 2, or 5 times a
    /// power of 10 so axis labels land on clean values instead of awkward
    /// fractions.
    /// </summary>
    private static double ComputeNiceStep(double range, int targetDivisions = 5)
    {
        if (range <= 0) return 1;

        double rawStep = range / targetDivisions;
        double magnitude = Math.Pow(10, Math.Floor(Math.Log10(rawStep)));
        double residual = rawStep / magnitude;

        double niceResidual = residual switch
        {
            <= 1 => 1,
            <= 2 => 2,
            <= 5 => 5,
            _ => 10,
        };

        return niceResidual * magnitude;
    }

    private static double RoundToPrecision(double value, double precision) =>
        precision > 0 ? Math.Round(value / precision) * precision : value;

    /// <summary>
    /// Determines the Y-axis major unit (numbered step) for a measurement,
    /// applying its rounding precision and min/max bounds from
    /// <see cref="AxisStepRules"/> — with one safety override: if the
    /// measurement's real data variation is smaller than the step the rules
    /// would otherwise produce, that variation would be completely invisible
    /// (the whole series would sit inside a single gridline band and read as
    /// a flat line). In that case the step is relaxed below the configured
    /// minimum so the actual, finer-grained variation stays visible.
    /// </summary>
    private static double DetermineAxisMajorUnit(string columnKey, double axisMin, double axisMax, double dataMin, double dataMax)
    {
        double axisRange = Math.Max(axisMax - axisMin, 1e-9);
        double dataRange = Math.Max(dataMax - dataMin, 0);

        AxisStepRules.TryGetValue(columnKey, out var rule);
        double precision = rule.Precision;

        double step = ComputeNiceStep(axisRange);
        if (precision > 0)
        {
            step = RoundToPrecision(step, precision);
            if (step <= 0) step = precision;
        }

        if (rule.MinStep.HasValue) step = Math.Max(step, rule.MinStep.Value);
        if (rule.MaxStep.HasValue) step = Math.Min(step, rule.MaxStep.Value);

        // Safety override: don't let a coarse configured minimum swallow real
        // variation that's smaller than one step.
        if (dataRange > 0 && dataRange < step)
        {
            double relaxedStep = ComputeNiceStep(dataRange, targetDivisions: 4);
            if (relaxedStep > 0 && relaxedStep < step)
                step = relaxedStep;
        }

        return step > 0 ? step : 1;
    }

    /// <summary>
    /// Computes a tight [min, max] window around the actual data (and, when
    /// highlighting, the configured thresholds) with modest padding — rather
    /// than e.g. always starting at 0, which for a measurement like
    /// Barometric Pressure (~950–1050 hPa) would crush the real variation
    /// into a sliver at the top of the chart and read as a flat line.
    ///
    /// "Can't go negative" is treated as a clamp on the computed lower bound,
    /// not a forced starting point: it only kicks in if the tight window
    /// would otherwise dip below 0.
    /// </summary>
    private static (double AxisMin, double AxisMax) ComputeAxisBounds(
    double dataMin,
    double dataMax,
    bool allowNegative)
    {
        double range = dataMax - dataMin;

        double pad = range * 0.08;

        if (pad <= 0)
            pad = Math.Abs(dataMax) * 0.1 +
                  (Math.Abs(dataMax) < 1 ? 0.1 : 1);

        double axisMin = dataMin - pad;
        double axisMax = dataMax + pad;

        if (!allowNegative)
            axisMin = Math.Max(0, axisMin);

        return (axisMin, axisMax);
    }



    /// <summary>
    /// Nice, calendar-friendly major-tick candidates for a real value/date
    /// axis, in ascending order. Days is the axis quantity (Excel's date
    /// serial unit — 1.0 = 1 day); Format is the label format that reads
    /// naturally at that granularity.
    /// </summary>
    private static readonly (double Days, string Format)[] TimeAxisStepCandidates =
    [
        (1.0 / 24, "HH:mm"),          // 1 hour
        (1.0 / 12, "HH:mm"),          // 2 hours
        (1.0 / 8,  "HH:mm"),          // 3 hours
        (1.0 / 4,  "HH:mm"),          // 6 hours
        (1.0 / 2,  "MM/dd HH:mm"),    // 12 hours
        (1,        "MM/dd"),         // 1 day
        (2,        "MM/dd"),
        (3,        "MM/dd"),
        (7,        "MM/dd"),         // 1 week
        (14,       "MM/dd/yy"),
        (30,       "MM/dd/yy"),
        (90,       "MMM yy"),
        (182,      "MMM yy"),
        (365,      "MMM yy"),
    ];

    /// <summary>
    /// Chooses a major-tick spacing and label format for Scatter's X-axis
    /// (a true value/date axis) based purely on the span the buckets cover —
    /// independent of how the data was bucketed, since axis readability is
    /// about the visible span, not the aggregation resolution. Picks the
    /// finest candidate from <see cref="TimeAxisStepCandidates"/> that still
    /// keeps the number of visible ticks reasonable (≤ ~10), falling back to
    /// a scaled-up step for spans longer than every candidate covers.
    /// </summary>
    private static TimeAxisSettings DetermineTimeAxisSettings(DateTime[] buckets)
    {
        if (buckets.Length <= 1)
            return new TimeAxisSettings("MM/dd/yy HH:mm", 1);

        double spanDays = Math.Max((buckets[^1] - buckets[0]).TotalDays, 1.0 / 24);
        const int targetTicks = 10;

        foreach (var (days, format) in TimeAxisStepCandidates)
        {
            if (spanDays / days <= targetTicks)
                return new TimeAxisSettings(format, days);
        }

        var (lastDays, lastFormat) = TimeAxisStepCandidates[^1];
        double scaledStep = ComputeNiceStep(spanDays, targetTicks);
        return new TimeAxisSettings(lastFormat, Math.Max(scaledStep, lastDays));
    }

    // ── Naming helper ────────────────────────────────────────────────────────

    private static string SafeSheetObjectName(string raw) =>
        raw.Replace("%", "pct")
           .Replace("/", "_")
           .Replace(" ", "_")
           .Replace("(", "")
           .Replace(")", "")
           .Replace("²", "2")
           .Replace("³", "3");
}