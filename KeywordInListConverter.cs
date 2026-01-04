using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Data;

namespace SkyrimCraftingTool;
public class KeywordInListConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        string keyword = value as string;
        var list = parameter as ObservableCollection<string>;

        return list?.Contains(keyword) ?? false;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool isChecked = (bool)value;
        string keyword = parameter as string;
        var list = parameter as ObservableCollection<string>;

        if (isChecked)
        {
            if (!list.Contains(keyword))
                list.Add(keyword);
        }
        else
        {
            list.Remove(keyword);
        }

        return System.Windows.Data.Binding.DoNothing;
    }
}
