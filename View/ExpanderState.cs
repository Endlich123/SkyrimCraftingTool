using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using SkyrimCraftingTool.Model;

namespace SkyrimCraftingTool.View
{
    // Remembers whether an Expander was open, across tab switches and restarts:
    // views:ExpanderState.Key="items.crafting". Stored in Input\prefs.json as "expander.<key>".
    //
    // Needed because a tab's view is BUILT AGAIN every time the tab is shown - MainWindow shows the
    // current one through a ContentControl and a DataTemplate - and every Expander in it came back
    // at whatever IsExpanded the XAML says. That XAML value stays the default: without an entry in
    // prefs.json nothing changes.
    //
    // Only for expanders whose state is nobody else's. One bound to a view model
    // (IsExpanded="{Binding ...}") keeps its binding and does not get a key.
    public static class ExpanderState
    {
        private const string Prefix = "expander.";

        // While a tour runs it opens and closes sections to point into them. That is the tour's
        // doing, not a preference, so nothing is written meanwhile. A counter, not a bool, so a
        // tour restarted from inside another cannot unlock it early.
        private static int _suspended;
        public static void Suspend() => _suspended++;
        public static void Resume() { if (_suspended > 0) _suspended--; }

        public static readonly DependencyProperty KeyProperty =
            DependencyProperty.RegisterAttached("Key", typeof(string), typeof(ExpanderState),
                new PropertyMetadata(null, OnKeyChanged));

        public static string? GetKey(DependencyObject element) => (string?)element.GetValue(KeyProperty);
        public static void SetKey(DependencyObject element, string? value) => element.SetValue(KeyProperty, value);

        private static void OnKeyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not Expander expander || DesignerProperties.GetIsInDesignMode(d)) return;

            expander.Expanded -= OnToggled;
            expander.Collapsed -= OnToggled;
            expander.Initialized -= OnInitialized;
            expander.Loaded -= OnLoaded;

            if (string.IsNullOrWhiteSpace(e.NewValue as string)) return;

            expander.Expanded += OnToggled;
            expander.Collapsed += OnToggled;
            expander.Loaded += OnLoaded;

            // After the XAML attributes, not before: IsExpanded="False" written after the key would
            // otherwise overwrite the restored value.
            if (expander.IsInitialized) Restore(expander);
            else expander.Initialized += OnInitialized;
        }

        private static void OnInitialized(object? sender, System.EventArgs e)
        {
            if (sender is Expander expander)
            {
                expander.Initialized -= OnInitialized;
                Restore(expander);
            }
        }

        // Again on every load: two instances of the same section can be alive at once (the armor
        // and the weapon block of the item view), and the one shown next has to pick up what was
        // changed in the other. Not during a tour, which may have opened this one on purpose.
        private static void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (_suspended == 0 && sender is Expander expander) Restore(expander);
        }

        private static void Restore(Expander expander)
        {
            var key = GetKey(expander);
            if (string.IsNullOrWhiteSpace(key)) return;

            bool open = AppPrefs.GetBool(Prefix + key, expander.IsExpanded);
            if (expander.IsExpanded != open) expander.IsExpanded = open;
        }

        private static void OnToggled(object sender, RoutedEventArgs e)
        {
            // Expanded/Collapsed BUBBLE: a section nested inside this one would otherwise be saved
            // under this one's key.
            if (!ReferenceEquals(e.OriginalSource, sender) || sender is not Expander expander) return;
            if (_suspended > 0) return;

            var key = GetKey(expander);
            if (string.IsNullOrWhiteSpace(key)) return;

            // Only on a real change: AppPrefs rewrites the whole file per call, and a restore that
            // just set the stored value fires this too.
            bool open = expander.IsExpanded;
            if (AppPrefs.GetBool(Prefix + key, !open) != open)
                AppPrefs.SetBool(Prefix + key, open);
        }
    }
}
