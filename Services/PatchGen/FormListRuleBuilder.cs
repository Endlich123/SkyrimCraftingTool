using System;
using System.Collections.Generic;
using System.Linq;
using SkyrimCraftingTool.Model;

namespace SkyrimCraftingTool.Services.PatchGen
{
    // Pure diff: (pristine FLST members, edited members) -> one filterByFormLists rule, or null when
    // the sets are equal. Target folder is "formList" - camelCase, that is SkyPatcher's own spelling.
    //
    // formsToAdd/formsToRemove are additive, so unlike an ESP FLST override this composes with other
    // mods touching the same list. See docs/EnchantmentPatch-Plan.md (E-P3).
    public static class FormListRuleBuilder
    {
        public static SkyPatcherRule? BuildRule(FormListPatchPair pair)
        {
            var orig = Normalize(pair.OriginalMembers);
            var edited = Normalize(pair.EditedMembers);

            var origSet = new HashSet<string>(orig, StringComparer.OrdinalIgnoreCase);
            var editedSet = new HashSet<string>(edited, StringComparer.OrdinalIgnoreCase);

            var added = edited.Where(k => !origSet.Contains(k)).ToList();
            var removed = orig.Where(k => !editedSet.Contains(k)).ToList();
            if (added.Count == 0 && removed.Count == 0) return null;

            var ops = new List<string>();
            if (added.Count > 0)
                ops.Add("formsToAdd=" + string.Join(",", added.Select(PatchFormat.RefKey8)));
            if (removed.Count > 0)
                ops.Add("formsToRemove=" + string.Join(",", removed.Select(PatchFormat.RefKey8)));

            // The rule targets the LIST, so it is grouped and filed under the FLST's own plugin -
            // not the plugin of whichever enchantment happens to point at it.
            var (plugin, formId) = KeyFactory.SplitMasterKey(pair.ListKey);

            return new SkyPatcherRule
            {
                FilterDirective = "filterByFormLists",
                TargetPlugin = plugin,
                TargetFormId = formId,
                Comment = $"worn-restriction list {pair.ListKey}",
                Operations = ops,
                ReferencedKeywordKeys = added.Concat(removed).ToList(),
            };
        }

        private static List<string> Normalize(IEnumerable<string>? keys)
        {
            var list = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (keys == null) return list;
            foreach (var raw in keys)
            {
                var k = (raw ?? "").Trim();
                if (k.Length == 0) continue;
                if (seen.Add(k)) list.Add(k);
            }
            return list;
        }
    }
}
