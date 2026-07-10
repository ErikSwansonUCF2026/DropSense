using System;
using System.Collections.Generic;
using System.Text;
using DropSense.Models;

namespace DropSense.Models;

public sealed class ExportConfiguration
{
    // Measurements Toggle
    public bool IncludeTemperature;
    public bool IncludeRelativeHumidity;
    public bool IncludeBarometricPressure;
    public bool IncludeSolarIrradiance;
    public bool IncludeVaporPressureDeficit;
    public bool IncludeDewPoint;
    public bool IncludeAbsoluteHumidity;
    public bool IncludeAccumulatedSolarRadiation;
    public bool IncludeDailyLightIntegral;
    public bool IncludePAR;


    // Statistics Toggle
    public bool StatMean;
    public bool StatMedian;
    public bool StatMode;
    public bool StatStdDev;
    public bool StatMinMax;
    public bool StatRange;
    public bool StatQuartiles;

    // Advanced Stats
    public bool StatCoefficientOfVariation;
    public bool StatMovingAverage;
    public int MovingAverageWindow;
    public bool StatZScore;
    // ZScoreAutoFlagThreshold removed — the Z-Score sheet's flagging now reads
    // AbsoluteThresholds/ZScoreThresholds below, the same as every other sheet,
    // instead of its own standalone value.

    // Anomaly Flagging

    public bool AnomalyFlaggingEnabled;
    public bool AnomalyUseAbsoluteThreshold;
    public bool AnomalyUseZScoreThreshold;

    // Absolute Thresholds
    public AbsoluteThresholdSettings AbsoluteThresholds;

    // Relative Thresholds
    public ZScoreThresholdSettings ZScoreThresholds;

    // Plant Fit
    public bool StatPlantFit;
    public List<Plant>? Plants;

    // Graphing
    public bool GraphingEnabled;
    public bool GraphTypeLine;
    public bool GraphTypeScatter;
    public bool GraphTypeBar;
    public bool GraphShadeZScoreRanges;
    public bool GraphShadeAbsoluteViolations;
    public bool GraphShowGridLines;
    public bool GraphShowMarkers;
    public bool GraphShowLegend;
    public bool GraphDualYAxis;
    public bool GraphSmoothCurves;

    public GraphTimeResolution GraphTimeResolution;

    // ── Derived helpers ───────────────────────────────────────────────────
    public string FilePath { get; set; } = string.Empty;
}

public sealed class AbsoluteThresholdSettings
{
    public double? TempMin;
    public double? TempMax;

    public double? RhMin;
    public double? RhMax;

    public double? PressMin;
    public double? PressMax;

    public double? SolarMin;
    public double? SolarMax;

    public double? VpdMin;
    public double? VpdMax;

    public double? DewPointMin;
    public double? DewPointMax;

    public double? AbsHumMin;
    public double? AbsHumMax;

    public double? AccSolarMin;
    public double? AccSolarMax;

    public double? DliMin;
    public double? DliMax;

    public double? ParMin;
    public double? ParMax;
}

public sealed class ZScoreThresholdSettings
{
    public double? TempZMin;
    public double? TempZMax;

    public double? RhZMin;
    public double? RhZMax;

    public double? PressZMin;
    public double? PressZMax;

    public double? SolarZMin;
    public double? SolarZMax;

    public double? VpdZMin;
    public double? VpdZMax;

    public double? DewPointZMin;
    public double? DewPointZMax;

    public double? AbsHumZMin;
    public double? AbsHumZMax;

    public double? AccSolarZMin;
    public double? AccSolarZMax;

    public double? DliZMin;
    public double? DliZMax;

    public double? ParZMin;
    public double? ParZMax;
}