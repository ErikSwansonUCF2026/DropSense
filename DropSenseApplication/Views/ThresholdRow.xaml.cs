using Microsoft.Maui.Controls;

namespace DropSense.Views
{
    /// <summary>
    /// A single absolute-threshold input row for the Anomaly Flagging section.
    /// Renders the measurement name, a coloured unit badge, and Min / Max numeric entries.
    /// <para>
    /// Usage:
    /// <code>
    ///   &lt;views:ThresholdRow Label="Temperature"
    ///                        Unit="°C"
    ///                        MinValue="{Binding TempAbsMin}"
    ///                        MaxValue="{Binding TempAbsMax}"
    ///                        MinPlaceholder="-40"
    ///                        MaxPlaceholder="85" /&gt;
    /// </code>
    /// </para>
    /// </summary>
    public partial class ThresholdRow : ContentView
    {
        // ── Label ────────────────────────────────────────────────────────────

        public static readonly BindableProperty LabelProperty =
            BindableProperty.Create(
                nameof(Label),
                typeof(string),
                typeof(ThresholdRow),
                defaultValue: string.Empty);

        /// <summary>Measurement display name shown on the left of the row.</summary>
        public string Label
        {
            get => (string)GetValue(LabelProperty);
            set => SetValue(LabelProperty, value);
        }

        // ── Unit ─────────────────────────────────────────────────────────────

        public static readonly BindableProperty UnitProperty =
            BindableProperty.Create(
                nameof(Unit),
                typeof(string),
                typeof(ThresholdRow),
                defaultValue: string.Empty);

        /// <summary>
        /// Unit abbreviation shown in the coloured badge beneath the label
        /// (e.g. "°C", "%", "hPa", "W/m²").
        /// </summary>
        public string Unit
        {
            get => (string)GetValue(UnitProperty);
            set => SetValue(UnitProperty, value);
        }

        // ── MinValue ─────────────────────────────────────────────────────────

        public static readonly BindableProperty MinValueProperty =
            BindableProperty.Create(
                nameof(MinValue),
                typeof(string),
                typeof(ThresholdRow),
                defaultValue: string.Empty,
                defaultBindingMode: BindingMode.TwoWay);

        /// <summary>
        /// The lower bound of the acceptable range.  Stored as a string so
        /// the Entry control can hold partial input mid-type.
        /// Two-way bindable to the parent ViewModel.
        /// </summary>
        public string MinValue
        {
            get => (string)GetValue(MinValueProperty);
            set => SetValue(MinValueProperty, value);
        }

        // ── MaxValue ─────────────────────────────────────────────────────────

        public static readonly BindableProperty MaxValueProperty =
            BindableProperty.Create(
                nameof(MaxValue),
                typeof(string),
                typeof(ThresholdRow),
                defaultValue: string.Empty,
                defaultBindingMode: BindingMode.TwoWay);

        /// <summary>
        /// The upper bound of the acceptable range.
        /// Two-way bindable to the parent ViewModel.
        /// </summary>
        public string MaxValue
        {
            get => (string)GetValue(MaxValueProperty);
            set => SetValue(MaxValueProperty, value);
        }

        // ── MinPlaceholder ───────────────────────────────────────────────────

        public static readonly BindableProperty MinPlaceholderProperty =
            BindableProperty.Create(
                nameof(MinPlaceholder),
                typeof(string),
                typeof(ThresholdRow),
                defaultValue: "Min");

        /// <summary>
        /// Placeholder text for the Min entry, typically the physical lower bound
        /// of the sensor's measurement range (e.g. "-40" for temperature).
        /// </summary>
        public string MinPlaceholder
        {
            get => (string)GetValue(MinPlaceholderProperty);
            set => SetValue(MinPlaceholderProperty, value);
        }

        // ── MaxPlaceholder ───────────────────────────────────────────────────

        public static readonly BindableProperty MaxPlaceholderProperty =
            BindableProperty.Create(
                nameof(MaxPlaceholder),
                typeof(string),
                typeof(ThresholdRow),
                defaultValue: "Max");

        /// <summary>
        /// Placeholder text for the Max entry, typically the physical upper bound
        /// of the sensor's measurement range (e.g. "85" for temperature).
        /// </summary>
        public string MaxPlaceholder
        {
            get => (string)GetValue(MaxPlaceholderProperty);
            set => SetValue(MaxPlaceholderProperty, value);
        }

        // ── Constructor ──────────────────────────────────────────────────────

        public ThresholdRow()
        {
            InitializeComponent();
        }
    }
}
