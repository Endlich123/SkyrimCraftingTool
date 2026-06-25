using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace SkyrimCraftingTool.Model
{
    public class BoolToBrushConverter : IValueConverter
    {
        public System.Windows.Media.Brush SelectedBrush { get; set; } =
            new SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 120, 215)); // blau

        public System.Windows.Media.Brush UnselectedBrush { get; set; } =
            new SolidColorBrush(System.Windows.Media.Color.FromRgb(60, 60, 60)); // dunkelgrau

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool b)
                return b ? SelectedBrush : UnselectedBrush;

            return UnselectedBrush;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => System.Windows.Data.Binding.DoNothing;
    }
}