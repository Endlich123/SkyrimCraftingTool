using SkyrimCraftingTool.Model;
using SkyrimCraftingTool.ViewModel;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Numerics;

namespace SkyrimCraftingTool.Services
{
    // Writes one PresetSlotConfig's enabled fields onto a single item. Since 2026-10-20 the only
    // caller is the modular preset applier (ModularPresetApplier): it builds the engine's result into
    // this shape. The slot presets this was written for are gone; their merge rules - recipe
    // creation, the exact-set ingredient merge, condition replacement (since 2026-11-09 every
    // editable condition, see MergeConditions), the breakdown block,
    // enchantment strip, placements added - are what this keeps.
    //
    // Writes go through the item's property setters. Returns the ItemNodeVM field names actually
    // touched (the names the Save*Handler classes key off), so the caller can await
    // MainContentVM.PersistFieldAsync per field instead of relying on the shared debouncer, which
    // would cancel one item's pending save the moment the next item's field change comes in.
    public static class PresetApplyService
    {
        public static List<string> ApplyConfig(ItemNodeVM item, PresetSlotConfig config)
        {
            var touched = new List<string>();
            if (item == null || config == null) return touched;
            ApplyMatches(item, new List<PresetSlotConfig> { config }, touched);
            return touched;
        }

        private static void ApplyMatches(ItemNodeVM item, List<PresetSlotConfig> matches, List<string> touched)
        {
            ApplyNumericFields(item, matches, touched);
            ApplyKeywords(item, matches, touched);
            ApplyRecipe(item, matches, isTemper: false, touched);
            ApplyRecipe(item, matches, isTemper: true, touched);
            ApplyBreakdown(item, matches, touched);
            ApplyContainer(item, matches, touched);
            ApplyEnchantment(item, matches, touched);
            ApplyEnchantmentAmount(item, matches, touched);
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
            // Only what actually changes: an equal value written back still costs a DB write per field
            // and marks the item edited for nothing (64,414 writes in a load-order-wide patch).
            if (TrySum(matches, c => c.Weight, out double weight) && item.Weight != (float)weight)
            {
                item.Weight = (float)weight;
                touched.Add(nameof(ItemNodeVM.Weight));
            }
            if (TrySum(matches, c => c.Value, out int cost) && item.Value != cost)
            {
                item.Value = cost;
                touched.Add(nameof(ItemNodeVM.Value));
            }

            if (item.IsArmor)
            {
                if (TrySum(matches, c => c.ArmorRating, out double ar) && item.ArmorRating != (float)ar)
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
            bool created = false;
            if (!hasRecipe)
            {
                if (isTemper) item.CreateTemperRecipe(); else item.CreateCraftingRecipe();
                hasRecipe = isTemper ? item.HasTemperRecipe : item.HasCraftingRecipe;
                created = hasRecipe;
            }
            if (!hasRecipe) return;

            int touchedBefore = touched.Count;

            if (!isTemper)
            {
                // Workbench can't be summed like a number — first enabled, non-empty value wins,
                // in ascending Armor-Slot-bit order (GetMatchingArmorConfigs already sorted `matches`).
                var wb = matches.Select(m => m.CraftRecipe.WorkbenchKey)
                    .Where(w => w.Enabled)
                    .Select(w => w.Value)
                    .FirstOrDefault(v => !string.IsNullOrEmpty(v));
                if (!string.IsNullOrEmpty(wb) && item.CraftingWorkbenchKey != wb)
                {
                    item.CraftingWorkbenchKey = wb;
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

            // A recipe created above lives ONLY in the ViewModel until one of ITS OWN fields gets
            // persisted: CreateCrafting/TemperRecipe builds the COBJRecord in memory, and
            // "CraftingRecipe"/"TemperRecipe" itself has no save handler (SaveRequestService just
            // logs "no handler" and drops the request). So whenever every merge above reported "no
            // change" — an enabled-but-empty ingredient/condition list, or a Workbench value that
            // already equals the Forge default CreateCraftingRecipe just set — the caller had
            // nothing to persist for this recipe and the item was left marked as edited with a
            // recipe no DB row backs. Reset could then never clear it: ResetCrafting/
            // TemperRecipeEdits looks that row up and used to bail when it wasn't there, so the
            // button stayed enabled and did nothing until an app restart rebuilt the tree.
            // Naming the Ingredients field routes the new record through Crafting/TemperSaveHandler,
            // which inserts it — with its empty ingredient list — exactly like the "+" button's
            // first save does.
            if (created && touched.Count == touchedBefore)
                touched.Add(isTemper ? nameof(ItemNodeVM.TemperIngredients) : nameof(ItemNodeVM.CraftingIngredients));
        }

        // Builds one desired total per material by summing across every matching slot that has
        // Ingredients enabled ("slots addieren"), then makes the item's ingredient list EXACTLY that
        // set: counts are set (not stacked), and any ingredient the preset doesn't include is
        // removed - same "old gets replaced by new" rule as Conditions and Container.
        // Exception: if no matching slot enables Ingredients, or every enabled slot's list is empty,
        // nothing is touched (matches the preset editor's "enabled but empty - won't be applied").
        private static bool MergeIngredients(ItemNodeVM item, ObservableCollection<IngredientEntryVM> target,
            List<PresetSlotConfig> matches, Func<PresetSlotConfig, RecipeConfig> select, bool isTemper)
        {
            var desiredCounts = new Dictionary<string, int>();
            foreach (var m in matches)
            {
                var recipe = select(m);
                if (!recipe.Ingredients.Enabled) continue;

                foreach (var ing in recipe.Ingredients.Value ?? new List<IngredientEntry>())
                {
                    if (string.IsNullOrEmpty(ing.Key)) continue;
                    desiredCounts[ing.Key] = desiredCounts.TryGetValue(ing.Key, out var c) ? c + ing.Count : ing.Count;
                }
            }

            if (desiredCounts.Count == 0) return false;

            bool changed = false;

            // Drop what the preset doesn't want.
            for (int i = target.Count - 1; i >= 0; i--)
            {
                if (!desiredCounts.ContainsKey(target[i].Key ?? ""))
                {
                    target.RemoveAt(i);
                    changed = true;
                }
            }

            foreach (var (key, count) in desiredCounts)
            {
                var existing = target.FirstOrDefault(i => i.Key == key);
                if (existing != null)
                {
                    if (existing.Count != count) { existing.Count = count; changed = true; }
                    continue;
                }

                var mat = item.Main?.FindMaterial(key);
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

        private static void ApplyBreakdown(ItemNodeVM item, List<PresetSlotConfig> matches, List<string> touched)
        {
            bool wanted = matches.Any(m =>
                m.BreakdownRecipe.WorkbenchKey.Enabled
                || m.BreakdownRecipe.Ingredients.Enabled
                || m.BreakdownRecipe.Conditions.Enabled);
            if (!wanted) return;

            // Workbench can't be summed - first enabled, non-empty value wins, in ascending
            // Armor-Slot-bit order (GetMatchingArmorConfigs already sorted `matches`). Without one,
            // a recipe that has to be created lands at the smelter, which is what the section's own
            // add button does.
            var bench = matches
                .Select(m => m.BreakdownRecipe.WorkbenchKey)
                .FirstOrDefault(w => w.Enabled && !string.IsNullOrEmpty(w.Value))?.Value;

            var row = item.BreakdownRecipe;
            if (row == null)
            {
                row = item.CreateBreakdownRecipe(bench ?? ViewModel.BreakdownRecipeVM.SmelterKey);
                if (row == null) return;
                touched.Add(nameof(ItemNodeVM.BreakdownRecipes));
            }
            else if (bench != null && row.SelectedBench?.Key != bench)
            {
                row.SelectedBench = ViewModel.BreakdownRecipeVM.Benches
                    .FirstOrDefault(b => string.Equals(b.Key, bench, StringComparison.OrdinalIgnoreCase));
                touched.Add(nameof(ItemNodeVM.BreakdownRecipes));
            }

            // The output: capped at one entry in the editor, so the first enabled, non-empty one
            // wins rather than anything being merged.
            var output = matches
                .Select(m => m.BreakdownRecipe.Ingredients)
                .Where(i => i.Enabled)
                .SelectMany(i => i.Value ?? new List<IngredientEntry>())
                .FirstOrDefault(e => !string.IsNullOrEmpty(e.Key));

            if (output != null)
            {
                var entry = row.OutputRow.FirstOrDefault();
                if (entry != null)
                {
                    // Resolve the material and select it, do NOT just set the Key.
                    //
                    // The ComboBox binds SelectedMaterial; Key alone leaves it with nothing
                    // selected, and the box stayed EMPTY until a restart re-ran the load path that
                    // resolves key -> material. MergeIngredients does the same two-step for the
                    // crafting block, for the same reason.
                    var material = item.Main?.FindMaterial(output.Key);

                    // The public setter, not SetSelectedMaterialSilent: the silent one is right for
                    // the crafting block, whose save handler re-reads the whole ingredient list
                    // afterwards, but a breakdown row only reaches its record through this
                    // notification. Silent here would leave the box filled and the record empty.
                    if (material != null) entry.SelectedMaterial = material;
                    else entry.Key = output.Key;   // dead reference - show the raw key, not a blank box

                    entry.Count = output.Count < 1 ? 1 : output.Count;
                    touched.Add(nameof(ItemNodeVM.BreakdownRecipes));
                }
            }

            if (MergeConditions(item, row.Conditions, matches, c => c.BreakdownRecipe))
                touched.Add(nameof(ItemNodeVM.BreakdownRecipes));
        }

        internal static bool MergeConditions(ItemNodeVM item, ObservableCollection<BaseConditionViewModel> target,
            List<PresetSlotConfig> matches, Func<PresetSlotConfig, RecipeConfig> select)
        {
            bool changed = false;
            bool cleared = false;

            // A list that already ENDS on an OR - as the plugin wrote it, nothing after it. The game
            // has nothing to pair that OR with, so "A or B or (end)" is "A or B". The preset's rows go
            // on the end, and the OR would join them: 4 Vigilant recipes became "global or global or
            // Steel Smithing" in the first test, the perk alone opening a recipe that wants quest
            // progress. 50 of 1,783 conditioned recipes on the real modlist end like this.
            // Taken BEFORE anything is removed: a row whose OR partner the preset replaces (global OR
            // perk -> global OR the preset's perk) is a group on purpose and keeps its OR.
            var danglingOr = target.LastOrDefault() is { HasOrFlag: true } last ? last : null;

            foreach (var m in matches)
            {
                var recipe = select(m);
                if (!recipe.Conditions.Enabled) continue;

                foreach (var entry in recipe.Conditions.Value ?? new List<ConditionEntry>())
                {
                    // A preset with conditions REPLACES every condition the editor can set - the recipe
                    // ends up as the preset says, not as the preset plus whatever editable rows the
                    // plugin had of other types (2026-11-09; before, only the types the preset itself
                    // named were replaced, so an old GetItemCount or GetLevel stayed on beside it).
                    // What the editor cannot set stays: read-only and unknown functions, and an
                    // editable type compared against a global - nothing in a preset could put them
                    // back. A preset without conditions leaves the item's alone (Enabled = false).
                    if (!cleared)
                    {
                        cleared = true;
                        var toRemove = target.Where(c => c.Type != CustomConditionType.ReadOnly).ToList();
                        foreach (var old in toRemove)
                            target.Remove(old); // CollectionChanged fires NotifyFieldChanged, same as Add below
                        if (toRemove.Count > 0) changed = true;
                    }

                    // Exact duplicate from an earlier matching slot in this same apply. EVERY field, not
                    // just type/target/run-on: "GetLevel >= 10" and "GetLevel < 30" share all three
                    // and are a range, not a repeat - the second one used to vanish here.
                    var wanted = entry.ToRecord();
                    bool alreadyPresent = target.Any(c => SameCondition(ConditionMapper.ToRecord(c, string.Empty), wanted));
                    if (alreadyPresent) continue;

                    if (danglingOr != null)
                    {
                        if (ReferenceEquals(target.LastOrDefault(), danglingOr)) danglingOr.ClearOrFlag();
                        danglingOr = null;
                    }

                    var vm = PresetConditionMapper.ToViewModel(entry, item.AllAvailablePerks, item.AllAvailableQuests);
                    target.Add(vm);
                    changed = true;
                }
            }
            return changed;
        }

        private static bool SameCondition(COBJConditionRecord a, COBJConditionRecord b)
            => a.ConditionType == b.ConditionType
            && string.Equals(a.Target, b.Target, StringComparison.OrdinalIgnoreCase)
            && a.Value == b.Value
            && a.RunOn == b.RunOn
            && (a.CompareOperator ?? "") == (b.CompareOperator ?? "")
            && (a.Flags ?? "") == (b.Flags ?? "")
            && string.Equals(a.RunOnRef ?? "", b.RunOnRef ?? "", StringComparison.OrdinalIgnoreCase)
            && (a.Extra ?? "") == (b.Extra ?? "");

        // A container already selected on the item (whether from before this Apply, or added by an
        // earlier matching slot in this same Apply) has its LVLi levels REPLACED by the preset's
        // levels, not left untouched - "old gets replaced by new" applies here too. A container
        // not yet present is added fresh with the preset's levels.
        private static void ApplyContainer(ItemNodeVM item, List<PresetSlotConfig> matches, List<string> touched)
        {
            var enabledStrings = matches
                .Where(m => m.Container.Enabled)
                .Select(m => m.Container.Value)
                .OfType<string>()
                .Where(v => v.Length > 0);

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
                    existing?.ApplyPlacements(entry.Placements);
                    changed = true;
                }
            }

            if (changed)
            {
                item.ContainerString = item.ContainerSelection.BuildString();
                touched.Add(nameof(ItemNodeVM.ContainerString));
            }
        }

        // One item has one enchantment, so unlike keywords there is nothing to merge: the first
        // enabled matching slot wins, the same tie-break the workbench field uses.
        //
        // An enabled field with an empty value is honoured as written and STRIPS the enchantment -
        // "none" is a real choice here, not a missing one. Enabled is what decides whether the
        // preset has an opinion at all.
        private static void ApplyEnchantment(ItemNodeVM item, List<PresetSlotConfig> matches, List<string> touched)
        {
            var cfg = matches.FirstOrDefault(m => m.Enchantment != null && m.Enchantment.Enabled);
            if (cfg == null) return;

            string key = cfg.Enchantment.Value ?? "";
            if (string.Equals(item.ObjectEffectKey ?? "", key, StringComparison.OrdinalIgnoreCase)) return;

            item.ObjectEffectKey = key;
            touched.Add(nameof(ItemNodeVM.ObjectEffectKey));
        }

        // The charge pool (EAMT) that goes with the enchantment above. Picked independently of it:
        // a slot may raise the charge of items that keep their own effect, or assign an effect and
        // leave the charge alone, so this looks at its own switch rather than the enchantment's.
        //
        // NOT summed across the matching slots the way Weight/Value/ArmorRating are: an item has one
        // charge pool, so two matching slots would otherwise hand a cuirass twice the charge the
        // preset actually names. First enabled slot wins - the enchantment and workbench tie-break.
        //
        // 0 is honoured as written: on an unenchanted item that is the correct value, and it is how
        // a preset takes a charge back off.
        private static void ApplyEnchantmentAmount(ItemNodeVM item, List<PresetSlotConfig> matches, List<string> touched)
        {
            var cfg = matches.FirstOrDefault(m => m.EnchantmentAmount != null && m.EnchantmentAmount.Enabled);
            if (cfg == null) return;

            int amount = cfg.EnchantmentAmount.Value;
            if (item.EnchantAmount == amount) return;

            item.EnchantAmount = amount;
            touched.Add(nameof(ItemNodeVM.EnchantAmount));
        }
    }
}
