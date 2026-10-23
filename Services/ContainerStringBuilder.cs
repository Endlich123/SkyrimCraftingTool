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
    // One container's placements as the writer needs them: IN ORDER. Not a dictionary, although the
    // parser hands one back - the byte stability described above is the whole point of this file,
    // and a dictionary does not promise an enumeration order after something has been removed from
    // it.
    public sealed record ContainerStringEntry(
        string ContainerKey, IReadOnlyList<(string Key, int Level, int Count)> Placements);

    public static class ContainerStringBuilder
    {
        // The view-model side, which is where most strings are written from.
        public static string Build(IEnumerable<ContainerEntryVM> containers)
            => Build(containers.Select(c => new ContainerStringEntry(
                c.ContainerKey,
                c.LVLiEntries.Where(x => x.Level > 0).Select(x => (x.Key, x.Level, x.Count)).ToList())));

        // ... and the plain-data side, for code that edits a string it was handed rather than a
        // selection it owns (ContainerPlacementEditor). One writer either way: the format lives in
        // exactly one place, and a second copy of it would drift the moment a field is added - the
        // trailing count already is such a field.
        public static string Build(IEnumerable<ContainerStringEntry> containers)
        {
            var sb = new StringBuilder();
            sb.Append("{");

            foreach (var c in containers)
            {
                sb.Append($"{c.ContainerKey}: {{");

                var active = c.Placements.Where(x => x.Level > 0).ToList();

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
