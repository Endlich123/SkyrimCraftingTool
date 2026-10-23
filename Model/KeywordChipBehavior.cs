using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using SkyrimCraftingTool.ViewModel;

namespace SkyrimCraftingTool.Model
{
    // The click on keyword chips, handled ONCE on the list instead of on every chip
    // (docs/Performance-Plan.md, P-P4).
    //
    // "All keywords" builds a chip for each of ~4.300 keywords, and switching it on took 2.9 s - about
    // 0.7 ms per chip, a lot for a box with a word in it. Part of that was what every chip carried for
    // itself: its own MouseBinding with a command binding. The list does it instead: a left mouse-down
    // anywhere in it is traced up to the chip it landed on, and that chip's ToggleSelectedCommand
    // runs. Same moment as the old MouseBinding (LeftClick is mouse DOWN), same CanExecute check, and
    // handled like it. A click between chips finds no keyword and does nothing, as before.
    //
    // Hooked to MouseDown, which BUBBLES from the chip to the list - not to MouseLeftButtonDown, which
    // is a direct event. The tooltip stayed a per-chip binding: ToolTipOpening is direct as well, so
    // the list never hears it, and the first version of this - a placeholder filled in on opening -
    // would have shown an empty tooltip on every chip. Its tests caught that.
    //
    // The look is untouched - this only moves WHERE the work happens.
    public static class KeywordChipBehavior
    {
        public static readonly DependencyProperty IsChipHostProperty =
            DependencyProperty.RegisterAttached(
                "IsChipHost", typeof(bool), typeof(KeywordChipBehavior),
                new PropertyMetadata(false, OnIsChipHostChanged));

        public static bool GetIsChipHost(DependencyObject d) => (bool)d.GetValue(IsChipHostProperty);
        public static void SetIsChipHost(DependencyObject d, bool value) => d.SetValue(IsChipHostProperty, value);

        private static void OnIsChipHostChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not UIElement host) return;

            host.RemoveHandler(UIElement.MouseDownEvent, (MouseButtonEventHandler)OnMouseDown);

            if (e.NewValue is true)
                host.AddHandler(UIElement.MouseDownEvent, (MouseButtonEventHandler)OnMouseDown);
        }

        private static void OnMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left) return;

            var keyword = KeywordAt(e.OriginalSource as DependencyObject, sender as DependencyObject);
            if (keyword == null) return;

            var command = keyword.ToggleSelectedCommand;
            if (command.CanExecute(null)) command.Execute(null);
            e.Handled = true;
        }

        // Walks up from what was hit to the first element carrying a keyword, stopping at the list.
        private static KeywordSelectionVM? KeywordAt(DependencyObject? hit, DependencyObject? host)
        {
            for (var node = hit; node != null && node != host; node = ParentOf(node))
            {
                if (node is FrameworkElement { DataContext: KeywordSelectionVM keyword })
                    return keyword;
            }
            return null;
        }

        // Text runs are not visuals; their logical parent leads back into the visual tree.
        private static DependencyObject? ParentOf(DependencyObject node)
            => node is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(node)
                : LogicalTreeHelper.GetParent(node);
    }
}
