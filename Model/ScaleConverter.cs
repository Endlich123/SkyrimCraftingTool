using System;
using System.Globalization;
using System.Windows.Data;

namespace SkyrimCraftingTool.Model
{
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
