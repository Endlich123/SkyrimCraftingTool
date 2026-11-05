using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace SkyrimCraftingTool.Model
{
    // true -> Visible, false -> Hidden (NOT Collapsed): the element keeps its space. For something
    // that is covered by an overlay and must neither show through nor let its neighbours slide into
    // the gap it would leave - the editable OR box under a read-only condition row.
    public class BoolToHiddenConverter : IValueConverter
    {
        public object Convert(object value, System.Type targetType, object parameter, CultureInfo culture)
            => value is bool b && b ? Visibility.Visible : Visibility.Hidden;

        public object ConvertBack(object value, System.Type targetType, object parameter, CultureInfo culture)
            => Binding.DoNothing;
    }
}
