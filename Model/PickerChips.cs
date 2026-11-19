using System;
using System.Collections;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Data;
using SkyrimCraftingTool.ViewModel;

namespace SkyrimCraftingTool.Model
{
    // Hangs a PickerFilter on a ComboBox so ItemComboBoxStyle's dropdown can show its chips
    // (Controls.xaml, "ChipBar"). A box without one looks exactly as before.
    //
    // THE FILTER IS HANDED TO THE CHIP BAR AS ITS DATACONTEXT, FROM HERE. The obvious way - template
    // bindings on the path (model:PickerChips.Filter).Chips - does not resolve: WPF reports
    // "property not found on ComboBox" for an attached-property path inside this ControlTemplate
    // (caught by PickerChipBarViewTests, 2026-11-07). So the bar's DataContext is null in the template
    // (no binding noise on the hundreds of boxes without chips) and set to the filter whenever the
    // template is there: on change of the property and on every Loaded.
    public static class PickerChips
    {
        public static readonly DependencyProperty FilterProperty =
            DependencyProperty.RegisterAttached(
                "Filter",
                typeof(PickerFilter),
                typeof(PickerChips),
                new FrameworkPropertyMetadata(null, OnFilterChanged));

        public static PickerFilter? GetFilter(DependencyObject element) => (PickerFilter?)element.GetValue(FilterProperty);
        public static void SetFilter(DependencyObject element, PickerFilter? value) => element.SetValue(FilterProperty, value);

        private static void OnFilterChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not System.Windows.Controls.Control box) return;

            box.Loaded -= OnLoaded;
            if (e.NewValue != null) box.Loaded += OnLoaded;

            Attach(box);
        }

        private static void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.Control box) Attach(box);
        }

        private static void Attach(System.Windows.Controls.Control box)
        {
            if (box.Template?.FindName("ChipBar", box) is FrameworkElement bar)
                bar.DataContext = GetFilter(box);
        }
    }

    // For pickers whose list is someone else's - the HasPerk condition boxes all bind their item's
    // perk catalogue, but each condition has its own chips. Bindings, in order:
    //   [0] the list   [1] the PickerFilter   [2] its Version (only to re-run)   [3] the current pick
    // Unfiltered it hands the list back untouched (same instance, so the ComboBox does not rebuild).
    // Filtered it always keeps [3]: a ComboBox whose SelectedItem leaves its ItemsSource writes null
    // back, and on a condition that blanks the perk.
    public sealed class PickerOptionsConverter : IMultiValueConverter
    {
        public object? Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            var source = values.Length > 0 ? values[0] as IEnumerable : null;
            var filter = values.Length > 1 ? values[1] as PickerFilter : null;
            var current = values.Length > 3 ? values[3] : null;

            if (source == null) return null;
            if (filter == null) return source;

            filter.EnsureSource(source);
            if (!filter.HasActive) return source;

            return source.Cast<object>()
                .Where(o => ReferenceEquals(o, current) || filter.Passes(o))
                .ToList();
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
