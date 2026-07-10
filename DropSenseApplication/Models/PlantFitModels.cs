using DropSense.Models;

namespace DropSense.Models;

// ── Per-channel breakdown ──────────────────────────────────────────────────

/// <summary>
/// Scoring result for a single <see cref="MeasurementChannel"/> against a
/// specific plant's <see cref="LibraryThreshold"/>.
/// </summary>
public sealed record PlantChannelScore
{
    /// <summary>Which sensor channel this score covers.</summary>
    public required MeasurementChannel Channel { get; init; }

    /// <summary>The CSV column key used for this channel (e.g. "temp_c").</summary>
    public required string ColumnKey { get; init; }

    /// <summary>
    /// Channel fitness score: 0 (very poor) – 5 (ideal).
    /// Derived as <c>clamp(5 − meanPenalty, 0, 5)</c>.
    /// </summary>
    public required double Score { get; init; }

    /// <summary>
    /// Time-weighted mean penalty accumulated across all readings for this channel.
    /// Penalty = 0 inside Ideal, ramps to 1.5 at the Safe boundary,
    /// then steeply to 5 beyond Safe.
    /// </summary>
    public required double MeanPenalty { get; init; }

    /// <summary>Number of sensor rows that had a non-null value for this channel.</summary>
    public required int ReadingsUsed { get; init; }

    /// <summary>
    /// Fraction (0–100) of total rows that contributed data.
    /// Low coverage means the score should be treated with caution.
    /// </summary>
    public required double CoveragePercent { get; init; }

    /// <summary>
    /// True when CoveragePercent is below the reliability threshold (default 70 %).
    /// Flagged channels are visually distinguished in the Excel output.
    /// </summary>
    public bool LowCoverage => CoveragePercent < 70.0;
}

// ── Per-plant aggregate ────────────────────────────────────────────────────

/// <summary>
/// Aggregate fit result for one <see cref="Plant"/> evaluated against a
/// specific sensor session's <see cref="ProcessDataResult"/>.
/// </summary>
public sealed record PlantFitResult
{
    /// <summary>The plant that was evaluated.</summary>
    public required Plant Plant { get; init; }

    /// <summary>
    /// Overall fit rating: simple mean of all <see cref="ChannelScores"/>.
    /// <c>double.NaN</c> when no scorable channels exist.
    /// </summary>
    public required double FitRating { get; init; }

    /// <summary>One entry per threshold channel that could be scored.</summary>
    public required IReadOnlyList<PlantChannelScore> ChannelScores { get; init; }

    /// <summary>
    /// Number of channels defined on the plant that had no matching sensor data
    /// (either unmapped channel or all-null column).  Non-zero values imply the
    /// fit rating is based on partial information.
    /// </summary>
    public required int UnscoredChannels { get; init; }
}
