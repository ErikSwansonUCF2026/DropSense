// ExportXlsxService.PlantFit.cs
// Partial class — plant fitness scoring and Excel sheet output.
//
// Design constraint: NO statistics are recomputed here.
// Mean and std_dev are read directly from ProcessDataResult.Stats (already
// populated by ProcessDataAsync).  Per-reading work is limited to the
// trivial linear transform  z = (x − μ) / σ  and the two-slope penalty
// lookup — both O(1) per row.  Time weights are computed once and shared
// across every plant evaluated in the same session.

using DropSense.Models;
using OfficeOpenXml;
using OfficeOpenXml.Style;
using DrawingColor = System.Drawing.Color;

namespace DropSense.Services;

public partial class ExportXlsxService
{
    // ══════════════════════════════════════════════════════════════════════════
    //  Channel → CSV column key mapping
    //  Must stay in sync with BuildColumnSelectors() in the main file.
    // ══════════════════════════════════════════════════════════════════════════

    private static readonly Dictionary<MeasurementChannel, string> ChannelColumnMap = new()
    {
        [MeasurementChannel.Temperature]               = "temp_c",
        [MeasurementChannel.RelativeHumidity]          = "humidity_%",
        [MeasurementChannel.BarometricPressure]        = "pressure_hpa",
        [MeasurementChannel.SolarIrradiance]           = "irradiance_wm2",
        [MeasurementChannel.VaporPressureDeficit]      = "vpd_kpa",
        [MeasurementChannel.DewPointTemperature]       = "dew_point_c",
        [MeasurementChannel.AbsoluteHumidity]          = "abs_humidity_gm3",
        [MeasurementChannel.AccumulatedSolarRadiation] = "accumulated_irradiance_kwh_m2",
        [MeasurementChannel.DailyLightIntegral]        = "dli_mol_m2_d",
        [MeasurementChannel.EstimatedPAR]              = "par_umol_m2_s",
    };

    // ══════════════════════════════════════════════════════════════════════════
    //  Public scoring entry point
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Scores each plant in <paramref name="plants"/> against the microclimate
    /// data already captured in <paramref name="processed"/>.
    ///
    /// Reuse contract
    /// ──────────────
    /// • Mean (μ) and std_dev (σ) are pulled from <c>processed.Stats</c> —
    ///   the rows computed once inside <c>ProcessDataAsync</c>.
    /// • Threshold band boundaries are converted into the sensor's own
    ///   distributional z-space  (z = (threshold − μ) / σ)  so that all
    ///   channels become unit-free and comparable without a second statistics pass.
    /// • Per-row work is limited to  z = (x − μ) / σ  and
    ///   <see cref="ComputePenalty"/> — both O(1).
    /// • Time weights (Δt per row) are computed once and shared across all plants.
    /// </summary>
    public static IReadOnlyList<PlantFitResult> ScorePlants(
        IEnumerable<Plant> plants,
        ProcessDataResult processed)
    {
        if (processed.RowsProcessed == 0 || processed.Rows.Count == 0)
            return [];

        // ── Pull precomputed stat rows (no recalculation) ──────────────────
        StatRow? meanRow   = processed.Stats.FirstOrDefault(s => s.Label == "mean");
        StatRow? stdDevRow = processed.Stats.FirstOrDefault(s => s.Label == "std_dev");

        if (meanRow is null || stdDevRow is null)
            return [];

        // ── Column selectors — reuse the shared map from the main file ─────
        var selectors = BuildColumnSelectors();

        // ── Time weights: computed once, reused for every plant ────────────
        double[] timeWeights = ComputeTimeWeights(processed.Rows);

        // ── Score each plant ───────────────────────────────────────────────
        var results = new List<PlantFitResult>();

        foreach (Plant plant in plants)
        {
            int unscoredCount = 0;
            var channelScores = new List<PlantChannelScore>();

            foreach (LibraryThreshold threshold in plant.storedThresholds)
            {
                // Map enum → column key
                if (!ChannelColumnMap.TryGetValue(threshold.libChannel, out string? colKey) ||
                    !selectors.TryGetValue(colKey, out var selector))
                {
                    unscoredCount++;
                    continue;
                }

                // ── Pull μ and σ from already-computed Stats ───────────────
                double? mean   = meanRow.Values.GetValueOrDefault(colKey);
                double? stddev = stdDevRow.Values.GetValueOrDefault(colKey);

                if (!mean.HasValue || !stddev.HasValue || stddev.Value == 0)
                {
                    unscoredCount++;
                    continue;
                }

                double μ = mean.Value;
                double σ = stddev.Value;

                // ── Convert threshold bounds into distributional z-space ────
                //    null bound → open-ended (±∞); never penalises that side.
                double zIdealLo = ToZ(threshold.IdealMin, μ, σ, double.NegativeInfinity);
                double zIdealHi = ToZ(threshold.IdealMax, μ, σ, double.PositiveInfinity);
                double zSafeLo  = ToZ(threshold.SafeMin,  μ, σ, double.NegativeInfinity);
                double zSafeHi  = ToZ(threshold.SafeMax,  μ, σ, double.PositiveInfinity);

                // ── Single pass: accumulate time-weighted penalties ────────
                //    z per reading = (x − μ) / σ  — one multiply + one subtract,
                //    reusing μ/σ already held in local variables above.
                double weightedPenaltySum = 0.0;
                double weightSum          = 0.0;
                int    readingsUsed       = 0;

                for (int i = 0; i < processed.Rows.Count; i++)
                {
                    double? raw = selector(processed.Rows[i]);
                    if (!raw.HasValue) continue;

                    double z       = (raw.Value - μ) / σ;
                    double penalty = ComputePenalty(z, zIdealLo, zIdealHi, zSafeLo, zSafeHi);
                    double w       = timeWeights[i];

                    weightedPenaltySum += penalty * w;
                    weightSum          += w;
                    readingsUsed++;
                }

                if (weightSum == 0 || readingsUsed == 0)
                {
                    unscoredCount++;
                    continue;
                }

                double meanPenalty = weightedPenaltySum / weightSum;
                double score       = Math.Max(0.0, Math.Min(5.0, 5.0 - meanPenalty));

                channelScores.Add(new PlantChannelScore
                {
                    Channel         = threshold.libChannel,
                    ColumnKey       = colKey,
                    Score           = Math.Round(score, 3),
                    MeanPenalty     = Math.Round(meanPenalty, 4),
                    ReadingsUsed    = readingsUsed,
                    CoveragePercent = Math.Round(100.0 * readingsUsed / processed.Rows.Count, 1),
                });
            }

            double fitRating = channelScores.Count > 0
                ? Math.Round(channelScores.Average(s => s.Score), 2)
                : double.NaN;

            results.Add(new PlantFitResult
            {
                Plant            = plant,
                FitRating        = fitRating,
                ChannelScores    = channelScores.AsReadOnly(),
                UnscoredChannels = unscoredCount,
            });
        }

        return results.AsReadOnly();
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  Penalty curve
    //  ─────────────────────────────────────────────────────────────────────────
    //  All arguments are in the sensor's distributional z-space, so the function
    //  is unit-free and identical for every channel.
    //
    //  Zones (measured as deviation from the nearest Ideal edge):
    //
    //    dev = 0               → penalty 0.0   (inside Ideal)
    //    dev = safeMargin      → penalty 1.5   (exactly at Safe boundary)
    //    dev = safeMargin + 2σ → penalty 5.0   (cap; steep-slope reference)
    //
    //  The safe margin itself is expressed in distributional σ units, so a
    //  narrow physical threshold band that spans only a fraction of the
    //  sensor's natural variability will have a correspondingly small margin
    //  and ramp faster — which is correct: a plant whose tolerances are tighter
    //  than typical microclimate swings is inherently harder to please.
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Returns a penalty in [0, 5] for a reading whose distributional z-score
    /// is <paramref name="z"/>, given threshold band boundaries also expressed
    /// in distributional z-space.
    /// </summary>
    private static double ComputePenalty(
        double z,
        double zIdealLo, double zIdealHi,
        double zSafeLo,  double zSafeHi)
    {
        // ── Inside Ideal ───────────────────────────────────────────────────
        if (z >= zIdealLo && z <= zIdealHi)
            return 0.0;

        // ── Determine which side we deviated on ────────────────────────────
        //    dev  = distance past the nearest Ideal edge (always ≥ 0)
        //    safeMargin = z-width between that Ideal edge and its Safe edge
        double dev, safeMargin;

        if (z < zIdealLo)
        {
            dev        = zIdealLo - z;
            safeMargin = double.IsNegativeInfinity(zSafeLo)
                         ? double.PositiveInfinity     // open-ended: never outside Safe
                         : zIdealLo - zSafeLo;
        }
        else
        {
            dev        = z - zIdealHi;
            safeMargin = double.IsPositiveInfinity(zSafeHi)
                         ? double.PositiveInfinity
                         : zSafeHi - zIdealHi;
        }

        // Open-ended safe boundary on this side → treat excursion as costless
        if (double.IsPositiveInfinity(safeMargin))
            return 0.0;

        // ── Inside Safe, outside Ideal: gentle linear ramp 0 → 1.5 ────────
        if (dev <= safeMargin)
            return safeMargin > 0 ? dev / safeMargin * 1.5 : 1.5;

        // ── Outside Safe: steep linear ramp 1.5 → 5 ───────────────────────
        //    Reaches cap at 2 additional distributional σ beyond the Safe edge.
        //    This constant ("steep reference sigma") is deliberately tunable:
        //    a tighter value makes brief Safe excursions maximally costly;
        //    a looser value reserves the cap for sustained, severe violations.
        const double SteepReferenceSigma = 2.0;

        double extra = dev - safeMargin;
        return Math.Min(5.0, 1.5 + extra / SteepReferenceSigma * 3.5);
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  Time weight helper
    //  ─────────────────────────────────────────────────────────────────────────
    //  Computes Δt per row using midpoint differencing so that dense sampling
    //  during a brief excursion does not disproportionately inflate its
    //  contribution to the weighted penalty sum.
    //
    //  Falls back to uniform weights (1.0) when:
    //    • Timestamps are absent, all null, or all identical
    //    • Sum of computed weights rounds to zero (import artifacts)
    // ══════════════════════════════════════════════════════════════════════════

    private static double[] ComputeTimeWeights(IReadOnlyList<SensorRow> rows)
    {
        int n = rows.Count;
        double[] w = new double[n];

        if (n == 1) { w[0] = 1.0; return w; }

        // Attempt to extract DateTimes from SensorRow.Timestamp.
        // The property is typed as object? in the Data sheet writer (format "@"),
        // so we try a direct cast first, then string parse.
        DateTime?[] ts = rows.Select(r =>
        {
            if (string.IsNullOrWhiteSpace(r.Timestamp))
                return (DateTime?)null;

            return DateTime.TryParse(r.Timestamp, out var parsed)
                ? parsed
                : null;
        }).ToArray();

        // Forward-fill then backward-fill gaps to keep the array contiguous
        for (int i = 1; i < n; i++)
            if (!ts[i].HasValue) ts[i] = ts[i - 1];
        for (int i = n - 2; i >= 0; i--)
            if (!ts[i].HasValue) ts[i] = ts[i + 1];

        // If timestamps are still all null or all identical → uniform weights
        if (!ts[0].HasValue || ts[0] == ts[n - 1])
        {
            Array.Fill(w, 1.0);
            return w;
        }

        // Midpoint differencing (trapezoidal / Voronoi interval)
        w[0]     = (ts[1]!.Value     - ts[0]!.Value).TotalSeconds;
        w[n - 1] = (ts[n - 1]!.Value - ts[n - 2]!.Value).TotalSeconds;

        for (int i = 1; i < n - 1; i++)
            w[i] = (ts[i + 1]!.Value - ts[i - 1]!.Value).TotalSeconds / 2.0;

        // Negative intervals indicate out-of-order data; fall back to uniform
        if (w.Any(x => x < 0) || w.Sum() <= 0)
            Array.Fill(w, 1.0);

        return w;
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  Excel sheet writer
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Writes a "Plant Fit" sheet to <paramref name="package"/>.
    ///
    /// Layout
    /// ──────
    ///  Row 1   : Section heading
    ///  Row 2   : Column headers (Plant | Fit Rating | channel columns…)
    ///  Row 3…  : One row per plant
    ///            • Fit Rating cell is filled on the green→amber→red gradient
    ///            • Each channel cell is filled by its own score gradient
    ///            • Low-coverage channels (< 70 %) show a suffix
    ///            • Unscored channels show "—"
    ///  After the last plant row: a legend block explaining the colour scale
    ///  and penalty zones.
    /// </summary>
    internal static void WritePlantFitSheet(
        ExcelPackage package,
        IReadOnlyList<PlantFitResult> results,
        ProcessDataResult processed)
    {
        if (results.Count == 0) return;

        ExcelWorksheet ws = package.Workbook.Worksheets.Add("Plant Fit");

        const string BodyFont   = "Arial";
        const string HeaderBg   = "1F4E79";
        const string HeaderFg   = "FFFFFF";
        const string AltRowBg   = "F5F5F5";
        const string WarnFg     = "E65100";   // low-coverage text

        // ── Collect every channel that appears in any result ───────────────
        //    Order: preserve ChannelColumnMap declaration order for consistency
        var channelOrder = ChannelColumnMap.Keys.ToList();

        var presentChannels = channelOrder
            .Where(ch => results.Any(r => r.ChannelScores.Any(s => s.Channel == ch)))
            .ToList();

        // ── Column index map ───────────────────────────────────────────────
        //    Col 1 : Common Name
        //    Col 2 : Fit Rating
        //    Col 3…: one per presentChannel
        //    Last  : Unscored count
        int colName       = 1;
        int colFitRating  = 2;
        int colFirstCh    = 3;
        int colUnscored   = colFirstCh + presentChannels.Count;

        // ── Section title ──────────────────────────────────────────────────
        int titleRow = 1;
        ws.Cells[titleRow, colName, titleRow, colUnscored].Merge = true;
        ws.Cells[titleRow, colName].Value = "Plant Microclimate Fit Assessment";
        var titleCell = ws.Cells[titleRow, colName];
        titleCell.Style.Font.Bold = true;
        titleCell.Style.Font.Size = 13;
        titleCell.Style.Font.Name = BodyFont;
        titleCell.Style.Font.Color.SetColor(HexToColor(HeaderBg));
        titleCell.Style.HorizontalAlignment = ExcelHorizontalAlignment.Left;
        titleCell.Style.VerticalAlignment   = ExcelVerticalAlignment.Center;
        ws.Row(titleRow).Height = 22;

        // ── Sub-title / method note ────────────────────────────────────────
        int subtitleRow = 2;
        ws.Cells[subtitleRow, colName, subtitleRow, colUnscored].Merge = true;
        ws.Cells[subtitleRow, colName].Value =
            "Scores (0–5) reflect time-weighted exposure within Ideal / Safe threshold bands, " +
            "converted to distributional z-space using session mean and std_dev. " +
            "⚠ = data coverage < 70 %.";
        var subCell = ws.Cells[subtitleRow, colName];
        subCell.Style.Font.Italic = true;
        subCell.Style.Font.Size   = 8;
        subCell.Style.Font.Name   = BodyFont;
        subCell.Style.Font.Color.SetColor(DrawingColor.DimGray);
        ws.Row(subtitleRow).Height = 14;

        // ── Header row ─────────────────────────────────────────────────────
        int headerRow = 3;

        void WriteHeader(int col, string text)
        {
            var cell = ws.Cells[headerRow, col];
            cell.Value = text;
            StyleHeader(cell, HeaderBg, HeaderFg, BodyFont);
            cell.Style.WrapText = true;
        }

        WriteHeader(colName,      "Plant");
        WriteHeader(colFitRating, "Fit Rating\n(0–5)");
        WriteHeader(colUnscored,  "Unscored\nChannels");

        for (int i = 0; i < presentChannels.Count; i++)
        {
            // Short display label: use the column key (already readable)
            string label = ChannelColumnMap[presentChannels[i]]
                .Replace("_", " ")
                .Replace("%", "(%)")
                .Replace("c", "°C");
            WriteHeader(colFirstCh + i, label);
        }

        ws.Row(headerRow).Height = 30;

        // ── Data rows ──────────────────────────────────────────────────────
        for (int r = 0; r < results.Count; r++)
        {
            PlantFitResult result = results[r];
            int excelRow = headerRow + 1 + r;
            bool stripe  = r % 2 == 1;
            string rowBg = stripe ? AltRowBg : "FFFFFF";

            // Plant name
            var nameCell = ws.Cells[excelRow, colName];
            nameCell.Value = result.Plant.CommonName;
            StyleBodyCell(nameCell, BodyFont, rowBg, bold: true);

            // Overall Fit Rating — coloured cell
            var ratingCell = ws.Cells[excelRow, colFitRating];
            if (!double.IsNaN(result.FitRating))
            {
                ratingCell.Value = result.FitRating;
                ratingCell.Style.Numberformat.Format = "0.00";
                ratingCell.Style.Font.Bold = true;
                ApplyScoreColor(ratingCell, result.FitRating, BodyFont);
            }
            else
            {
                ratingCell.Value = "N/A";
                StyleBodyCell(ratingCell, BodyFont, rowBg);
                ratingCell.Style.Font.Color.SetColor(DrawingColor.Gray);
            }

            // Per-channel scores
            for (int ci = 0; ci < presentChannels.Count; ci++)
            {
                var cell      = ws.Cells[excelRow, colFirstCh + ci];
                MeasurementChannel ch = presentChannels[ci];
                PlantChannelScore? cs = result.ChannelScores
                    .FirstOrDefault(s => s.Channel == ch);

                if (cs is null)
                {
                    // This plant has no threshold for this channel
                    cell.Value = "—";
                    StyleBodyCell(cell, BodyFont, rowBg);
                    cell.Style.Font.Color.SetColor(DrawingColor.LightGray);
                }
                else
                {
                    string display = cs.Score.ToString("0.000");
                    if (cs.LowCoverage) display += " ⚠";

                    cell.Value = display;
                    ApplyScoreColor(cell, cs.Score, BodyFont);

                    if (cs.LowCoverage)
                        cell.Style.Font.Color.SetColor(HexToColor(WarnFg));

                    cell.AddComment(
                        $"Mean penalty : {cs.MeanPenalty:0.0000}\n" +
                        $"Readings used: {cs.ReadingsUsed:N0}\n" +
                        $"Coverage     : {cs.CoveragePercent:0.0}%",
                        "DropSense");
                }
            }

            // Unscored channel count
            var unscoredCell = ws.Cells[excelRow, colUnscored];
            unscoredCell.Value = result.UnscoredChannels;
            StyleBodyCell(unscoredCell, BodyFont, rowBg);
            if (result.UnscoredChannels > 0)
                unscoredCell.Style.Font.Color.SetColor(HexToColor(WarnFg));
        }

        // ── Legend block ───────────────────────────────────────────────────
        int legendStartRow = headerRow + results.Count + 2;

        ws.Cells[legendStartRow, colName].Value = "Score legend";
        ws.Cells[legendStartRow, colName].Style.Font.Bold = true;
        ws.Cells[legendStartRow, colName].Style.Font.Name = BodyFont;

        var legendItems = new (double Score, string Label)[]
        {
            (5.0,  "5.0  →  All readings inside Ideal band (zero penalty)"),
            (4.0,  "4.0  →  Mostly Ideal; minor Ideal-band excursions only"),
            (3.0,  "3.0  →  Frequent Ideal excursions, rarely outside Safe"),
            (2.0,  "2.0  →  Regular Safe-band excursions with moderate duration"),
            (1.0,  "1.0  →  Prolonged or severe Safe-band violations"),
            (0.0,  "0.0  →  Sustained exposure beyond Safe band at high intensity"),
        };

        for (int li = 0; li < legendItems.Length; li++)
        {
            var (score, label) = legendItems[li];
            int legendRow = legendStartRow + 1 + li;

            var swatchCell = ws.Cells[legendRow, colName];
            swatchCell.Value = score.ToString("0.0");
            ApplyScoreColor(swatchCell, score, BodyFont);
            swatchCell.Style.Font.Bold = true;

            var labelCell = ws.Cells[legendRow, colName + 1, legendRow, colUnscored];
            labelCell.Merge = true;
            labelCell.Value = label;
            labelCell.Style.Font.Name = BodyFont;
            labelCell.Style.Font.Size = 9;
        }

        // ── Column widths & freeze ─────────────────────────────────────────
        ws.Column(colName).Width      = 22;
        ws.Column(colFitRating).Width = 12;
        ws.Column(colUnscored).Width  = 12;

        for (int ci = 0; ci < presentChannels.Count; ci++)
            ws.Column(colFirstCh + ci).Width = 14;

        ws.View.FreezePanes(headerRow + 1, colFirstCh);
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  Private helpers
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Converts a nullable threshold value to distributional z-space.
    /// Returns <paramref name="fallback"/> when the threshold is null (open-ended).
    /// </summary>
    private static double ToZ(float? threshold, double μ, double σ, double fallback)
        => threshold.HasValue ? ((double)threshold.Value - μ) / σ : fallback;

    /// <summary>
    /// Fills a score cell with a green → amber → red gradient.
    ///
    ///   5.0  →  #2E7D32  (deep green)
    ///   3.0  →  #F9A825  (amber)
    ///   0.0  →  #C62828  (deep red)
    ///
    /// Interpolation is done separately in each half so the amber midpoint
    /// lands precisely at 3.0 regardless of the full range.
    /// </summary>
    private static void ApplyScoreColor(ExcelRange cell, double score, string font)
    {
        score = Math.Clamp(score, 0.0, 5.0);

        DrawingColor bg;

        if (score >= 3.0)
        {
            // Green half: amber (3) → deep green (5)
            double t = (score - 3.0) / 2.0;
            bg = Lerp(
                HexToColor("F9A825"),   // amber
                HexToColor("2E7D32"),   // deep green
                t);
        }
        else
        {
            // Red half: deep red (0) → amber (3)
            double t = score / 3.0;
            bg = Lerp(
                HexToColor("C62828"),   // deep red
                HexToColor("F9A825"),   // amber
                t);
        }

        // Choose white or black text for legibility (WCAG relative luminance)
        double luminance = 0.2126 * bg.R / 255.0
                         + 0.7152 * bg.G / 255.0
                         + 0.0722 * bg.B / 255.0;
        DrawingColor fg = luminance > 0.45 ? DrawingColor.Black : DrawingColor.White;

        cell.Style.Fill.PatternType = ExcelFillStyle.Solid;
        cell.Style.Fill.BackgroundColor.SetColor(bg);
        cell.Style.Font.Color.SetColor(fg);
        cell.Style.Font.Name = font;
        cell.Style.Font.Size = 9;
        cell.Style.HorizontalAlignment = ExcelHorizontalAlignment.Center;
        cell.Style.VerticalAlignment   = ExcelVerticalAlignment.Center;
    }

    /// <summary>Linearly interpolates between two <see cref="Color"/> values.</summary>
    private static DrawingColor Lerp(DrawingColor a, DrawingColor b, double t)
    {
        t = Math.Clamp(t, 0.0, 1.0);
        return DrawingColor.FromArgb(
            (int)(a.R + (b.R - a.R) * t),
            (int)(a.G + (b.G - a.G) * t),
            (int)(a.B + (b.B - a.B) * t));
    }
}
