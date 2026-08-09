using SkyrimCraftingTool.ViewModel;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SkyrimCraftingTool.Services
{
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
                    sb.Append($"{lv.Key},{lv.Level}");
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
