using DropSense.Models;
using DropSense.Services;
using Microsoft.UI.Xaml.Media.Animation;
using OfficeOpenXml;
using OfficeOpenXml.Drawing.Chart;
using OfficeOpenXml.Style;
using OfficeOpenXml.Table;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;


namespace DropSense.Services;

public interface IExportXlsxService
{
    Task<ProcessDataResult> ProcessDataAsync(string? filePath, CancellationToken ct = default);
    Task<XlsxResult> WriteXlsxAsync(ExportConfiguration config, ProcessDataResult processed, CancellationToken ct = default);
    Task OpenOrSaveFileAsync(string outputPath, CancellationToken ct = default);

}

public class ExportXlsxService : IExportXlsxService
{
    private readonly ICsvService _csvService;
    public ExportXlsxService(ICsvService csvService)
    {
        _csvService = csvService;
    }

    public async Task<ProcessDataResult> ProcessDataAsync(string? filePath, CancellationToken ct = default)
    {
        CsvParseResult result = await _csvService.ParseAsync(filePath, ct);
        IReadOnlyList<SensorRow> rows = result.Rows;

        if (rows.Count == 0)
            return new ProcessDataResult();

        // ── Fixed column set ───────────────────────────────────────────────────
        var columns = new (string Header, Func<SensorRow, double?> Selector)[]
        {
            ("temp_c",                        r => r.TempC),
            ("humidity_%",                    r => r.HumidityPct),
            ("pressure_hpa",                  r => r.PressureHpa),
            ("irradiance_wm2",                r => r.IrradianceWm2),
            ("vpd_kpa",                       r => r.Vpd),
            ("dew_point_c",                   r => r.DewPointC),
            ("abs_humidity_gm3",              r => r.AbsHumidityGm3),
            ("accumulated_irradiance_kwh_m2", r => r.AccumulatedIrradianceKwhM2),
            ("par_umol_m2_s",                 r => r.ParEstimate),          // ← new
            ("dli_mol_m2_d",                  r => r.DailyLightIntegral),   // ← new
        };

        // ── Fixed stat set ─────────────────────────────────────────────────────
        var statDefs = new (string Label, Func<double[], double?> Compute)[]
        {
            ("mean",    v => v.Average()),
            ("median",  v => Median(v)),
            ("mode",    v => Mode(v)),
            ("std_dev", v => StdDev(v)),
            ("min",     v => v.Min()),
            ("max",     v => v.Max()),
            ("range",   v => v.Max() - v.Min()),
            ("q1",      v => Percentile(v, 25)),
            ("q2",      v => Percentile(v, 50)),
            ("q3",      v => Percentile(v, 75)),
        };

        // ── Pre-extract valid values per column ────────────────────────────────
        var columnValues = columns
            .Select(col => rows
                .Select(r => col.Selector(r))
                .Where(v => v.HasValue)
                .Select(v => v!.Value)
                .ToArray())
            .ToArray();

        int rowsSkipped = rows.Count - columnValues.Max(v => v.Length);

        // ── Compute stat rows ──────────────────────────────────────────────────
        var statRows = statDefs.Select(def => new StatRow
        {
            Label = def.Label,
            Values = columns
                .Select((col, i) => (col.Header, Value: columnValues[i].Length > 0
                    ? def.Compute(columnValues[i])
                    : null))
                .ToDictionary(x => x.Header, x => x.Value)
        }).ToArray();

        // ── Write stats CSV ────────────────────────────────────────────────────
        string dir = Path.GetDirectoryName(filePath) ?? string.Empty;
        string baseName = Path.GetFileNameWithoutExtension(filePath);
        string outPath = Path.Combine(dir, $"{baseName}_stats.csv");

        await using var writer = new StreamWriter(outPath, append: false, Encoding.UTF8);

        await writer.WriteLineAsync("stat," + string.Join(",", columns.Select(c => c.Header)));

        foreach (StatRow row in statRows)
        {
            ct.ThrowIfCancellationRequested();

            var cells = columns.Select(col =>
            {
                double? v = row.Values[col.Header];
                return v.HasValue ? v.Value.ToString("G6") : string.Empty;
            });

            await writer.WriteLineAsync(row.Label + "," + string.Join(",", cells));
        }

        await writer.FlushAsync(ct);

        return new ProcessDataResult
        {
            StatsCsvPath = outPath,
            Columns = columns.Select(c => c.Header).ToArray(),
            Stats = statRows,
            RowsProcessed = rows.Count,
            Rows = rows,
            RowsSkipped = rowsSkipped,
        };
    }

    public async Task<XlsxResult> WriteXlsxAsync(
        ExportConfiguration config,
        ProcessDataResult processed,
        CancellationToken ct = default)
    {
        if (processed.RowsProcessed == 0)
            return new XlsxResult();

        string appDir = AppContext.BaseDirectory;
        string excelDir = Path.Combine(appDir, "ExcelSheets");
        Directory.CreateDirectory(excelDir);

        string timestamp = DateTime.Now.ToString("MMddHHmm");
        string outPath = Path.Combine(excelDir, $"Dropsense_{timestamp}_export.xlsx");

        using var package = new ExcelPackage();

        WriteRawDataSheet(package, processed.Rows, config);
        WriteRawDataSheet(package, processed);
        WriteStatsSheet(package, processed);

        if (config.StatCoefficientOfVariation)
            WriteCvSheet(package, processed);

        if (config.StatMovingAverage)
        {
            WriteMovingAverageSheet(package, processed, processed.Rows, config); // config carries window + chart type
        }

        if (config.StatZScore)
            WriteZScoreSheet(package, processed, processed.Rows,
                             autoFlagThreshold: config.ZScoreAutoFlagThreshold);

        await package.SaveAsAsync(new FileInfo(outPath), ct);

        return new XlsxResult
        {
            XlsxPath = outPath,
            SheetsWritten = package.Workbook.Worksheets.Count,
        };
    }

    // ── Sheet writers ──────────────────────────────────────────────────────────

    private static void WriteStatsSheet(ExcelPackage package, ProcessDataResult data)
    {
        ExcelWorksheet ws = package.Workbook.Worksheets.Add("Statistics");

        // ── Styles ────────────────────────────────────────────────────────────
        string headerBg = "1F4E79";        // dark blue
        string headerFg = "FFFFFF";
        string altRowBg = "D6E4F0";        // light blue stripe
        string warnBg = "FFF2CC";        // amber — out-of-range stat cells
        var bodyFont = "Arial";

        // ── Header row ────────────────────────────────────────────────────────
        ws.Cells[1, 1].Value = "Statistic";
        StyleHeader(ws.Cells[1, 1], headerBg, headerFg, bodyFont);

        for (int c = 0; c < data.Columns.Count; c++)
        {
            var cell = ws.Cells[1, c + 2];
            cell.Value = data.Columns[c];
            StyleHeader(cell, headerBg, headerFg, bodyFont);
        }

        // ── Data rows ─────────────────────────────────────────────────────────
        for (int r = 0; r < data.Stats.Count; r++)
        {
            StatRow stat = data.Stats[r];
            int excelRow = r + 2;
            bool stripe = r % 2 == 1;

            var labelCell = ws.Cells[excelRow, 1];
            labelCell.Value = stat.Label;
            StyleBodyCell(labelCell, bodyFont, stripe ? altRowBg : "FFFFFF", bold: true);

            for (int c = 0; c < data.Columns.Count; c++)
            {
                var cell = ws.Cells[excelRow, c + 2];
                double? v = stat.Values.GetValueOrDefault(data.Columns[c]);

                if (v.HasValue)
                {
                    cell.Value = Math.Round(v.Value, 4);
                    cell.Style.Numberformat.Format = "0.0000";
                }
                else
                {
                    cell.Value = "—";
                }

                StyleBodyCell(cell, bodyFont, stripe ? altRowBg : "FFFFFF");
            }
        }

        // ── Totals-style border under header ─────────────────────────────────
        using (var headerRange = ws.Cells[1, 1, 1, data.Columns.Count + 1])
        {
            headerRange.Style.Border.Bottom.Style = ExcelBorderStyle.Medium;
            headerRange.Style.Border.Bottom.Color.SetColor(System.Drawing.Color.White);
        }

        // ── Auto-fit columns ──────────────────────────────────────────────────
        ws.Cells[ws.Dimension.Address].AutoFitColumns(10, 30);
        ws.View.FreezePanes(2, 2); // freeze header row + label column
    }

    private static void WriteRawDataSheet(ExcelPackage package, ProcessDataResult data)
    {
        ExcelWorksheet ws = package.Workbook.Worksheets.Add("Summary");

        string headerBg = "1F4E79";
        string headerFg = "FFFFFF";
        var bodyFont = "Arial";

        // ── Metadata block ────────────────────────────────────────────────────
        // ── Bug: WriteRawDataSheet(ProcessDataResult) has wrong cell reference ─────
        // "Source CSV" value was written to B5 instead of B3
        ws.Cells["A1"].Value = "Rows Processed"; ws.Cells["B1"].Value = data.RowsProcessed;
        ws.Cells["A2"].Value = "Rows Skipped"; ws.Cells["B2"].Value = data.RowsSkipped;
        ws.Cells["A3"].Value = "Source CSV"; ws.Cells["B3"].Value = data.StatsCsvPath;  // ← was B5

        using (var meta = ws.Cells["A1:A5"])
        {
            meta.Style.Font.Bold = true;
            meta.Style.Font.Name = bodyFont;
        }
        ws.Cells["B1:B5"].Style.Font.Name = bodyFont;
        ws.Cells["B5"].Style.Font.Color.SetColor(System.Drawing.Color.Gray);

        // ── Column headers ────────────────────────────────────────────────────
        int headerRow = 7;
        ws.Cells[headerRow, 1].Value = "Statistic";
        StyleHeader(ws.Cells[headerRow, 1], headerBg, headerFg, bodyFont);

        for (int c = 0; c < data.Columns.Count; c++)
        {
            var cell = ws.Cells[headerRow, c + 2];
            cell.Value = data.Columns[c];
            StyleHeader(cell, headerBg, headerFg, bodyFont);
        }

        // ── Stat values (cross-reference to Statistics sheet) ─────────────────
        for (int r = 0; r < data.Stats.Count; r++)
        {
            StatRow stat = data.Stats[r];
            int excelRow = headerRow + 1 + r;

            ws.Cells[excelRow, 1].Value = stat.Label;
            ws.Cells[excelRow, 1].Style.Font.Bold = true;
            ws.Cells[excelRow, 1].Style.Font.Name = bodyFont;

            for (int c = 0; c < data.Columns.Count; c++)
            {
                // Formula reference back to Statistics sheet
                ws.Cells[excelRow, c + 2].Formula =
                    $"Statistics!{ExcelCellAddress.GetColumnLetter(c + 2)}{r + 2}";
                ws.Cells[excelRow, c + 2].Style.Numberformat.Format = "0.0000";
                ws.Cells[excelRow, c + 2].Style.Font.Name = bodyFont;
            }
        }

        ws.Cells[ws.Dimension.Address].AutoFitColumns(10, 30);
        ws.View.FreezePanes(headerRow + 1, 2);
    }

    private static void WriteRawDataSheet(
        ExcelPackage package,
        IReadOnlyList<SensorRow> rows,
        ExportConfiguration config)
    {
        ExcelWorksheet ws = package.Workbook.Worksheets.Add("Data");

        string headerBg = "1F4E79";
        string headerFg = "FFFFFF";
        string altRowBg = "D6E4F0";
        string warnBg = "FFF2CC";
        string bodyFont = "Arial";

        // ── Column manifest ────────────────────────────────────────────────────
        // Each entry: (header, value selector, warning selector, number format)
        var cols = new List<(
            string Header,
            Func<SensorRow, object?> Value,
            Func<SensorRow, bool> Warn,
            string Format)>();

        cols.Add(("Timestamp", r => r.Timestamp, _ => false, "@"));

        if (config.IncludeTemperature)
            cols.Add(("Temp (°C)", r => r.TempC, r => r.TempWarn, "0.00"));
        if (config.IncludeRelativeHumidity)
            cols.Add(("Humidity (%)", r => r.HumidityPct, r => r.HumidityWarn, "0.00"));
        if (config.IncludeBarometricPressure)
            cols.Add(("Pressure (hPa)", r => r.PressureHpa, r => r.PressureWarn, "0.00"));
        if (config.IncludeSolarIrradiance)
            cols.Add(("Irradiance (W/m²)", r => r.IrradianceWm2, r => r.IrradianceWarn, "0.00"));
        if (config.IncludeVaporPressureDeficit)
            cols.Add(("VPD (kPa)", r => r.Vpd, _ => false, "0.000"));
        if (config.IncludeDewPoint)
            cols.Add(("Dew Point (°C)", r => r.DewPointC, _ => false, "0.00"));
        if (config.IncludeAbsoluteHumidity)
            cols.Add(("Abs. Humidity (g/m³)", r => r.AbsHumidityGm3, _ => false, "0.000"));
        if (config.IncludeAccumulatedSolarRadiation)
            cols.Add(("Accum. Irrad. (kWh/m²)", r => r.AccumulatedIrradianceKwhM2, _ => false, "0.0000"));
        if (config.IncludeSolarIrradiance)                                                                 // ← PAR follows irradiance toggle
            cols.Add(("PAR (µmol/m²/s)", r => r.ParEstimate, r => r.IrradianceWarn, "0.000"));
        if (config.IncludeDailyLightIntegral)                                                              // ← new toggle
            cols.Add(("DLI (mol/m²/d)", r => r.DailyLightIntegral, _ => false, "0.0000"));

        // ── Header row ─────────────────────────────────────────────────────────
        for (int c = 0; c < cols.Count; c++)
        {
            var cell = ws.Cells[1, c + 1];
            cell.Value = cols[c].Header;
            StyleHeader(cell, headerBg, headerFg, bodyFont);
        }

        // ── Data rows ──────────────────────────────────────────────────────────
        for (int r = 0; r < rows.Count; r++)
        {
            SensorRow row = rows[r];
            int excelRow = r + 2;
            bool stripe = r % 2 == 1;

            for (int c = 0; c < cols.Count; c++)
            {
                var (_, getValue, getWarn, fmt) = cols[c];
                var cell = ws.Cells[excelRow, c + 1];

                object? val = getValue(row);
                cell.Value = val ?? (object)"—";

                if (val is double or float)
                    cell.Style.Numberformat.Format = fmt;

                bool warn = getWarn(row);
                string bg = warn ? warnBg
                           : stripe ? altRowBg
                           : "FFFFFF";

                StyleBodyCell(cell, bodyFont, bg);
            }
        }

        // ── Table ──────────────────────────────────────────────────────────────
        var tableRange = ws.Cells[1, 1, rows.Count + 1, cols.Count];
        var table = ws.Tables.Add(tableRange, "SensorData");
        table.TableStyle = TableStyles.Medium2;
        table.ShowFilter = true;
        table.ShowHeader = true;

        ws.Cells[ws.Dimension.Address].AutoFitColumns(12, 30);
        ws.View.FreezePanes(2, 1);
    }

    // ── CV sheet ───────────────────────────────────────────────────────────────

    private static void WriteCvSheet(ExcelPackage package, ProcessDataResult data)
    {
        ExcelWorksheet ws = package.Workbook.Worksheets.Add("Coeff. of Variation");

        string headerBg = "1F4E79";
        string headerFg = "FFFFFF";
        string altRowBg = "D6E4F0";
        string bodyFont = "Arial";

        // ── Summary table headers ──────────────────────────────────────────────
        ws.Cells[1, 1].Value = "Measurement";
        ws.Cells[1, 2].Value = "Mean";
        ws.Cells[1, 3].Value = "Std Dev";
        ws.Cells[1, 4].Value = "CV (%)";
        StyleHeader(ws.Cells[1, 1], headerBg, headerFg, bodyFont);
        StyleHeader(ws.Cells[1, 2], headerBg, headerFg, bodyFont);
        StyleHeader(ws.Cells[1, 3], headerBg, headerFg, bodyFont);
        StyleHeader(ws.Cells[1, 4], headerBg, headerFg, bodyFont);

        StatRow? meanRow = data.Stats.FirstOrDefault(s => s.Label == "mean");
        StatRow? stdDevRow = data.Stats.FirstOrDefault(s => s.Label == "std_dev");

        int dataRow = 2;
        int chartStartCol = 6;

        var chartSeries = new List<(string Header, int TableRow)>();

        for (int c = 0; c < data.Columns.Count; c++)
        {
            string col = data.Columns[c];
            double? mean = meanRow?.Values.GetValueOrDefault(col);
            double? stdev = stdDevRow?.Values.GetValueOrDefault(col);
            double? cv = (mean.HasValue && stdev.HasValue && mean != 0)
                ? Math.Round(stdev.Value / Math.Abs(mean.Value) * 100.0, 4)
                : null;

            bool stripe = c % 2 == 1;
            ws.Cells[dataRow, 1].Value = col;
            ws.Cells[dataRow, 2].Value = mean.HasValue ? Math.Round(mean.Value, 4) : "—";
            ws.Cells[dataRow, 3].Value = stdev.HasValue ? Math.Round(stdev.Value, 4) : "—";
            ws.Cells[dataRow, 4].Value = cv.HasValue ? cv.Value : "—";

            if (mean.HasValue) ws.Cells[dataRow, 2].Style.Numberformat.Format = "0.0000";
            if (stdev.HasValue) ws.Cells[dataRow, 3].Style.Numberformat.Format = "0.0000";
            if (cv.HasValue) ws.Cells[dataRow, 4].Style.Numberformat.Format = "0.00\"%\"";

            for (int col2 = 1; col2 <= 4; col2++)
                StyleBodyCell(ws.Cells[dataRow, col2], bodyFont, stripe ? altRowBg : "FFFFFF",
                              bold: col2 == 1);

            chartSeries.Add((col, dataRow));
            dataRow++;
        }

        // ── CV bar chart ───────────────────────────────────────────────────────
        var chart = ws.Drawings.AddChart("CV_Chart", eChartType.BarClustered);
        chart.Title.Text = "Coefficient of Variation (%) by Measurement";
        chart.SetPosition(1, 0, chartStartCol, 0);
        chart.SetSize(600, 400);

        foreach (var (header, row) in chartSeries)
        {
            var series = chart.Series.Add(
                ws.Cells[row, 4],
                ws.Cells[row, 1]);
            series.Header = header;
        }

        ws.Cells[ws.Dimension.Address].AutoFitColumns(12, 30);
    }

    // ── Moving average sheet ───────────────────────────────────────────────────
    private static void WriteMovingAverageSheet(
     ExcelPackage package,
     ProcessDataResult data,
     IReadOnlyList<SensorRow> rows,
     ExportConfiguration config)
    {
        int window = config.MovingAverageWindow > 0 ? config.MovingAverageWindow : 10;

        ExcelWorksheet ws = package.Workbook.Worksheets.Add("Moving Average");

        string headerBg = "1F4E79";
        string headerFg = "FFFFFF";
        string altRowBg = "D6E4F0";
        string bodyFont = "Arial";

        var selectors = BuildColumnSelectors();

        eChartType chartType = config.GraphTypeScatter ? eChartType.XYScatterLines
                             : config.GraphTypeBar ? eChartType.BarClustered
                             : eChartType.Line;

        // ── Build MA arrays once ───────────────────────────────────────────────
        var maColumns = new List<(string Col, double[] Ma)>();

        foreach (string col in data.Columns)
        {
            if (!selectors.TryGetValue(col, out var selector)) continue;

            double[] raw = rows
                .Select(r => selector(r))
                .Where(v => v.HasValue)
                .Select(v => v!.Value)
                .ToArray();

            if (raw.Length == 0) continue;
            maColumns.Add((col, MovingAverage(raw, window)));
        }

        if (maColumns.Count == 0) return;

        int maxRows = maColumns.Max(m => m.Ma.Length);

        // ── Layout ────────────────────────────────────────────────────────────
        // Charts stack vertically in a single column on the left.
        // Each chart is 640 × 320 px.  At Excel's default column width of
        // ~64px per unit, 640px ≈ 10 column-units.  We set cols A–J (1–10)
        // to exactly 9 units wide each (EPPlus width units ≈ character widths,
        // ~7px each at 11pt Calibri → 9 * 7 = 63px → 10 cols ≈ 630px, close
        // enough for the chart to sit without spilling into the table).
        //
        // Data table starts at column TABLE_COL (col 12) with a 1-col gap.
        //
        //  ┌─────────────────────────┬──┬──────────────────────────┐
        //  │  Chart 1 (cols A–J)     │  │  Data table (col L+)     │
        //  ├─────────────────────────┤  │                          │
        //  │  Chart 2                │  │                          │
        //  ├─────────────────────────┤  │                          │
        //  │  …                      │  │                          │
        //  └─────────────────────────┴──┴──────────────────────────┘

        const int ChartWidthPx = 640;
        const int ChartHeightPx = 320;
        const int ChartCols = 10;   // columns A–J reserved for charts
        const int GapCols = 1;    // col K = breathing room
        const int TableStartCol = ChartCols + GapCols + 1;  // col L = 12

        // Set chart column widths so the 10 columns total ≈ ChartWidthPx
        // 640px / 10 cols = 64px per col.  EPPlus width unit ≈ 7px → 64/7 ≈ 9.1
        for (int c = 1; c <= ChartCols; c++)
            ws.Column(c).Width = 9.1;

        // Gap column
        ws.Column(ChartCols + 1).Width = 2;

        // ── Data table ────────────────────────────────────────────────────────
        ws.Cells[1, TableStartCol].Value = "Row";
        StyleHeader(ws.Cells[1, TableStartCol], headerBg, headerFg, bodyFont);

        for (int c = 0; c < maColumns.Count; c++)
        {
            var (col, ma) = maColumns[c];
            int tableCol = TableStartCol + 1 + c;

            ws.Cells[1, tableCol].Value = $"{col} (MA{window})";
            StyleHeader(ws.Cells[1, tableCol], headerBg, headerFg, bodyFont);

            for (int r = 0; r < ma.Length; r++)
            {
                int excelRow = r + 2;
                bool stripe = r % 2 == 1;

                if (c == 0)
                {
                    ws.Cells[excelRow, TableStartCol].Value = r + 1;
                    StyleBodyCell(ws.Cells[excelRow, TableStartCol],
                                  bodyFont, stripe ? altRowBg : "FFFFFF");
                }

                var cell = ws.Cells[excelRow, tableCol];
                cell.Value = Math.Round(ma[r], 4);
                cell.Style.Numberformat.Format = "0.0000";
                StyleBodyCell(cell, bodyFont, stripe ? altRowBg : "FFFFFF");
            }

            ws.Column(tableCol).AutoFit();
        }

        ws.Column(TableStartCol).Width = 8;

        // ── Charts — single vertical stack anchored at col A ──────────────────
        // EPPlus SetPosition(anchorRow, rowOffsetPx, anchorCol, colOffsetPx)
        // anchorRow and anchorCol are 0-indexed.
        // We anchor every chart at column 0 (col A) and step the row by the
        // number of rows each chart height consumes.
        //
        // At Excel default row height 15pt ≈ 20px:
        //   320px / 20px = 16 rows per chart.
        // We add 1 row of padding between charts.

        const int RowHeightPx = 20;   // default Excel row height in px
        const int ChartRowSpan = ChartHeightPx / RowHeightPx;   // 16 rows
        const int PaddingRows = 1;

        int anchorRow = 0;   // 0-indexed; starts at the very top

        for (int c = 0; c < maColumns.Count; c++)
        {
            var (col, ma) = maColumns[c];
            int tableCol = TableStartCol + 1 + c;

            string safeName = $"MA_{col
                .Replace("%", "pct")
                .Replace("/", "_")
                .Replace(" ", "_")
                .Replace("(", "")
                .Replace(")", "")
                .Replace("²", "2")
                .Replace("³", "3")}";

            var chart = ws.Drawings.AddChart(safeName, chartType);

            chart.Title.Text = $"{col}  —  Moving Average (window = {window})";
            chart.Title.Font.Size = 10;
            chart.Title.Font.Bold = true;
            chart.Legend.Remove();

            // Anchor at col A (index 0), row = anchorRow
            chart.SetPosition(anchorRow, 0, 0, 5);
            chart.SetSize(ChartWidthPx, ChartHeightPx);

            // Series references the data table
            var xRange = ws.Cells[2, TableStartCol, ma.Length + 1, TableStartCol];
            var yRange = ws.Cells[2, tableCol, ma.Length + 1, tableCol];

            var series = chart.Series.Add(yRange, xRange);
            series.Header = col;

            chart.XAxis.Title.Text = "Row";
            chart.YAxis.Title.Text = col;
            chart.XAxis.Title.Font.Size = 8;
            chart.YAxis.Title.Font.Size = 8;
            chart.XAxis.RemoveGridlines();

            if (config.GraphShowGridLines)
                chart.YAxis.MajorGridlines.Width = 0.5;

            anchorRow += ChartRowSpan + PaddingRows;
        }

        // Freeze the header row; data table scrolls independently of charts
        ws.View.FreezePanes(2, TableStartCol);
    }

    // ── Z-score sheet ──────────────────────────────────────────────────────────

    private static void WriteZScoreSheet(
    ExcelPackage package,
    ProcessDataResult data,
    IReadOnlyList<SensorRow> rows,
    double autoFlagThreshold = 3.0)
    {
        ExcelWorksheet ws = package.Workbook.Worksheets.Add("Z-Score");

        string headerBg = "1F4E79";
        string headerFg = "FFFFFF";
        string bodyFont = "Arial";
        string flagBg = "FFF9C4";   // pale amber for auto-flagged cells
        string flagFg = "E65100";   // deep orange text for flagged

        var selectors = BuildColumnSelectors();
        int currentCol = 1;

        foreach (string col in data.Columns)
        {
            if (!selectors.TryGetValue(col, out var selector)) continue;

            double[] raw = rows
                .Select(r => selector(r))
                .Where(v => v.HasValue)
                .Select(v => v!.Value)
                .ToArray();

            double[]? zRaw = ZScores(raw);
            if (zRaw == null) continue;

            // Sort ascending by z-score for the lookup table
            int[] order = Enumerable.Range(0, raw.Length).OrderBy(i => zRaw[i]).ToArray();
            double[] sortedV = order.Select(i => raw[i]).ToArray();
            double[] sortedZ = order.Select(i => zRaw[i]).ToArray();

            List<(double Z, double Value)> zTable = BuildZScoreTable(sortedV, sortedZ);

            int colZ = currentCol;
            int colV = currentCol + 1;

            // ── Merged measurement name header (spans both columns) ────────────
            ws.Cells[1, colZ, 1, colV].Merge = true;
            ws.Cells[1, colZ].Value = col;
            StyleZHeader(ws.Cells[1, colZ], headerBg, headerFg, bodyFont, merged: true);

            // ── Sub-headers ───────────────────────────────────────────────────
            ws.Cells[2, colZ].Value = "Z-Score";
            ws.Cells[2, colV].Value = "Value";
            StyleZHeader(ws.Cells[2, colZ], "2E6DA4", headerFg, bodyFont);
            StyleZHeader(ws.Cells[2, colV], "2E6DA4", headerFg, bodyFont);

            // ── Data rows ─────────────────────────────────────────────────────
            int dataStartRow = 3;

            for (int r = 0; r < zTable.Count; r++)
            {
                var (z, value) = zTable[r];
                int excelRow = dataStartRow + r;

                // Determine band and alternation within band
                var (baseBg, altBg) = GetZScoreBandColors(z);

                // Alternate every other row within the same integer band
                // so consecutive rows at similar z are visually distinct
                int bandRow = (int)Math.Floor(Math.Abs(z) * 100) % 2; // 0 or 1
                string rowBg = bandRow == 0 ? baseBg : altBg;

                bool flagged = Math.Abs(z) > autoFlagThreshold;
                string fg = flagged ? flagFg : "000000";

                var zCell = ws.Cells[excelRow, colZ];
                var vCell = ws.Cells[excelRow, colV];

                zCell.Value = z;
                vCell.Value = value;

                zCell.Style.Numberformat.Format = "0.00";
                vCell.Style.Numberformat.Format = "0.000000";

                // ── Cell fill ─────────────────────────────────────────────────
                foreach (var cell in new[] { zCell, vCell })
                {
                    cell.Style.Fill.PatternType = ExcelFillStyle.Solid;
                    cell.Style.Fill.BackgroundColor.SetColor(
                        flagged ? HexToColor(flagBg) : HexToColor(rowBg));

                    cell.Style.Font.Name = bodyFont;
                    cell.Style.Font.Size = 9;
                    cell.Style.Font.Color.SetColor(HexToColor(fg));
                    cell.Style.Font.Bold = flagged;

                    cell.Style.HorizontalAlignment = ExcelHorizontalAlignment.Right;
                    cell.Style.VerticalAlignment = ExcelVerticalAlignment.Center;

                    // ── Borders ───────────────────────────────────────────────
                    // Thin inner borders; medium on band boundaries
                    bool isBandBoundary = Math.Abs(z % 1.0) < 0.005; // near integer

                    var borderStyle = isBandBoundary
                        ? ExcelBorderStyle.Medium
                        : ExcelBorderStyle.Hair;
                    var borderColor = isBandBoundary
                        ? HexToColor("9E9E9E")
                        : HexToColor("BDBDBD");

                    cell.Style.Border.Top.Style = borderStyle;
                    cell.Style.Border.Top.Color.SetColor(borderColor);
                    cell.Style.Border.Bottom.Style = ExcelBorderStyle.Hair;
                    cell.Style.Border.Bottom.Color.SetColor(HexToColor("BDBDBD"));
                    cell.Style.Border.Left.Style = ExcelBorderStyle.Thin;
                    cell.Style.Border.Left.Color.SetColor(HexToColor("9E9E9E"));
                    cell.Style.Border.Right.Style = ExcelBorderStyle.Thin;
                    cell.Style.Border.Right.Color.SetColor(HexToColor("9E9E9E"));
                }

                // Left border of z column is the table's outer left edge — make it medium
                zCell.Style.Border.Left.Style = ExcelBorderStyle.Medium;
                zCell.Style.Border.Left.Color.SetColor(HexToColor("616161"));

                // Right border of value column is the outer right edge
                vCell.Style.Border.Right.Style = ExcelBorderStyle.Medium;
                vCell.Style.Border.Right.Color.SetColor(HexToColor("616161"));
            }

            // ── Outer bottom border on last data row ───────────────────────────
            int lastRow = dataStartRow + zTable.Count - 1;
            ws.Cells[lastRow, colZ].Style.Border.Bottom.Style = ExcelBorderStyle.Medium;
            ws.Cells[lastRow, colZ].Style.Border.Bottom.Color.SetColor(HexToColor("616161"));
            ws.Cells[lastRow, colV].Style.Border.Bottom.Style = ExcelBorderStyle.Medium;
            ws.Cells[lastRow, colV].Style.Border.Bottom.Color.SetColor(HexToColor("616161"));

            // ── Column widths ─────────────────────────────────────────────────
            ws.Column(colZ).Width = 10;
            ws.Column(colV).Width = 14;

            currentCol += 3; // 2 data cols + 1 gap
        }

        ws.View.FreezePanes(3, 1);
    }

    // Dedicated header styler for z-score sheet (slightly different contract)
    private static void StyleZHeader(
        ExcelRange cell, string bgHex, string fgHex, string font, bool merged = false)
    {
        cell.Style.Font.Bold = true;
        cell.Style.Font.Name = font;
        cell.Style.Font.Size = merged ? 10 : 9;
        cell.Style.Font.Color.SetColor(HexToColor(fgHex));
        cell.Style.Fill.PatternType = ExcelFillStyle.Solid;
        cell.Style.Fill.BackgroundColor.SetColor(HexToColor(bgHex));
        cell.Style.HorizontalAlignment = ExcelHorizontalAlignment.Center;
        cell.Style.VerticalAlignment = ExcelVerticalAlignment.Center;

        cell.Style.Border.Top.Style = ExcelBorderStyle.Medium;
        cell.Style.Border.Bottom.Style = ExcelBorderStyle.Medium;
        cell.Style.Border.Left.Style = ExcelBorderStyle.Medium;
        cell.Style.Border.Right.Style = ExcelBorderStyle.Medium;
        cell.Style.Border.Top.Color.SetColor(HexToColor("616161"));
        cell.Style.Border.Bottom.Color.SetColor(HexToColor("616161"));
        cell.Style.Border.Left.Color.SetColor(HexToColor("616161"));
        cell.Style.Border.Right.Color.SetColor(HexToColor("616161"));
    }

    // ── Shared column selector map ─────────────────────────────────────────────
    // Centralises the header → SensorRow mapping so MA and Z-score sheets
    // don't duplicate the selector logic from ProcessDataAsync.

    private static Dictionary<string, Func<SensorRow, double?>> BuildColumnSelectors() =>
        new()
        {
            ["temp_c"] = r => r.TempC,
            ["humidity_%"] = r => r.HumidityPct,
            ["pressure_hpa"] = r => r.PressureHpa,
            ["irradiance_wm2"] = r => r.IrradianceWm2,
            ["vpd_kpa"] = r => r.Vpd,
            ["dew_point_c"] = r => r.DewPointC,
            ["abs_humidity_gm3"] = r => r.AbsHumidityGm3,
            ["accumulated_irradiance_kwh_m2"] = r => r.AccumulatedIrradianceKwhM2,
            ["par_umol_m2_s"] = r => r.ParEstimate,
            ["dli_mol_m2_d"] = r => r.DailyLightIntegral,
        };


    // ── Style helpers ──────────────────────────────────────────────────────────

    private static void StyleHeader(ExcelRange cell, string bgHex, string fgHex, string font)
    {
        cell.Style.Font.Bold = true;
        cell.Style.Font.Name = font;
        cell.Style.Font.Color.SetColor(HexToColor(fgHex));
        cell.Style.Fill.PatternType = ExcelFillStyle.Solid;
        cell.Style.Fill.BackgroundColor.SetColor(HexToColor(bgHex));
        cell.Style.HorizontalAlignment = ExcelHorizontalAlignment.Center;
        cell.Style.VerticalAlignment = ExcelVerticalAlignment.Center;
    }

    private static void StyleBodyCell(ExcelRange cell, string font, string bgHex, bool bold = false)
    {
        cell.Style.Font.Name = font;
        cell.Style.Font.Bold = bold;
        cell.Style.Fill.PatternType = ExcelFillStyle.Solid;
        cell.Style.Fill.BackgroundColor.SetColor(HexToColor(bgHex));
        cell.Style.HorizontalAlignment = ExcelHorizontalAlignment.Right;
        cell.Style.Border.Bottom.Style = ExcelBorderStyle.Hair;
        cell.Style.Border.Bottom.Color.SetColor(System.Drawing.Color.LightGray);
    }

    private static System.Drawing.Color HexToColor(string hex) =>
        System.Drawing.Color.FromArgb(
            Convert.ToInt32(hex[..2], 16),
            Convert.ToInt32(hex[2..4], 16),
            Convert.ToInt32(hex[4..6], 16));

    public async Task OpenOrSaveFileAsync(string outputPath, CancellationToken ct = default)
    {
        return;
    }

    // ── Private stat helpers ───────────────────────────────────────────────────

    private static double Median(double[] sorted)
    {
        double[] s = [.. sorted.Order()];
        int n = s.Length;
        return n % 2 == 1 ? s[n / 2] : (s[n / 2 - 1] + s[n / 2]) / 2.0;
    }

    private static double? Mode(double[] values)
    {
        if (values.Length == 0) return null;
        return values
            .GroupBy(v => v)
            .OrderByDescending(g => g.Count())
            .First().Key;
    }

    private static double StdDev(double[] values)
    {
        if (values.Length < 2) return 0;
        double mean = values.Average();
        return Math.Sqrt(values.Sum(v => (v - mean) * (v - mean)) / (values.Length - 1));
    }

    private static double Percentile(double[] values, double percentile)
    {
        double[] s = [.. values.Order()];
        double rank = (percentile / 100.0) * (s.Length - 1);
        int lower = (int)Math.Floor(rank);
        int upper = (int)Math.Ceiling(rank);
        return lower == upper ? s[lower] : s[lower] + (rank - lower) * (s[upper] - s[lower]);
    }

    private static double? CoefficientOfVariation(double[] values)
    {
        if (values.Length < 2) return null;
        double mean = values.Average();
        if (mean == 0) return null;  // CV undefined when mean is zero
        return Math.Round(StdDev(values) / Math.Abs(mean) * 100.0, 4); // expressed as %
    }

    /// <summary>
    /// Simple moving average with a window of <paramref name="window"/> samples.
    /// Returns one value per input row; the first (window-1) entries use an
    /// expanding window (same behaviour as pandas ewm min_periods=1).
    /// </summary>
    private static double[] MovingAverage(double[] values, int window = 10)
    {
        if (values.Length == 0) return [];
        double[] result = new double[values.Length];
        double sum = 0;

        for (int i = 0; i < values.Length; i++)
        {
            sum += values[i];
            int count = Math.Min(i + 1, window);
            int start = i - count + 1;
            if (i >= window) sum -= values[i - window];
            result[i] = sum / count;
        }
        return result;
    }

    /// <summary>
    /// Z-score for each value:  z = (x - mean) / stddev.
    /// Returns null array when stddev is 0 (all values identical).
    /// </summary>
    private static double[]? ZScores(double[] values)
    {
        if (values.Length < 2) return null;
        double mean = values.Average();
        double stddev = StdDev(values);
        if (stddev == 0) return null;
        return values.Select(v => Math.Round((v - mean) / stddev, 6)).ToArray();
    }

    /// <summary>
    /// Builds a lookup table of (zScore → value) at 0.01 resolution,
    /// spanning from floor(minZ, 2dp) to ceil(maxZ, 2dp).
    /// Uses linear interpolation between the two closest sorted samples.
    /// </summary>
    private static List<(double Z, double Value)> BuildZScoreTable(
        double[] sortedValues,
        double[] sortedZScores)
    {
        var table = new List<(double, double)>();
        double minZ = Math.Floor(sortedZScores[0] * 100) / 100.0;
        double maxZ = Math.Ceiling(sortedZScores[^1] * 100) / 100.0;

        for (double z = minZ; z <= maxZ + 1e-9; z = Math.Round(z + 0.01, 2))
        {
            // Binary search for the bracketing z-scores
            int lo = 0, hi = sortedZScores.Length - 1;
            while (lo < hi)
            {
                int mid = (lo + hi) / 2;
                if (sortedZScores[mid] < z) lo = mid + 1; else hi = mid;
            }

            double interpolated;
            if (lo == 0 || sortedZScores[lo] == z)
            {
                interpolated = sortedValues[lo];
            }
            else
            {
                double zLo = sortedZScores[lo - 1], zHi = sortedZScores[lo];
                double vLo = sortedValues[lo - 1], vHi = sortedValues[lo];
                double t = (z - zLo) / (zHi - zLo);
                interpolated = vLo + t * (vHi - vLo);
            }
            table.Add((z, Math.Round(interpolated, 6)));
        }
        return table;
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  Z-score band color system
    //  ─────────────────────────────────────────────────────────────────────────
    //  Each integer band [N, N+1) gets a base color. Within the band, rows
    //  alternate between the base and a slightly lighter variant (+15 lightness)
    //  so runs of rows at the same z-magnitude remain visually distinct.
    //
    //  Negative bands:  rose → red → deep red  (below average)
    //  Zero band:       neutral gray-white
    //  Positive bands:  mint → green → deep green  (above average)
    // ══════════════════════════════════════════════════════════════════════════

    private static readonly (string Base, string Alt)[] PositiveBandColors =
    [
        ("E8F5E9", "F1FAF2"),   // z  0–1    mint
        ("C8E6C9", "D8EED9"),   // z  1–2    light green
        ("A5D6A7", "B8DFB9"),   // z  2–3    medium green
        ("81C784", "96D397"),   // z  3–4    green
        ("4CAF50", "66BB6A"),   // z  4–5    strong green
        ("388E3C", "4CAF50"),   // z  5+     deep green
    ];

    private static readonly (string Base, string Alt)[] NegativeBandColors =
    [
        ("FFEBEE", "FFF3F5"),   // z  0– -1  blush
    ("FFCDD2", "FFD9DC"),   // z -1– -2  rose
    ("EF9A9A", "F5AEAE"),   // z -2– -3  light red
    ("E57373", "EA8A8A"),   // z -3– -4  red
    ("F44336", "EF5350"),   // z -4– -5  strong red
    ("C62828", "D32F2F"),   // z -5+     deep red
    ];

    // Returns (baseHex, altHex) for a given z-score value
    private static (string Base, string Alt) GetZScoreBandColors(double z)
    {
        if (z >= 0)
        {
            int band = Math.Min((int)Math.Floor(z), PositiveBandColors.Length - 1);
            return PositiveBandColors[band];
        }
        else
        {
            int band = Math.Min((int)Math.Floor(Math.Abs(z)), NegativeBandColors.Length - 1);
            return NegativeBandColors[band];
        }
    }

}