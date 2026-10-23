using System;
using System.Collections.Generic;
using System.Linq;

namespace SkyrimCraftingTool.Model
{
    // The building blocks of a modular preset (docs/ItemPresets-Plan.md). Instead of one preset that
    // spreads values over slot bits, an item is described by four independent choices - Type,
    // Material, Slot definition(s), Keyword addons - and the keywords it ends up with are the OUTPUT
    // of combination rules over those choices, never an input. That is what lets an item be turned
    // from armor into clothing: the rule recomputes ClothingBody instead of ArmorCuirass, nothing has
    // to be re-detected from keywords the item carried before.
    //
    // Each catalog is persisted on its own (see ModularCatalogStore) so people can swap their
    // materials or slot layouts without touching the rest.

    // Everything a modular preset run needs, loaded together.
    public class ModularCatalog
    {
        public List<TypeModule> Types { get; set; } = new();
        public List<MaterialModule> Materials { get; set; } = new();
        public List<SlotDefinition> Slots { get; set; } = new();
        public List<KeywordAddon> Addons { get; set; } = new();
        public List<CombinationRule> Rules { get; set; } = new();
        public List<ExtraModule> Extras { get; set; } = new();
    }

    // What the old slot presets could do besides values and recipes (user, 2026-10-20: "its own
    // building block"): an enchantment with its charge, and leveled-list placements. Chosen in the
    // build box like addons - never pre-filled, it is always a deliberate pick.
    public class ExtraModule
    {
        public string Name { get; set; } = "";

        // Null = leave the item's enchantment alone; "" = strip it (a real choice, see
        // PresetApplyService.ApplyEnchantment); a key = assign that enchantment.
        public string? EnchantmentKey { get; set; }

        // The item's charge pool (EAMT). Null = leave it.
        public int? EnchantmentAmount { get; set; }

        // ContainerString format (ContainerStringBuilder/Parser). Added to the item's placements,
        // never replacing them - the way the slot presets applied it. "" or "{}" = none.
        public string Container { get; set; } = "";
    }

    // Clothing / Heavy / Light. ArmorType holds the exact string ItemNodeVM.ArmorType uses
    // ("Clothing", "HeavyArmor", "LightArmor") - that is the BOD2 field the patch writes.
    public class TypeModule
    {
        public string Name { get; set; } = "";
        public string ArmorType { get; set; } = "";

        // Set totals for a piece whose material is unknown (user, 2026-10-20: most clothing and
        // many light mod armors carry no material the pre-fill knows). Values only - no recipe and
        // no material keyword, those cannot be guessed. Null = no fallback: armor keeps its values,
        // clothing gets 0.
        public SetTotals? FallbackTotals { get; set; }

        // Recipes for the same case (user, 2026-10-20: "then clothing can get recipes too"). Used
        // per kind when the item has no material, or its material lists nothing for that kind. An
        // empty material list = no fallback for that kind. Scaled by the slot factors like any
        // material recipe.
        public MaterialRecipe FallbackCraft { get; set; } = new();
        public MaterialRecipe FallbackTemper { get; set; } = new();
        public MaterialRecipe FallbackBreakdown { get; set; } = new();

        // Round computed values to these steps; 0 = keep 2 (armor) / 3 (weight) decimals. Vanilla
        // armor rating is whole numbers and weight half steps: rounding alone took the dry run from
        // 81 % to 90 % of vanilla pieces within one armor point (2026-10-22).
        public double ArmorRatingStep { get; set; }
        public double WeightStep { get; set; }
    }

    // Material - in practice the TIER: LVL10-12 have no material of their own, so the keyword is a
    // free choice rather than the identity of the module.
    public class MaterialModule
    {
        public string Name { get; set; } = "";

        // Plugin|FormID, e.g. ArmorMaterialSteel. Empty = no material keyword (clothing).
        // Only slot definitions with CarriesMaterialKeyword get it: Matching Set checks
        // WornApparelHasKeywordCount == 4, so a fifth piece with the keyword switches it OFF.
        public string MaterialKeyword { get; set; } = "";

        // Full-set totals per ArmorType ("HeavyArmor" -> 4 pieces of Steel Heavy add up to this).
        // A type without an entry gets 0 everywhere - which is exactly what clothing wants.
        public Dictionary<string, SetTotals> Totals { get; set; } = new();

        public MaterialRecipe Craft { get; set; } = new();
        public MaterialRecipe Temper { get; set; } = new();

        // Same shape, read the other way round: Materials holds what the breakdown PRODUCES, and a
        // COBJ creates one kind of object, so only the first entry with a non-zero count is used.
        public MaterialRecipe Breakdown { get; set; } = new();

        // Optional fallback for the dry-run when the item carries none of the material keywords
        // (~1,300 light armor pieces use a mod's own material). Syntax: see NameMatcher.
        public string NamePatterns { get; set; } = "";
        public string NameExclusions { get; set; } = "";

        // Exceptions to the slot shares for THIS material, per type and slot definition - only
        // where vanilla breaks the pattern: a Daedric helmet weighs 30 % of its set, the slot says 14 %;
        // Dragonplate boots take as many scales as the cuirass. Null fields = the slot's own numbers.
        public List<MaterialSlotOverride> SlotOverrides { get; set; } = new();

        public ShareOverride? OverrideFor(string armorType, string slot)
            => SlotOverrides?.FirstOrDefault(o => string.Equals(o.ArmorType, armorType, StringComparison.OrdinalIgnoreCase)
                                                && string.Equals(o.Slot, slot, StringComparison.OrdinalIgnoreCase));
    }

    public class MaterialSlotOverride : ShareOverride
    {
        public string ArmorType { get; set; } = "";
        public string Slot { get; set; } = "";
    }

    public class SetTotals
    {
        public double ArmorRating { get; set; }
        public int Value { get; set; }
        public double Weight { get; set; }
    }

    public class MaterialRecipe
    {
        // Empty = leave the item's workbench alone (temper derives its own at COBJ creation).
        public string WorkbenchKey { get; set; } = "";

        // Position matters: slot definition factor N applies to material N.
        public List<MaterialEntry> Materials { get; set; } = new();

        public List<ConditionEntry> Conditions { get; set; } = new();
    }

    public class MaterialEntry
    {
        public string Key { get; set; } = "";

        // Base amount; the item gets Count x the slot definition's factor for this position.
        public double Count { get; set; } = 1;

        // Catalysts (Daedra Heart, Void Salts, filled soul gems) are the same on every piece -
        // Fixed skips the slot factor entirely.
        public bool Fixed { get; set; }
    }

    // User-defined: "Hair" = 31, "Helm" = 31 + 42. Bits are ArmorSlotMask bit indices (slot - 30),
    // the same numbering PresetSlotConfig.NodeKey uses.
    public class ShareOverride
    {
        public double? ArmorRating { get; set; }
        public double? Value { get; set; }
        public double? Weight { get; set; }
        // null = the slot's own RecipeFactors. Vanilla: a heavy helm takes 2 leather strips, a light
        // one 1; a heavy shield 1, a light one 2.
        public List<double>? RecipeFactors { get; set; }
    }

    public class SlotDefinition
    {
        public string Name { get; set; } = "";
        public List<int> Bits { get; set; } = new();

        // Percent of the material's set totals (Body 40, Helm 14 ...). An item that occupies
        // several definitions gets the SUM - an outfit split over several items should still add
        // up to one set.
        public double Share { get; set; }

        // Own shares for value and weight; null = Share. Vanilla splits them very differently: a
        // cuirass is ~44 % of the set's armor rating, ~53 % of its value and ~64 % of its weight
        // (Daedric 49/3200/50 of 108/6050/81), the same across every material.
        public double? ValueShare { get; set; }
        public double? WeightShare { get; set; }

        // Optional per ArmorType ("HeavyArmor", ...), each value null = the defaults above. Vanilla
        // again: a light cuirass is 50 % of its set's armor rating, a heavy one 44.5 %; a light shield
        // weighs 54 % of its set, a heavy one 21 %. Fitted per type, 81-85 % of vanilla pieces land
        // within 1 armor point; with one share for all types only 56 %.
        public Dictionary<string, ShareOverride> TypeShares { get; set; } = new();

        public double ArmorRatingShareFor(string armorType) => Override(armorType)?.ArmorRating ?? Share;
        public double ValueShareFor(string armorType) => Override(armorType)?.Value ?? ValueShare ?? Share;
        public double WeightShareFor(string armorType) => Override(armorType)?.Weight ?? WeightShare ?? Share;
        public List<double> RecipeFactorsFor(string armorType) => Override(armorType)?.RecipeFactors ?? RecipeFactors;

        private ShareOverride? Override(string armorType)
            => TypeShares?.FirstOrDefault(kv => string.Equals(kv.Key, armorType, StringComparison.OrdinalIgnoreCase)).Value;

        // One factor per position of the material's recipe list; missing positions count as 0.
        public List<double> RecipeFactors { get; set; } = new();

        // Main pieces only (Helm, Body, Hands, Feet, Shield) - see MaterialModule.MaterialKeyword.
        public bool CarriesMaterialKeyword { get; set; }
    }

    // Extra keywords independent of the rules, e.g. Survival_ArmorCold or MatchingSetHeavyExclude.
    // "Addons" in the UI (renamed 2026-10-20; the class and Addons.json keep their names so existing
    // catalogs load). Keywords to add, multipliers - and what picks it automatically: the item
    // already carrying all its keywords, OR the item's name matching NamePatterns. Either alone,
    // both, or neither (then it is only ever picked by hand).
    public class KeywordAddon
    {
        public string Name { get; set; } = "";
        public List<string> Keywords { get; set; } = new();

        // Same syntax as MaterialModule.NamePatterns (NameMatcher): "gold*, jewel*, ornate".
        public string NamePatterns { get; set; } = "";
        public string NameExclusions { get; set; } = "";

        // Picked automatically only on items with one of these slot definitions; empty = any.
        // "Outerwear" (coat*, vest* -> x10) must reach a coat on accessory slot 48, never a coat
        // already counted as Body - there the multiplier would make it ten bodies.
        public List<string> OnlySlots { get; set; } = new();

        // The same for the plugin an item comes from (the part of its key before '|'): picked
        // automatically only from OnlyPlugins (empty = any) and never from ExcludePlugins. A
        // trailing * matches the start of a file name ("TAWOBA*"). Picking by hand ignores both.
        public List<string> OnlyPlugins { get; set; } = new();
        public List<string> ExcludePlugins { get; set; } = new();

        // Other addons (by name) that come along whenever this one is in - picked automatically or
        // by hand (user, 2026-10-21: "Premium" -> "Rich"). The way to chain addons WITHOUT giving
        // Premium the Rich keyword: an addon's keywords are also its trigger, so that would have
        // handed Premium to every item that already carries ClothingRich.
        public List<string> AlsoAddons { get; set; } = new();

        // Multipliers for the values the engine COMPUTES (material totals x slot share) - e.g.
        // "Expensive" = JewelryExpensive + value x10. Never applied to values an item keeps (no
        // material): those are the item's own, and every further Apply would multiply them again.
        // Several addons multiply together. 1 = no effect, which is also what a file written
        // before these existed deserializes to.
        public double ArmorRatingFactor { get; set; } = 1;
        public double ValueFactor { get; set; } = 1;
        public double WeightFactor { get; set; } = 1;

        // Weapon catalogs only (the class is shared): multiplies the computed damage.
        public double DamageFactor { get; set; } = 1;

        // Ingredients ADDED to the recipe the material or type gives (user, 2026-10-20: the
        // material x slot factor already scales; an addon adds on top). A flat amount per piece,
        // not scaled by the slot. Only added to a recipe that exists - an addon never creates one.
        public List<IngredientEntry> CraftAdditions { get; set; } = new();
        public List<IngredientEntry> TemperAdditions { get; set; } = new();

        // A trigger like NamePatterns, on the EditorID: "*" anywhere, comma-separated, any case.
        // Creature skins have names like "Chicken" or "Cave Bear" - only the EditorID (SkinChicken,
        // DLC2_SkinBoar, DLC2NetchSkin) gives them away.
        public string EditorIdPatterns { get; set; } = "";

        // What the addon holds back. LeaveUntouched: the item is not changed at all (creature skins,
        // FX records). KeepValues / KeepRecipes: the item keeps its own armor rating, value and
        // weight / its recipes, everything else is built as usual - for uniques like the Shield of
        // Solitude, whose numbers follow no set.
        public bool LeaveUntouched { get; set; }
        public bool KeepValues { get; set; }
        public bool KeepRecipes { get; set; }
        public bool KeepKeywords { get; set; }
    }

    // "Heavy + Body -> ArmorCuirass". Each condition list is an OR over names; an empty list means
    // "any". Every matching rule contributes - keywords are unioned, duplicates count once.
    public class CombinationRule
    {
        public string Name { get; set; } = "";
        public List<string> Types { get; set; } = new();
        public List<string> Slots { get; set; } = new();
        public List<string> Materials { get; set; } = new();

        // Optional safety net: the rule does not fire if the item ALSO has one of these slot
        // definitions ("ClothingCirclet, but not on a hood that already counts as head").
        public List<string> ExcludeSlots { get; set; } = new();

        // Addon conditions: the rule needs ONE of Addons (empty = any) and none of ExcludeAddons.
        // Lets "Clothing base" (ArmorClothing) step aside for the Jewelry addon - vanilla jewelry
        // carries ArmorJewelry INSTEAD of ArmorClothing (1,548 items, only 1 with both).
        public List<string> Addons { get; set; } = new();
        public List<string> ExcludeAddons { get; set; } = new();

        public List<string> Keywords { get; set; } = new();
    }

    // What one item is assigned: the dry-run pre-fills it, the user may correct it, the engine
    // turns it into values. Names refer to the catalog entries.
    public class ModularSelection
    {
        public string TypeName { get; set; } = "";
        public string MaterialName { get; set; } = "";
        public List<string> SlotNames { get; set; } = new();
        public List<string> AddonNames { get; set; } = new();
        public List<string> ExtraNames { get; set; } = new();
    }
}
