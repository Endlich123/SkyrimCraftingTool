using System;
using System.Collections.Generic;

namespace SkyrimCraftingTool.Services
{
    // An armor with a template (TNAM) wears its template's body slots in game. Measured 2026-10-27
    // against KID's log: its slot counts (30: 1 003, 32: 6 102, 43: 519, 44: 718) match the template's
    // slots armor for armor, and the record's own slots nowhere (30 would be 1 400). 397 armors of the
    // user's list have own slots that differ - Thaumaturgy's enchanted Daedric helmets kept slots the
    // plain helmet no longer has.
    //
    // The tool keeps the record's own mask (what the plugin says, what a patch is diffed against) and
    // shows and classifies by this one. Slots are edited on the template; SkyPatcher carries a change
    // there over to the variants (user, 2026-10-27).
    public static class ArmorTemplateSlots
    {
        // lookup: an armor key -> its slot mask as the tool has it now (edits included) and its own
        // template key, or null when the armor is not in the scan.
        // Returns the slots of the chain's root - a template can have a template of its own - or null
        // when the item's template is not in the scan (then its own slots are all there is). rootKey
        // is the armor the slots come from.
        public static uint? Inherited(string templateKey, Func<string, (uint Mask, string TemplateKey)?> lookup, out string rootKey)
        {
            rootKey = "";
            uint? mask = null;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var key = templateKey;
            while (!string.IsNullOrWhiteSpace(key) && seen.Add(key) && seen.Count <= 8)
            {
                if (lookup(key) is not { } armor) break;
                mask = armor.Mask;
                rootKey = key;
                key = armor.TemplateKey;
            }
            return mask;
        }
    }
}
