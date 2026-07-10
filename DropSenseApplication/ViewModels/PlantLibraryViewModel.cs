// DropSense — ViewModels/PlantLibraryViewModel.cs
// ══════════════════════════════════════════════════════════════════════════════
// Verified against PlantLibraryService.cs — all type/property names match:
//   Threshold type   → LibraryThreshold          (DropSense.Models)
//   Channel property → LibraryThreshold.libChannel
//   Plant collection → Plant.storedThresholds
//   Channel enum     → MeasurementChannel         (DropSense.Models)
//   Service          → IPlantLibraryService       (DropSense.Services)
//
// Persistence contract (v6):
//   • Cold start     — GetAllPlantsAsync() returns Plants with nested
//                      storedThresholds already populated (service deserialises
//                      them from JSON).  BuildActiveRowsFromPlant() uses that
//                      data so thresholds are visible before the card expands.
//   • Expand         — SyncFromServiceAsync() re-reads from the service to pick
//                      up any changes made in another session or by a background
//                      task.  Existing IsEditing state is preserved.
//   • Add channel    — A blank LibraryThreshold is written to the service
//                      immediately (so it owns the persistence) and the row is
//                      marked IsNew = true.  IsNew drives "Cancel = delete".
//   • Save row       — Calls UpdateThresholdAsync (record already exists).
//                      Clears IsNew and IsEditing.  Refreshes badge count.
//   • Cancel on new  — Calls DeleteThresholdAsync to remove the blank record,
//                      then removes the row from ActiveRows.
//   • Cancel on edit — Restores snapshotted text values; no service call.
//   • Delete row     — Calls DeleteThresholdAsync, removes from ActiveRows and
//                      Plant.storedThresholds.
//
// Fix (v7) — thread marshalling:
//   Every place that mutates a UI-bound ObservableCollection (ActiveRows,
//   Plant.storedThresholds, PlantEntries) after an `await` on the service is
//   now wrapped in MainThread.InvokeOnMainThreadAsync. Previously, if the
//   service's async I/O resumed its continuation on a background thread,
//   BindableLayout reacting to a CollectionChanged event raised off the UI
//   thread could throw mid-loop — aborting a Clear()+re-Add() rebuild and
//   leaving ActiveRows (and the threshold-count badge, which reads
//   Plant.storedThresholds.Count) empty even though the persisted JSON was
//   never touched. This is what caused thresholds to appear "erased" after
//   collapsing and re-expanding a plant card.
// ══════════════════════════════════════════════════════════════════════════════

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using DropSense.Models;
using DropSense.Services;

namespace DropSense.ViewModels
{
    // ══════════════════════════════════════════════════════════════════════════
    //  ChannelPickerItem
    // ══════════════════════════════════════════════════════════════════════════
    public sealed class ChannelPickerItem
    {
        public MeasurementChannel Channel { get; init; }
        public string Label { get; init; } = string.Empty;
        public string Unit { get; init; } = string.Empty;

        public override string ToString() => $"{Label}  ({Unit})";
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  ThresholdEditorRow
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

        public string IdealMinText
        {
            get => _idealMinText;
            set { _idealMinText = value; OnPropertyChanged(); OnPropertyChanged(nameof(DisplaySummary)); OnPropertyChanged(nameof(IsDirty)); }
        }
        public string IdealMaxText
        {
            get => _idealMaxText;
            set { _idealMaxText = value; OnPropertyChanged(); OnPropertyChanged(nameof(DisplaySummary)); OnPropertyChanged(nameof(IsDirty)); }
        }
        public string SafeMinText
        {
            get => _safeMinText;
            set { _safeMinText = value; OnPropertyChanged(); OnPropertyChanged(nameof(DisplaySummary)); OnPropertyChanged(nameof(IsDirty)); }
        }
        public string SafeMaxText
        {
            get => _safeMaxText;
            set { _safeMaxText = value; OnPropertyChanged(); OnPropertyChanged(nameof(DisplaySummary)); OnPropertyChanged(nameof(IsDirty)); }
        }

        /// <summary>True while the inline edit fields are open for this row.</summary>
        public bool IsEditing
        {
            get => _isEditing;
            set { _isEditing = value; OnPropertyChanged(); }
        }

        /// <summary>
        /// True when this row was just added via the picker and has never been
        /// saved.  Drives Cancel behaviour: cancel on a new row deletes the
        /// blank service record rather than just restoring snapshot text.
        /// </summary>
        public bool IsNew { get; set; }

        /// <summary>True when any edit field differs from the last-saved snapshot.</summary>
        public bool IsDirty =>
            IdealMinText != _snapIdealMin ||
            IdealMaxText != _snapIdealMax ||
            SafeMinText != _snapSafeMin ||
            SafeMaxText != _snapSafeMax;

        // ── Collapsed summary ─────────────────────────────────────────────────
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

                return parts.Count > 0 ? string.Join("   •   ", parts) : $"No bounds set ({Unit})";
            }
        }

        // ── Snapshot — used by Cancel to restore on discard ───────────────────
        private string _snapIdealMin = string.Empty;
        private string _snapIdealMax = string.Empty;
        private string _snapSafeMin = string.Empty;
        private string _snapSafeMax = string.Empty;

        /// <summary>
        /// Opens edit mode and takes a snapshot of current values so Cancel
        /// can restore them without a service round-trip.
        /// </summary>
        public void BeginEdit()
        {
            _snapIdealMin = IdealMinText;
            _snapIdealMax = IdealMaxText;
            _snapSafeMin = SafeMinText;
            _snapSafeMax = SafeMaxText;
            IsEditing = true;
        }

        /// <summary>
        /// Restores snapshot values and closes edit mode.
        /// For new rows the VM calls DeleteThresholdAsync before calling this.
        /// </summary>
        public void CancelEdit()
        {
            IdealMinText = _snapIdealMin;
            IdealMaxText = _snapIdealMax;
            SafeMinText = _snapSafeMin;
            SafeMaxText = _snapSafeMax;
            IsEditing = false;
        }

        /// <summary>
        /// Commits the current text values as the new clean snapshot so
        /// IsDirty returns false immediately after a successful save.
        /// </summary>
        public void CommitSnapshot()
        {
            _snapIdealMin = IdealMinText;
            _snapIdealMax = IdealMaxText;
            _snapSafeMin = SafeMinText;
            _snapSafeMax = SafeMaxText;
            IsNew = false;
            IsEditing = false;
            OnPropertyChanged(nameof(IsDirty));
        }

        // ── Populate from a persisted LibraryThreshold ────────────────────────
        public void LoadFrom(LibraryThreshold t)
        {
            IdealMinText = t.IdealMin?.ToString("G") ?? string.Empty;
            IdealMaxText = t.IdealMax?.ToString("G") ?? string.Empty;
            SafeMinText = t.SafeMin?.ToString("G") ?? string.Empty;
            SafeMaxText = t.SafeMax?.ToString("G") ?? string.Empty;

            // After loading from persistence the values ARE the snapshot
            CommitSnapshot();
            IsNew = false;
            IsEditing = false;
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
    // ══════════════════════════════════════════════════════════════════════════
    public sealed class PlantEntryViewModel : INotifyPropertyChanged
    {
        // ── Channel catalogue ─────────────────────────────────────────────────
        private static readonly (MeasurementChannel Ch, string Label, string Unit, bool IsDerived)[] AllChannels =
        {
            // Base measurements — direct sensor readings (alphabetical)
            (MeasurementChannel.BarometricPressure,        "Barometric Pressure",     "hPa",       false),
            (MeasurementChannel.RelativeHumidity,          "Relative Humidity",       "%",         false),
            (MeasurementChannel.SolarIrradiance,           "Solar Irradiance",        "W/m²",      false),
            (MeasurementChannel.Temperature,               "Temperature",             "°C",        false),

            // Derived measurements — computed from base readings (alphabetical)
            (MeasurementChannel.AbsoluteHumidity,          "Absolute Humidity",       "g/m³",      true),
            (MeasurementChannel.AccumulatedSolarRadiation, "Accum. Solar Radiation",  "MJ/m²",     true),
            (MeasurementChannel.DailyLightIntegral,        "Daily Light Integral",    "mol/m²/d",  true),
            (MeasurementChannel.DewPointTemperature,       "Dew Point",               "°C",        true),
            (MeasurementChannel.EstimatedPAR,              "Estimated PAR",           "µmol/m²/s", true),
            (MeasurementChannel.VaporPressureDeficit,      "Vapour Pressure Deficit", "hPa",       true),
        };

        private readonly IPlantLibraryService _svc;
        private readonly Action<string> _onError;
        private readonly Action<string> _onSuccess;

        // ── Exposed model ─────────────────────────────────────────────────────
        public Plant Plant { get; }

        // ── Active threshold rows ─────────────────────────────────────────────
        public ObservableCollection<ThresholdEditorRow> ActiveRows { get; } = new();

        // ── Available channels picker ─────────────────────────────────────────
        public ObservableCollection<ChannelPickerItem> AvailableChannels { get; } = new();

        private ChannelPickerItem? _selectedNewChannel;
        public ChannelPickerItem? SelectedNewChannel
        {
            get => _selectedNewChannel;
            set { _selectedNewChannel = value; OnPropertyChanged(); }
        }

        public bool HasNoThresholds => ActiveRows.Count == 0;
        public bool HasAvailableChannels => AvailableChannels.Any(i => !i.Label.StartsWith("──"));

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
            CancelEditThresholdCommand = new AsyncCommand<ThresholdEditorRow>(CancelEditThresholdAsync);
            SaveThresholdRowCommand = new AsyncCommand<ThresholdEditorRow>(SaveThresholdRowAsync);
            DeleteThresholdRowCommand = new AsyncCommand<ThresholdEditorRow>(DeleteThresholdRowAsync);

            // Populate from what the service already loaded into Plant.storedThresholds
            // at cold start (GetAllPlantsAsync returns fully-hydrated plants).
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
        /// Used at construction time; the data comes from the service's cold-
        /// start deserialisation so no extra I/O is needed.
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
                row.LoadFrom(t);  // also sets clean snapshot + clears IsNew/IsEditing
                ActiveRows.Add(row);
            }

            OnPropertyChanged(nameof(HasNoThresholds));
        }

        /// <summary>
        /// Authoritative re-sync from the service (called on expand, or after
        /// any operation that could leave local state stale).
        /// Preserves IsEditing state on rows that are currently open.
        /// Does NOT overwrite rows that are IsNew (not yet saved) — those are
        /// local-only until the user hits Save or Cancel.
        /// </summary>
        private async Task SyncFromServiceAsync()
        {
            try
            {
                // NOTE: everything up to and including this await may resume on a
                // background (thread-pool) thread, depending on how the service
                // implements its I/O. Mutating UI-bound ObservableCollections off
                // the UI thread is what was causing thresholds (and the badge
                // count) to be wiped on re-expand: BindableLayout reacting to a
                // CollectionChanged event raised from a non-UI thread can throw
                // mid-loop, aborting the rebuild with ActiveRows/storedThresholds
                // left partially or fully empty. Everything that touches
                // ActiveRows / Plant.storedThresholds below is therefore
                // explicitly marshalled back onto the UI thread.
                // Materialize into our own list immediately. Even with the
                // service-side fix (GetThresholdsAsync now returns a copy),
                // this guards against the exact aliasing bug that caused
                // thresholds to be wiped: never hold a reference from the
                // service that could turn out to alias a collection we're
                // about to Clear()/rebuild.
                var saved = (await _svc.GetThresholdsAsync(Plant.PlantId)).ToList();
                var map = saved.ToDictionary(t => t.libChannel);

                await MainThread.InvokeOnMainThreadAsync(() =>
                {
                    // Channels currently being edited (preserve edit state)
                    var editingChannels = ActiveRows
                        .Where(r => r.IsEditing && !r.IsNew)
                        .Select(r => r.Channel)
                        .ToHashSet();

                    // New rows that haven't been saved yet — keep them as-is
                    var newRows = ActiveRows
                        .Where(r => r.IsNew)
                        .ToDictionary(r => r.Channel);

                    ActiveRows.Clear();

                    // Re-add persisted rows in catalogue order
                    foreach (var (ch, label, unit, _) in AllChannels)
                    {
                        if (newRows.TryGetValue(ch, out var newRow))
                        {
                            // Re-insert the unsaved new row at its natural position
                            ActiveRows.Add(newRow);
                            continue;
                        }

                        if (!map.TryGetValue(ch, out var t)) continue;

                        var row = new ThresholdEditorRow { Channel = ch, Label = label, Unit = unit };
                        row.LoadFrom(t);

                        if (editingChannels.Contains(ch))
                            row.BeginEdit();  // reopen edit mode if it was open before sync

                        ActiveRows.Add(row);
                    }

                    // Keep Plant.storedThresholds in sync for the badge count
                    Plant.storedThresholds.Clear();
                    foreach (var t in saved) Plant.storedThresholds.Add(t);

                    OnPropertyChanged(nameof(Plant));
                    OnPropertyChanged(nameof(HasNoThresholds));
                    RebuildAvailableChannels();
                });
            }
            catch (Exception ex)
            {
                _onError($"Could not refresh thresholds: {ex.Message}");
            }
        }

        /// <summary>
        /// Rebuilds the channel picker: channels NOT already in ActiveRows,
        /// ordered Base then Derived, each group alphabetical.
        /// Group headers are inlined as non-selectable items (Label starts with ──).
        /// </summary>
        private void RebuildAvailableChannels()
        {
            var activeSet = ActiveRows.Select(r => r.Channel).ToHashSet();

            AvailableChannels.Clear();

            bool addedBaseHeader = false;
            bool addedDerivedHeader = false;

            foreach (var (ch, label, unit, isDerived) in AllChannels)
            {
                if (activeSet.Contains(ch)) continue;

                if (!isDerived && !addedBaseHeader)
                {
                    AvailableChannels.Add(new ChannelPickerItem { Label = "── Base Measurements ──", Unit = string.Empty });
                    addedBaseHeader = true;
                }
                if (isDerived && !addedDerivedHeader)
                {
                    AvailableChannels.Add(new ChannelPickerItem { Label = "── Derived Measurements ──", Unit = string.Empty });
                    addedDerivedHeader = true;
                }

                AvailableChannels.Add(new ChannelPickerItem { Channel = ch, Label = label, Unit = unit });
            }

            OnPropertyChanged(nameof(HasAvailableChannels));
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Add channel from picker
        // ─────────────────────────────────────────────────────────────────────
        private async Task AddSelectedChannelAsync()
        {
            if (SelectedNewChannel is null || SelectedNewChannel.Label.StartsWith("──"))
            {
                _onError("Select a channel to add.");
                return;
            }

            // Guard against a race where the channel was added in another session
            var existing = await _svc.GetThresholdAsync(Plant.PlantId, SelectedNewChannel.Channel);
            if (existing is not null)
            {
                _onError($"{SelectedNewChannel.Label} is already configured.");
                SelectedNewChannel = null;
                await SyncFromServiceAsync();
                return;
            }

            // Persist a blank record immediately so the service owns the data.
            // IsNew = true on the row means Cancel will delete this record.
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

                // Marshal back to the UI thread before touching bound collections —
                // see the comment in SyncFromServiceAsync for why this matters.
                await MainThread.InvokeOnMainThreadAsync(() =>
                {
                    var row = new ThresholdEditorRow
                    {
                        Channel = SelectedNewChannel!.Channel,
                        Label = SelectedNewChannel.Label,
                        Unit = SelectedNewChannel.Unit,
                        IsNew = true,
                    };
                    row.LoadFrom(dto);   // sets clean snapshot (all empty)
                    row.BeginEdit();     // open in edit mode immediately

                    ActiveRows.Add(row);

                    // NOTE: do NOT also do Plant.storedThresholds.Add(dto) here.
                    // The service's Plant instances are shared by reference with
                    // this ViewModel's Plant (GetAllPlantsAsync hands back the
                    // same object graph it stores internally), so the
                    // AddThresholdAsync call above has already appended dto to
                    // this exact Plant.storedThresholds list. Adding it again
                    // here would silently double-count the badge.
                    OnPropertyChanged(nameof(Plant));
                    OnPropertyChanged(nameof(HasNoThresholds));

                    SelectedNewChannel = null;
                    RebuildAvailableChannels();
                });
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

                // The record always exists in the service at this point:
                //   • Existing rows were loaded from JSON
                //   • New rows were written to the service in AddSelectedChannelAsync
                // So we always call Update here.
                await _svc.UpdateThresholdAsync(Plant.PlantId, dto);

                // Commit snapshot — clears IsDirty, IsNew, closes IsEditing
                row.CommitSnapshot();

                // Refresh Plant.storedThresholds for accurate badge count
                await RefreshBadgeAsync();

                _onSuccess($"{row.Label} saved.");
            }
            catch (Exception ex) { _onError(ex.Message); }
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Cancel individual threshold row edit
        // ─────────────────────────────────────────────────────────────────────
        private async Task CancelEditThresholdAsync(ThresholdEditorRow? row)
        {
            if (row is null) return;

            if (row.IsNew)
            {
                // The blank record was written to the service when it was added.
                // Cancelling a new-unsaved row means the user changed their mind
                // entirely — remove the service record and the UI row.
                try
                {
                    await _svc.DeleteThresholdAsync(Plant.PlantId, row.Channel);
                }
                catch (Exception ex)
                {
                    // Log but don't block the UI — the row is still removed below
                    _onError($"Could not remove unsaved threshold: {ex.Message}");
                }

                // Marshal back to the UI thread before touching bound collections —
                // see the comment in SyncFromServiceAsync for why this matters.
                await MainThread.InvokeOnMainThreadAsync(() =>
                {
                    ActiveRows.Remove(row);

                    var t = Plant.storedThresholds.FirstOrDefault(t => t.libChannel == row.Channel);
                    if (t is not null) Plant.storedThresholds.Remove(t);

                    OnPropertyChanged(nameof(Plant));
                    OnPropertyChanged(nameof(HasNoThresholds));
                    RebuildAvailableChannels();
                });
            }
            else
            {
                // Just restore snapshot values; no service call needed
                row.CancelEdit();
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Delete individual threshold row
        // ─────────────────────────────────────────────────────────────────────
        private async Task DeleteThresholdRowAsync(ThresholdEditorRow? row)
        {
            if (row is null) return;

            try
            {
                // Guard: service throws KeyNotFoundException if missing, which
                // is fine — we still remove the UI row below.
                try
                {
                    await _svc.DeleteThresholdAsync(Plant.PlantId, row.Channel);
                }
                catch (KeyNotFoundException) { /* already gone — proceed */ }

                // Marshal back to the UI thread before touching bound collections —
                // see the comment in SyncFromServiceAsync for why this matters.
                await MainThread.InvokeOnMainThreadAsync(() =>
                {
                    ActiveRows.Remove(row);

                    var t = Plant.storedThresholds.FirstOrDefault(t => t.libChannel == row.Channel);
                    if (t is not null) Plant.storedThresholds.Remove(t);

                    OnPropertyChanged(nameof(Plant));
                    OnPropertyChanged(nameof(HasNoThresholds));
                    RebuildAvailableChannels();
                });

                _onSuccess($"{row.Label} threshold removed.");
            }
            catch (Exception ex) { _onError(ex.Message); }
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Badge count helper
        // ─────────────────────────────────────────────────────────────────────
        private async Task RefreshBadgeAsync()
        {
            try
            {
                // See the aliasing note in SyncFromServiceAsync — materialize
                // before touching Plant.storedThresholds.
                var refreshed = (await _svc.GetThresholdsAsync(Plant.PlantId)).ToList();

                // Marshal back to the UI thread before touching bound collections —
                // see the comment in SyncFromServiceAsync for why this matters.
                await MainThread.InvokeOnMainThreadAsync(() =>
                {
                    Plant.storedThresholds.Clear();
                    foreach (var t in refreshed) Plant.storedThresholds.Add(t);
                    OnPropertyChanged(nameof(Plant));
                });
            }
            catch { /* badge count is cosmetic — swallow */ }
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

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? n = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  PlantLibraryViewModel  — root VM
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

        public string NewCommonName
        {
            get => _newCommonName;
            set { _newCommonName = value; OnPropertyChanged(); }
        }
        public string NewScientificName
        {
            get => _newScientificName;
            set { _newScientificName = value; OnPropertyChanged(); }
        }
        public string NewNotes
        {
            get => _newNotes;
            set { _newNotes = value; OnPropertyChanged(); }
        }

        private bool _initialized;

        public ICommand AddPlantCommand { get; }
        public ICommand DeletePlantCommand { get; }

        public PlantLibraryViewModel(IPlantLibraryService svc)
        {
            _svc = svc;
            AddPlantCommand = new AsyncCommand(AddPlantAsync);
            DeletePlantCommand = new AsyncCommand<PlantEntryViewModel>(DeletePlantAsync);
        }

        /// <summary>
        /// Called once by the view on first Loaded.  Safe to call multiple
        /// times: subsequent calls are no-ops so navigation back to the page
        /// doesn't wipe live state.
        /// </summary>
        public async Task InitializeAsync()
        {
            if (_initialized) return;
            _initialized = true;
            await LoadAllPlantsAsync();
        }

        /// <summary>
        /// Hard-refresh from the service.  Call this if the view needs to
        /// reflect changes made in a background task or another page.
        /// Resets _initialized so the next InitializeAsync re-loads.
        /// </summary>
        public async Task ForceRefreshAsync()
        {
            _initialized = false;
            await InitializeAsync();
        }

        public void ClearNewPlantForm()
            => NewCommonName = NewScientificName = NewNotes = string.Empty;

        // ─────────────────────────────────────────────────────────────────────
        //  Plant load / add / delete
        // ─────────────────────────────────────────────────────────────────────
        private async Task LoadAllPlantsAsync()
        {
            IsBusy = true;
            try
            {
                // GetAllPlantsAsync returns fully-hydrated Plant objects whose
                // storedThresholds are already populated from JSON.
                // PlantEntryViewModel.BuildActiveRowsFromPlant() uses that data
                // directly so threshold rows are ready before the card expands.
                var all = await _svc.GetAllPlantsAsync();

                // Marshal back to the UI thread before touching bound collections —
                // see the comment in PlantEntryViewModel.SyncFromServiceAsync for why.
                await MainThread.InvokeOnMainThreadAsync(() =>
                {
                    PlantEntries.Clear();
                    foreach (var p in all) PlantEntries.Add(MakeEntry(p));
                    OnPropertyChanged(nameof(HasNoPlants));
                });
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

                // Marshal back to the UI thread before touching bound collections —
                // see the comment in PlantEntryViewModel.SyncFromServiceAsync for why.
                await MainThread.InvokeOnMainThreadAsync(() =>
                {
                    PlantEntries.Add(MakeEntry(saved));
                    OnPropertyChanged(nameof(HasNoPlants));
                    ClearNewPlantForm();
                });
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

                // Marshal back to the UI thread before touching bound collections —
                // see the comment in PlantEntryViewModel.SyncFromServiceAsync for why.
                await MainThread.InvokeOnMainThreadAsync(() =>
                {
                    PlantEntries.Remove(entry);
                    OnPropertyChanged(nameof(HasNoPlants));
                });
                ShowBanner($"'{entry.Plant.CommonName}' removed.", isError: false);
            }
            catch (Exception ex) { ShowBanner(ex.Message, isError: true); }
            finally { IsBusy = false; }
        }

        private PlantEntryViewModel MakeEntry(Plant p) =>
            new(p, _svc,
                onError: msg => ShowBanner(msg, isError: true),
                onSuccess: msg => ShowBanner(msg, isError: false));

        // ─────────────────────────────────────────────────────────────────────
        //  Banner
        // ─────────────────────────────────────────────────────────────────────
        private CancellationTokenSource? _bannerCts;
        private void ShowBanner(string msg, bool isError)
        {
            _bannerCts?.Cancel();
            _bannerCts?.Dispose();
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