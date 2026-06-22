using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Input;
using Microsoft.Maui.Controls;
using DropSense.Services;
using DropSense.Models;

namespace DropSense.ViewModels
{
    /// <summary>
    /// ViewModel for the Analysis &amp; Export page.
    /// All export implementation is deferred to <see cref="IExportXlsxService"/>;
    /// this class is responsible only for collecting user settings and orchestrating
    /// the call to that service.
    /// </summary>
    public class AnalysisExportViewModel : INotifyPropertyChanged
    {
        // ──────────────────────────────────────────────────────────────────────
        //  Dependencies
        // ──────────────────────────────────────────────────────────────────────

        private readonly IExportXlsxService _exportService;
        private readonly IFileSessionService _fileSessionService;

        public AnalysisExportViewModel(IExportXlsxService exportService, IFileSessionService fileSessionService)
        {
            // Null-safe for design-time / XAML instantiation without DI
            _exportService = exportService;
            _fileSessionService = fileSessionService;
            CreateXlsxCommand = new Command(
                execute: async () => await ExecuteCreateXlsxAsync(),
                canExecute: () => IsNotExporting);
        }

        // ──────────────────────────────────────────────────────────────────────
        //  Commands
        // ──────────────────────────────────────────────────────────────────────

        public ICommand CreateXlsxCommand { get; }

        // ──────────────────────────────────────────────────────────────────────
        //  Export state
        // ──────────────────────────────────────────────────────────────────────

        private bool _isExporting;
        public bool IsExporting
        {
            get => _isExporting;
            private set
            {
                SetField(ref _isExporting, value);
                OnPropertyChanged(nameof(IsNotExporting));
                (CreateXlsxCommand as Command)?.ChangeCanExecute();
            }
        }

        public bool IsNotExporting => !_isExporting;

        private string _exportStatusMessage;
        public string ExportStatusMessage
        {
            get => _exportStatusMessage;
            private set
            {
                SetField(ref _exportStatusMessage, value);
                OnPropertyChanged(nameof(HasExportStatusMessage));
            }
        }

        public bool HasExportStatusMessage => !string.IsNullOrWhiteSpace(_exportStatusMessage);

        private string _validationMessage;
        public string ValidationMessage
        {
            get => _validationMessage;
            private set
            {
                SetField(ref _validationMessage, value);
                OnPropertyChanged(nameof(HasValidationMessage));
            }
        }

        public bool HasValidationMessage => !string.IsNullOrWhiteSpace(_validationMessage);

        // ──────────────────────────────────────────────────────────────────────
        //  GROUP 1 · MEASUREMENTS
        // ──────────────────────────────────────────────────────────────────────

        // Recorded measurements
        private bool _includeTemperature = true;
        public bool IncludeTemperature { get => _includeTemperature; set => SetField(ref _includeTemperature, value); }

        private bool _includeRelativeHumidity = true;
        public bool IncludeRelativeHumidity { get => _includeRelativeHumidity; set => SetField(ref _includeRelativeHumidity, value); }

        private bool _includeBarometricPressure = true;
        public bool IncludeBarometricPressure { get => _includeBarometricPressure; set => SetField(ref _includeBarometricPressure, value); }

        private bool _includeSolarIrradiance = true;
        public bool IncludeSolarIrradiance { get => _includeSolarIrradiance; set => SetField(ref _includeSolarIrradiance, value); }

        // Derived measurements
        private bool _includeVaporPressureDeficit;
        public bool IncludeVaporPressureDeficit { get => _includeVaporPressureDeficit; set => SetField(ref _includeVaporPressureDeficit, value); }

        private bool _includeDewPoint;
        public bool IncludeDewPoint { get => _includeDewPoint; set => SetField(ref _includeDewPoint, value); }

        private bool _includeAbsoluteHumidity;
        public bool IncludeAbsoluteHumidity { get => _includeAbsoluteHumidity; set => SetField(ref _includeAbsoluteHumidity, value); }

        private bool _includeAccumulatedSolarRadiation;
        public bool IncludeAccumulatedSolarRadiation { get => _includeAccumulatedSolarRadiation; set => SetField(ref _includeAccumulatedSolarRadiation, value); }

        private bool _includeDailyLightIntegral;
        public bool IncludeDailyLightIntegral { get => _includeDailyLightIntegral; set => SetField(ref _includeDailyLightIntegral, value); }

        // ──────────────────────────────────────────────────────────────────────
        //  GROUP 2 · STATISTICS
        // ──────────────────────────────────────────────────────────────────────

        // Descriptive
        private bool _statMean = true;
        public bool StatMean { get => _statMean; set => SetField(ref _statMean, value); }

        private bool _statMedian;
        public bool StatMedian { get => _statMedian; set => SetField(ref _statMedian, value); }

        private bool _statMode;
        public bool StatMode { get => _statMode; set => SetField(ref _statMode, value); }

        private bool _statStdDev;
        public bool StatStdDev { get => _statStdDev; set => SetField(ref _statStdDev, value); }

        private bool _statMinMax = true;
        public bool StatMinMax { get => _statMinMax; set => SetField(ref _statMinMax, value); }

        private bool _statRange;
        public bool StatRange { get => _statRange; set => SetField(ref _statRange, value); }

        private bool _statQuartiles;
        public bool StatQuartiles { get => _statQuartiles; set => SetField(ref _statQuartiles, value); }

        // Advanced
        private bool _statCoefficientOfVariation;
        public bool StatCoefficientOfVariation { get => _statCoefficientOfVariation; set => SetField(ref _statCoefficientOfVariation, value); }

        private bool _statMovingAverage;
        public bool StatMovingAverage
        {
            get => _statMovingAverage;
            set { SetField(ref _statMovingAverage, value); }
        }

        private string _movingAverageWindow = "10";
        public string MovingAverageWindow { get => _movingAverageWindow; set => SetField(ref _movingAverageWindow, value); }

        private bool _statZScore;
        public bool StatZScore
        {
            get => _statZScore;
            set { SetField(ref _statZScore, value); }
        }

        /// <summary>
        /// Absolute Z-score value above which a data point is automatically flagged
        /// when computing per-point Z-scores (Group 2 advanced, independent of Group 3).
        /// </summary>
        private string _zScoreAutoFlagThreshold = "3.0";
        public string ZScoreAutoFlagThreshold { get => _zScoreAutoFlagThreshold; set => SetField(ref _zScoreAutoFlagThreshold, value); }

        // ──────────────────────────────────────────────────────────────────────
        //  GROUP 3 · ANOMALY FLAGGING
        // ──────────────────────────────────────────────────────────────────────

        private bool _anomalyFlaggingEnabled;
        public bool AnomalyFlaggingEnabled { get => _anomalyFlaggingEnabled; set => SetField(ref _anomalyFlaggingEnabled, value); }

        // Threshold method — mutually exclusive; managed via RadioButton grouping
        private bool _anomalyUseAbsoluteThreshold = true;
        public bool AnomalyUseAbsoluteThreshold
        {
            get => _anomalyUseAbsoluteThreshold;
            set
            {
                if (SetField(ref _anomalyUseAbsoluteThreshold, value) && value)
                    AnomalyUseZScoreThreshold = false;
            }
        }

        private bool _anomalyUseZScoreThreshold;
        public bool AnomalyUseZScoreThreshold
        {
            get => _anomalyUseZScoreThreshold;
            set
            {
                if (SetField(ref _anomalyUseZScoreThreshold, value) && value)
                    AnomalyUseAbsoluteThreshold = false;
            }
        }

        // ── Absolute thresholds (min / max per measurement) ────────────────

        // Temperature  (°C)
        private string _tempAbsMin = "-40"; public string TempAbsMin { get => _tempAbsMin; set => SetField(ref _tempAbsMin, value); }
        private string _tempAbsMax = "85";  public string TempAbsMax { get => _tempAbsMax; set => SetField(ref _tempAbsMax, value); }

        // Relative Humidity  (%)
        private string _rhAbsMin = "0";   public string RhAbsMin { get => _rhAbsMin; set => SetField(ref _rhAbsMin, value); }
        private string _rhAbsMax = "100"; public string RhAbsMax { get => _rhAbsMax; set => SetField(ref _rhAbsMax, value); }

        // Barometric Pressure  (hPa)
        private string _pressAbsMin = "800";  public string PressAbsMin { get => _pressAbsMin; set => SetField(ref _pressAbsMin, value); }
        private string _pressAbsMax = "1100"; public string PressAbsMax { get => _pressAbsMax; set => SetField(ref _pressAbsMax, value); }

        // Solar Irradiance  (W/m²)
        private string _solarAbsMin = "0";    public string SolarAbsMin { get => _solarAbsMin; set => SetField(ref _solarAbsMin, value); }
        private string _solarAbsMax = "1500"; public string SolarAbsMax { get => _solarAbsMax; set => SetField(ref _solarAbsMax, value); }

        // Vapor Pressure Deficit  (hPa)
        private string _vpdAbsMin = "0";  public string VpdAbsMin { get => _vpdAbsMin; set => SetField(ref _vpdAbsMin, value); }
        private string _vpdAbsMax = "100"; public string VpdAbsMax { get => _vpdAbsMax; set => SetField(ref _vpdAbsMax, value); }

        // Dew Point Temperature  (°C)
        private string _dewPointAbsMin = "-40"; public string DewPointAbsMin { get => _dewPointAbsMin; set => SetField(ref _dewPointAbsMin, value); }
        private string _dewPointAbsMax = "35";  public string DewPointAbsMax { get => _dewPointAbsMax; set => SetField(ref _dewPointAbsMax, value); }

        // Absolute Humidity  (g/m³)
        private string _absHumAbsMin = "0";  public string AbsHumAbsMin { get => _absHumAbsMin; set => SetField(ref _absHumAbsMin, value); }
        private string _absHumAbsMax = "100"; public string AbsHumAbsMax { get => _absHumAbsMax; set => SetField(ref _absHumAbsMax, value); }

        // Accumulated Solar Radiation  (MJ/m²)
        private string _accSolarAbsMin = "0";  public string AccSolarAbsMin { get => _accSolarAbsMin; set => SetField(ref _accSolarAbsMin, value); }
        private string _accSolarAbsMax = "40"; public string AccSolarAbsMax { get => _accSolarAbsMax; set => SetField(ref _accSolarAbsMax, value); }

        // Daily Light Integral  (mol/m²/d)
        private string _dliAbsMin = "0";  public string DliAbsMin { get => _dliAbsMin; set => SetField(ref _dliAbsMin, value); }
        private string _dliAbsMax = "70"; public string DliAbsMax { get => _dliAbsMax; set => SetField(ref _dliAbsMax, value); }

        // ── Z-Score thresholds (min-z / max-z per measurement) ─────────────
        // These represent the lower and upper Z-score bounds (e.g. -3.0 / +3.0).
        // Stored as strings to allow partial input in Entry controls.

        private string _tempZMin = "-3.0"; public string TempZMin { get => _tempZMin; set => SetField(ref _tempZMin, value); }
        private string _tempZMax = "3.0";  public string TempZMax { get => _tempZMax; set => SetField(ref _tempZMax, value); }

        private string _rhZMin = "-3.0"; public string RhZMin { get => _rhZMin; set => SetField(ref _rhZMin, value); }
        private string _rhZMax = "3.0";  public string RhZMax { get => _rhZMax; set => SetField(ref _rhZMax, value); }

        private string _pressZMin = "-3.0"; public string PressZMin { get => _pressZMin; set => SetField(ref _pressZMin, value); }
        private string _pressZMax = "3.0";  public string PressZMax { get => _pressZMax; set => SetField(ref _pressZMax, value); }

        private string _solarZMin = "-3.0"; public string SolarZMin { get => _solarZMin; set => SetField(ref _solarZMin, value); }
        private string _solarZMax = "3.0";  public string SolarZMax { get => _solarZMax; set => SetField(ref _solarZMax, value); }

        private string _vpdZMin = "-3.0"; public string VpdZMin { get => _vpdZMin; set => SetField(ref _vpdZMin, value); }
        private string _vpdZMax = "3.0";  public string VpdZMax { get => _vpdZMax; set => SetField(ref _vpdZMax, value); }

        private string _dewPointZMin = "-3.0"; public string DewPointZMin { get => _dewPointZMin; set => SetField(ref _dewPointZMin, value); }
        private string _dewPointZMax = "3.0";  public string DewPointZMax { get => _dewPointZMax; set => SetField(ref _dewPointZMax, value); }

        private string _absHumZMin = "-3.0"; public string AbsHumZMin { get => _absHumZMin; set => SetField(ref _absHumZMin, value); }
        private string _absHumZMax = "3.0";  public string AbsHumZMax { get => _absHumZMax; set => SetField(ref _absHumZMax, value); }

        private string _accSolarZMin = "-3.0"; public string AccSolarZMin { get => _accSolarZMin; set => SetField(ref _accSolarZMin, value); }
        private string _accSolarZMax = "3.0";  public string AccSolarZMax { get => _accSolarZMax; set => SetField(ref _accSolarZMax, value); }

        private string _dliZMin = "-3.0"; public string DliZMin { get => _dliZMin; set => SetField(ref _dliZMin, value); }
        private string _dliZMax = "3.0";  public string DliZMax { get => _dliZMax; set => SetField(ref _dliZMax, value); }

        // ──────────────────────────────────────────────────────────────────────
        //  GROUP 4 · GRAPHING
        // ──────────────────────────────────────────────────────────────────────

        private bool _graphingEnabled;
        public bool GraphingEnabled { get => _graphingEnabled; set => SetField(ref _graphingEnabled, value); }

        // Chart type (mutually exclusive chips — ViewModel enforces this)
        private bool _graphTypeLine = true;
        public bool GraphTypeLine
        {
            get => _graphTypeLine;
            set { if (SetField(ref _graphTypeLine, value) && value) { GraphTypeScatter = false; GraphTypeBar = false; } }
        }

        private bool _graphTypeScatter;
        public bool GraphTypeScatter
        {
            get => _graphTypeScatter;
            set { if (SetField(ref _graphTypeScatter, value) && value) { GraphTypeLine = false; GraphTypeBar = false; } }
        }

        private bool _graphTypeBar;
        public bool GraphTypeBar
        {
            get => _graphTypeBar;
            set { if (SetField(ref _graphTypeBar, value) && value) { GraphTypeLine = false; GraphTypeScatter = false; } }
        }

        // Anomaly overlay
        private bool _graphShadeZScoreRanges;
        public bool GraphShadeZScoreRanges { get => _graphShadeZScoreRanges; set => SetField(ref _graphShadeZScoreRanges, value); }

        private bool _graphShadeAbsoluteViolations;
        public bool GraphShadeAbsoluteViolations { get => _graphShadeAbsoluteViolations; set => SetField(ref _graphShadeAbsoluteViolations, value); }

        // Appearance
        private bool _graphShowGridLines = true;
        public bool GraphShowGridLines { get => _graphShowGridLines; set => SetField(ref _graphShowGridLines, value); }

        private bool _graphShowMarkers = true;
        public bool GraphShowMarkers { get => _graphShowMarkers; set => SetField(ref _graphShowMarkers, value); }

        private bool _graphShowLegend = true;
        public bool GraphShowLegend { get => _graphShowLegend; set => SetField(ref _graphShowLegend, value); }

        private bool _graphDualYAxis;
        public bool GraphDualYAxis { get => _graphDualYAxis; set => SetField(ref _graphDualYAxis, value); }

        private bool _graphSmoothCurves;
        public bool GraphSmoothCurves { get => _graphSmoothCurves; set => SetField(ref _graphSmoothCurves, value); }

        // Time-axis resolution (mutually exclusive)
        private bool _graphTimeResAuto = true;
        public bool GraphTimeResAuto
        {
            get => _graphTimeResAuto;
            set { if (SetField(ref _graphTimeResAuto, value) && value) { GraphTimeResHourly = false; GraphTimeResDaily = false; GraphTimeResWeekly = false; } }
        }

        private bool _graphTimeResHourly;
        public bool GraphTimeResHourly
        {
            get => _graphTimeResHourly;
            set { if (SetField(ref _graphTimeResHourly, value) && value) { GraphTimeResAuto = false; GraphTimeResDaily = false; GraphTimeResWeekly = false; } }
        }

        private bool _graphTimeResDaily;
        public bool GraphTimeResDaily
        {
            get => _graphTimeResDaily;
            set { if (SetField(ref _graphTimeResDaily, value) && value) { GraphTimeResAuto = false; GraphTimeResHourly = false; GraphTimeResWeekly = false; } }
        }

        private bool _graphTimeResWeekly;
        public bool GraphTimeResWeekly
        {
            get => _graphTimeResWeekly;
            set { if (SetField(ref _graphTimeResWeekly, value) && value) { GraphTimeResAuto = false; GraphTimeResHourly = false; GraphTimeResDaily = false; } }
        }

        // ──────────────────────────────────────────────────────────────────────
        //  EXPORT ORCHESTRATION
        // ──────────────────────────────────────────────────────────────────────

        private async Task ExecuteCreateXlsxAsync()
        {
            ValidationMessage = null;
            ExportStatusMessage = null;

            if (!ValidateSettings())
                return;

            IsExporting = true;
            ExportStatusMessage = "Building export configuration…";

            try
            {
                var config = BuildExportConfiguration();

                // ── Step 1: Process / compute the data ────────────────────────
                ExportStatusMessage = "Processing measurements and statistics…";
                var processed = await _exportService.ProcessDataAsync(config.FilePath);

                // ── Step 2: Write the .xlsx document ──────────────────────────
                ExportStatusMessage = "Writing spreadsheet…";
                var xlsxResult = await _exportService.WriteXlsxAsync(config, processed);

                // ── Step 3: Prompt the user to save / open ────────────────────
                ExportStatusMessage = "Opening file…";
                await _exportService.OpenOrSaveFileAsync(xlsxResult.XlsxPath);

                ExportStatusMessage = "Export complete.";
            }
            catch (System.Exception ex)
            {
                ExportStatusMessage = $"Export failed: {ex.Message}";
            }
            finally
            {
                IsExporting = false;
            }
        }

        /// <summary>
        /// Validates user inputs before starting the export.
        /// Returns true when all inputs are acceptable.
        /// </summary>
        private bool ValidateSettings()
        {
            var selected = GetSelectedMeasurements();
            if (selected.Count == 0)
            {
                ValidationMessage = "Select at least one measurement to include in the export.";
                return false;
            }

            if (StatMovingAverage &&
                (!int.TryParse(MovingAverageWindow, out int window) || window < 2))
            {
                ValidationMessage = "Moving Average window must be an integer of 2 or greater.";
                return false;
            }

            if (StatZScore &&
                (!double.TryParse(ZScoreAutoFlagThreshold, out double zAuto) || zAuto <= 0))
            {
                ValidationMessage = "Z-Score auto-flag threshold must be a positive number (e.g. 3.0).";
                return false;
            }

            if (AnomalyFlaggingEnabled && AnomalyUseZScoreThreshold)
            {
                // Spot-check Temperature Z bounds as a representative validation
                if (!double.TryParse(TempZMin, out _) || !double.TryParse(TempZMax, out _))
                {
                    ValidationMessage = "One or more Z-score threshold values are not valid numbers.";
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Assembles all user-configured settings into a single configuration object
        /// that is passed to the export service layer.
        /// </summary>
        private ExportConfiguration BuildExportConfiguration()
        {
            return new ExportConfiguration
            {
                // Measurements
                IncludeTemperature = IncludeTemperature,
                IncludeRelativeHumidity = IncludeRelativeHumidity,
                IncludeBarometricPressure = IncludeBarometricPressure,
                IncludeSolarIrradiance = IncludeSolarIrradiance,
                IncludeVaporPressureDeficit = IncludeVaporPressureDeficit,
                IncludeDewPoint = IncludeDewPoint,
                IncludeAbsoluteHumidity = IncludeAbsoluteHumidity,
                IncludeAccumulatedSolarRadiation = IncludeAccumulatedSolarRadiation,
                IncludeDailyLightIntegral = IncludeDailyLightIntegral,

                // Statistics
                StatMean = StatMean,
                StatMedian = StatMedian,
                StatMode = StatMode,
                StatStdDev = StatStdDev,
                StatMinMax = StatMinMax,
                StatRange = StatRange,
                StatQuartiles = StatQuartiles,
                StatCoefficientOfVariation = StatCoefficientOfVariation,
                StatMovingAverage = StatMovingAverage,
                MovingAverageWindow = int.TryParse(MovingAverageWindow, out int w) ? w : 10,
                StatZScore = StatZScore,
                ZScoreAutoFlagThreshold = double.TryParse(ZScoreAutoFlagThreshold, out double zAuto) ? zAuto : 3.0,

                // Anomaly flagging
                AnomalyFlaggingEnabled = AnomalyFlaggingEnabled,
                AnomalyUseAbsoluteThreshold = AnomalyUseAbsoluteThreshold,
                AnomalyUseZScoreThreshold = AnomalyUseZScoreThreshold,

                AbsoluteThresholds = new AbsoluteThresholdSettings
                {
                    TempMin = ParseDouble(TempAbsMin),
                    TempMax = ParseDouble(TempAbsMax),
                    RhMin = ParseDouble(RhAbsMin),
                    RhMax = ParseDouble(RhAbsMax),
                    PressMin = ParseDouble(PressAbsMin),
                    PressMax = ParseDouble(PressAbsMax),
                    SolarMin = ParseDouble(SolarAbsMin),
                    SolarMax = ParseDouble(SolarAbsMax),
                    VpdMin = ParseDouble(VpdAbsMin),
                    VpdMax = ParseDouble(VpdAbsMax),
                    DewPointMin = ParseDouble(DewPointAbsMin),
                    DewPointMax = ParseDouble(DewPointAbsMax),
                    AbsHumMin = ParseDouble(AbsHumAbsMin),
                    AbsHumMax = ParseDouble(AbsHumAbsMax),
                    AccSolarMin = ParseDouble(AccSolarAbsMin),
                    AccSolarMax = ParseDouble(AccSolarAbsMax),
                    DliMin = ParseDouble(DliAbsMin),
                    DliMax = ParseDouble(DliAbsMax),
                },

                ZScoreThresholds = new ZScoreThresholdSettings
                {
                    TempZMin = ParseDouble(TempZMin),
                    TempZMax = ParseDouble(TempZMax),
                    RhZMin = ParseDouble(RhZMin),
                    RhZMax = ParseDouble(RhZMax),
                    PressZMin = ParseDouble(PressZMin),
                    PressZMax = ParseDouble(PressZMax),
                    SolarZMin = ParseDouble(SolarZMin),
                    SolarZMax = ParseDouble(SolarZMax),
                    VpdZMin = ParseDouble(VpdZMin),
                    VpdZMax = ParseDouble(VpdZMax),
                    DewPointZMin = ParseDouble(DewPointZMin),
                    DewPointZMax = ParseDouble(DewPointZMax),
                    AbsHumZMin = ParseDouble(AbsHumZMin),
                    AbsHumZMax = ParseDouble(AbsHumZMax),
                    AccSolarZMin = ParseDouble(AccSolarZMin),
                    AccSolarZMax = ParseDouble(AccSolarZMax),
                    DliZMin = ParseDouble(DliZMin),
                    DliZMax = ParseDouble(DliZMax),
                },

                // Graphing
                GraphingEnabled = GraphingEnabled,
                GraphTypeLine = GraphTypeLine,
                GraphTypeScatter = GraphTypeScatter,
                GraphTypeBar = GraphTypeBar,
                GraphShadeZScoreRanges = GraphShadeZScoreRanges,
                GraphShadeAbsoluteViolations = GraphShadeAbsoluteViolations,
                GraphShowGridLines = GraphShowGridLines,
                GraphShowMarkers = GraphShowMarkers,
                GraphShowLegend = GraphShowLegend,
                GraphDualYAxis = GraphDualYAxis,
                GraphSmoothCurves = GraphSmoothCurves,
                GraphTimeResolution = GetSelectedTimeResolution(),

                FilePath = _fileSessionService.ActiveFilePath ?? string.Empty,

            };
        }

        // ──────────────────────────────────────────────────────────────────────
        //  Helpers
        // ──────────────────────────────────────────────────────────────────────

        private List<string> GetSelectedMeasurements()
        {
            var list = new List<string>();
            if (IncludeTemperature)               list.Add("Temperature");
            if (IncludeRelativeHumidity)          list.Add("RelativeHumidity");
            if (IncludeBarometricPressure)        list.Add("BarometricPressure");
            if (IncludeSolarIrradiance)           list.Add("SolarIrradiance");
            if (IncludeVaporPressureDeficit)      list.Add("VaporPressureDeficit");
            if (IncludeDewPoint)                  list.Add("DewPoint");
            if (IncludeAbsoluteHumidity)          list.Add("AbsoluteHumidity");
            if (IncludeAccumulatedSolarRadiation) list.Add("AccumulatedSolarRadiation");
            if (IncludeDailyLightIntegral)        list.Add("DailyLightIntegral");
            return list;
        }

        private GraphTimeResolution GetSelectedTimeResolution()
        {
            if (GraphTimeResHourly) return GraphTimeResolution.Hourly;
            if (GraphTimeResDaily)  return GraphTimeResolution.Daily;
            if (GraphTimeResWeekly) return GraphTimeResolution.Weekly;
            return GraphTimeResolution.Auto;
        }

        private static double? ParseDouble(string s)
            => double.TryParse(s, out double v) ? v : (double?)null;

        // ──────────────────────────────────────────────────────────────────────
        //  INotifyPropertyChanged
        // ──────────────────────────────────────────────────────────────────────

        public event PropertyChangedEventHandler PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

        /// <summary>Sets the backing field and raises PropertyChanged if the value changed.</summary>
        /// <returns>True when the value actually changed.</returns>
        protected bool SetField<T>(ref T field, T value, [CallerMemberName] string propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            OnPropertyChanged(propertyName);
            return true;
        }
    }
}
