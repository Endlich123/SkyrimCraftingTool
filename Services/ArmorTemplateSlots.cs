using System;
using System.Collections.Generic;

namespace SkyrimCraftingTool.Services
{
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
