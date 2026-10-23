using SkyrimCraftingTool.Model;
using System;
using System.Collections.Generic;
using System.Linq;

namespace SkyrimCraftingTool.Services.ModularPresets
{
    // The weapon as the engine sees it - like ModularItemState, a plain snapshot a test can build.
    public class WeaponItemState
    {
        public string Name { get; set; } = "";
        public string EditorID { get; set; } = "";
        public string Plugin { get; set; } = "";
        public List<string> Keywords { get; set; } = new();
        public bool IsEnchanted { get; set; }

        // The weapon's current damage: what the critical damage is derived from when the preset
        // leaves the damage itself alone (no material, or KeepValues).
        public int Damage { get; set; }
    }

    // Null = leave the weapon's own value alone. Every field is optional on the weapon side: a type
    // sets only what it has values for (decided 2026-10-23).
    public class WeaponResult
    {
        public int? Damage { get; set; }
        public double? Weight { get; set; }
        public int? Value { get; set; }
        public double? Speed { get; set; }
        public double? Reach { get; set; }
        public double? Stagger { get; set; }
        public int? CritDamage { get; set; }
        public double? CritPercentMult { get; set; }

        public List<string> Keywords { get; set; } = new();

        public ModularRecipeResult? Craft { get; set; }
        public ModularRecipeResult? Temper { get; set; }
        public ModularRecipeResult? Breakdown { get; set; }

        public string? EnchantmentKey { get; set; }
        public int? EnchantmentAmount { get; set; }
        public List<string> Containers { get; set; } = new();

        public List<string> Warnings { get; set; } = new();
    }

    public class WeaponPrefillResult
    {
        public ModularSelection Selection { get; set; } = new();

        // "keyword: <key>", "editor id: <pattern>", "name: <term>" - shown in the dry run.
        public string TypeSource { get; set; } = "";
        public string MaterialSource { get; set; } = "";
        public Dictionary<string, string> AddonSources { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> Notes { get; set; } = new();
    }

    public static class WeaponPrefill
    {
        private static readonly StringComparer Names = StringComparer.OrdinalIgnoreCase;

        public static WeaponPrefillResult Prefill(WeaponCatalog catalog, WeaponItemState item)
        {
            var result = new WeaponPrefillResult();
            var sel = result.Selection;
            var keywords = new HashSet<string>(item.Keywords, Names);

            // Type: the FIRST type in the list that matches - by keyword, else EditorID, else name.
            // Mods put their own keyword next to the vanilla one (ShWeapTypeHalberd + WeapTypeBattleaxe),
            // so the specific type has to stand above the general one.
            foreach (var t in catalog.Types)
            {
                var kw = t.Keywords.FirstOrDefault(keywords.Contains);
                string? source = kw != null ? "keyword"
                    : ModularPrefill.EditorIdMatch(item.EditorID, t.EditorIdPatterns) is string ed ? "editor id: " + ed
                    : NameMatcher.Match(item.Name, t.NamePatterns, t.NameExclusions) is string hit ? "name: " + hit
                    : null;
                if (source == null) continue;
                sel.TypeName = t.Name;
                result.TypeSource = source;
                break;
            }
            if (sel.TypeName.Length == 0)
                result.Notes.Add("No weapon type found (neither keyword, EditorID nor name) - the weapon is left as it is.");

            // Material: keyword first, then the name fallback, first in the list wins.
            var byKeyword = catalog.Materials.FirstOrDefault(m => !string.IsNullOrWhiteSpace(m.MaterialKeyword) && keywords.Contains(m.MaterialKeyword));
            if (byKeyword != null)
            {
                sel.MaterialName = byKeyword.Name;
                result.MaterialSource = "keyword";
            }
            else
            {
                foreach (var m in catalog.Materials)
                {
                    if (NameMatcher.Match(item.Name, m.NamePatterns, m.NameExclusions) is not string hit) continue;
                    sel.MaterialName = m.Name;
                    result.MaterialSource = "name: " + hit;
                    break;
                }
            }
            if (sel.MaterialName.Length == 0 && sel.TypeName.Length > 0)
                result.Notes.Add("No material found (neither keyword nor name) - damage, weight and value stay as they are.");

            // Addons: the armor rules, with the weapon type where armor has its slots (OnlySlots).
            var scope = sel.TypeName.Length > 0 ? new[] { sel.TypeName } : Array.Empty<string>();
            ModularPrefill.PickAddons(catalog.Addons, item.Name, item.EditorID, item.Plugin, keywords, scope,
                sel.AddonNames, result.AddonSources);

            return result;
        }
    }

    // Turns one weapon's selection (weapon type, material, addons, extras) into its values.
    //   Damage    = type base + material bonus for the type's class, x addon damage factors
    //   Weight    = type base + material bonus, x addon weight factors, rounded to the type's step
    //   Value     = material base value x type value factor, x addon value factors
    //   Speed     = the class bonus's speed (bows) or the type's; Reach, Stagger from the type
    //   Crit      = damage x the type's crit factor, rounded down; crit chance as the type sets it
    // A material exception for the type (WeaponTypeOverride) replaces the formula for that field.
    public static class WeaponPresetEngine
    {
        private static readonly StringComparer Names = StringComparer.OrdinalIgnoreCase;

        // weaponMaterialKeywords: the load order's WeapMaterial* keywords (see MaterialKeywordsOf) -
        // replaced together when another material is chosen.
        public static WeaponResult Compute(WeaponCatalog catalog, ModularSelection selection, WeaponItemState item,
            IReadOnlyCollection<string>? weaponMaterialKeywords = null)
        {
            var result = new WeaponResult();

            // No weapon type = the weapon is left exactly as it is, keywords included. Recomputing them
            // without a type took VendorItemWeapon off every untyped mod weapon (ZaZ crops and canes),
            // because the vendor rule only fires for the types it names.
            var type = catalog.Types.FirstOrDefault(t => Names.Equals(t.Name, selection.TypeName));
            if (type == null)
            {
                if (!string.IsNullOrWhiteSpace(selection.TypeName))
                    result.Warnings.Add($"Weapon type '{selection.TypeName}' not found - the weapon is left as it is.");
                result.Keywords = item.Keywords.ToList();
                return result;
            }

            WeaponMaterialModule? material = null;
            if (!string.IsNullOrWhiteSpace(selection.MaterialName))
            {
                material = catalog.Materials.FirstOrDefault(m => Names.Equals(m.Name, selection.MaterialName));
                if (material == null) result.Warnings.Add($"Material '{selection.MaterialName}' not found.");
            }

            var addons = new List<KeywordAddon>();
            foreach (var name in ModularPresetEngine.WithAlsoAddons(catalog.Addons, selection.AddonNames))
            {
                var addon = catalog.Addons.FirstOrDefault(a => Names.Equals(a.Name, name));
                if (addon == null) result.Warnings.Add($"Addon '{name}' not found.");
                else addons.Add(addon);
            }

            var hold = addons.FirstOrDefault(a => a.LeaveUntouched);
            if (hold != null)
                return new WeaponResult { Keywords = item.Keywords.ToList(), Warnings = { $"Left untouched (addon '{hold.Name}')." } };

            bool keepValues = addons.Any(a => a.KeepValues);
            bool keepRecipes = addons.Any(a => a.KeepRecipes);
            bool keepKeywords = addons.Any(a => a.KeepKeywords);

            // --- Values ---
            if (type != null && !keepValues)
                ComputeValues(type, material, addons, item, result);

            // --- Keywords ---
            result.Keywords = keepKeywords
                ? item.Keywords.ToList()
                : ComputeKeywords(catalog, type, material, addons, item.Keywords, weaponMaterialKeywords);

            // --- Extras ---
            var extras = ModularPresetEngine.ApplyExtras(catalog.Extras, selection.ExtraNames, result.Warnings);
            result.EnchantmentKey = extras.EnchantmentKey;
            result.EnchantmentAmount = extras.EnchantmentAmount;
            result.Containers.AddRange(extras.Containers);

            // --- Recipes: the material's, scaled by the type's factors (or the material's exception
            // for this type). No material or no type = the weapon's recipes stay as they are. ---
            if (material != null && type != null && !keepRecipes)
            {
                var factors = material.OverrideFor(type.Name)?.RecipeFactors ?? type.RecipeFactors;
                bool enchanted = item.IsEnchanted || !string.IsNullOrWhiteSpace(result.EnchantmentKey);

                ModularRecipeResult? Build(MaterialRecipe r, bool single) => ModularPresetEngine.BuildRecipe(r, single,
                    // No factors at all = the material's base amounts as they are.
                    factorAt: i => factors is { Count: > 0 } ? (i < factors.Count ? factors[i] : 0) : 1,
                    fixedDropped: i => material.OverrideFor(type.Name)?.RecipeFactors is { } f && i < f.Count && f[i] == 0);

                // Enchanted weapons: no new craft or temper recipe - loot, as with armor.
                if (!enchanted)
                {
                    result.Craft = Build(material.Craft, false);
                    result.Temper = Build(material.Temper, false);
                }
                result.Breakdown = Build(material.Breakdown, true);

                foreach (var a in addons)
                {
                    ModularPresetEngine.AddIngredients(result.Craft, a.CraftAdditions, a.Name, "crafting", result.Warnings);
                    ModularPresetEngine.AddIngredients(result.Temper, a.TemperAdditions, a.Name, "tempering", result.Warnings);
                }
            }

            return result;
        }

        private static void ComputeValues(WeaponTypeModule type, WeaponMaterialModule? material, List<KeywordAddon> addons,
            WeaponItemState item, WeaponResult result)
        {
            double damageF = 1, weightF = 1, valueF = 1;
            foreach (var a in addons)
            {
                damageF *= a.DamageFactor;
                weightF *= a.WeightFactor;
                valueF *= a.ValueFactor;
            }

            var bonus = material?.BonusFor(type.WeaponClass);
            var ovr = material?.OverrideFor(type.Name);

            // Damage, weight and value need a material: the type's base alone is the lowest tier, and
            // writing it onto a weapon of unknown material would downgrade it (the armor lesson,
            // 2026-10-22: the no-material fallback halved the armor rating of light mod armor).
            if (material != null)
            {
                if (ovr?.Damage is int d)
                    result.Damage = (int)Math.Round(d * damageF, MidpointRounding.AwayFromZero);
                else if (type.BaseDamage is double baseDamage)
                    result.Damage = (int)Math.Round((baseDamage + (bonus?.Damage ?? 0)) * damageF, MidpointRounding.AwayFromZero);

                if (ovr?.Weight is double w)
                    result.Weight = ModularPresetEngine.Step(w * weightF, type.WeightStep, 3);
                else if (type.BaseWeight is double baseWeight)
                    result.Weight = ModularPresetEngine.Step((baseWeight + (bonus?.Weight ?? 0)) * weightF, type.WeightStep, 3);

                if (ovr?.Value is int v)
                    result.Value = (int)Math.Round(v * valueF, MidpointRounding.AwayFromZero);
                else if (material.BaseValue is int baseValue && type.ValueFactor is double vf)
                    result.Value = (int)Math.Round(baseValue * vf * valueF, MidpointRounding.AwayFromZero);
            }

            // Handling belongs to the type and is the same for every material - only a bow's speed
            // comes with the material.
            result.Speed = ovr?.Speed ?? bonus?.Speed ?? type.Speed;
            result.Reach = ovr?.Reach ?? type.Reach;
            result.Stagger = ovr?.Stagger ?? type.Stagger;

            // From the damage the weapon ends up with - the new one, or its own when that stays. A
            // material's exception for the type wins (vanilla Glass daggers have 0).
            if (ovr?.CritDamage is int crit)
                result.CritDamage = crit;
            else if (type.CritFactor is double cf)
                result.CritDamage = (int)Math.Floor((result.Damage ?? item.Damage) * cf);
            result.CritPercentMult = ovr?.CritChance ?? type.CritChance;
        }

        // Rules (their Types = weapon types), the material keyword and the addons' keywords. Never
        // touched: the WeapType* keywords the types recognise by - they follow the animation.
        private static List<string> ComputeKeywords(WeaponCatalog catalog, WeaponTypeModule? type, WeaponMaterialModule? material,
            List<KeywordAddon> addons, List<string> current, IReadOnlyCollection<string>? weaponMaterialKeywords)
        {
            var produced = new List<string>();
            var seen = new HashSet<string>(Names);
            void Add(string k) { if (!string.IsNullOrWhiteSpace(k) && seen.Add(k)) produced.Add(k); }

            var addonNames = new HashSet<string>(addons.Select(a => a.Name), Names);
            foreach (var rule in catalog.Rules)
            {
                if (rule.Types.Count > 0 && (type == null || !rule.Types.Contains(type.Name, Names))) continue;
                if (rule.Materials.Count > 0 && (material == null || !rule.Materials.Contains(material.Name, Names))) continue;
                if (rule.Addons is { Count: > 0 } && !rule.Addons.Any(addonNames.Contains)) continue;
                if (rule.ExcludeAddons is { Count: > 0 } && rule.ExcludeAddons.Any(addonNames.Contains)) continue;
                foreach (var k in rule.Keywords) Add(k);
            }
            if (material != null) Add(material.MaterialKeyword);
            foreach (var a in addons)
                foreach (var k in a.Keywords) Add(k);

            var managed = new HashSet<string>(Names);
            foreach (var r in catalog.Rules) managed.UnionWith(r.Keywords.Where(k => !string.IsNullOrWhiteSpace(k)));
            foreach (var a in catalog.Addons) managed.UnionWith(a.Keywords.Where(k => !string.IsNullOrWhiteSpace(k)));
            var allMaterials = new HashSet<string>(weaponMaterialKeywords ?? Array.Empty<string>(), Names);
            allMaterials.UnionWith(catalog.Materials.Select(m => m.MaterialKeyword).Where(k => !string.IsNullOrWhiteSpace(k)));

            // Material keywords are replaced only when a material was chosen that the weapon does not
            // carry yet. Carrying it already, its other material keywords stay (as with armor); no
            // material at all, every one stays - they are what the smithing perks read.
            if (material != null && !current.Contains(material.MaterialKeyword, Names))
                managed.UnionWith(allMaterials);
            else
                managed.ExceptWith(allMaterials.Where(k => !produced.Contains(k, Names)));

            foreach (var t in catalog.Types) managed.ExceptWith(t.Keywords);

            var final = new List<string>();
            var finalSeen = new HashSet<string>(Names);
            foreach (var k in current)
                if (!managed.Contains(k) && finalSeen.Add(k)) final.Add(k);
            foreach (var k in produced)
                if (finalSeen.Add(k)) final.Add(k);
            return final;
        }

        // The load order's weapon material keywords, by name (WeapMaterial*, mod ones included).
        // Dragonborn spells it out: DLC2WeaponMaterialNordic, DLC2WeaponMaterialStalhrim.
        public static HashSet<string> MaterialKeywordsOf(IEnumerable<FormIDRecord>? keywords)
        {
            var set = new HashSet<string>(Names);
            foreach (var k in keywords ?? Enumerable.Empty<FormIDRecord>())
                if (!string.IsNullOrEmpty(k.Key) && IsMaterialKeywordName(k.Name))
                    set.Add(k.Key);
            return set;
        }

        public static bool IsMaterialKeywordName(string? name)
            => name != null && (name.Contains("WeapMaterial", StringComparison.OrdinalIgnoreCase)
                                || name.Contains("WeaponMaterial", StringComparison.OrdinalIgnoreCase));
    }
}
