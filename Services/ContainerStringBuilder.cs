using SkyrimCraftingTool.ViewModel;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace SkyrimCraftingTool.Services
{
    // Writes the ContainerString that ContainerStringParser reads back.
    //
    // The count is only written when it is not 1. Two reasons, and the second is the important one:
    // a string stays byte-identical to what older builds produced as long as nobody sets a count, so
    // existing items are not all marked as changed by a version upgrade - and a preset file written
    // here still loads in a build that predates the field.
    public static class ContainerStringBuilder
    {
        public static string Build(IEnumerable<ContainerEntryVM> containers)
        {
            var sb = new StringBuilder();
            sb.Append("{");

            foreach (var c in containers)
            {
                sb.Append($"{c.ContainerKey}: {{");

                var active = c.LVLiEntries.Where(x => x.Level > 0).ToList();

                for (int i = 0; i < active.Count; i++)
                {
                    var lv = active[i];
                    sb.Append(lv.Count == ContainerStringParser.DefaultCount
                        ? $"{lv.Key},{lv.Level.ToString(CultureInfo.InvariantCulture)}"
                        : $"{lv.Key},{lv.Level.ToString(CultureInfo.InvariantCulture)},{lv.Count.ToString(CultureInfo.InvariantCulture)}");

                    if (i < active.Count - 1)
                        sb.Append("; ");
                }

                sb.Append("}; ");
            }

            sb.Append("}");
            return sb.ToString();
        }
    }
}
