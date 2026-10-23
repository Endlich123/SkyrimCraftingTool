using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Data;

namespace SkyrimCraftingTool.Model
{
    public class SlotMaskToTextConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is uint mask)
            {
                var entries = new List<string>();

                foreach (ArmorSlotMask slot in Enum.GetValues(typeof(ArmorSlotMask)))
                {
                    if (slot == ArmorSlotMask.None)
                        continue;

                    uint flag = (uint)slot;

                    if ((mask & flag) != 0)
                    {
                        int bit = (int)Math.Log(flag, 2);
                        entries.Add($"{slot} (Slot {bit})");
                    }
                }

                return string.Join(", ", entries);
            }

            return "";
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is string text)
            {
                uint mask = 0;

                foreach (var part in text.Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    var name = part.Trim();

                    // Entferne "(Slot X)"
                    int idx = name.IndexOf(" (Slot");
                    if (idx > 0)
                        name = name.Substring(0, idx);

                    if (Enum.TryParse(name, out ArmorSlotMask slot))
                        mask |= (uint)slot;
                }

                return mask;
            }

            return 0u;
        }
    }

}
