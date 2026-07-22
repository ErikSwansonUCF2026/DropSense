// DropSense — ViewModels/SettingsViewModel.cs
//
// ══════════════════════════════════════════════════════════════════════════════


using DropSense.Services;
using DropSense.Models;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;


namespace DropSense.ViewModels;

// ─────────────────────────────────────────────────────────────────────────────
// Input type enum — controls which controls render for a threshold card
// ─────────────────────────────────────────────────────────────────────────────

public enum ChannelInputType
{
    DualDecimal,   // Min text + Max text + dual-handle range slider
    MaxOnly,       // Max text + single-handle range slider (no min)
    Binary,        // Toggle only — no numeric inputs, fixed 2 °C threshold on wire
}

// ─────────────────────────────────────────────────────────────────────────────
// ThresholdEntry — one card in the threshold section
// ─────────────────────────────────────────────────────────────────────────────

public class ThresholdEntry : BaseViewModel
{
    // ── Identity ──────────────────────────────────────────────────────────────
    public MeasurementChannelByte Channel { get; }
    public string Label { get; }
    public string Unit { get; }
    public ChannelInputType InputType { get; }

    // Physical slider range — used by XAML Slider.Minimum / Maximum
    public double RangeMin { get; }
    public double RangeMax { get; }

    // Per-channel placeholder text shown in Entry when field is empty
    public string PlaceholderMin { get; }
    public string PlaceholderMax { get; }

    // XAML visibility helpers derived from InputType
    public bool HasMinInput => InputType == ChannelInputType.DualDecimal;
    public bool HasMaxInput => InputType == ChannelInputType.DualDecimal ||
                                  InputType == ChannelInputType.MaxOnly;
    public bool IsBinaryChannel => InputType == ChannelInputType.Binary;

    // ── Enable toggle ─────────────────────────────────────────────────────────
    private bool _isEnabled;
    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (SetProperty(ref _isEnabled, value))
            {
                if (!value)
                {
                    SafeMinText = string.Empty;
                    SafeMaxText = string.Empty;
                    // Slider thumb positions reset to range boundaries
                    SliderMin = RangeMin;
                    SliderMax = RangeMax;
                }
                OnPropertyChanged(nameof(IsDisabled));
            }
        }
    }
    public bool IsDisabled => !IsEnabled;

    // ── Text fields (two-way bound in Entry) ──────────────────────────────────
    private string _safeMinText = string.Empty;
    public string SafeMinText
    {
        get => _safeMinText;
        set
        {
            if (SetProperty(ref _safeMinText, value))
            {
                // Keep slider thumb in sync when user types
                if (float.TryParse(value, System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out var f))
                {
                    _sliderMin = Math.Clamp((double)f, RangeMin, RangeMax);
                    OnPropertyChanged(nameof(SliderMin));
                }
                OnPropertyChanged(nameof(HasValidationError));
                OnPropertyChanged(nameof(ValidationError));
            }
        }
    }

    private string _safeMaxText = string.Empty;
    public string SafeMaxText
    {
        get => _safeMaxText;
        set
        {
            if (SetProperty(ref _safeMaxText, value))
            {
                if (float.TryParse(value, System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out var f))
                {
                    _sliderMax = Math.Clamp((double)f, RangeMin, RangeMax);
                    OnPropertyChanged(nameof(SliderMax));
                }
                OnPropertyChanged(nameof(HasValidationError));
                OnPropertyChanged(nameof(ValidationError));
            }
        }
    }

    // ── Slider positions (two-way bound in Slider) ────────────────────────────
    // Stored separately so the slider can update without re-triggering text
    // parse on every tiny movement. Text ← Slider sync is intentional (user
    // drags slider → text updates); Text → Slider sync is done in the setters
    // above (user types → slider moves).

    private double _sliderMin;
    public double SliderMin
    {
        get => _sliderMin;
        set
        {
            if (SetProperty(ref _sliderMin, value))
            {
                // Keep text in sync when slider moves, formatted to 1 decimal
                var formatted = value.ToString("F1", System.Globalization.CultureInfo.InvariantCulture);
                if (_safeMinText != formatted)
                {
                    _safeMinText = formatted;
                    OnPropertyChanged(nameof(SafeMinText));
                }
                OnPropertyChanged(nameof(HasValidationError));
                OnPropertyChanged(nameof(ValidationError));
            }
        }
    }

    private double _sliderMax;
    public double SliderMax
    {
        get => _sliderMax;
        set
        {
            if (SetProperty(ref _sliderMax, value))
            {
                var formatted = value.ToString("F1", System.Globalization.CultureInfo.InvariantCulture);
                if (_safeMaxText != formatted)
                {
                    _safeMaxText = formatted;
                    OnPropertyChanged(nameof(SafeMaxText));
                }
                OnPropertyChanged(nameof(HasValidationError));
                OnPropertyChanged(nameof(ValidationError));
            }
        }
    }

    // ── Computed float? properties — used only by ToThresholdSetting() ────────
    public float? SafeMin =>
        float.TryParse(SafeMinText, System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : null;

    public float? SafeMax =>
        float.TryParse(SafeMaxText, System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : null;

    // ── Validation ────────────────────────────────────────────────────────────
    public bool HasValidationError =>
        IsEnabled && HasMinInput && HasMaxInput &&
        SafeMin.HasValue && SafeMax.HasValue &&
        SafeMin.Value >= SafeMax.Value;

    public string ValidationError =>
        HasValidationError ? "Safe Min must be less than Safe Max" : string.Empty;

    // ── Constructor ───────────────────────────────────────────────────────────
    public ThresholdEntry(
        MeasurementChannelByte channel,
        string label,
        string unit,
        double rangeMin,
        double rangeMax,
        ChannelInputType inputType = ChannelInputType.DualDecimal,
        string placeholderMin = "",
        string placeholderMax = "")
    {
        Channel = channel;
        Label = label;
        Unit = unit;
        RangeMin = rangeMin;
        RangeMax = rangeMax;
        InputType = inputType;
        PlaceholderMin = placeholderMin;
        PlaceholderMax = placeholderMax;

        // Initialise slider positions to range boundaries
        _sliderMin = rangeMin;
        _sliderMax = rangeMax;
    }

    // ── Wire serialisation ────────────────────────────────────────────────────
    internal ThresholdSetting? ToThresholdSetting()
    {
        if (!IsEnabled) return null;

        // Binary channel (DewPoint / Condensation Risk):
        // The wire value is always SafeMax = 2.0f — alert when temperature
        // is within 2 °C of the dew point. No user-configurable limits.
        if (IsBinaryChannel)
        {
            return new ThresholdSetting
            {
                Channel = Channel,
                SafeMin = null,
                SafeMax = 2.0f,
            };
        }

        return new ThresholdSetting
        {
            Channel = Channel,
            SafeMin = HasMinInput ? SafeMin : null,
            SafeMax = HasMaxInput ? SafeMax : null,
        };
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// SettingsViewModel
// ─────────────────────────────────────────────────────────────────────────────

public class SettingsViewModel : BaseViewModel
{
    private readonly IDeviceConnectionService _connectionService;
    private readonly ISettingsService _settings;
    private readonly INavigationService _nav;
    private readonly IAlertService _alertService;
    private readonly IPlantLibraryService _plantLibrary;

    /// <summary>
    /// Overall time budget for a single "Send Settings" attempt, covering
    /// connect + service/characteristic resolution + write + ACK wait. Passed
    /// down as a CancellationToken to IDeviceConnectionService.SendSettingsAsync
    /// so the whole operation is bounded end-to-end from the UI's perspective —
    /// without this, the service is called with CancellationToken.None (which
    /// never fires), and a stalled lock acquisition or native BLE call anywhere
    /// in the chain would leave IsBusy stuck true indefinitely with no way for
    /// the user to recover short of restarting the app.
    /// </summary>
    private static readonly TimeSpan SendSettingsTimeout = TimeSpan.FromSeconds(20);

    public ObservableCollection<string> ExceptionLog { get; } = new();

    // ── Sentinel "Custom" entry ─────────────────────────────────────────────
    // Always first in PlantOptions. Selecting it means "manual mode" — the
    // threshold cards are left exactly as they are (whatever the user has
    // typed, or whatever was restored from persisted preferences) and no
    // plant data is applied over them.
    public static readonly Plant CustomPlantOption = new()
    {
        PlantId = -1,
        CommonName = "Custom",
        Notes = "Manually configured thresholds"
    };

    // Guards against re-entrancy when we programmatically set SelectedPlant
    // (e.g. on initial load or from ResetDefaults) so we don't accidentally
    // re-run the plant-apply logic against a plant that isn't really "selected"
    // by the user.
    private bool _suppressPlantSelectionHandling;

    public SettingsViewModel(
        IDeviceConnectionService connectionService,
        ISettingsService settings,
        INavigationService nav,
        IAlertService alertService,
        IPlantLibraryService plantLibrary)
    {
        _connectionService = connectionService;
        _settings = settings;
        _nav = nav;
        _alertService = alertService;
        _plantLibrary = plantLibrary;
        // ── Threshold collection ──────────────────────────────────────────────
        // Arguments: channel, label, unit, rangeMin, rangeMax,
        //            inputType, placeholderMin, placeholderMax
        Thresholds = new ObservableCollection<ThresholdEntry>
        {
            new(MeasurementChannelByte.Temperature,
                "Temperature", "°C", -40, 85,
                ChannelInputType.DualDecimal,
                placeholderMin: "0",     // 0 °C
                placeholderMax: "45"),   // 45 °C

            new(MeasurementChannelByte.Humidity,
                "Humidity", "%", 0, 100,
                ChannelInputType.DualDecimal,
                placeholderMin: "30",    // 30 %
                placeholderMax: ""),     // no max placeholder

            new(MeasurementChannelByte.Pressure,
                "Pressure", "hPa", 800, 1100,
                ChannelInputType.DualDecimal,
                placeholderMin: "",      // no placeholders
                placeholderMax: ""),

            new(MeasurementChannelByte.Irradiance,
                "Light Stress", "W/m²", 0, 1500,   // renamed from "Irradiance"
                ChannelInputType.MaxOnly,            // max only
                placeholderMin: "",
                placeholderMax: "800"),              // 800 W/m²

            new(MeasurementChannelByte.VPD,
                "VPD", "hPa", 0, 30,
                ChannelInputType.DualDecimal,
                placeholderMin: "3",     // 3 hPa
                placeholderMax: "30"),   // 30 hPa

            new(MeasurementChannelByte.DewPoint,
                "Condensation Risk", "", 0, 2,
                ChannelInputType.Binary),            // toggle only
        };

        SendSettingsCommand = new Command(async () => await SendSettingsAsync(), () => !IsBusy);
        ResetDefaultsCommand = new Command(ResetDefaults);
        NavigateBackCommand = new Command(async () => await _nav.NavigateToAsync("//DashboardPage"));

        _connectionService.ConnectionStateChanged += (_, state) =>
        {
            OnPropertyChanged(nameof(IsDeviceConnected));
            OnPropertyChanged(nameof(ConnectionStatusText));
            OnPropertyChanged(nameof(ConnectionStatusColor));
            OnPropertyChanged(nameof(HasDeviceName));
            OnPropertyChanged(nameof(LastDeviceName));
            ((Command)SendSettingsCommand).ChangeCanExecute();
        };

        RestorePersistedValues();

        // Custom always sits first; it's the default selection until the
        // plant library finishes loading (or if loading fails).
        PlantOptions.Add(CustomPlantOption);
        _selectedPlant = CustomPlantOption;

        _ = LoadPlantsAsync();
    }

    // ── Plant selection ───────────────────────────────────────────────────────
    public ObservableCollection<Plant> PlantOptions { get; } = new();

    private Plant? _selectedPlant;
    public Plant? SelectedPlant
    {
        get => _selectedPlant;
        set
        {
            if (SetProperty(ref _selectedPlant, value))
            {
                if (_suppressPlantSelectionHandling || value is null) return;

                if (value.PlantId == CustomPlantOption.PlantId)
                {
                    // "Custom" — leave whatever is currently in the cards alone.
                    return;
                }

                ApplyPlantThresholds(value);
            }
        }
    }

    private async Task LoadPlantsAsync()
    {
        try
        {
            var plants = await _plantLibrary.GetAllPlantsAsync();
            foreach (var plant in plants)
                PlantOptions.Add(plant);
        }
        catch (Exception ex)
        {
            ShowStatus("Couldn't load the plant library. You can still configure thresholds manually.", true);
            System.Diagnostics.Debug.WriteLine($"[Settings] Plant library load failed: {ex}");
        }
    }

    /// <summary>
    /// Applies a plant's stored thresholds to the threshold cards. Any channel
    /// the plant doesn't have data for (or where the stored entry carries no
    /// usable min/max at all) falls back to the standard default state —
    /// disabled, with the fields cleared — the same state ResetDefaults leaves
    /// a channel in.
    /// </summary>
    private void ApplyPlantThresholds(Plant plant)
    {
        foreach (var entry in Thresholds)
        {
            var libraryChannel = ToLibraryChannel(entry.Channel);
            var match = libraryChannel.HasValue
                ? plant.storedThresholds.FirstOrDefault(t => t.libChannel == libraryChannel.Value)
                : null;

            var min = match?.SafeMin ?? match?.IdealMin;
            var max = match?.SafeMax ?? match?.IdealMax;

            var hasUsableData = match is not null && (min.HasValue || max.HasValue);

            if (hasUsableData)
            {
                entry.IsEnabled = true;

                if (!entry.IsBinaryChannel)
                {
                    entry.SafeMinText = entry.HasMinInput && min.HasValue
                        ? min.Value.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)
                        : string.Empty;

                    entry.SafeMaxText = entry.HasMaxInput && max.HasValue
                        ? max.Value.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)
                        : string.Empty;
                }
            }
            else
            {
                // No data for this channel on this plant — fall back to defaults.
                entry.IsEnabled = false;
                entry.SafeMinText = string.Empty;
                entry.SafeMaxText = string.Empty;
            }
        }
    }

    /// <summary>
    /// Maps the wire-facing MeasurementChannelByte used by ThresholdEntry to the
    /// MeasurementChannel used by IPlantLibraryService/LibraryThreshold. The two
    /// enums use different member names for the same concepts (e.g. Humidity vs
    /// RelativeHumidity), so this is an explicit lookup rather than a name or
    /// numeric cast. Channels that exist on MeasurementChannel but have no UI
    /// card (AbsoluteHumidity, AccumulatedSolarRadiation, DailyLightIntegral,
    /// EstimatedPAR) are simply never a target here — they stay unlisted.
    /// Returns null for anything unmapped, so the caller treats it the same as
    /// "no plant data available" rather than throwing.
    /// </summary>
    private static MeasurementChannel? ToLibraryChannel(MeasurementChannelByte channel) => channel switch
    {
        MeasurementChannelByte.Temperature => MeasurementChannel.Temperature,
        MeasurementChannelByte.Humidity => MeasurementChannel.RelativeHumidity,
        MeasurementChannelByte.Pressure => MeasurementChannel.BarometricPressure,
        MeasurementChannelByte.Irradiance => MeasurementChannel.SolarIrradiance,
        MeasurementChannelByte.VPD => MeasurementChannel.VaporPressureDeficit,
        MeasurementChannelByte.DewPoint => MeasurementChannel.DewPointTemperature,
        _ => null
    };

    // ── Timing ────────────────────────────────────────────────────────────────

    private string _measurementIntervalText = "60";
    public string MeasurementIntervalText
    {
        get => _measurementIntervalText;
        set { if (SetProperty(ref _measurementIntervalText, value)) OnPropertyChanged(nameof(MeasurementIntervalValid)); }
    }
    public bool MeasurementIntervalValid =>
        ushort.TryParse(MeasurementIntervalText, out var v) && v >= 1;
    public ushort MeasurementIntervalSeconds =>
        ushort.TryParse(MeasurementIntervalText, out var v) && v >= 1 ? v : (ushort)60;

    private string _alertCheckIntervalText = "300";
    public string AlertCheckIntervalText
    {
        get => _alertCheckIntervalText;
        set { if (SetProperty(ref _alertCheckIntervalText, value)) OnPropertyChanged(nameof(AlertCheckIntervalValid)); }
    }
    public bool AlertCheckIntervalValid =>
        ushort.TryParse(AlertCheckIntervalText, out var v) && v >= 1;
    public ushort AlertCheckIntervalSeconds =>
        ushort.TryParse(AlertCheckIntervalText, out var v) && v >= 1 ? v : (ushort)300;

    // ── Auto Start ─────────────────────────────────────────────────────────
    private bool _autoStart;
    public bool AutoStart { get => _autoStart; set => SetProperty(ref _autoStart, value); }

    // ── Thresholds ────────────────────────────────────────────────────────────
    public ObservableCollection<ThresholdEntry> Thresholds { get; }

    // ── Responsive column count ───────────────────────────────────────────────
    // Set by SettingsView.xaml.cs when the ContentView's SizeChanged fires.
    // 1 = narrow (single column), 2 = wide (two columns side by side).
    // Breakpoint: 700 logical pixels.
    private int _thresholdColumns = 1;
    public int ThresholdColumns
    {
        get => _thresholdColumns;
        set
        {
            // Clamp to allowed range: 1–2
            var clamped = Math.Clamp(value, 1, 2);

            if (SetProperty(ref _thresholdColumns, clamped))
                OnPropertyChanged(nameof(ThresholdCardWidthFraction));
        }
    }

    // FlexLayout children read this to set their WidthRequest.
    // The XAML code-behind computes the actual pixel value from this + container width.
    // 1 col → fraction 1.0, 2 cols → fraction 0.5
    public double ThresholdCardWidthFraction => ThresholdColumns == 2 ? 0.5 : 1.0;

    // ── Busy / status ─────────────────────────────────────────────────────────
    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        set { if (SetProperty(ref _isBusy, value)) { OnPropertyChanged(nameof(IsNotBusy)); ((Command)SendSettingsCommand).ChangeCanExecute(); } }
    }
    public bool IsNotBusy => !IsBusy;

    private string _statusMessage = string.Empty;
    public string StatusMessage
    {
        get => _statusMessage;
        set
        {
            if (SetProperty(ref _statusMessage, value))
            {
                OnPropertyChanged(nameof(HasStatusMessage));
                OnPropertyChanged(nameof(ShowSuccessBanner));
                OnPropertyChanged(nameof(ShowErrorBanner));
            }
        }
    }

    private bool _statusIsError;
    public bool StatusIsError
    {
        get => _statusIsError;
        set
        {
            if (SetProperty(ref _statusIsError, value))
            {
                OnPropertyChanged(nameof(ShowSuccessBanner));
                OnPropertyChanged(nameof(ShowErrorBanner));
            }
        }
    }

    public bool HasStatusMessage => !string.IsNullOrEmpty(StatusMessage);
    public bool ShowSuccessBanner => HasStatusMessage && !StatusIsError;
    public bool ShowErrorBanner => HasStatusMessage && StatusIsError;

    // ── Connection ────────────────────────────────────────────────────────────
    public bool IsDeviceConnected => _connectionService.State == ConnectionState.Connected;
    public string ConnectionStatusText => _connectionService.State switch
    {
        ConnectionState.Connected => $"● {_connectionService.ConnectedDeviceName}",
        ConnectionState.Connecting => "Connecting…",
        ConnectionState.Transferring => "Sending…",
        ConnectionState.Error => "Connection error",
        _ => "Not connected — will connect when sent"
    };
    public Color ConnectionStatusColor =>
        _connectionService.State == ConnectionState.Connected ? Colors.LimeGreen : Colors.OrangeRed;

    public string LastDeviceName => _settings.LastConnectedDeviceName ?? string.Empty;
    public bool HasDeviceName => !string.IsNullOrWhiteSpace(_settings.LastConnectedDeviceName);

    // ── Commands ──────────────────────────────────────────────────────────────
    public ICommand SendSettingsCommand { get; }
    public ICommand ResetDefaultsCommand { get; }
    public ICommand NavigateBackCommand { get; }

    // ── SendSettingsAsync ─────────────────────────────────────────────────────
    private async Task SendSettingsAsync()
    {
        if (!MeasurementIntervalValid) { ShowStatus("Measurement interval must be a whole number ≥ 1.", true); return; }
        if (!AlertCheckIntervalValid) { ShowStatus("Alert check interval must be a whole number ≥ 1.", true); return; }
        System.Diagnostics.Debug.WriteLine($"AutoStart: {AutoStart}");

        var invalid = Thresholds.FirstOrDefault(t => t.HasValidationError);
        if (invalid is not null) { ShowStatus($"{invalid.Label}: Safe Min must be less than Safe Max.", true); return; }

        IsBusy = true;
        StatusMessage = string.Empty;

        // Bound the whole send attempt end-to-end. Without a real token here,
        // IDeviceConnectionService.SendSettingsAsync defaults to
        // CancellationToken.None, which can never fire — meaning a stuck lock
        // or a wedged native BLE call anywhere in the connect/write/ACK chain
        // would leave IsBusy stuck true forever with no way for the user to
        // recover other than restarting the app.
        using var sendCts = new CancellationTokenSource(SendSettingsTimeout);

        try
        {
            var thresholds = Thresholds
                .Select(t => t.ToThresholdSetting())
                .Where(t => t is not null)
                .Cast<ThresholdSetting>()
                .ToList();

            var deviceSettings = new DeviceSettings
            {
                MeasurementIntervalSeconds = MeasurementIntervalSeconds,
                AlertCheckIntervalSeconds = AlertCheckIntervalSeconds,
                Thresholds = thresholds,
                AutoStartEnabled = AutoStart
            };

            await _connectionService.SendSettingsAsync(deviceSettings, stayConnected: false, sendCts.Token);
            if (AutoStart)
            {

                // StartAlertPollingAsync returns a CancellationTokenSource (not a Task),
                // so do not await it. Call it to start polling and ignore or store
                // the returned CancellationTokenSource if you need to cancel later.
                _connectionService.StartAlertPollingAsync(AlertCheckIntervalSeconds, _alertService);
            }
            PersistCurrentValues();
            ShowStatus("Settings sent successfully.", false);
        }
        catch (InvalidOperationException ex) { ShowStatus($"Device rejected settings: {ex.Message}", true); System.Diagnostics.Debug.WriteLine($"[Settings] NACK: {ex}"); }
        catch (InvalidDataException ex) { ShowStatus($"Send failed: {ex.Message}", true); System.Diagnostics.Debug.WriteLine($"[Settings] Data: {ex}"); }
        catch (TimeoutException ex) { ShowStatus($"Couldn't reach the device: {ex.Message}", true); System.Diagnostics.Debug.WriteLine($"[Settings] Timeout: {ex}"); }
        catch (OperationCanceledException) when (sendCts.IsCancellationRequested)
        {
            ShowStatus($"Sending settings took too long (over {SendSettingsTimeout.TotalSeconds:0}s) and was cancelled. Check the device is powered on and in range, then try again.", true);
            System.Diagnostics.Debug.WriteLine("[Settings] SendSettingsAsync cancelled — overall timeout elapsed.");
        }
        catch (OperationCanceledException) { ShowStatus("Cancelled.", false); }
        catch (Exception ex) { ShowStatus("Failed to send settings. Check connection and retry.", true); System.Diagnostics.Debug.WriteLine($"[Settings] {ex}"); }
        finally { IsBusy = false; }
    }

    // ── ResetDefaults ─────────────────────────────────────────────────────────
    private void ResetDefaults()
    {
        MeasurementIntervalText = "60";
        AlertCheckIntervalText = "300";
        AutoStart = false;
        StatusMessage = string.Empty;

        foreach (var t in Thresholds)
        {
            t.IsEnabled = false;
            t.SafeMinText = string.Empty;
            t.SafeMaxText = string.Empty;
        }

        // Reset Defaults always restores the dropdown to "Custom" too — the
        // thresholds it just cleared ARE the custom/default state, so the
        // selector should reflect that rather than continuing to show a
        // plant whose values no longer match what's on screen.
        _suppressPlantSelectionHandling = true;
        SelectedPlant = CustomPlantOption;
        _suppressPlantSelectionHandling = false;
    }

    // ── Persistence ───────────────────────────────────────────────────────────
    private void PersistCurrentValues()
    {
        Preferences.Set("settings_measurement_interval", MeasurementIntervalText);
        Preferences.Set("settings_alert_interval", AlertCheckIntervalText);
        Preferences.Set("settings_auto_start", AutoStart);
        foreach (var t in Thresholds)
        {
            var k = $"threshold_{t.Channel}";
            Preferences.Set($"{k}_enabled", t.IsEnabled);
            Preferences.Set($"{k}_min", t.SafeMinText);
            Preferences.Set($"{k}_max", t.SafeMaxText);
        }
    }

    private void RestorePersistedValues()
    {
        MeasurementIntervalText = Preferences.Get("settings_measurement_interval", "60");
        AlertCheckIntervalText = Preferences.Get("settings_alert_interval", "300");
        Preferences.Set("settings_auto_start", AutoStart);
        foreach (var t in Thresholds)
        {
            var k = $"threshold_{t.Channel}";
            t.IsEnabled = Preferences.Get($"{k}_enabled", false);
            t.SafeMinText = Preferences.Get($"{k}_min", string.Empty);
            t.SafeMaxText = Preferences.Get($"{k}_max", string.Empty);
        }
    }

    private void ShowStatus(string message, bool isError)
    {
        StatusMessage = message;
        StatusIsError = isError;
    }

    public void OnWidthChanged(double width)
    {
        // Same 2-column breakpoint as before, now driven automatically —
        // delete the SizeChanged wiring that used to live in SettingsView.xaml.cs.
        ThresholdColumns = width < TabletBreakpointForThresholds ? 1 : 2;
    }

    const double TabletBreakpointForThresholds = 700;
}