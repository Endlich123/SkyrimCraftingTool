using SkyrimCraftingTool.ViewModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace SkyrimCraftingTool.View
{
    /// <summary>
    /// Interaction logic for PresetsConfigView.xaml
    /// </summary>
    public partial class PresetsConfigView : System.Windows.Controls.UserControl
    {
        public PresetsConfigView()
        {
            InitializeComponent();
        }

        // Set only for the duration of the tvi.Focus() call in SlotNode_PreviewMouseLeftButtonDown -
        // see MainContentView for the full rationale. In short: Focus() makes the TreeViewItem
        // select itself natively, which re-fires SelectedItemChanged for that same click; that echo
        // must be ignored (the mouse path owns slot clicks end-to-end), but genuine keyboard arrow
        // navigation still has to flow through so the detail panel stays in sync.
        private bool _suppressNextSlotSelectionChanged;

        private void PresetsTreeView_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            if (DataContext is not PresetsConfigVM vm) return;
            if (e.NewValue is PresetSlotNodeVM && _suppressNextSlotSelectionChanged) return;

            // Preset-root / branch clicks and keyboard nav use normal single-select: drop any
            // leftover multi-selection so IsMultiSelectActive doesn't stay stuck true.
            vm.ClearMultiSelection();
            vm.SelectedNode = e.NewValue;
        }

        // Intercepts slot/type-level clicks before the TreeView's built-in single-select logic sees
        // them (e.Handled = true), so Ctrl/Shift there get their own multi-select semantics.
        private void SlotNode_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is not TreeViewItem tvi || tvi.DataContext is not PresetSlotNodeVM clicked)
                return;
            if (DataContext is not PresetsConfigVM vm)
                return;

            e.Handled = true;

            _suppressNextSlotSelectionChanged = true;
            tvi.Focus();
            _suppressNextSlotSelectionChanged = false;

            bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
            bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);

            vm.HandleSlotNodeClick(clicked, ctrl, shift);
        }
    }
}
