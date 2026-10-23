using SkyrimCraftingTool.Model;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace SkyrimCraftingTool.Services.ModularPresets
{
    // P7: turns an old slot preset (LVL2_Iron_Heavy ...) into a Material building block. The body
    // slot is the reference piece: its values are the body's share of the set, its recipe the base
    // amounts every slot definition scales.
    public static class SlotPresetImporter
    {
        private const string ArmorHeavy = "Skyrim.esm|06BBD2";
        private const string ArmorLight = "Skyrim.esm|06BBD3";

        // Catalysts the LVL presets put on every piece unscaled. Anything else counts as fixed only
        // when the preset shows it unscaled - see IsFixed.
        private static readonly HashSet<string> KnownCatalysts = new(StringComparer.OrdinalIgnoreCase)
        {
            "Skyrim.esm|03AD5B", // Daedra Heart
            "Skyrim.esm|03AD5E", // Fire Salts (what the LVL presets use; Void Salts would be 03AD60)
            "Skyrim.esm|03AD60", // Void Salts
            "Skyrim.esm|02E4FF", // filled Grand Soul Gem
            "Skyrim.esm|02E504", // filled Black Soul Gem
        };

        // The other craftable main pieces, compared with the body to spot unscaled ingredients.
        private static readonly string[] CompareBits = { "1", "3", "7", "9" };

        // Null with a reason when the preset has nothing a material could take: no body slot, or no
        // body values at all.
        public static MaterialModule? ToMaterial(PresetFile preset, double bodySharePercent, out string? skipped)
        {
            skipped = null;
            var body = preset.ArmorSlots.FirstOrDefault(s => s.NodeKey == "2");
            if (body == null)
            {
                skipped = "no body slot";
                return null;
            }

            var keywords = body.Keywords.Enabled ? body.Keywords.Value ?? new List<string>() : new List<string>();
            string? armorType = keywords.Contains(ArmorHeavy, StringComparer.OrdinalIgnoreCase) ? "HeavyArmor"
                : keywords.Contains(ArmorLight, StringComparer.OrdinalIgnoreCase) ? "LightArmor"
                : null;

            var material = new MaterialModule
            {
                Name = preset.PresetName,
                MaterialKeyword = keywords.FirstOrDefault(k =>
                    !ModularPresetEngine.BaselineManagedKeywords.Contains(k)) ?? "",
                NamePatterns = PatternFromName(preset.PresetName),
            };

            // Clothing (no class keyword): values stay out - clothing gets 0 by design - but the
            // recipe still comes over, it is real data the user wrote.
            if (armorType != null && bodySharePercent > 0 && (body.ArmorRating.Enabled || body.Value.Enabled || body.Weight.Enabled))
            {
                double f = 100.0 / bodySharePercent;
                material.Totals[armorType] = new SetTotals
                {
                    ArmorRating = body.ArmorRating.Enabled ? Math.Round(body.ArmorRating.Value * f, 2) : 0,
                    Value = body.Value.Enabled ? (int)Math.Round(body.Value.Value * f, MidpointRounding.AwayFromZero) : 0,
                    Weight = body.Weight.Enabled ? Math.Round(body.Weight.Value * f, 3) : 0,
                };
            }

            var others = preset.ArmorSlots.Where(s => CompareBits.Contains(s.NodeKey)).ToList();
            material.Craft = Recipe(body.CraftRecipe, others.Select(o => o.CraftRecipe).ToList(), allFixed: false);
            // Temper: one primary material on every piece in the LVL presets - always fixed.
            material.Temper = Recipe(body.TemperRecipe, new List<RecipeConfig>(), allFixed: true);
            material.Breakdown = Recipe(body.BreakdownRecipe, new List<RecipeConfig>(), allFixed: true);

            if (armorType == null && material.Craft.Materials.Count == 0 && material.Temper.Materials.Count == 0)
            {
                skipped = "neither values nor recipes on the body slot";
                return null;
            }
            return material;
        }

        private static MaterialRecipe Recipe(RecipeConfig source, List<RecipeConfig> others, bool allFixed)
        {
            var recipe = new MaterialRecipe
            {
                // The temper bench is derived when the COBJ is created; the presets leave it off.
                WorkbenchKey = source.WorkbenchKey.Enabled ? source.WorkbenchKey.Value ?? "" : "",
            };
            if (source.Ingredients.Enabled)
            {
                foreach (var ing in Merged(source))
                {
                    recipe.Materials.Add(new MaterialEntry
                    {
                        Key = ing.Key,
                        Count = ing.Count,
                        Fixed = allFixed || IsFixed(ing, others),
                    });
                }
            }
            if (source.Conditions.Enabled)
                recipe.Conditions = (source.Conditions.Value ?? new List<ConditionEntry>())
                    .Select(c => new ConditionEntry { ConditionType = c.ConditionType, Target = c.Target, Value = c.Value, RunOn = c.RunOn })
                    .ToList();
            return recipe;
        }

        // Fixed when it is a known catalyst, or when every other piece takes exactly as many as the
        // body although the body takes more than one - a scaled ingredient would have shrunk.
        // (At 1 the two cannot be told apart, and need not be: any factor above 0 costs at least 1.)
        private static bool IsFixed(IngredientEntry ing, List<RecipeConfig> others)
        {
            if (KnownCatalysts.Contains(ing.Key)) return true;
            if (ing.Count <= 1) return false;

            var counts = others
                .Where(o => o.Ingredients.Enabled)
                .Select(o => Merged(o).FirstOrDefault(x => string.Equals(x.Key, ing.Key, StringComparison.OrdinalIgnoreCase))?.Count)
                .Where(c => c.HasValue)
                .ToList();
            return counts.Count > 0 && counts.All(c => c == ing.Count);
        }

        // One entry per material. LVL10_Unique_Heavy (2026-10-20) lists the same material twice
        // (x1 and x2); compared entry by entry, the two halves scaled differently and the totals no
        // longer matched the old recipe. The game sums them anyway.
        private static List<IngredientEntry> Merged(RecipeConfig recipe)
        {
            var merged = new List<IngredientEntry>();
            foreach (var ing in recipe.Ingredients.Value ?? new List<IngredientEntry>())
            {
                if (string.IsNullOrWhiteSpace(ing.Key) || ing.Count <= 0) continue;
                var existing = merged.FirstOrDefault(x => string.Equals(x.Key, ing.Key, StringComparison.OrdinalIgnoreCase));
                if (existing != null) existing.Count += ing.Count;
                else merged.Add(new IngredientEntry { Key = ing.Key, Count = ing.Count });
            }
            return merged;
        }

        // "LVL3.5_SteelPlate_Heavy" -> "steel plate*": the middle part, split at its capitals; the
        // last word as a prefix. A tier word like "Godly" yields "godly*", which simply never hits.
        internal static string PatternFromName(string presetName)
        {
            var parts = presetName.Split('_', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) return "";
            var middle = parts.Length >= 3 ? string.Join(" ", parts.Skip(1).Take(parts.Length - 2)) : parts[1];
            var words = Regex.Split(Regex.Replace(middle, "(?<=[a-z])(?=[A-Z])", " "), @"\s+")
                .Where(w => w.Length > 0)
                .Select(w => w.ToLowerInvariant())
                .ToList();
            if (words.Count == 0) return "";
            return string.Join(" ", words) + "*";
        }

        // Tier order by the number in "LVL3.5_..." - not the file order, where LVL10 sorts before
        // LVL9. It matters: LVL9 Dragon and the LVL10-12 tiers share the Dragonplate keyword, and the
        // first material with an item's keyword wins, so vanilla dragon armor must meet LVL9 first.
        public static List<PresetFile> OrderByTier(IEnumerable<PresetFile> presets)
            => presets
                .Select(p => (p, tier: TierOf(p.PresetName)))
                .OrderBy(x => x.tier)
                .ThenBy(x => x.p.PresetName, StringComparer.OrdinalIgnoreCase)
                .Select(x => x.p)
                .ToList();

        private static double TierOf(string name)
        {
            var m = Regex.Match(name, @"^LVL(\d+(?:\.\d+)?)", RegexOptions.IgnoreCase);
            return m.Success && double.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var t) ? t : double.MaxValue;
        }

        // Import order: more specific name patterns first ("steel plate*" before "steel*"), because
        // the first material whose pattern matches wins. Otherwise keep the file order.
        public static List<MaterialModule> OrderForMatching(IEnumerable<MaterialModule> materials)
            => materials
                .Select((m, i) => (m, i))
                .OrderByDescending(x => x.m.NamePatterns.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length)
                .ThenBy(x => x.i)
                .Select(x => x.m)
                .ToList();
    }
}
