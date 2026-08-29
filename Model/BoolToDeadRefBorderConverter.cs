using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace SkyrimCraftingTool.Model
{
    // Red border when a bound key no longer resolves against the active load order
    // (see IReferenceResolver / ItemNodeVM.Is*DeadRef, IngredientEntryVM.IsDeadReference, ...).
    // Kept visually distinct from BoolToChangedBorderConverter's amber "edited" state.
    // Fully-qualifies Media.Brush because the project also references WinForms (System.Drawing.Brush).
    public class BoolToDeadRefBorderConverter : IValueConverter
    {
        public System.Windows.Media.Brush DeadBrush { get; set; } =
            new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xC0, 0x39, 0x2B)); // red

        public System.Windows.Media.Brush OkBrush { get; set; } =
            new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x55, 0x55, 0x55)); // ColorBorder

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => value is bool b && b ? DeadBrush : OkBrush;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => System.Windows.Data.Binding.DoNothing;
    }
}
