using SkyrimCraftingTool.ViewModel;
using System;
using System.Collections.Generic;
using System.Linq;

namespace SkyrimCraftingTool.Services
{
    // Armor/Weapon keyword exclusivity rules, shared between ItemNodeVM (editing a real item) and
    // PresetSlotNodeVM (editing a preset's keyword list) so a preset's Armor slot behaves exactly like
    // an item of that type would - e.g. picking ArmorClothing there also blocks the plain Armor*
    // keywords, instead of only catching the conflict later when the preset gets applied.
    public static class KeywordRuleEngine
    {
        public static void ApplyExclusivityRules(IEnumerable<KeywordSelectionVM> allKeywords, KeywordSelectionVM changedKeyword)
        {
            if (changedKeyword == null) return;
            var keywords = allKeywords as IList<KeywordSelectionVM> ?? allKeywords.ToList();

            bool isLight = IsArmorLight(changedKeyword);
            bool isHeavy = IsArmorHeavy(changedKeyword);
            bool isClothing = IsArmorClothing(changedKeyword);

            // ---------------------------------------------------------
            // 0) ArmorLight / ArmorHeavy / ArmorClothing are exclusive
            // ---------------------------------------------------------
            if (changedKeyword.IsSelected && IsArmorCategory(changedKeyword))
            {
                foreach (var kw in keywords)
                {
                    if (kw != changedKeyword && IsArmorCategory(kw) && kw.IsSelected)
                        kw.IsSelected = false;
                }
            }

            if (changedKeyword.IsSelected && IsArmorMaterial(changedKeyword))
            {
                foreach (var kw in keywords)
                {
                    if (kw != changedKeyword && IsArmorMaterial(kw) && kw.IsSelected)
                        kw.IsSelected = false;
                }
            }

            if (changedKeyword.IsSelected && IsWeapMaterial(changedKeyword))
            {
                foreach (var kw in keywords)
                {
                    if (kw != changedKeyword && IsWeapMaterial(kw) && kw.IsSelected)
                        kw.IsSelected = false;
                }
            }

            // ---------------------------------------------------------
            // 1) ArmorLight / ArmorHeavy -> blocks all Clothing*
            // ---------------------------------------------------------
            if (changedKeyword.IsSelected && (isLight || isHeavy))
            {
                foreach (var kw in keywords)
                {
                    if (IsClothingKeyword(kw))
                    {
                        kw.IsReadOnly = true;
                        kw.IsSelected = false;
                    }
                }
            }
            else
            {
                // Release Clothing again if no Light/Heavy is active
                bool anyLightOrHeavy = keywords.Any(kw => kw.IsSelected &&
                    (IsArmorLight(kw) || IsArmorHeavy(kw)));

                foreach (var kw in keywords)
                {
                    if (IsClothingKeyword(kw))
                    {
                        kw.IsReadOnly = anyLightOrHeavy;
                        if (anyLightOrHeavy && kw.IsSelected)
                            kw.IsSelected = false;
                    }
                }
            }

            // ---------------------------------------------------------
            // 2) ArmorClothing -> blocks all Armor* except exceptions
            // ---------------------------------------------------------
            if (changedKeyword.IsSelected && isClothing)
            {
                foreach (var kw in keywords)
                {
                    if (IsArmorKeyword(kw) &&
                        !IsArmorMaterial(kw) &&
                        !IsArmorLight(kw) &&
                        !IsArmorHeavy(kw) &&
                        !IsArmorClothing(kw))
                    {
                        kw.IsReadOnly = true;
                        kw.IsSelected = false;
                    }
                }
            }
            else
            {
                // Release Armor again if no Clothing is active
                bool anyClothing = keywords.Any(kw => kw.IsSelected && IsArmorClothing(kw));

                foreach (var kw in keywords)
                {
                    if (IsArmorKeyword(kw) &&
                        !IsArmorMaterial(kw) &&
                        !IsArmorLight(kw) &&
                        !IsArmorHeavy(kw) &&
                        !IsArmorClothing(kw))
                    {
                        kw.IsReadOnly = anyClothing;
                        if (anyClothing && kw.IsSelected)
                            kw.IsSelected = false;
                    }
                }
            }
        }

        public static bool IsArmorLight(KeywordSelectionVM kw) =>
            kw?.Name?.StartsWith("ArmorLight", StringComparison.OrdinalIgnoreCase) ?? false;

        public static bool IsArmorHeavy(KeywordSelectionVM kw) =>
            kw?.Name?.StartsWith("ArmorHeavy", StringComparison.OrdinalIgnoreCase) ?? false;

        public static bool IsArmorClothing(KeywordSelectionVM kw) =>
            kw?.Name?.StartsWith("ArmorClothing", StringComparison.OrdinalIgnoreCase) ?? false;

        public static bool IsArmorMaterial(KeywordSelectionVM kw) =>
            kw?.Name?.StartsWith("ArmorMaterial", StringComparison.OrdinalIgnoreCase) ?? false;

        public static bool IsWeapMaterial(KeywordSelectionVM kw) =>
            kw?.Name?.StartsWith("WeapMaterial", StringComparison.OrdinalIgnoreCase) ?? false;

        public static bool IsClothingKeyword(KeywordSelectionVM kw) =>
            kw?.Name?.StartsWith("Clothing", StringComparison.OrdinalIgnoreCase) ?? false;

        public static bool IsArmorKeyword(KeywordSelectionVM kw) =>
            kw?.Name?.StartsWith("Armor", StringComparison.OrdinalIgnoreCase) ?? false;

        public static bool IsArmorCategory(KeywordSelectionVM kw)
        {
            if (kw?.Name == null) return false;
            var name = kw.Name.ToLowerInvariant();
            return name.StartsWith("armorlight") ||
                   name.StartsWith("armorheavy") ||
                   name.StartsWith("armorclothing");
        }
    }
}
