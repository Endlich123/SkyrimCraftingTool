using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace SkyrimCraftingTool.Model
{
    // Chip ground: selected vs not. Used for keyword/slot chips across the item, preset and
    // multi-select views.
    //
    // The brushes come from ThemeService.LiveBrush rather than from the palette dictionary, and not
    // by accident: a binding that runs through a converter is only re-evaluated when its SOURCE
    // changes, so a themed brush handed out here would still be the old theme's brush after a
    // switch. LiveBrush returns a shared instance ThemeService rewrites in place, so every chip
    // repaints at once. See Services/ThemeService.cs.
    public class BoolToBrushConverter : IValueConverter
    {
        public Brush SelectedBrush { get; set; } = Services.ThemeService.LiveBrush("ColorSurfaceInfo");
        public Brush UnselectedBrush { get; set; } = Services.ThemeService.LiveBrush("ColorChipBackground");

        public object Convert(object value, System.Type targetType, object parameter, CultureInfo culture)
            => value is bool b && b ? SelectedBrush : UnselectedBrush;

        public object ConvertBack(object value, System.Type targetType, object parameter, CultureInfo culture)
            => Binding.DoNothing;
    }
}
