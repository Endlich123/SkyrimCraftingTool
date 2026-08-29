using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace SkyrimCraftingTool.Model
{
    // Highlights a field's border when it differs from the value ItemNodeVM captured at load time
    // (see ItemNodeVM.CaptureOriginalSnapshot / Is*Changed properties).
    public class BoolToChangedBorderConverter : IValueConverter
    {
        public System.Windows.Media.Brush ChangedBrush { get; set; } =
            new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xD9, 0xA3, 0x00)); // ColorWarning

        public System.Windows.Media.Brush UnchangedBrush { get; set; } =
            new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x55, 0x55, 0x55)); // ColorBorder

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool b && b)
                return ChangedBrush;

            return UnchangedBrush;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => System.Windows.Data.Binding.DoNothing;
    }
}
