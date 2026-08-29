using SkyrimCraftingTool.Model;
using SkyrimCraftingTool.ViewModel;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Numerics;

namespace SkyrimCraftingTool.Services
{
    // Applies a Preset's enabled fields onto a single item, merging across every Armor-Slot the item
    // occupies (or the single matching Weapon-Type). Writes go through the item's normal property
    // setters, so the existing autosave pipeline (NotifyFieldChanged -> SaveRequestService) picks
    // them up automatically for a single-item apply — see ItemNodeVM.ApplyPresetCommand.
    //
    // Returns the list of ItemNodeVM field names actually touched (using the exact names the
    // Save*Handler classes key off), so a bulk caller (MultiSelectDetailVM) can explicitly await
    // MainContentVM.PersistFieldAsync per field instead of relying on the shared debouncer, which
    // would cancel one item's pending save the moment the next item's field change comes in.
    public static class PresetApplyService
    {
        public static List<string> Apply(ItemNodeVM item, PresetFile preset)
        {
            var touched = new List<string>();
            if (item == null || preset == null) return touched;

            var matches = item.IsArmor
                ? GetMatchingArmorConfigs(item, preset)
                : GetMatchingWeaponConfigs(item, preset);

            if (matches.Count == 0) return touched;

            ApplyNumericFields(item, matches, touched);
            ApplyKeywords(item, matches, touched);
            ApplyRecipe(item, matches, isTemper: false, touched);
            ApplyRecipe(item, matches, isTemper: true, touched);
            ApplyContainer(item, matches, touched);

            return touched;
        }

        // All slots the item occupies (BodySlotMask can have multiple bits set), sorted ascending by
        // bit — the order the "first enabled non-empty wins" Workbench tie-break rule uses below.
        private static List<PresetSlotConfig> GetMatchingArmorConfigs(ItemNodeVM item, PresetFile preset)
        {
            var matches = new List<(int Bit, PresetSlotConfig Config)>();
            foreach (var cfg in preset.ArmorSlots)
            {
                if (!int.TryParse(cfg.NodeKey, out int bit)) continue;
                uint flag = 1u << bit;
                if ((item.BodySlotMask & flag) != 0)
                    matches.Add((bit, cfg));
            }
            return matches.OrderBy(m => m.Bit).Select(m => m.Config).ToList();
        }

        // A weapon has exactly one WeapType keyword (read-only, enforced elsewhere) — at most one match.
        private static List<PresetSlotConfig> GetMatchingWeaponConfigs(ItemNodeVM item, PresetFile preset)
        {
            var weapTypeKey = item.AllKeywords
                .FirstOrDefault(k => k.IsSelected && k.Name != null && k.Name.StartsWith("WeapType", StringComparison.OrdinalIgnoreCase))
                ?.Key;
            if (weapTypeKey == null) return new List<PresetSlotConfig>();

            var cfg = preset.WeaponTypes.FirstOrDefault(s => s.NodeKey == weapTypeKey);
            return cfg != null ? new List<PresetSlotConfig> { cfg } : new List<PresetSlotConfig>();
        }

        private static bool TrySum<T>(List<PresetSlotConfig> matches, Func<PresetSlotConfig, FieldValue<T>> selector, out T sum)
            where T : struct, INumber<T>
        {
            sum = T.Zero;
            bool any = false;
            foreach (var m in matches)
            {
                var fv = selector(m);
                if (fv.Enabled)
                {
                    sum += fv.Value;
                    any = true;
                }
            }
            return any;
        }

        private static void ApplyNumericFields(ItemNodeVM item, List<PresetSlotConfig> matches, List<string> touched)
        {
            if (TrySum(matches, c => c.Weight, out double weight))
            {
                item.Weight = (float)weight;
                touched.Add(nameof(ItemNodeVM.Weight));
            }
            if (TrySum(matches, c => c.Value, out int cost))
            {
                item.Value = cost;
                touched.Add(nameof(ItemNodeVM.Value));
            }

            if (item.IsArmor)
            {
                if (TrySum(matches, c => c.ArmorRating, out double ar))
                {
                    item.ArmorRating = (float)ar;
                    touched.Add(nameof(ItemNodeVM.ArmorRating));
                }
            }
            else
            {
                if (TrySum(matches, c => c.Damage, out int dmg)) { item.Damage = dmg; touched.Add(nameof(ItemNodeVM.Damage)); }
                if (TrySum(matches, c => c.Speed, out double spd)) { item.Speed = (float)spd; touched.Add(nameof(ItemNodeVM.Speed)); }
                if (TrySum(matches, c => c.Reach, out double rch)) { item.Reach = (float)rch; touched.Add(nameof(ItemNodeVM.Reach)); }
                if (TrySum(matches, c => c.Stagger, out double stg)) { item.Stagger = (float)stg; touched.Add(nameof(ItemNodeVM.Stagger)); }
            }
        }

        // Only slots with Keywords.Enabled participate at all - if none of the matching slots opted
        // in, keywords are left completely untouched (same "Enabled = preset controls this field"
        // rule as every other field). Once at least one matching slot opts in, the union of their
        // Keyword lists becomes the item's COMPLETE keyword set: anything not in that union gets
        // deselected too, not just left stacked on top of - "old gets replaced by new" applies
        // here exactly like Ingredients/Conditions/Container. Read-only keywords (e.g. the item's
        // WeapType) are never touched either way. Selection still goes through the same IsSelected
        // setter a manual click uses, so ApplyKeywordRules (Light/Heavy/Clothing exclusivity etc.)
        // still runs and SelectedKeywordKeys stays in sync.
        private static void ApplyKeywords(ItemNodeVM item, List<PresetSlotConfig> matches, List<string> touched)
        {
            var enabledMatches = matches.Where(m => m.Keywords.Enabled).ToList();
            if (enabledMatches.Count == 0) return;

            var desiredKeys = enabledMatches
                .SelectMany(m => m.Keywords.Value ?? new List<string>())
                .Distinct()
                .ToHashSet();

            bool changed = false;
            foreach (var kw in item.AllKeywords)
            {
                if (kw.IsReadOnly) continue;
                bool shouldBeSelected = desiredKeys.Contains(kw.Key);
                if (kw.IsSelected != shouldBeSelected)
                {
                    kw.IsSelected = shouldBeSelected;
                    changed = true;
                }
            }

            if (changed)
                touched.Add(nameof(ItemNodeVM.SelectedKeywordKeys));
        }

        private static void ApplyRecipe(ItemNodeVM item, List<PresetSlotConfig> matches, bool isTemper, List<string> touched)
        {
            Func<PresetSlotConfig, RecipeConfig> select = isTemper ? c => c.TemperRecipe : c => c.CraftRecipe;

            bool needed = matches.Any(m =>
            {
                var r = select(m);
                return (!isTemper && r.WorkbenchKey.Enabled) || r.Ingredients.Enabled || r.Conditions.Enabled;
            });
            if (!needed) return;

            bool hasRecipe = isTemper ? item.HasTemperRecipe : item.HasCraftingRecipe;
            if (!hasRecipe)
            {
                // Prerequisite the user flagged during planning: a preset can't set Workbench/Ingredients/
                // Conditions on an item with no COBJ yet — create one first, the same way the "+" button does.
                if (isTemper) item.CreateTemperRecipe(); else item.CreateCraftingRecipe();
                hasRecipe = isTemper ? item.HasTemperRecipe : item.HasCraftingRecipe;
            }
            if (!hasRecipe) return;

            if (!isTemper)
            {
                // Workbench can't be summed like a number — first enabled, non-empty value wins,
                // in ascending Armor-Slot-bit order (GetMatchingArmorConfigs already sorted `matches`).
                var wb = matches.Select(m => m.CraftRecipe.WorkbenchKey)
                    .FirstOrDefault(w => w.Enabled && !string.IsNullOrEmpty(w.Value));
                if (wb != null && item.CraftingWorkbenchKey != wb.Value)
                {
                    item.CraftingWorkbenchKey = wb.Value;
                    touched.Add(nameof(ItemNodeVM.CraftingWorkbenchKey));
                }
            }
            // Temper's workbench is always auto-derived from the item at COBJ-creation time (see
            // CreateNewCOBJRecordForItem) — never user-settable, so there's nothing to apply here.

            var targetIngredients = isTemper ? item.TemperIngredients : item.CraftingIngredients;
            if (MergeIngredients(item, targetIngredients, matches, select, isTemper))
                touched.Add(isTemper ? nameof(ItemNodeVM.TemperIngredients) : nameof(ItemNodeVM.CraftingIngredients));

            var targetConditions = isTemper ? item.TemperConditions : item.CraftingConditions;
            if (MergeConditions(item, targetConditions, matches, select))
                touched.Add(isTemper ? nameof(ItemNodeVM.TemperConditions) : nameof(ItemNodeVM.CraftingConditions));
        }

        // Sums each material's count across every matching slot that has Ingredients enabled (keeps
        // the "slots addieren" rule) into a single desired total per material, then SETS the item's
        // count for that material to this total — the preset's computed total replaces whatever was
        // already on the item (manually set, or left over from an earlier Apply), it does not stack
        // on top of it.
        private static bool MergeIngredients(ItemNodeVM item, ObservableCollection<IngredientEntryVM> target,
            List<PresetSlotConfig> matches, Func<PresetSlotConfig, RecipeConfig> select, bool isTemper)
        {
            var desiredCounts = new Dictionary<string, int>();
            foreach (var m in matches)
            {
                var recipe = select(m);
                if (!recipe.Ingredients.Enabled) continue;

                foreach (var ing in recipe.Ingredients.Value ?? new List<IngredientEntry>())
                    desiredCounts[ing.Key] = desiredCounts.TryGetValue(ing.Key, out var c) ? c + ing.Count : ing.Count;
            }

            bool changed = false;
            foreach (var (key, count) in desiredCounts)
            {
                var existing = target.FirstOrDefault(i => i.Key == key);
                if (existing != null)
                {
                    if (existing.Count != count) { existing.Count = count; changed = true; }
                    continue;
                }

                var mat = item.Main?.AllAvailableMaterials?.FirstOrDefault(x => x.Key == key);
                var entry = new IngredientEntryVM(item, isTemper);
                entry.InitializeMaterials(item.Main?.AllAvailableMaterials ?? new List<FormIDRecord>());
                if (mat != null)
                    entry.SetSelectedMaterialSilent(mat);
                else
                    entry.Key = key;
                entry.Count = count;

                target.Add(entry);
                changed = true;
            }
            return changed;
        }

        // For every ConditionType the (enabled, matching) preset slots define, the item's EXISTING
        // conditions of that same type are cleared once, then replaced by the preset's condition(s)
        // of that type — so a preset's HasPerk requirement replaces the item's old HasPerk
        // requirement instead of coexisting alongside it as a second, possibly conflicting one.
        // Conditions of a type no matching slot touches are left completely alone. Multiple matching
        // slots can still each contribute their own distinct condition of the same type (keeps the
        // "slots addieren" rule) - only an exact (Type, Target, RunOn) duplicate is skipped.
        private static bool MergeConditions(ItemNodeVM item, ObservableCollection<BaseConditionViewModel> target,
            List<PresetSlotConfig> matches, Func<PresetSlotConfig, RecipeConfig> select)
        {
            bool changed = false;
            var clearedTypes = new HashSet<string>();

            foreach (var m in matches)
            {
                var recipe = select(m);
                if (!recipe.Conditions.Enabled) continue;

                foreach (var entry in recipe.Conditions.Value ?? new List<ConditionEntry>())
                {
                    if (clearedTypes.Add(entry.ConditionType))
                    {
                        var toRemove = target
                            .Where(c => ConditionMapper.ToRecord(c, string.Empty).ConditionType == entry.ConditionType)
                            .ToList();
                        foreach (var old in toRemove)
                            target.Remove(old); // CollectionChanged fires NotifyFieldChanged, same as Add below
                        if (toRemove.Count > 0) changed = true;
                    }

                    bool alreadyPresent = target.Any(c =>
                    {
                        var rec = ConditionMapper.ToRecord(c, string.Empty);
                        return rec.ConditionType == entry.ConditionType && rec.Target == entry.Target && rec.RunOn == entry.RunOn;
                    });
                    if (alreadyPresent) continue; // exact duplicate from an earlier matching slot in this same apply

                    var vm = PresetConditionMapper.ToViewModel(entry, item.AllAvailablePerks, item.AllAvailableQuests);
                    target.Add(vm);
                    changed = true;
                }
            }
            return changed;
        }

        // A container already selected on the item (whether from before this Apply, or added by an
        // earlier matching slot in this same Apply) has its LVLi levels REPLACED by the preset's
        // levels, not left untouched - "old gets replaced by new" applies here too. A container
        // not yet present is added fresh with the preset's levels.
        private static void ApplyContainer(ItemNodeVM item, List<PresetSlotConfig> matches, List<string> touched)
        {
            var enabledStrings = matches
                .Where(m => m.Container.Enabled && !string.IsNullOrEmpty(m.Container.Value))
                .Select(m => m.Container.Value);

            bool changed = false;
            foreach (var containerString in enabledStrings)
            {
                foreach (var entry in ContainerStringParser.Parse(containerString))
                {
                    var existing = item.ContainerSelection.SelectedContainers.FirstOrDefault(sc => sc.ContainerKey == entry.ContainerKey);
                    if (existing == null)
                    {
                        item.ContainerSelection.ToggleContainer(entry.ContainerKey);
                        existing = item.ContainerSelection.SelectedContainers.FirstOrDefault(sc => sc.ContainerKey == entry.ContainerKey);
                    }
                    existing?.ApplyLevels(entry.Levels);
                    changed = true;
                }
            }

            if (changed)
            {
                item.ContainerString = item.ContainerSelection.BuildString();
                touched.Add(nameof(ItemNodeVM.ContainerString));
            }
        }
    }
}
