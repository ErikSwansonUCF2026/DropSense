// DropSense — ViewModels/PlantLibraryViewModel.cs
// ══════════════════════════════════════════════════════════════════════════════
// Verified against PlantLibraryService.cs — all type/property names match:
//   Threshold type   → LibraryThreshold          (DropSense.Models)
//   Channel property → LibraryThreshold.libChannel
//   Plant collection → Plant.storedThresholds
//   Channel enum     → MeasurementChannel         (DropSense.Models)
//   Service          → IPlantLibraryService       (DropSense.Services)
//
// Threshold UX (v5):
//   • Only active thresholds are shown as rows (IsEnabled = has saved data)
//   • Each row has inline Edit and Delete actions
//   • A grouped Picker lets the user add any channel not yet configured
//     – Group 0: Base Measurements   (direct sensor readings, alphabetical)
//     – Group 1: Derived Measurements (computed values, alphabetical)
//   • Saving and deleting each row calls the service immediately — no batch save
// ══════════════════════════════════════════════════════════════════════════════

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using DropSense.Models;   // Plant, LibraryThreshold, MeasurementChannel
using DropSense.Services; // IPlantLibraryService

namespace DropSense.ViewModels
{
    // ══════════════════════════════════════════════════════════════════════════
    //  ChannelPickerItem
    //  One entry in the "Add threshold" Picker.  Carries the display string
    //  the Picker shows and the enum value the VM acts on.
    // ══════════════════════════════════════════════════════════════════════════
    public sealed class ChannelPickerItem
    {
        public MeasurementChannel Channel { get; init; }
        public string Label { get; init; } = string.Empty;
        public string Unit { get; init; } = string.Empty;

        // Picker ItemDisplayBinding uses this
        public override string ToString() => $"{Label}  ({Unit})";
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  ThresholdEditorRow
    //  Represents one active (saved) threshold visible in the panel.
    //  Carries both read-only display state and editable text fields.
    //  IsEditing drives inline edit mode for this row.
    // ══════════════════════════════════════════════════════════════════════════
    public sealed class ThresholdEditorRow : INotifyPropertyChanged
    {
        public MeasurementChannel Channel { get; init; }
        public string Label { get; init; } = string.Empty;
        public string Unit { get; init; } = string.Empty;

        // ── Edit fields ───────────────────────────────────────────────────────
        private string _idealMinText = string.Empty;
        private string _idealMaxText = string.Empty;
        private string _safeMinText = string.Empty;
        private string _safeMaxText = string.Empty;
        private bool _isEditing;

        public string IdealMinText { get => _idealMinText; set { _idealMinText = value; OnPropertyChanged(); OnPropertyChanged(nameof(DisplaySummary)); } }
        public string IdealMaxText { get => _idealMaxText; set { _idealMaxText = value; OnPropertyChanged(); OnPropertyChanged(nameof(DisplaySummary)); } }
        public string SafeMinText { get => _safeMinText; set { _safeMinText = value; OnPropertyChanged(); OnPropertyChanged(nameof(DisplaySummary)); } }
        public string SafeMaxText { get => _safeMaxText; set { _safeMaxText = value; OnPropertyChanged(); OnPropertyChanged(nameof(DisplaySummary)); } }

        /// <summary>True while the inline edit fields are open for this row.</summary>
        public bool IsEditing { get => _isEditing; set { _isEditing = value; OnPropertyChanged(); } }

        /// <summary>
        /// One-line summary shown in collapsed read-only view.
        /// Shows only bands that have at least one value set.
        /// </summary>
        public string DisplaySummary
        {
            get
            {
                var parts = new List<string>();

                bool hasIdeal = !string.IsNullOrWhiteSpace(IdealMinText) ||
                                !string.IsNullOrWhiteSpace(IdealMaxText);
                bool hasSafe = !string.IsNullOrWhiteSpace(SafeMinText) ||
                                !string.IsNullOrWhiteSpace(SafeMaxText);

                if (hasIdeal)
                {
                    var lo = string.IsNullOrWhiteSpace(IdealMinText) ? "—" : IdealMinText;
                    var hi = string.IsNullOrWhiteSpace(IdealMaxText) ? "—" : IdealMaxText;
                    parts.Add($"Ideal {lo}–{hi} {Unit}");
                }
                if (hasSafe)
                {
                    var lo = string.IsNullOrWhiteSpace(SafeMinText) ? "—" : SafeMinText;
                    var hi = string.IsNullOrWhiteSpace(SafeMaxText) ? "—" : SafeMaxText;
                    parts.Add($"Safe {lo}–{hi} {Unit}");
                }

                return parts.Count > 0 ? string.Join("   •   ", parts) : $"({Unit})";
            }
        }

        // ── Snapshot fields used by Cancel to restore on discard ─────────────
        private string _snapIdealMin = string.Empty;
        private string _snapIdealMax = string.Empty;
        private string _snapSafeMin = string.Empty;
        private string _snapSafeMax = string.Empty;

        public void BeginEdit()
        {
            _snapIdealMin = IdealMinText;
            _snapIdealMax = IdealMaxText;
            _snapSafeMin = SafeMinText;
            _snapSafeMax = SafeMaxText;
            IsEditing = true;
        }

        public void CancelEdit()
        {
            IdealMinText = _snapIdealMin;
            IdealMaxText = _snapIdealMax;
            SafeMinText = _snapSafeMin;
            SafeMaxText = _snapSafeMax;
            IsEditing = false;
        }

        // ── Populate from a persisted LibraryThreshold ────────────────────────
        public void LoadFrom(LibraryThreshold t)
        {
            IdealMinText = t.IdealMin?.ToString("G") ?? string.Empty;
            IdealMaxText = t.IdealMax?.ToString("G") ?? string.Empty;
            SafeMinText = t.SafeMin?.ToString("G") ?? string.Empty;
            SafeMaxText = t.SafeMax?.ToString("G") ?? string.Empty;
        }

        // ── Build a LibraryThreshold from current text values ─────────────────
        public LibraryThreshold ToLibraryThreshold() => new()
        {
            libChannel = Channel,
            Unit = Unit,
            IdealMin = ParseFloat(IdealMinText),
            IdealMax = ParseFloat(IdealMaxText),
            SafeMin = ParseFloat(SafeMinText),
            SafeMax = ParseFloat(SafeMaxText),
        };

        public bool IsAllBlank =>
            string.IsNullOrWhiteSpace(IdealMinText) &&
            string.IsNullOrWhiteSpace(IdealMaxText) &&
            string.IsNullOrWhiteSpace(SafeMinText) &&
            string.IsNullOrWhiteSpace(SafeMaxText);

        private static float? ParseFloat(string s)
            => float.TryParse(s,
                System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture,
                out var v) ? v : null;

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? n = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  PlantEntryViewModel
    //  Wraps one Plant; owns expand/edit-details state, the active threshold
    //  rows, and the grouped channel picker.
    // ══════════════════════════════════════════════════════════════════════════
    public sealed class PlantEntryViewModel : INotifyPropertyChanged
    {
        // ── Channel catalogue ─────────────────────────────────────────────────
        // Split into Base (direct sensor readings) and Derived (computed).
        // Each group is sorted alphabetically by Label.

        private static readonly (MeasurementChannel Ch, string Label, string Unit, bool IsDerived)[] AllChannels =
        {
            // Base measurements — direct sensor readings
            (MeasurementChannel.BarometricPressure,        "Barometric Pressure",     "hPa",       false),
            (MeasurementChannel.RelativeHumidity,          "Relative Humidity",       "%",         false),
            (MeasurementChannel.SolarIrradiance,           "Solar Irradiance",        "W/m²",      false),
            (MeasurementChannel.Temperature,               "Temperature",             "°C",        false),

            // Derived measurements — computed from base readings
            (MeasurementChannel.AbsoluteHumidity,          "Absolute Humidity",       "g/m³",      true),
            (MeasurementChannel.AccumulatedSolarRadiation, "Accum. Solar Radiation",  "MJ/m²",     true),
            (MeasurementChannel.DailyLightIntegral,        "Daily Light Integral",    "mol/m²/d",  true),
            (MeasurementChannel.DewPointTemperature,       "Dew Point",               "°C",        true),
            (MeasurementChannel.EstimatedPAR,              "Estimated PAR",           "µmol/m²/s", true),
            (MeasurementChannel.VaporPressureDeficit,      "Vapour Pressure Deficit", "kPa",       true),
        };

        private readonly IPlantLibraryService _svc;
        private readonly Action<string> _onError;
        private readonly Action<string> _onSuccess;

        // ── Exposed model ─────────────────────────────────────────────────────
        public Plant Plant { get; }

        // ── Active threshold rows — only channels with saved data ─────────────
        public ObservableCollection<ThresholdEditorRow> ActiveRows { get; } = new();

        // ── Grouped Picker items — channels NOT yet configured ─────────────────
        // Flat list; the Picker displays them with a section-header prefix.
        // We rebuild this whenever ActiveRows changes.
        public ObservableCollection<ChannelPickerItem> AvailableChannels { get; } = new();

        private ChannelPickerItem? _selectedNewChannel;
        public ChannelPickerItem? SelectedNewChannel
        {
            get => _selectedNewChannel;
            set { _selectedNewChannel = value; OnPropertyChanged(); }
        }

        public bool HasNoThresholds => ActiveRows.Count == 0;

        // ── Expand state ──────────────────────────────────────────────────────
        private bool _isExpanded;
        public bool IsExpanded
        {
            get => _isExpanded;
            set { _isExpanded = value; OnPropertyChanged(); OnPropertyChanged(nameof(ExpandChevron)); }
        }
        public string ExpandChevron => IsExpanded ? "▲" : "▼";

        // ── Edit-details state ────────────────────────────────────────────────
        private bool _isEditingDetails;
        private string _editCommonName = string.Empty;
        private string _editScientificName = string.Empty;
        private string _editNotes = string.Empty;

        public bool IsEditingDetails { get => _isEditingDetails; set { _isEditingDetails = value; OnPropertyChanged(); } }
        public string EditCommonName { get => _editCommonName; set { _editCommonName = value; OnPropertyChanged(); } }
        public string EditScientificName { get => _editScientificName; set { _editScientificName = value; OnPropertyChanged(); } }
        public string EditNotes { get => _editNotes; set { _editNotes = value; OnPropertyChanged(); } }

        public bool HasNotes => !string.IsNullOrWhiteSpace(Plant.Notes);

        // ── Commands ──────────────────────────────────────────────────────────
        public ICommand ToggleExpandCommand { get; }
        public ICommand BeginEditDetailsCommand { get; }
        public ICommand SaveEditDetailsCommand { get; }
        public ICommand CancelEditDetailsCommand { get; }
        public ICommand AddSelectedChannelCommand { get; }
        public ICommand BeginEditThresholdCommand { get; }
        public ICommand SaveThresholdRowCommand { get; }
        public ICommand CancelEditThresholdCommand { get; }
        public ICommand DeleteThresholdRowCommand { get; }

        // ── Constructor ───────────────────────────────────────────────────────
        public PlantEntryViewModel(
            Plant plant,
            IPlantLibraryService svc,
            Action<string> onError,
            Action<string> onSuccess)
        {
            Plant = plant;
            _svc = svc;
            _onError = onError;
            _onSuccess = onSuccess;

            ToggleExpandCommand = new AsyncCommand(ToggleExpandAsync);
            BeginEditDetailsCommand = new RelayCommand(BeginEditDetails);
            SaveEditDetailsCommand = new AsyncCommand(SaveEditDetailsAsync);
            CancelEditDetailsCommand = new RelayCommand(() => IsEditingDetails = false);
            AddSelectedChannelCommand = new AsyncCommand(AddSelectedChannelAsync);
            BeginEditThresholdCommand = new RelayCommand<ThresholdEditorRow>(r => r?.BeginEdit());
            CancelEditThresholdCommand = new RelayCommand<ThresholdEditorRow>(r => r?.CancelEdit());
            SaveThresholdRowCommand = new AsyncCommand<ThresholdEditorRow>(SaveThresholdRowAsync);
            DeleteThresholdRowCommand = new AsyncCommand<ThresholdEditorRow>(DeleteThresholdRowAsync);

            BuildActiveRowsFromPlant();
            RebuildAvailableChannels();
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Expand
        // ─────────────────────────────────────────────────────────────────────
        private async Task ToggleExpandAsync()
        {
            IsExpanded = !IsExpanded;
            if (IsExpanded)
                await SyncFromServiceAsync();
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Active rows — build & sync
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Populates ActiveRows from the in-memory Plant.storedThresholds.
        /// Called on construction so the list is ready before the card expands.
        /// </summary>
        private void BuildActiveRowsFromPlant()
        {
            ActiveRows.Clear();
            foreach (var t in Plant.storedThresholds)
            {
                var meta = AllChannels.FirstOrDefault(c => c.Ch == t.libChannel);
                if (meta == default) continue;

                var row = new ThresholdEditorRow
                {
                    Channel = meta.Ch,
                    Label = meta.Label,
                    Unit = meta.Unit,
                };
                row.LoadFrom(t);
                ActiveRows.Add(row);
            }
        }

        /// <summary>
        /// Authoritative re-sync from the service.
        /// Rebuilds ActiveRows and repopulates AvailableChannels picker.
        /// </summary>
        private async Task SyncFromServiceAsync()
        {
            try
            {
                var saved = await _svc.GetThresholdsAsync(Plant.PlantId);
                var map = saved.ToDictionary(t => t.libChannel);

                // Rebuild active rows preserving IsEditing state where possible
                var editingChannels = ActiveRows
                    .Where(r => r.IsEditing)
                    .Select(r => r.Channel)
                    .ToHashSet();

                ActiveRows.Clear();

                foreach (var (ch, label, unit, _) in AllChannels)
                {
                    if (!map.TryGetValue(ch, out var t)) continue;

                    var row = new ThresholdEditorRow { Channel = ch, Label = label, Unit = unit };
                    row.LoadFrom(t);
                    if (editingChannels.Contains(ch))
                        row.BeginEdit();
                    ActiveRows.Add(row);
                }

                // Keep Plant.storedThresholds in sync so badge count is accurate
                Plant.storedThresholds.Clear();
                foreach (var t in saved) Plant.storedThresholds.Add(t);
                OnPropertyChanged(nameof(Plant));
                OnPropertyChanged(nameof(HasNoThresholds));

                RebuildAvailableChannels();
            }
            catch (Exception ex)
            {
                _onError($"Could not refresh thresholds: {ex.Message}");
            }
        }

        /// <summary>
        /// Rebuilds the Picker list: channels from AllChannels that are NOT
        /// already in ActiveRows, ordered Base then Derived, each group
        /// alphabetical.  Group headers are inlined as disabled items using
        /// the "── Group ──" prefix so standard Picker can show them.
        /// </summary>
        private void RebuildAvailableChannels()
        {
            var activeSet = ActiveRows.Select(r => r.Channel).ToHashSet();

            AvailableChannels.Clear();

            bool addedBaseHeader = false;
            bool addedDerivedHeader = false;

            // AllChannels is already alphabetical within each group
            foreach (var (ch, label, unit, isDerived) in AllChannels)
            {
                if (activeSet.Contains(ch)) continue;

                if (!isDerived && !addedBaseHeader)
                {
                    AvailableChannels.Add(new ChannelPickerItem
                    {
                        // Channel = default(0) acts as a non-selectable header marker
                        Label = "── Base Measurements ──",
                        Unit = string.Empty,
                    });
                    addedBaseHeader = true;
                }

                if (isDerived && !addedDerivedHeader)
                {
                    AvailableChannels.Add(new ChannelPickerItem
                    {
                        Label = "── Derived Measurements ──",
                        Unit = string.Empty,
                    });
                    addedDerivedHeader = true;
                }

                AvailableChannels.Add(new ChannelPickerItem
                {
                    Channel = ch,
                    Label = label,
                    Unit = unit,
                });
            }

            OnPropertyChanged(nameof(HasAvailableChannels));
        }

        /// <summary>True when there are still channels available to add.</summary>
        public bool HasAvailableChannels => AvailableChannels.Any(i => !i.Label.StartsWith("──"));

        // ─────────────────────────────────────────────────────────────────────
        //  Add channel from picker
        // ─────────────────────────────────────────────────────────────────────
        private async Task AddSelectedChannelAsync()
        {
            if (SelectedNewChannel is null ||
                SelectedNewChannel.Label.StartsWith("──"))
            {
                _onError("Select a channel to add.");
                return;
            }

            // Check it hasn't been added already (guard against race)
            var existing = await _svc.GetThresholdAsync(Plant.PlantId, SelectedNewChannel.Channel);
            if (existing is not null)
            {
                _onError($"{SelectedNewChannel.Label} is already configured.");
                SelectedNewChannel = null;
                await SyncFromServiceAsync();
                return;
            }

            // Add a record with blank values so it becomes visible immediately.
            // The user then fills in the values and saves the row inline.
            var dto = new LibraryThreshold
            {
                libChannel = SelectedNewChannel.Channel,
                Unit = SelectedNewChannel.Unit,
                IdealMin = null,
                IdealMax = null,
                SafeMin = null,
                SafeMax = null,
            };

            try
            {
                await _svc.AddThresholdAsync(Plant.PlantId, dto);

                // Build an editing row directly — skip the full sync round-trip
                var row = new ThresholdEditorRow
                {
                    Channel = SelectedNewChannel.Channel,
                    Label = SelectedNewChannel.Label,
                    Unit = SelectedNewChannel.Unit,
                };
                row.LoadFrom(dto);
                row.BeginEdit();   // open in edit mode so the user can fill values immediately
                ActiveRows.Add(row);

                // Update in-memory plant list
                Plant.storedThresholds.Add(dto);
                OnPropertyChanged(nameof(Plant));
                OnPropertyChanged(nameof(HasNoThresholds));

                SelectedNewChannel = null;
                RebuildAvailableChannels();
            }
            catch (Exception ex) { _onError(ex.Message); }
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Save individual threshold row
        // ─────────────────────────────────────────────────────────────────────
        private async Task SaveThresholdRowAsync(ThresholdEditorRow? row)
        {
            if (row is null) return;

            try
            {
                var dto = row.ToLibraryThreshold();
                var existing = await _svc.GetThresholdAsync(Plant.PlantId, row.Channel);

                if (existing is null)
                    await _svc.AddThresholdAsync(Plant.PlantId, dto);
                else
                    await _svc.UpdateThresholdAsync(Plant.PlantId, dto);

                row.IsEditing = false;

                // Sync plant badge count
                var refreshed = await _svc.GetThresholdsAsync(Plant.PlantId);
                Plant.storedThresholds.Clear();
                foreach (var t in refreshed) Plant.storedThresholds.Add(t);
                OnPropertyChanged(nameof(Plant));

                _onSuccess($"{row.Label} saved.");
            }
            catch (Exception ex) { _onError(ex.Message); }
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Delete individual threshold row
        // ─────────────────────────────────────────────────────────────────────
        private async Task DeleteThresholdRowAsync(ThresholdEditorRow? row)
        {
            if (row is null) return;
            try
            {
                var existing = await _svc.GetThresholdAsync(Plant.PlantId, row.Channel);
                if (existing is not null)
                    await _svc.DeleteThresholdAsync(Plant.PlantId, row.Channel);

                ActiveRows.Remove(row);

                // Keep plant badge in sync
                var t = Plant.storedThresholds.FirstOrDefault(t => t.libChannel == row.Channel);
                if (t is not null) Plant.storedThresholds.Remove(t);
                OnPropertyChanged(nameof(Plant));
                OnPropertyChanged(nameof(HasNoThresholds));

                RebuildAvailableChannels();
                _onSuccess($"{row.Label} threshold removed.");
            }
            catch (Exception ex) { _onError(ex.Message); }
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Edit plant details
        // ─────────────────────────────────────────────────────────────────────
        private void BeginEditDetails()
        {
            EditCommonName = Plant.CommonName;
            EditScientificName = Plant.ScientificName ?? string.Empty;
            EditNotes = Plant.Notes ?? string.Empty;
            IsEditingDetails = true;
        }

        private async Task SaveEditDetailsAsync()
        {
            if (string.IsNullOrWhiteSpace(EditCommonName))
            {
                _onError("Common name is required.");
                return;
            }
            try
            {
                Plant.CommonName = EditCommonName.Trim();
                Plant.ScientificName = NullIfBlank(EditScientificName);
                Plant.Notes = NullIfBlank(EditNotes);

                await _svc.UpdatePlantAsync(Plant);

                IsEditingDetails = false;
                OnPropertyChanged(nameof(Plant));
                OnPropertyChanged(nameof(HasNotes));
                _onSuccess("Plant updated.");
            }
            catch (Exception ex) { _onError(ex.Message); }
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Helpers
        // ─────────────────────────────────────────────────────────────────────
        private static string? NullIfBlank(string s)
            => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

        // ─────────────────────────────────────────────────────────────────────
        //  INotifyPropertyChanged
        // ─────────────────────────────────────────────────────────────────────
        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? n = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  PlantLibraryViewModel  — root VM, unchanged from v4 except doc update
    // ══════════════════════════════════════════════════════════════════════════
    public sealed class PlantLibraryViewModel : INotifyPropertyChanged
    {
        private readonly IPlantLibraryService _svc;

        public ObservableCollection<PlantEntryViewModel> PlantEntries { get; } = new();

        private bool _isBusy;
        private string _statusMessage = string.Empty;
        private bool _showSuccessBanner;
        private bool _showErrorBanner;

        public bool IsBusy { get => _isBusy; private set { _isBusy = value; OnPropertyChanged(); } }
        public string StatusMessage { get => _statusMessage; private set { _statusMessage = value; OnPropertyChanged(); } }
        public bool ShowSuccessBanner { get => _showSuccessBanner; private set { _showSuccessBanner = value; OnPropertyChanged(); } }
        public bool ShowErrorBanner { get => _showErrorBanner; private set { _showErrorBanner = value; OnPropertyChanged(); } }
        public bool HasNoPlants => PlantEntries.Count == 0;

        private string _newCommonName = string.Empty;
        private string _newScientificName = string.Empty;
        private string _newNotes = string.Empty;

        public string NewCommonName { get => _newCommonName; set { _newCommonName = value; OnPropertyChanged(); } }
        public string NewScientificName { get => _newScientificName; set { _newScientificName = value; OnPropertyChanged(); } }
        public string NewNotes { get => _newNotes; set { _newNotes = value; OnPropertyChanged(); } }

        private bool _initialized;

        public ICommand AddPlantCommand { get; }
        public ICommand DeletePlantCommand { get; }

        public PlantLibraryViewModel(IPlantLibraryService svc)
        {
            _svc = svc;
            AddPlantCommand = new AsyncCommand(AddPlantAsync);
            DeletePlantCommand = new AsyncCommand<PlantEntryViewModel>(DeletePlantAsync);
        }

        public async Task InitializeAsync()
        {
            if (_initialized) return;
            _initialized = true;
            await LoadAllPlantsAsync();
        }

        public void ClearNewPlantForm()
            => NewCommonName = NewScientificName = NewNotes = string.Empty;

        private async Task LoadAllPlantsAsync()
        {
            IsBusy = true;
            try
            {
                var all = await _svc.GetAllPlantsAsync();
                PlantEntries.Clear();
                foreach (var p in all) PlantEntries.Add(MakeEntry(p));
                OnPropertyChanged(nameof(HasNoPlants));
            }
            catch (Exception ex) { ShowBanner(ex.Message, isError: true); }
            finally { IsBusy = false; }
        }

        private async Task AddPlantAsync()
        {
            if (string.IsNullOrWhiteSpace(NewCommonName))
            {
                ShowBanner("Common name is required.", isError: true);
                return;
            }
            IsBusy = true;
            try
            {
                var saved = await _svc.AddPlantAsync(new Plant
                {
                    CommonName = NewCommonName.Trim(),
                    ScientificName = NullIfBlank(NewScientificName),
                    Notes = NullIfBlank(NewNotes),
                });
                PlantEntries.Add(MakeEntry(saved));
                OnPropertyChanged(nameof(HasNoPlants));
                ClearNewPlantForm();
                ShowBanner($"'{saved.CommonName}' added to the library.", isError: false);
            }
            catch (Exception ex) { ShowBanner(ex.Message, isError: true); }
            finally { IsBusy = false; }
        }

        private async Task DeletePlantAsync(PlantEntryViewModel? entry)
        {
            if (entry is null) return;
            IsBusy = true;
            try
            {
                await _svc.DeletePlantAsync(entry.Plant.PlantId);
                PlantEntries.Remove(entry);
                OnPropertyChanged(nameof(HasNoPlants));
                ShowBanner($"'{entry.Plant.CommonName}' removed.", isError: false);
            }
            catch (Exception ex) { ShowBanner(ex.Message, isError: true); }
            finally { IsBusy = false; }
        }

        private PlantEntryViewModel MakeEntry(Plant p) =>
            new(p, _svc,
                onError: msg => ShowBanner(msg, isError: true),
                onSuccess: msg => ShowBanner(msg, isError: false));

        private CancellationTokenSource? _bannerCts;
        private void ShowBanner(string msg, bool isError)
        {
            _bannerCts?.Cancel();
            _bannerCts = new CancellationTokenSource();
            var token = _bannerCts.Token;
            StatusMessage = msg;
            ShowSuccessBanner = !isError;
            ShowErrorBanner = isError;
            Task.Delay(3500, token).ContinueWith(_ =>
            {
                if (token.IsCancellationRequested) return;
                MainThread.BeginInvokeOnMainThread(
                    () => ShowSuccessBanner = ShowErrorBanner = false);
            }, TaskScheduler.Default);
        }

        private static string? NullIfBlank(string s)
            => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? n = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  Command helpers
    // ══════════════════════════════════════════════════════════════════════════
    internal sealed class RelayCommand : ICommand
    {
        private readonly Action _execute;
        private readonly Func<bool>? _can;
        public RelayCommand(Action execute, Func<bool>? can = null) { _execute = execute; _can = can; }
        public bool CanExecute(object? _) => _can?.Invoke() ?? true;
        public void Execute(object? _) => _execute();
        public event EventHandler? CanExecuteChanged;
    }

    internal sealed class RelayCommand<T> : ICommand
    {
        private readonly Action<T?> _execute;
        public RelayCommand(Action<T?> execute) => _execute = execute;
        public bool CanExecute(object? _) => true;
        public void Execute(object? p) => _execute(p is T t ? t : default);
        public event EventHandler? CanExecuteChanged;
    }

    internal sealed class AsyncCommand : ICommand
    {
        private readonly Func<Task> _execute;
        private readonly Func<bool>? _can;
        private bool _running;
        public AsyncCommand(Func<Task> execute, Func<bool>? can = null) { _execute = execute; _can = can; }
        public bool CanExecute(object? _) => !_running && (_can?.Invoke() ?? true);
        public async void Execute(object? _)
        {
            if (_running) return;
            _running = true; CanExecuteChanged?.Invoke(this, EventArgs.Empty);
            try { await _execute(); }
            finally { _running = false; CanExecuteChanged?.Invoke(this, EventArgs.Empty); }
        }
        public event EventHandler? CanExecuteChanged;
    }

    internal sealed class AsyncCommand<T> : ICommand
    {
        private readonly Func<T?, Task> _execute;
        private bool _running;
        public AsyncCommand(Func<T?, Task> execute) => _execute = execute;
        public bool CanExecute(object? _) => !_running;
        public async void Execute(object? p)
        {
            if (_running) return;
            _running = true; CanExecuteChanged?.Invoke(this, EventArgs.Empty);
            try { await _execute(p is T t ? t : default); }
            finally { _running = false; CanExecuteChanged?.Invoke(this, EventArgs.Empty); }
        }
        public event EventHandler? CanExecuteChanged;
    }
}