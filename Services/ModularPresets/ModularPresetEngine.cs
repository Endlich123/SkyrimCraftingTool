using SkyrimCraftingTool.Model;
using System;
using System.Collections.Generic;
using System.Linq;

namespace SkyrimCraftingTool.Services.ModularPresets
{
    // The item as the modular engine sees it - deliberately not ItemNodeVM, so the engine stays a
    // pure function that a test can feed without a tree, a database or a UI thread.
    public class ModularItemState
    {
        public string Name { get; set; } = "";

        // The plugin the record comes from - its key's part before '|'.
        public string Plugin { get; set; } = "";
        public string ArmorType { get; set; } = "";
        public uint BodySlotMask { get; set; }
        public List<string> Keywords { get; set; } = new();

        // Has an enchantment (ObjectEffectKey). Such a piece gets no NEW craft or temper recipe:
        // vanilla ships 2,240 enchanted armor variants and gives exactly one of them a recipe - they
        // are loot, and they temper through their template.
        public bool IsEnchanted { get; set; }

        // For addons that trigger on it (KeywordAddon.EditorIdPatterns).
        public string EditorID { get; set; } = "";
    }

    public class ModularRecipeResult
    {
        public string WorkbenchKey { get; set; } = "";
        public List<IngredientEntry> Ingredients { get; set; } = new();
        public List<ConditionEntry> Conditions { get; set; } = new();
    }

    public class ModularResult
    {
        public string ArmorType { get; set; } = "";
        public uint BodySlotMask { get; set; }
        // Null = leave the item's own value alone: armor whose material was not found has no set
        // totals to take a share of, and writing 0 there wiped thousands of items in the first
        // dry run. Clothing is the exception - no material is its normal state and its values are
        // meant to be 0.
        public double? ArmorRating { get; set; }
        public int? Value { get; set; }
        public double? Weight { get; set; }

        // The item's complete keyword list after the run: its foreign keywords untouched, the ones
        // the catalog manages replaced by what the rules produce.
        public List<string> Keywords { get; set; } = new();

        // Null = no material, so the item's recipe is left as it is.
        public ModularRecipeResult? Craft { get; set; }
        public ModularRecipeResult? Temper { get; set; }
        public ModularRecipeResult? Breakdown { get; set; }

        // From the extras. Null = untouched; EnchantmentKey "" = strip.
        public string? EnchantmentKey { get; set; }
        public int? EnchantmentAmount { get; set; }

        // Every extra's placements, each added to the item's own.
        public List<string> Containers { get; set; } = new();

        public List<string> Warnings { get; set; } = new();
    }

    // Turns one item's selection (Type, Material, Slot definitions, Addons) into the values it gets.
    // Used by both the dry-run (shows the result) and the preset patch (applies it).
    public static class ModularPresetEngine
    {
        private static readonly StringComparer Names = StringComparer.OrdinalIgnoreCase;

        // The vanilla keywords that say WHAT an item is - type, slot, vendor. Always the rules' call,
        // even when the user's catalog never mentions one: an item turned from Light into Heavy
        // must not keep ArmorLight just because no rule happens to produce it. (A test caught
        // exactly that on the first run.)
        public static readonly IReadOnlyCollection<string> BaselineManagedKeywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Skyrim.esm|06BBD2", "Skyrim.esm|06BBD3", "Skyrim.esm|06BBE8", "Skyrim.esm|06BBE9", // ArmorHeavy/Light/Clothing/Jewelry
            "Skyrim.esm|06C0EC", "Skyrim.esm|06C0EE", "Skyrim.esm|06C0EF", "Skyrim.esm|06C0ED", "Skyrim.esm|0965B2", // Cuirass/Helmet/Gauntlets/Boots/Shield
            "Skyrim.esm|0A8657", "Skyrim.esm|10CD11", "Skyrim.esm|10CD13", "Skyrim.esm|10CD12", // ClothingBody/Head/Hands/Feet
            "Skyrim.esm|10CD09", "Skyrim.esm|10CD0A", "Skyrim.esm|10CD08", // ClothingRing/Necklace/Circlet
            "Skyrim.esm|08F959", "Skyrim.esm|08F95B", "Skyrim.esm|08F95A", // VendorItemArmor/Clothing/Jewelry
        };

        private static readonly HashSet<string> SlotKeywords = new(StringComparer.OrdinalIgnoreCase)
        {
            "Skyrim.esm|06C0EC", "Skyrim.esm|06C0EE", "Skyrim.esm|06C0EF", "Skyrim.esm|06C0ED", "Skyrim.esm|0965B2",
            "Skyrim.esm|0A8657", "Skyrim.esm|10CD11", "Skyrim.esm|10CD13", "Skyrim.esm|10CD12",
            "Skyrim.esm|10CD09", "Skyrim.esm|10CD0A", "Skyrim.esm|10CD08",
        };

        private static readonly HashSet<string> TypeKeywords = new(StringComparer.OrdinalIgnoreCase)
        {
            "Skyrim.esm|06BBD2", "Skyrim.esm|06BBD3", "Skyrim.esm|06BBE8", "Skyrim.esm|06BBE9",
        };

        private static readonly HashSet<string> VendorKeywords = new(StringComparer.OrdinalIgnoreCase)
        {
            "Skyrim.esm|08F959", "Skyrim.esm|08F95B", "Skyrim.esm|08F95A",
        };

        // materialKeywords: every keyword of the load order that IS a material (the caller knows
        // names, the engine only keys - see MaterialKeywordsOf). Handled apart from the baseline,
        // because dropping one is not always right: see ComputeKeywords.
        public static ModularResult Compute(ModularCatalog catalog, ModularSelection selection, ModularItemState item,
            IReadOnlyCollection<string>? materialKeywords = null)
        {
            var result = new ModularResult();

            var type = catalog.Types.FirstOrDefault(t => Names.Equals(t.Name, selection.TypeName));
            if (type == null)
            {
                result.Warnings.Add($"Type '{selection.TypeName}' not found - ArmorType unchanged.");
                result.ArmorType = item.ArmorType;
            }
            else
            {
                result.ArmorType = type.ArmorType;
            }

            MaterialModule? material = null;
            if (!string.IsNullOrWhiteSpace(selection.MaterialName))
            {
                material = catalog.Materials.FirstOrDefault(m => Names.Equals(m.Name, selection.MaterialName));
                if (material == null)
                    result.Warnings.Add($"Material '{selection.MaterialName}' not found.");
            }

            var slots = new List<SlotDefinition>();
            foreach (var name in selection.SlotNames)
            {
                var slot = catalog.Slots.FirstOrDefault(s => Names.Equals(s.Name, name));
                if (slot == null) result.Warnings.Add($"Slot definition '{name}' not found.");
                else slots.Add(slot);
            }

            var addons = new List<KeywordAddon>();
            foreach (var name in WithAlsoAddons(catalog, selection.AddonNames))
            {
                var addon = catalog.Addons.FirstOrDefault(a => Names.Equals(a.Name, name));
                if (addon == null) result.Warnings.Add($"Addon '{name}' not found.");
                else addons.Add(addon);
            }

            // An addon that says "leave this item alone" (creature skins): nothing is computed, the
            // result is the item as it is.
            var hold = addons.FirstOrDefault(a => a.LeaveUntouched);
            if (hold != null)
            {
                return new ModularResult
                {
                    ArmorType = item.ArmorType,
                    BodySlotMask = item.BodySlotMask,
                    Keywords = item.Keywords.ToList(),
                    Warnings = { $"Left untouched (addon '{hold.Name}')." },
                };
            }
            bool keepValues = addons.Any(a => a.KeepValues);
            bool keepRecipes = addons.Any(a => a.KeepRecipes);
            bool keepKeywords = addons.Any(a => a.KeepKeywords);

            // --- Slots ---
            if (slots.Count == 0)
            {
                result.Warnings.Add("No slot definition - slots, values and recipes left as they are.");
                result.BodySlotMask = item.BodySlotMask;
            }
            else
            {
                uint mask = 0;
                foreach (var s in slots)
                    foreach (var bit in s.Bits)
                        if (bit >= 0 && bit < 32) mask |= 1u << bit;
                result.BodySlotMask = mask;
            }

            // --- Values: set totals x summed share ---
            // The material's own exception for a slot wins over the slot's numbers.
            ShareOverride? Ovr(SlotDefinition s) => material?.OverrideFor(result.ArmorType, s.Name);
            double share = slots.Sum(s => Ovr(s)?.ArmorRating ?? s.ArmorRatingShareFor(result.ArmorType)) / 100.0;
            double valueShare = slots.Sum(s => Ovr(s)?.Value ?? s.ValueShareFor(result.ArmorType)) / 100.0;
            double weightShare = slots.Sum(s => Ovr(s)?.Weight ?? s.WeightShareFor(result.ArmorType)) / 100.0;
            var totals = material != null && material.Totals.TryGetValue(result.ArmorType, out var t) ? t : null;
            bool isClothing = string.Equals(result.ArmorType, "Clothing", StringComparison.OrdinalIgnoreCase);

            // No usable material totals: the type's fallback, if it has one. Values only - recipes
            // and the material keyword still follow the material, if there is one.
            if (totals == null && type?.FallbackTotals != null)
            {
                if (material != null)
                    result.Warnings.Add($"Material '{material.Name}' has no totals for {result.ArmorType} - using the type's fallback values.");
                totals = type.FallbackTotals;
            }
            if (keepValues)
            {
                // A unique keeps its own numbers (addon with KeepValues) - null = left as it is.
            }
            else if (slots.Count == 0)
            {
                // Nothing to take a share of: share 0 would write 0 over the item's real values (and
                // leave recipes holding only their catalysts). Slotless records - TNG skins, Devious
                // Devices inventory placeholders - are left alone unless a slot definition WITHOUT
                // slots claims them (ModularPrefill.CoverMask).
            }
            else if (totals != null)
            {
                // The addons' multipliers, on computed values only (see KeywordAddon).
                double arF = 1, valueF = 1, weightF = 1;
                foreach (var a in addons)
                {
                    arF *= a.ArmorRatingFactor;
                    valueF *= a.ValueFactor;
                    weightF *= a.WeightFactor;
                }
                result.ArmorRating = Step(totals.ArmorRating * share * arF, type?.ArmorRatingStep ?? 0, 2);
                result.Value = (int)Math.Round(totals.Value * valueShare * valueF, MidpointRounding.AwayFromZero);
                result.Weight = Step(totals.Weight * weightShare * weightF, type?.WeightStep ?? 0, 3);
            }
            else if (isClothing)
            {
                result.ArmorRating = 0;
                result.Value = 0;
                result.Weight = 0;
            }
            else if (material != null)
            {
                // A missing material is already reported by whoever chose it (pre-fill note, or the
                // "not found" warning above); this one is the material existing without numbers.
                result.Warnings.Add($"Material '{material.Name}' has no totals for {result.ArmorType} - values unchanged.");
            }

            // --- Keywords ---
            // KeepKeywords: vanilla's own quirks stay (Imperial helmets: Heavy with ArmorLight).
            result.Keywords = keepKeywords ? item.Keywords.ToList() : ComputeKeywords(catalog, type, material, slots, addons, item.Keywords, materialKeywords);

            var extras = ApplyExtras(catalog.Extras, selection.ExtraNames, result.Warnings);
            result.EnchantmentKey = extras.EnchantmentKey;
            result.EnchantmentAmount = extras.EnchantmentAmount;
            result.Containers.AddRange(extras.Containers);

            // --- Recipes: the material's, per kind; where it lists nothing (or there is no
            // material), the type's fallback. Neither = the item's recipe is left alone. ---
            MaterialRecipe? Pick(MaterialRecipe? fromMaterial, MaterialRecipe? fromType)
                => fromMaterial is { Materials.Count: > 0 } ? fromMaterial
                 : fromType is { Materials.Count: > 0 } ? fromType
                 : null;

            var craft = Pick(material?.Craft, type?.FallbackCraft);
            var temper = Pick(material?.Temper, type?.FallbackTemper);
            var breakdown = Pick(material?.Breakdown, type?.FallbackBreakdown);
            if (keepRecipes)
            {
                craft = null;
                temper = null;
                breakdown = null;
            }
            if (item.IsEnchanted || !string.IsNullOrWhiteSpace(result.EnchantmentKey))
            {
                // Left alone, not removed: an enchanted piece that already has a recipe keeps it.
                craft = null;
                temper = null;
            }
            if (slots.Count > 0)
            {
                if (craft != null) result.Craft = BuildRecipe(craft, slots, result.ArmorType, material, singleOutput: false);
                if (temper != null) result.Temper = BuildRecipe(temper, slots, result.ArmorType, material, singleOutput: false);
                if (breakdown != null) result.Breakdown = BuildRecipe(breakdown, slots, result.ArmorType, material, singleOutput: true);

                // A recipe was there, and the slot factors took every entry out - a factor of 0
                // means "none of this". Said in so many words: "the item gets no recipe" alone sent
                // the user looking at the type's fallback, which was fine (2026-10-20).
                string slotNames = string.Join(", ", slots.Select(s => s.Name));
                if (craft != null && result.Craft == null)
                    result.Warnings.Add($"No crafting recipe: the recipe factors of '{slotNames}' are 0 for every ingredient.");
                if (temper != null && result.Temper == null)
                    result.Warnings.Add($"No tempering recipe: the recipe factors of '{slotNames}' are 0 for every ingredient.");

                // The addons' flat additions, on top - never a recipe of their own.
                foreach (var a in addons)
                {
                    AddIngredients(result.Craft, a.CraftAdditions, a.Name, "crafting", result.Warnings);
                    AddIngredients(result.Temper, a.TemperAdditions, a.Name, "tempering", result.Warnings);
                }
            }

            return result;
        }

        // The chosen addons plus everything they bring along (KeywordAddon.AlsoAddons), each once and
        // in pick order. Transitive, and safe against cycles (A -> B -> A): a name is taken once.
        // Here as well as in the pre-fill, so an addon added by hand in the build box brings its
        // companions too.
        public static List<string> WithAlsoAddons(ModularCatalog catalog, IEnumerable<string> names)
            => WithAlsoAddons(catalog.Addons, names);

        // The same over a plain addon list - the weapon catalog has its own.
        public static List<string> WithAlsoAddons(IReadOnlyList<KeywordAddon> addons, IEnumerable<string> names)
        {
            var result = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var queue = new Queue<string>(names);
            while (queue.Count > 0)
            {
                var name = queue.Dequeue();
                if (!seen.Add(name)) continue;
                result.Add(name);
                var addon = addons.FirstOrDefault(a => Names.Equals(a.Name, name));
                foreach (var also in addon?.AlsoAddons ?? new List<string>())
                    queue.Enqueue(also);
            }
            return result;
        }

        // Everything the catalog can produce, plus the vanilla baseline. Exactly these get removed
        // from an item before the produced ones are added; every other keyword (Survival from a mod,
        // DD, TAWOBA ...) stays. Material keywords outside the catalog: see ComputeKeywords.
        public static HashSet<string> ManagedKeywords(ModularCatalog catalog)
        {
            var managed = new HashSet<string>(BaselineManagedKeywords, StringComparer.OrdinalIgnoreCase);
            foreach (var r in catalog.Rules) managed.UnionWith(r.Keywords.Where(k => !string.IsNullOrWhiteSpace(k)));
            foreach (var a in catalog.Addons) managed.UnionWith(a.Keywords.Where(k => !string.IsNullOrWhiteSpace(k)));
            foreach (var m in catalog.Materials)
                if (!string.IsNullOrWhiteSpace(m.MaterialKeyword)) managed.Add(m.MaterialKeyword);
            return managed;
        }

        // Which keywords of a load order are materials - by name, so mod materials (DLC2, WACCF,
        // a mod's own ArmorMaterialX) count too.
        public static HashSet<string> MaterialKeywordsOf(IEnumerable<FormIDRecord>? keywords)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var k in keywords ?? Enumerable.Empty<FormIDRecord>())
                if (!string.IsNullOrEmpty(k.Key) && k.Name != null && k.Name.Contains("ArmorMaterial", StringComparison.OrdinalIgnoreCase))
                    set.Add(k.Key);
            return set;
        }

        // A foreign material keyword (one the catalog does not know) is:
        //   - replaced when a material was chosen that the item did not carry yet (see below for the
        //     second keywords of an item that already carries it);
        //   - dropped when none of the item's slots carries a material - accessories with a
        //     material keyword are what breaks Matching Set (== 4 items), and 70-100 % of mod
        //     accessories ship one;
        //   - KEPT on a main piece whose material was not found - it is also what the smithing
        //     perks double the temper bonus by, and nothing would replace it.
        private static List<string> ComputeKeywords(
            ModularCatalog catalog, TypeModule? type, MaterialModule? material,
            List<SlotDefinition> slots, List<KeywordAddon> addons, List<string> current,
            IReadOnlyCollection<string>? materialKeywords)
        {
            var produced = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            void Add(string k) { if (!string.IsNullOrWhiteSpace(k) && seen.Add(k)) produced.Add(k); }

            var slotNames = new HashSet<string>(slots.Select(s => s.Name), Names);
            var addonNames = new HashSet<string>(addons.Select(a => a.Name), Names);
            foreach (var rule in catalog.Rules)
            {
                if (rule.Types.Count > 0 && (type == null || !rule.Types.Contains(type.Name, Names))) continue;
                if (rule.Slots.Count > 0 && !rule.Slots.Any(slotNames.Contains)) continue;
                if (rule.Materials.Count > 0 && (material == null || !rule.Materials.Contains(material.Name, Names))) continue;
                if (rule.ExcludeSlots.Any(slotNames.Contains)) continue;
                if (rule.Addons is { Count: > 0 } && !rule.Addons.Any(addonNames.Contains)) continue;
                if (rule.ExcludeAddons is { Count: > 0 } && rule.ExcludeAddons.Any(addonNames.Contains)) continue;
                foreach (var k in rule.Keywords) Add(k);
            }

            if (material != null && slots.Any(s => s.CarriesMaterialKeyword))
                Add(material.MaterialKeyword);

            foreach (var a in addons)
                foreach (var k in a.Keywords) Add(k);

            var managed = ManagedKeywords(catalog);
            bool carriesMaterial = slots.Any(s => s.CarriesMaterialKeyword);
            if (materialKeywords != null && (material != null || !carriesMaterial))
                managed.UnionWith(materialKeywords);

            // A main piece that already carries the chosen material keeps its OTHER material keywords:
            // vanilla ships Iron+IronBanded, ImperialLight+ImperialStudded, Leather+DLC1Vampire on
            // purpose, and WACCF/USLEEP hang recipes on theirs (WAF_ArmorMaterialDraugr, ...). Two
            // keywords on ONE piece never push a Matching Set count past 4. Only a real material change
            // (by name, or chosen by hand) replaces them all.
            if (material != null && carriesMaterial && current.Contains(material.MaterialKeyword, Names))
            {
                var allMaterials = new HashSet<string>(materialKeywords ?? Array.Empty<string>(), Names);
                allMaterials.UnionWith(catalog.Materials.Select(m => m.MaterialKeyword).Where(k => !string.IsNullOrWhiteSpace(k)));
                managed.ExceptWith(current.Where(allMaterials.Contains));
            }

            // No slot = nothing to judge a slot or vendor keyword by: what the item has stays. 1,250
            // Devious Devices inventory items (slotless by design) lost every vendor keyword and could
            // no longer be sold (keyword comparison, 2026-10-21). A vendor keyword the rules DO produce
            // without a slot ("Heavy base" -> VendorItemArmor) still replaces the old one.
            if (slots.Count == 0)
            {
                managed.ExceptWith(SlotKeywords);
                if (!produced.Any(VendorKeywords.Contains)) managed.ExceptWith(VendorKeywords);
                // Same for the type keyword: 53 slotless toe rings and piercings lost ArmorJewelry, 4 DD
                // dresses ArmorClothing, because the clothing rules all need a slot.
                if (!produced.Any(TypeKeywords.Contains)) managed.ExceptWith(TypeKeywords);
            }

            var final = new List<string>();
            var finalSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var k in current)
                if (!managed.Contains(k) && finalSeen.Add(k)) final.Add(k);
            foreach (var k in produced)
                if (finalSeen.Add(k)) final.Add(k);
            return final;
        }

        internal sealed class ExtrasOutcome
        {
            public string? EnchantmentKey;
            public int? EnchantmentAmount;
            public List<string> Containers = new();
        }

        // Extras: one enchantment and one charge per item, so a later extra overrides an earlier one
        // (and says so); placements all add up. Shared with the weapon engine.
        internal static ExtrasOutcome ApplyExtras(IReadOnlyList<ExtraModule> catalogExtras, IEnumerable<string> names, List<string> warnings)
        {
            var outcome = new ExtrasOutcome();
            foreach (var name in names)
            {
                var extra = catalogExtras.FirstOrDefault(x => Names.Equals(x.Name, name));
                if (extra == null) { warnings.Add($"Extra '{name}' not found."); continue; }

                if (extra.EnchantmentKey != null)
                {
                    if (outcome.EnchantmentKey != null && !string.Equals(outcome.EnchantmentKey, extra.EnchantmentKey, StringComparison.OrdinalIgnoreCase))
                        warnings.Add($"Extra '{extra.Name}' overrides the enchantment of an earlier extra.");
                    outcome.EnchantmentKey = extra.EnchantmentKey;
                }
                if (extra.EnchantmentAmount.HasValue)
                {
                    if (outcome.EnchantmentAmount.HasValue && outcome.EnchantmentAmount != extra.EnchantmentAmount)
                        warnings.Add($"Extra '{extra.Name}' overrides the charge of an earlier extra.");
                    outcome.EnchantmentAmount = extra.EnchantmentAmount;
                }
                if (!string.IsNullOrWhiteSpace(extra.Container) && extra.Container.Trim() != "{}")
                    outcome.Containers.Add(extra.Container);
            }
            return outcome;
        }

        // Round to a step (1 = whole numbers, 0.5 = half steps); step 0 = plain decimals.
        internal static double Step(double x, double step, int decimals)
            => step > 0
                ? Math.Round(Math.Round(x / step, MidpointRounding.AwayFromZero) * step, decimals)
                : Math.Round(x, decimals, MidpointRounding.AwayFromZero);

        internal static void AddIngredients(ModularRecipeResult? recipe, List<IngredientEntry>? additions, string addon, string kind, List<string> warnings)
        {
            var real = (additions ?? new List<IngredientEntry>()).Where(x => !string.IsNullOrWhiteSpace(x.Key) && x.Count > 0).ToList();
            if (real.Count == 0) return;
            if (recipe == null)
            {
                warnings.Add($"Addon '{addon}' adds {kind} ingredients, but the item gets no {kind} recipe to add them to.");
                return;
            }
            foreach (var add in real)
            {
                var existing = recipe.Ingredients.FirstOrDefault(i => string.Equals(i.Key, add.Key, StringComparison.OrdinalIgnoreCase));
                if (existing != null) existing.Count += add.Count;
                else recipe.Ingredients.Add(new IngredientEntry { Key = add.Key, Count = add.Count });
            }
        }

        // Count for position i = base count x (sum of the slot definitions' factor i), rounded half
        // up; Fixed entries (catalysts) skip the factor. A factor of 0 drops the entry - the user's
        // "Leather x0" means "this piece takes no leather" - but any factor above 0 costs at least
        // one (gauntlets at x0.35 of a single ingot still take an ingot, as the LVL presets did).
        private static ModularRecipeResult? BuildRecipe(MaterialRecipe recipe, List<SlotDefinition> slots, string armorType, MaterialModule? material, bool singleOutput)
        {
            List<double> FactorsOf(SlotDefinition s) => material?.OverrideFor(armorType, s.Name)?.RecipeFactors ?? s.RecipeFactorsFor(armorType);
            return BuildRecipe(recipe, singleOutput,
                factorAt: i => slots.Sum(s => FactorsOf(s) is var f && i < f.Count ? f[i] : 0),
                // Fixed (catalysts) ignore the factor - except a 0 the material sets for that slot on
                // purpose: vanilla light shields take no leather.
                fixedDropped: i => slots.All(s => material?.OverrideFor(armorType, s.Name)?.RecipeFactors is { } f && i < f.Count && f[i] == 0));
        }

        // The recipe shape both engines share: factorAt(i) scales position i, fixedDropped(i) says a
        // Fixed entry (catalyst) is taken out on purpose.
        internal static ModularRecipeResult? BuildRecipe(MaterialRecipe recipe, bool singleOutput,
            Func<int, double> factorAt, Func<int, bool> fixedDropped)
        {
            if (recipe.Materials.Count == 0) return null;

            var ingredients = new List<IngredientEntry>();
            for (int i = 0; i < recipe.Materials.Count; i++)
            {
                var entry = recipe.Materials[i];
                if (string.IsNullOrWhiteSpace(entry.Key)) continue;

                double factor = entry.Fixed ? (fixedDropped(i) ? 0 : 1) : factorAt(i);
                if (factor <= 0 || entry.Count <= 0) continue;
                int count = Math.Max(1, (int)Math.Round(entry.Count * factor, MidpointRounding.AwayFromZero));

                var existing = ingredients.FirstOrDefault(x => string.Equals(x.Key, entry.Key, StringComparison.OrdinalIgnoreCase));
                if (existing != null) existing.Count += count;
                else ingredients.Add(new IngredientEntry { Key = entry.Key, Count = count });
            }

            if (ingredients.Count == 0) return null;
            if (singleOutput && ingredients.Count > 1) ingredients = ingredients.Take(1).ToList();

            return new ModularRecipeResult
            {
                WorkbenchKey = recipe.WorkbenchKey,
                Ingredients = ingredients,
                Conditions = recipe.Conditions.Select(c => new ConditionEntry
                {
                    ConditionType = c.ConditionType,
                    Target = c.Target,
                    Value = c.Value,
                    RunOn = c.RunOn,
                }).ToList(),
            };
        }
    }
}
