// DropSense — ViewModels/PlantLibraryViewModel.cs
// ══════════════════════════════════════════════════════════════════════════════
// Drives the Plant Library page.  Handles:
//   • First-load initialisation (lazy, fired on first navigation to the page)
//   • Plant list CRUD
//   • Per-plant Threshold CRUD via an inline editor panel
//   • Status banners (success / error) with auto-dismiss

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using DropSense.Models;
using DropSense.Services;

namespace DropSense.ViewModels
{
    // ── Flat threshold row used by the UI editor ──────────────────────────────
    public class ThresholdEditorRow : INotifyPropertyChanged
    {
        public MeasurementChannel Channel   { get; init; }
        public string             Label     { get; init; } = string.Empty;
        public string             Unit      { get; init; } = string.Empty;

        // Editable text fields (nullable floats as strings)
        private string _idealMinText = string.Empty;
        private string _idealMaxText = string.Empty;
        private string _safeMinText  = string.Empty;
        private string _safeMaxText  = string.Empty;
        private bool   _isEnabled;

        public string IdealMinText { get => _idealMinText; set { _idealMinText = value; OnPropertyChanged(); } }
        public string IdealMaxText { get => _idealMaxText; set { _idealMaxText = value; OnPropertyChanged(); } }
        public string SafeMinText  { get => _safeMinText;  set { _safeMinText  = value; OnPropertyChanged(); } }
        public string SafeMaxText  { get => _safeMaxText;  set { _safeMaxText  = value; OnPropertyChanged(); } }

        /// <summary>True = row has at least one threshold value saved.</summary>
        public bool IsEnabled { get => _isEnabled; set { _isEnabled = value; OnPropertyChanged(); } }

        // Populate from a persisted Threshold (or leave blank if not yet set)
        public void LoadFrom(LibraryThreshold? t)
        {
            if (t is null)
            {
                IdealMinText = IdealMaxText = SafeMinText = SafeMaxText = string.Empty;
                IsEnabled = false;
                return;
            }
            IdealMinText = t.IdealMin?.ToString("G") ?? string.Empty;
            IdealMaxText = t.IdealMax?.ToString("G") ?? string.Empty;
            SafeMinText  = t.SafeMin?.ToString("G")  ?? string.Empty;
            SafeMaxText  = t.SafeMax?.ToString("G")  ?? string.Empty;
            IsEnabled    = true;
        }

        // Build a Threshold model from current text values
        public LibraryThreshold ToThreshold() => new()
        {
            libChannel  = Channel,
            Unit     = Unit,
            IdealMin = ParseNullableFloat(IdealMinText),
            IdealMax = ParseNullableFloat(IdealMaxText),
            SafeMin  = ParseNullableFloat(SafeMinText),
            SafeMax  = ParseNullableFloat(SafeMaxText),
        };

        private static float? ParseNullableFloat(string s)
            => float.TryParse(s, System.Globalization.NumberStyles.Any,
                              System.Globalization.CultureInfo.InvariantCulture, out var v)
               ? v : null;

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? n = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }

    // ── Main ViewModel ────────────────────────────────────────────────────────
    public class PlantLibraryViewModel : INotifyPropertyChanged
    {
        private readonly IPlantLibraryService _svc;

        // ── Static channel metadata ───────────────────────────────────────────
        private static readonly (MeasurementChannel Channel, string Label, string Unit)[] ChannelMeta =
        {
            (MeasurementChannel.Temperature,               "Temperature",               "°C"),
            (MeasurementChannel.RelativeHumidity,          "Relative Humidity",         "%"),
            (MeasurementChannel.BarometricPressure,        "Barometric Pressure",       "hPa"),
            (MeasurementChannel.SolarIrradiance,           "Solar Irradiance",          "W/m²"),
            (MeasurementChannel.VaporPressureDeficit,      "Vapour Pressure Deficit",   "kPa"),
            (MeasurementChannel.DewPointTemperature,       "Dew Point",                 "°C"),
            (MeasurementChannel.AbsoluteHumidity,          "Absolute Humidity",         "g/m³"),
            (MeasurementChannel.AccumulatedSolarRadiation, "Accum. Solar Radiation",    "MJ/m²"),
            (MeasurementChannel.DailyLightIntegral,        "Daily Light Integral",      "mol/m²/d"),
            (MeasurementChannel.EstimatedPAR,              "Estimated PAR",             "µmol/m²/s"),
        };

        // ── Observable state ──────────────────────────────────────────────────
        public ObservableCollection<Plant>              Plants          { get; } = new();
        public ObservableCollection<ThresholdEditorRow> ThresholdRows   { get; } = new();

        // ── Busy / banner ─────────────────────────────────────────────────────
        private bool   _isBusy;
        private string _statusMessage    = string.Empty;
        private bool   _showSuccessBanner;
        private bool   _showErrorBanner;

        public bool   IsBusy             { get => _isBusy;            private set { _isBusy = value; OnPropertyChanged(); } }
        public string StatusMessage      { get => _statusMessage;     private set { _statusMessage = value; OnPropertyChanged(); } }
        public bool   ShowSuccessBanner  { get => _showSuccessBanner; private set { _showSuccessBanner = value; OnPropertyChanged(); } }
        public bool   ShowErrorBanner    { get => _showErrorBanner;   private set { _showErrorBanner = value; OnPropertyChanged(); } }

        // ── Plant form fields ─────────────────────────────────────────────────
        private string _newCommonName     = string.Empty;
        private string _newScientificName = string.Empty;
        private string _newNotes          = string.Empty;

        public string NewCommonName     { get => _newCommonName;     set { _newCommonName     = value; OnPropertyChanged(); } }
        public string NewScientificName { get => _newScientificName; set { _newScientificName = value; OnPropertyChanged(); } }
        public string NewNotes          { get => _newNotes;          set { _newNotes          = value; OnPropertyChanged(); } }

        // ── Selected plant (drives the threshold editor panel) ────────────────
        private Plant? _selectedPlant;
        public  Plant? SelectedPlant
        {
            get => _selectedPlant;
            set
            {
                _selectedPlant = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsPlantSelected));
                OnPropertyChanged(nameof(IsEditingPlant));
                _ = LoadThresholdRowsAsync();
            }
        }

        public bool IsPlantSelected => _selectedPlant is not null;

        // ── Edit-plant in-place ───────────────────────────────────────────────
        private bool   _isEditingPlant;
        private string _editCommonName     = string.Empty;
        private string _editScientificName = string.Empty;
        private string _editNotes          = string.Empty;

        public bool   IsEditingPlant    { get => _isEditingPlant;    set { _isEditingPlant    = value; OnPropertyChanged(); } }
        public string EditCommonName    { get => _editCommonName;    set { _editCommonName    = value; OnPropertyChanged(); } }
        public string EditScientificName{ get => _editScientificName;set { _editScientificName= value; OnPropertyChanged(); } }
        public string EditNotes         { get => _editNotes;         set { _editNotes         = value; OnPropertyChanged(); } }

        // ── Init guard ────────────────────────────────────────────────────────
        private bool _initialized;

        // ── Commands ──────────────────────────────────────────────────────────
        public ICommand InitializeCommand         { get; }
        public ICommand AddPlantCommand           { get; }
        public ICommand SelectPlantCommand        { get; }
        public ICommand BeginEditPlantCommand     { get; }
        public ICommand SaveEditPlantCommand      { get; }
        public ICommand CancelEditPlantCommand    { get; }
        public ICommand DeletePlantCommand        { get; }
        public ICommand SaveThresholdsCommand     { get; }
        public ICommand DeleteThresholdCommand    { get; }

        // ── Constructor ───────────────────────────────────────────────────────
        public PlantLibraryViewModel(IPlantLibraryService plantLibraryService)
        {
            _svc = plantLibraryService;

            InitializeCommand      = new AsyncCommand(InitializeAsync);
            AddPlantCommand        = new AsyncCommand(AddPlantAsync);
            SelectPlantCommand     = new AsyncCommand<Plant>(SelectPlantAsync);
            BeginEditPlantCommand  = new RelayCommand(BeginEditPlant,  () => IsPlantSelected);
            SaveEditPlantCommand   = new AsyncCommand(SaveEditPlantAsync);
            CancelEditPlantCommand = new RelayCommand(CancelEditPlant);
            DeletePlantCommand     = new AsyncCommand<Plant>(DeletePlantAsync);
            SaveThresholdsCommand  = new AsyncCommand(SaveThresholdsAsync, () => IsPlantSelected);
            DeleteThresholdCommand = new AsyncCommand<ThresholdEditorRow>(DeleteThresholdAsync);
        }

        // ═════════════════════════════════════════════════════════════════════
        //  Initialise (lazy – called once by the view's Appearing event)
        // ═════════════════════════════════════════════════════════════════════
        public async Task InitializeAsync()
        {
            if (_initialized) return;
            _initialized = true;
            await RefreshPlantsAsync();
        }

        // ═════════════════════════════════════════════════════════════════════
        //  Plant CRUD
        // ═════════════════════════════════════════════════════════════════════
        private async Task RefreshPlantsAsync()
        {
            IsBusy = true;
            try
            {
                var all = await _svc.GetAllPlantsAsync();
                Plants.Clear();
                foreach (var p in all) Plants.Add(p);
            }
            catch (Exception ex) { ShowError($"Failed to load plants: {ex.Message}"); }
            finally { IsBusy = false; }
        }

        private async Task AddPlantAsync()
        {
            if (string.IsNullOrWhiteSpace(NewCommonName)) { ShowError("Common name is required."); return; }
            IsBusy = true;
            try
            {
                var plant = await _svc.AddPlantAsync(new Plant
                {
                    CommonName     = NewCommonName.Trim(),
                    ScientificName = string.IsNullOrWhiteSpace(NewScientificName) ? null : NewScientificName.Trim(),
                    Notes          = string.IsNullOrWhiteSpace(NewNotes)          ? null : NewNotes.Trim(),
                });
                Plants.Add(plant);
                NewCommonName = NewScientificName = NewNotes = string.Empty;
                ShowSuccess($"'{plant.CommonName}' added to the library.");
            }
            catch (Exception ex) { ShowError(ex.Message); }
            finally { IsBusy = false; }
        }

        private Task SelectPlantAsync(Plant? plant)
        {
            SelectedPlant = plant;
            IsEditingPlant = false;
            return Task.CompletedTask;
        }

        private void BeginEditPlant()
        {
            if (SelectedPlant is null) return;
            EditCommonName     = SelectedPlant.CommonName;
            EditScientificName = SelectedPlant.ScientificName ?? string.Empty;
            EditNotes          = SelectedPlant.Notes          ?? string.Empty;
            IsEditingPlant     = true;
        }

        private async Task SaveEditPlantAsync()
        {
            if (SelectedPlant is null) return;
            if (string.IsNullOrWhiteSpace(EditCommonName)) { ShowError("Common name is required."); return; }
            IsBusy = true;
            try
            {
                SelectedPlant.CommonName     = EditCommonName.Trim();
                SelectedPlant.ScientificName = string.IsNullOrWhiteSpace(EditScientificName) ? null : EditScientificName.Trim();
                SelectedPlant.Notes          = string.IsNullOrWhiteSpace(EditNotes)          ? null : EditNotes.Trim();

                await _svc.UpdatePlantAsync(SelectedPlant);

                // Refresh the list entry
                var idx = Plants.IndexOf(SelectedPlant);
                if (idx >= 0) { Plants[idx] = SelectedPlant; SelectedPlant = Plants[idx]; }

                IsEditingPlant = false;
                ShowSuccess("Plant updated.");
            }
            catch (Exception ex) { ShowError(ex.Message); }
            finally { IsBusy = false; }
        }

        private void CancelEditPlant() => IsEditingPlant = false;

        private async Task DeletePlantAsync(Plant? plant)
        {
            if (plant is null) return;
            IsBusy = true;
            try
            {
                await _svc.DeletePlantAsync(plant.PlantId);
                Plants.Remove(plant);
                if (SelectedPlant?.PlantId == plant.PlantId) SelectedPlant = null;
                ShowSuccess($"'{plant.CommonName}' removed.");
            }
            catch (Exception ex) { ShowError(ex.Message); }
            finally { IsBusy = false; }
        }

        // ═════════════════════════════════════════════════════════════════════
        //  Threshold rows
        // ═════════════════════════════════════════════════════════════════════
        private async Task LoadThresholdRowsAsync()
        {
            ThresholdRows.Clear();
            if (SelectedPlant is null) return;

            var saved = await _svc.GetThresholdsAsync(SelectedPlant.PlantId);
            var savedMap = saved.ToDictionary(t => t.libChannel);

            foreach (var (ch, label, unit) in ChannelMeta)
            {
                var row = new ThresholdEditorRow { Channel = ch, Label = label, Unit = unit };
                row.LoadFrom(savedMap.TryGetValue(ch, out var t) ? t : null);
                ThresholdRows.Add(row);
            }
        }

        private async Task SaveThresholdsAsync()
        {
            if (SelectedPlant is null) return;
            IsBusy = true;
            try
            {
                foreach (var row in ThresholdRows)
                {
                    var t     = row.ToThreshold();
                    bool hasData = t.IdealMin.HasValue || t.IdealMax.HasValue ||
                                   t.SafeMin.HasValue  || t.SafeMax.HasValue;

                    var existing = await _svc.GetThresholdAsync(SelectedPlant.PlantId, row.Channel);

                    if (hasData && existing is null)
                        await _svc.AddThresholdAsync(SelectedPlant.PlantId, t);
                    else if (hasData && existing is not null)
                        await _svc.UpdateThresholdAsync(SelectedPlant.PlantId, t);
                    else if (!hasData && existing is not null)
                        await _svc.DeleteThresholdAsync(SelectedPlant.PlantId, row.Channel);
                    // else: no data and no record — nothing to do
                }
                await LoadThresholdRowsAsync();
                ShowSuccess("Thresholds saved.");
            }
            catch (Exception ex) { ShowError(ex.Message); }
            finally { IsBusy = false; }
        }

        private async Task DeleteThresholdAsync(ThresholdEditorRow? row)
        {
            if (row is null || SelectedPlant is null) return;
            IsBusy = true;
            try
            {
                var existing = await _svc.GetThresholdAsync(SelectedPlant.PlantId, row.Channel);
                if (existing is not null)
                    await _svc.DeleteThresholdAsync(SelectedPlant.PlantId, row.Channel);

                row.LoadFrom(null);
                ShowSuccess($"{row.Label} threshold cleared.");
            }
            catch (Exception ex) { ShowError(ex.Message); }
            finally { IsBusy = false; }
        }

        // ═════════════════════════════════════════════════════════════════════
        //  Banner helpers
        // ═════════════════════════════════════════════════════════════════════
        private CancellationTokenSource? _bannerCts;

        private void ShowSuccess(string msg) => ShowBanner(msg, isError: false);
        private void ShowError(string msg)   => ShowBanner(msg, isError: true);

        private void ShowBanner(string msg, bool isError)
        {
            _bannerCts?.Cancel();
            _bannerCts = new CancellationTokenSource();

            StatusMessage     = msg;
            ShowSuccessBanner = !isError;
            ShowErrorBanner   = isError;

            var token = _bannerCts.Token;
            Task.Delay(3500, token).ContinueWith(_ =>
            {
                if (token.IsCancellationRequested) return;
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    ShowSuccessBanner = ShowErrorBanner = false;
                });
            }, TaskScheduler.Default);
        }

        // ═════════════════════════════════════════════════════════════════════
        //  INotifyPropertyChanged
        // ═════════════════════════════════════════════════════════════════════
        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? n = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }

    // ── Minimal command helpers ───────────────────────────────────────────────
    internal sealed class RelayCommand : ICommand
    {
        private readonly Action       _execute;
        private readonly Func<bool>?  _canExecute;
        public RelayCommand(Action execute, Func<bool>? canExecute = null) { _execute = execute; _canExecute = canExecute; }
        public bool CanExecute(object? p) => _canExecute?.Invoke() ?? true;
        public void Execute(object? p)    => _execute();
        public event EventHandler? CanExecuteChanged;
        public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }

    internal sealed class AsyncCommand : ICommand
    {
        private readonly Func<Task>   _execute;
        private readonly Func<bool>?  _canExecute;
        private bool _running;
        public AsyncCommand(Func<Task> execute, Func<bool>? canExecute = null) { _execute = execute; _canExecute = canExecute; }
        public bool CanExecute(object? p) => !_running && (_canExecute?.Invoke() ?? true);
        public async void Execute(object? p)
        {
            if (_running) return;
            _running = true; CanExecuteChanged?.Invoke(this, EventArgs.Empty);
            try   { await _execute(); }
            finally { _running = false; CanExecuteChanged?.Invoke(this, EventArgs.Empty); }
        }
        public event EventHandler? CanExecuteChanged;
    }

    internal sealed class AsyncCommand<T> : ICommand
    {
        private readonly Func<T?, Task> _execute;
        private bool _running;
        public AsyncCommand(Func<T?, Task> execute) { _execute = execute; }
        public bool CanExecute(object? p) => !_running;
        public async void Execute(object? p)
        {
            if (_running) return;
            _running = true; CanExecuteChanged?.Invoke(this, EventArgs.Empty);
            try   { await _execute(p is T t ? t : default); }
            finally { _running = false; CanExecuteChanged?.Invoke(this, EventArgs.Empty); }
        }
        public event EventHandler? CanExecuteChanged;
    }
}
