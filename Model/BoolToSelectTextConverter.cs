using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Data;

namespace SkyrimCraftingTool.Model
{
    public class BoolToSelectTextConverter : IValueConverter
    {
        // Gibt "Selected" zurück, wenn true, sonst "Select"
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool b && b)
                return "Selected";
            return "Select";
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            // nicht benötigt für OneWay-Bindings
            return System.Windows.Data.Binding.DoNothing;
        }
    }
}
