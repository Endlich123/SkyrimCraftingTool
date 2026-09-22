using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace SkyrimCraftingTool.Model
{
    // Red border when a bound key no longer resolves against the active load order
    // (see IReferenceResolver / ItemNodeVM.Is*DeadRef, IngredientEntryVM.IsDeadReference, ...).
    // Kept visually distinct from BoolToChangedBorderConverter's amber "edited" state.
    //
    // Brushes via ThemeService.LiveBrush, not the palette - see BoolToBrushConverter for why.
    public class BoolToDeadRefBorderConverter : IValueConverter
    {
        public Brush DeadBrush { get; set; } = Services.ThemeService.LiveBrush("ColorDanger");
        public Brush OkBrush { get; set; } = Services.ThemeService.LiveBrush("ColorBorder");

        public object Convert(object value, System.Type targetType, object parameter, CultureInfo culture)
            => value is bool b && b ? DeadBrush : OkBrush;

        public object ConvertBack(object value, System.Type targetType, object parameter, CultureInfo culture)
            => Binding.DoNothing;
    }
}
