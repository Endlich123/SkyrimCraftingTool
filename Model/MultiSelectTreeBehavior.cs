using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SkyrimCraftingTool.ViewModel;

namespace SkyrimCraftingTool.Model
{
    // Ctrl/Shift multi-select on a TreeView whose leaves are rows and whose branches are grouping.
    //
    // ONE COPY OF A DANCE THAT IS EASY TO GET WRONG. Both sub-tab kinds of the Container/LeveledList
    // tab had this in their own code-behind, in the same two handlers with two different names, and
    // MainContentView has a third copy that these were written from. The behaviour is subtle enough
    // that the comments below are most of the file - which is the argument for having it once.
    //
    // Set MultiSelectTreeBehavior.Enabled="True" on the TreeView and
    // MultiSelectTreeBehavior.IsRow="True" on the ItemContainerStyle of the LEAF level. The
    // TreeView's DataContext answers IMultiSelectTree.
    public static class MultiSelectTreeBehavior
    {
        // --- The TreeView: single-select navigation ---

        public static readonly DependencyProperty EnabledProperty =
            DependencyProperty.RegisterAttached(
                "Enabled", typeof(bool), typeof(MultiSelectTreeBehavior),
                new PropertyMetadata(false, OnEnabledChanged));

        public static void SetEnabled(DependencyObject o, bool value) => o.SetValue(EnabledProperty, value);
        public static bool GetEnabled(DependencyObject o) => (bool)o.GetValue(EnabledProperty);

        // Set for the duration of the Focus() call below, and only that long.
        private static readonly DependencyProperty SuppressProperty =
            DependencyProperty.RegisterAttached(
                "Suppress", typeof(bool), typeof(MultiSelectTreeBehavior), new PropertyMetadata(false));

        private static void OnEnabledChanged(DependencyObject o, DependencyPropertyChangedEventArgs e)
        {
            if (o is not TreeView tree) return;

            tree.SelectedItemChanged -= OnSelectedItemChanged;
            if ((bool)e.NewValue) tree.SelectedItemChanged += OnSelectedItemChanged;
        }

        // Fires for every TreeView-native selection change - not just branch clicks, but ALSO leaf
        // clicks: the Focus() below makes a TreeViewItem select itself natively as a side effect of
        // receiving keyboard focus, regardless of e.Handled (that flag only affects routed-event
        // bubbling, not this focus-driven selection).
        //
        // Mouse clicks on leaves are owned end to end by the Preview handler. The native event
        // firing alongside the same click is redundant noise that must be ignored, which the
        // suppress flag - set only around the Focus() call that provokes it - distinguishes from
        // genuine navigation by arrow key, which never goes through the Preview handler at all and
        // must still arrive here. A first version of this in MainContentView cleared the selection
        // unconditionally here, which wiped out the multi-selection the click was about to build.
        private static void OnSelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            if (sender is not TreeView tree) return;
            if (tree.DataContext is not IMultiSelectTree target) return;

            if ((bool)tree.GetValue(SuppressProperty) && target.IsRow(e.NewValue)) return;

            target.SelectSingle(e.NewValue);
        }

        // --- The leaf rows: Ctrl and Shift ---

        public static readonly DependencyProperty IsRowProperty =
            DependencyProperty.RegisterAttached(
                "IsRow", typeof(bool), typeof(MultiSelectTreeBehavior),
                new PropertyMetadata(false, OnIsRowChanged));

        public static void SetIsRow(DependencyObject o, bool value) => o.SetValue(IsRowProperty, value);
        public static bool GetIsRow(DependencyObject o) => (bool)o.GetValue(IsRowProperty);

        private static void OnIsRowChanged(DependencyObject o, DependencyPropertyChangedEventArgs e)
        {
            if (o is not TreeViewItem item) return;

            item.PreviewMouseLeftButtonDown -= OnRowClicked;
            if ((bool)e.NewValue) item.PreviewMouseLeftButtonDown += OnRowClicked;
        }

        // Intercepts leaf clicks before the TreeView's built-in single-select logic sees them
        // (e.Handled = true), so Ctrl and Shift can have their own meaning there.
        private static void OnRowClicked(object sender, MouseButtonEventArgs e)
        {
            if (sender is not TreeViewItem item) return;

            var tree = FindTree(item);
            if (tree?.DataContext is not IMultiSelectTree target) return;

            e.Handled = true;

            // Focus() triggers the TreeViewItem's own native selection synchronously, if it triggers
            // it at all - so the flag is only ever set for that one call and cannot leak into a
            // later, unrelated event.
            tree.SetValue(SuppressProperty, true);
            item.Focus();
            tree.SetValue(SuppressProperty, false);

            target.HandleRowClick(
                item.DataContext,
                Keyboard.Modifiers.HasFlag(ModifierKeys.Control),
                Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
        }

        // The row's own DataContext is the row, not the tab, so the view model is fetched from the
        // TreeView the row sits in.
        private static TreeView? FindTree(DependencyObject from)
        {
            while (from != null && from is not TreeView)
                from = VisualTreeHelper.GetParent(from);

            return from as TreeView;
        }
    }
}
