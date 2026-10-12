using System;
using System.Globalization;
using System.Windows.Data;

namespace SkyrimCraftingTool.Model
{
    // A number times the ConverterParameter - for "at most this share of that element's height".
    //
    // Written for the Container/LeveledList pane, where the blocks above the contents list (the own
    // list's panel, the list's odds) are capped at a share of the pane and scroll inside it: a DockPanel
    // gives a top-docked block whatever it asks for and simply cuts off what does not fit, which in a
    // smaller window made "This list and its odds" unreachable (user, 2026-10-12).
    public sealed class ScaleConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is not double number || double.IsNaN(number) || double.IsInfinity(number))
                return double.PositiveInfinity;

            var factor = parameter switch
            {
                double d => d,
                string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
                _ => 1.0,
            };

            return Math.Max(0, number * factor);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
