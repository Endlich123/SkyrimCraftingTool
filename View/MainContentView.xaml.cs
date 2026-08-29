using SkyrimCraftingTool.ViewModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;

namespace SkyrimCraftingTool.View
{
    public partial class MainContentView : System.Windows.Controls.UserControl
    {
        public MainContentView()
        {
            InitializeComponent();
        }

        private async void MainContentView_Loaded(object sender, RoutedEventArgs e)
        {
            if (DataContext is MainContentVM vm)
                await vm.LoadInitialDataAsync();
        }

        // Set for the duration of tvi.Focus() below (and only that long) - see its comment for why.
        private bool _suppressNextItemSelectionChanged;

        // Fires for every TreeView-native selection change - not just plugin/category clicks, but
        // ALSO item-level clicks: tvi.Focus() below makes TreeViewItem select itself natively as a
        // side effect of receiving keyboard focus, regardless of e.Handled (that flag only affects
        // routed-event bubbling, not this focus-driven selection). A first version of this fix called
        // ClearMultiSelection() unconditionally here, which fired on every single item click too -
        // wiping out the multi-selection HandleItemNodeClick had just built (or was about to build)
        // BEFORE it could process that click's Ctrl/Shift semantics, collapsing every click down to
        // one item and breaking multi-select entirely.
        //
        // Mouse clicks on items are owned end-to-end by HandleItemNodeClick (see the Preview handler
        // below) - the native event firing alongside it for that same click is redundant noise that
        // must be ignored, which _suppressNextItemSelectionChanged (set only around the Focus() call
        // that provokes it) distinguishes from genuine item selection via keyboard arrow-key
        // navigation, which never goes through the Preview handler at all and must still flow through
        // here to keep the detail panel in sync. Plugin/category nodes have no Preview interception of
        // their own, so a click there always reaches here: clear any leftover multi-selection so
        // IsMultiSelectActive doesn't stay stuck true (MultiSelectDetailView) while SelectedNode also
        // updates (single-item/plugin view), which is what rendered both views on top of each other.
        private void MainTreeView_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            if (DataContext is not MainContentVM vm) return;
            if (e.NewValue is ItemNodeVM && _suppressNextItemSelectionChanged) return;

            vm.ClearMultiSelection();
            vm.SelectedNode = e.NewValue;
        }

        // Intercepts item-level clicks before TreeView's built-in single-select logic sees them
        // (e.Handled = true), so Ctrl/Shift there can get their own multi-select semantics.
        private void ItemNode_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is not TreeViewItem tvi || tvi.DataContext is not ItemNodeVM clicked)
                return;
            if (DataContext is not MainContentVM vm)
                return;

            e.Handled = true;

            // Focus() triggers TreeViewItem's own native selection synchronously (if it triggers it at
            // all) - the flag is only ever "true" for that one call, so it can't leak into unrelated
            // later events (e.g. a keyboard nav right after a click that happened not to change focus).
            _suppressNextItemSelectionChanged = true;
            tvi.Focus();
            _suppressNextItemSelectionChanged = false;

            bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
            bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);

            vm.HandleItemNodeClick(clicked, ctrl, shift);
        }

        private void Slider_ThumbDragCompleted(object sender, DragCompletedEventArgs e)
        {
            if (sender is Slider s)
            {
                // push binding explicitly
                BindingExpression be = s.GetBindingExpression(Slider.ValueProperty);
                be?.UpdateSource();

                // After updating the LVLi VM, rebuild the container string for the selected item
                if (DataContext is MainContentVM vm && vm.SelectedNode is ItemNodeVM item)
                {
                    item.ContainerString = item.ContainerSelection.BuildString();
                    // sync left-hand selection flags
                    vm.UpdateAllContainerSelectionFlags(item);
                }
            }
        }
    }
}
