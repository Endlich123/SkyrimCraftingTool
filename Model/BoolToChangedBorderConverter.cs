using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace SkyrimCraftingTool.Model
{
    // Highlights a field's border when it differs from the value ItemNodeVM captured at load time
    // (see ItemNodeVM.CaptureOriginalSnapshot / Is*Changed properties).
    //
    // Brushes via ThemeService.LiveBrush, not the palette - see BoolToBrushConverter for why a
    // converter cannot take a themed brush directly.
    public class BoolToChangedBorderConverter : IValueConverter
    {
        public Brush ChangedBrush { get; set; } = Services.ThemeService.LiveBrush("ColorWarning");
        public Brush UnchangedBrush { get; set; } = Services.ThemeService.LiveBrush("ColorBorder");

        public object Convert(object value, System.Type targetType, object parameter, CultureInfo culture)
            => value is bool b && b ? ChangedBrush : UnchangedBrush;

        public object ConvertBack(object value, System.Type targetType, object parameter, CultureInfo culture)
            => Binding.DoNothing;
    }
}
