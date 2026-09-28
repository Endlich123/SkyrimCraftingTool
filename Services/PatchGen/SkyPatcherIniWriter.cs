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

                // A group rule brings its own filter clauses - several of them, none of which is a
                // target (see SkyPatcherRule.FilterClauses). Every other rule filters on the one
                // record it edits, so "directive=target" is the whole filter.
                if (rule.FilterClauses.Count > 0)
                {
                    sb.Append(string.Join(":", rule.FilterClauses))
                      .Append(':')
                      .Append(string.Join(":", rule.Operations))
                      .Append('\n');
                    continue;
                }

                // A merged NPC rule names several targets on one line (see
                // NpcRuleBuilder.MergeIdenticalRules); every other rule names the single pair it was
                // built with.
                string targets = rule.ExplicitTargets.Count > 0
                    ? string.Join(",", rule.ExplicitTargets)
                    : rule.TargetPlugin + "|" + PatchFormat.FormId8(rule.TargetFormId);

                sb.Append(rule.FilterDirective)
                  .Append('=')
                  .Append(targets)
                  .Append(':')
                  .Append(string.Join(":", rule.Operations))
                  .Append('\n');
            }

            return sb.ToString();
        }
    }
}
