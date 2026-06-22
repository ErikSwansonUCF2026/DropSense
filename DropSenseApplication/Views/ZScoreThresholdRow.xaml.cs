using System;
using Microsoft.Maui.Controls;

namespace DropSense.Views
{
    /// <summary>
    /// A Z-score threshold input row for the Anomaly Flagging section.
    /// Renders the measurement name, a "Z-score" descriptor badge, and
    /// Lower-Z / Upper-Z numeric entries.
    ///
    /// <para>
    /// Validation guidance: Lower-Z should be ≤ 0 and Upper-Z should be ≥ 0.
    /// The ViewModel handles final numeric validation before export; this view
    /// provides only lightweight visual feedback on entry change.
    /// </para>
    ///
    /// <para>
    /// Usage:
    /// <code>
    ///   &lt;views:ZScoreThresholdRow Label="Temperature"
    ///                              MinZ="{Binding TempZMin}"
    ///                              MaxZ="{Binding TempZMax}" /&gt;
    /// </code>
    /// </para>
    /// </summary>
    public partial class ZScoreThresholdRow : ContentView
    {
        // ── Label ────────────────────────────────────────────────────────────

        public static readonly BindableProperty LabelProperty =
            BindableProperty.Create(
                nameof(Label),
                typeof(string),
                typeof(ZScoreThresholdRow),
                defaultValue: string.Empty);

        /// <summary>Measurement display name shown on the left of the row.</summary>
        public string Label
        {
            get => (string)GetValue(LabelProperty);
            set => SetValue(LabelProperty, value);
        }

        // ── MinZ ─────────────────────────────────────────────────────────────

        public static readonly BindableProperty MinZProperty =
            BindableProperty.Create(
                nameof(MinZ),
                typeof(string),
                typeof(ZScoreThresholdRow),
                defaultValue: "-3.0",
                defaultBindingMode: BindingMode.TwoWay);

        /// <summary>
        /// The lower Z-score boundary (typically a negative value, e.g. "-3.0").
        /// Data points whose Z-score is below this value are flagged as anomalous.
        /// Two-way bindable to the parent ViewModel.
        /// </summary>
        public string MinZ
        {
            get => (string)GetValue(MinZProperty);
            set => SetValue(MinZProperty, value);
        }

        // ── MaxZ ─────────────────────────────────────────────────────────────

        public static readonly BindableProperty MaxZProperty =
            BindableProperty.Create(
                nameof(MaxZ),
                typeof(string),
                typeof(ZScoreThresholdRow),
                defaultValue: "3.0",
                defaultBindingMode: BindingMode.TwoWay);

        /// <summary>
        /// The upper Z-score boundary (typically a positive value, e.g. "3.0").
        /// Data points whose Z-score exceeds this value are flagged as anomalous.
        /// Two-way bindable to the parent ViewModel.
        /// </summary>
        public string MaxZ
        {
            get => (string)GetValue(MaxZProperty);
            set => SetValue(MaxZProperty, value);
        }

        // ── Constructor ──────────────────────────────────────────────────────

        public ZScoreThresholdRow()
        {
            InitializeComponent();
        }

        // ── Entry change handler ─────────────────────────────────────────────

        /// <summary>
        /// Called whenever either Z entry's text changes.
        /// Applies a visual warning stroke on the entry when the value is
        /// out of the expected directional range:
        ///   • Lower-Z should be &lt;= 0
        ///   • Upper-Z should be >= 0
        ///
        /// This is purely cosmetic feedback; the ViewModel validates before export.
        /// </summary>
        private void OnZValueChanged(object sender, TextChangedEventArgs e)
        {
            if (sender is not Entry entry) return;

            bool isMinEntry = entry == MinZEntry;

            // Skip validation on incomplete/partial input (e.g. just "-")
            if (string.IsNullOrWhiteSpace(e.NewTextValue) || e.NewTextValue == "-")
            {
                ClearEntryWarning(entry);
                return;
            }

            if (!double.TryParse(e.NewTextValue, out double parsed))
            {
                // Non-numeric: show warning
                ApplyEntryWarning(entry);
                return;
            }

            bool isValid = isMinEntry
                ? parsed <= 0      // Lower-Z must be ≤ 0
                : parsed >= 0;     // Upper-Z must be ≥ 0

            if (isValid)
                ClearEntryWarning(entry);
            else
                ApplyEntryWarning(entry);
        }

        private static void ApplyEntryWarning(Entry entry)
        {
            // Visually signal a potential input error via text colour.
            // The wrapping Border's stroke could also be changed here if a
            // converter is wired to an IsError state; keeping it simple for now.
            entry.TextColor = Color.FromArgb("#E53935"); // ErrorRed equivalent
        }

        private static void ClearEntryWarning(Entry entry)
        {
            // Revert to the app's normal TextPrimary colour.
            // In production, resolve from the ResourceDictionary; hardcoded
            // here to avoid a dependency on Application.Current at control init.
            entry.TextColor = Color.FromArgb("#1A1A2E"); // TextPrimary
        }
    }
}
