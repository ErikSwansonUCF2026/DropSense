using Microsoft.Maui.Controls;

namespace DropSense.Views
{
    /// <summary>
    /// Pill-shaped selectable chip for mutually-exclusive option groups.
    /// <para>
    /// The chip toggles <see cref="IsSelected"/> on tap.  Mutual exclusivity
    /// between siblings is enforced by the parent ViewModel (each chip's
    /// <see cref="IsSelected"/> is bound two-way to a distinct VM property,
    /// and setting one to <c>true</c> sets the others to <c>false</c>).
    /// </para>
    /// <para>
    /// Usage:
    /// <code>
    ///   &lt;views:ChipToggle Label="Line"    IsSelected="{Binding GraphTypeLine}" /&gt;
    ///   &lt;views:ChipToggle Label="Scatter" IsSelected="{Binding GraphTypeScatter}" /&gt;
    ///   &lt;views:ChipToggle Label="Bar"     IsSelected="{Binding GraphTypeBar}" /&gt;
    /// </code>
    /// </para>
    /// </summary>
    public partial class ChipToggle : ContentView
    {
        // ── Label ────────────────────────────────────────────────────────────

        public static readonly BindableProperty LabelProperty =
            BindableProperty.Create(
                nameof(Label),
                typeof(string),
                typeof(ChipToggle),
                defaultValue: string.Empty);

        /// <summary>Text displayed inside the chip.</summary>
        public string Label
        {
            get => (string)GetValue(LabelProperty);
            set => SetValue(LabelProperty, value);
        }

        // ── IsSelected ───────────────────────────────────────────────────────

        public static readonly BindableProperty IsSelectedProperty =
            BindableProperty.Create(
                nameof(IsSelected),
                typeof(bool),
                typeof(ChipToggle),
                defaultValue: false,
                defaultBindingMode: BindingMode.TwoWay);

        /// <summary>
        /// Whether this chip is in the selected/active state.
        /// Two-way bindable — the ViewModel both reads and writes this.
        /// </summary>
        public bool IsSelected
        {
            get => (bool)GetValue(IsSelectedProperty);
            set => SetValue(IsSelectedProperty, value);
        }

        // ── Constructor ──────────────────────────────────────────────────────

        public ChipToggle()
        {
            InitializeComponent();
        }

        // ── Tap handler ──────────────────────────────────────────────────────

        /// <summary>
        /// Toggles IsSelected on tap.  The ViewModel's setter is responsible
        /// for clearing any competing siblings via mutual-exclusion logic.
        /// </summary>
        private void OnChipTapped(object sender, System.EventArgs e)
        {
            // Only allow de-selection if already selected; selecting is always allowed.
            // For true radio behaviour, remove the "if not already selected" guard:
            if (!IsSelected)
                IsSelected = true;
        }
    }
}
