using System.Collections.Generic;
using System.Text;

namespace SkyrimCraftingTool.Services.PatchGen
{
    // Serializes SkyPatcherRule[] to INI text. Deterministic, '\n' line endings, no trailing
    // blank line. Rules without operations are dropped. See docs/PatchGenerator-Plan.md §2.
    public static class SkyPatcherIniWriter
    {
        public static string Write(IEnumerable<SkyPatcherRule> rules, string? headerComment = null)
        {
            var sb = new StringBuilder();

            if (!string.IsNullOrWhiteSpace(headerComment))
            {
                foreach (var line in headerComment.Replace("\r\n", "\n").Split('\n'))
                    sb.Append("; ").Append(line).Append('\n');
            }

            bool anyHeader = sb.Length > 0;
            bool first = true;

            foreach (var rule in rules)
            {
                if (!rule.HasChanges) continue;

                if (!first || anyHeader) sb.Append('\n');
                first = false;

                if (!string.IsNullOrWhiteSpace(rule.Comment))
                    sb.Append("; ").Append(rule.Comment).Append('\n');

                sb.Append(rule.FilterDirective)
                  .Append('=')
                  .Append(rule.TargetPlugin)
                  .Append('|')
                  .Append(PatchFormat.FormId8(rule.TargetFormId))
                  .Append(':')
                  .Append(string.Join(":", rule.Operations))
                  .Append('\n');
            }

            return sb.ToString();
        }
    }
}
