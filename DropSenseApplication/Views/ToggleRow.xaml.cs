using Microsoft.Maui.Controls;

namespace DropSense.Views
{
    /// <summary>
    /// A labelled Switch row.
    /// <para>
    /// Usage in XAML:
    /// <code>
    ///   &lt;views:ToggleRow Label="Temperature"
    ///                    IsToggled="{Binding IncludeTemperature}"
    ///                    HintText="Optional hint shown below the label"
    ///                    IsEnabled="{Binding SomePrerequisite}" /&gt;
    /// </code>
    /// </para>
    /// </summary>
    public partial class ToggleRow : ContentView
    {
        // ── Label ────────────────────────────────────────────────────────────

        public static readonly BindableProperty LabelProperty =
            BindableProperty.Create(
                nameof(Label),
                typeof(string),
                typeof(ToggleRow),
                defaultValue: string.Empty);

        /// <summary>Primary display label shown to the left of the switch.</summary>
        public string Label
        {
            get => (string)GetValue(LabelProperty);
            set => SetValue(LabelProperty, value);
        }

        // ── HintText ─────────────────────────────────────────────────────────

        public static readonly BindableProperty HintTextProperty =
            BindableProperty.Create(
                nameof(HintText),
                typeof(string),
                typeof(ToggleRow),
                defaultValue: null);

        /// <summary>
        /// Optional secondary hint shown in smaller text beneath the label.
        /// The hint row is collapsed when this is null or empty.
        /// </summary>
        public string HintText
        {
            get => (string)GetValue(HintTextProperty);
            set => SetValue(HintTextProperty, value);
        }

        // ── IsToggled ────────────────────────────────────────────────────────

        public static readonly BindableProperty IsToggledProperty =
            BindableProperty.Create(
                nameof(IsToggled),
                typeof(bool),
                typeof(ToggleRow),
                defaultValue: false,
                defaultBindingMode: BindingMode.TwoWay);

        /// <summary>The on/off state of the inner Switch. Two-way bindable.</summary>
        public bool IsToggled
        {
            get => (bool)GetValue(IsToggledProperty);
            set => SetValue(IsToggledProperty, value);
        }

        // ── Constructor ──────────────────────────────────────────────────────

        public ToggleRow()
        {
            InitializeComponent();
        }
    }
}
