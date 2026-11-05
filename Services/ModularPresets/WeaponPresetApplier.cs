using SkyrimCraftingTool.Model;
using SkyrimCraftingTool.Services.TemplateChildren;
using SkyrimCraftingTool.ViewModel;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace SkyrimCraftingTool.Services.ModularPresets
{
    // The weapon preset patch: pre-fill + engine like the dry run, then the result is written to the
    // weapon. The armor applier's twin - same bulk scope, lean loading, busy overlay and perf line.
    public static class WeaponPresetApplier
    {
        // The weapon as the engine sees it: a loaded item's live keyword list, otherwise the cache.
        public static WeaponItemState StateOf(MainContentVM main, ItemNodeVM item)
        {
            main.WeaponCache.TryGetValue(item.Key, out var rec);
            var keywords = item.HasLoadedDetails ? item.SelectedKeywordKeys : rec?.Keywords ?? new List<string>();
            return new WeaponItemState
            {
                Name = item.Name ?? "",
                EditorID = item.EditorID ?? "",
                Plugin = ModularPresetApplier.PluginOf(item.Key),
                Keywords = keywords.ToList(),
                Damage = item.Damage,
                IsEnchanted = !string.IsNullOrWhiteSpace(item.HasLoadedDetails ? item.ObjectEffectKey : rec?.ObjectEffectKey),
            };
        }

        // Display only, like its armor twin (Kinder-Patcher, 2026-10-28).
        // The weapon twin of ModularPresetApplier.TakeTemplateValues: an ENCHANTED weapon made from a
        // template (CNAM) gets the template's numbers - Thaumaturgy's CC crossbow variants carry 0
        // damage of their own (2026-10-26). Addons only the variant has (and that worked on it, not on
        // a template that keeps its values) multiply on top; critical damage follows the damage, as in
        // the engine. Held back by KeepValues / LeaveUntouched on the variant and when the template is
        // not scanned. The template is computed from its own detection, without dry-run choices.
        public static void TakeTemplateValues(MainContentVM main, WeaponCatalog catalog, ItemNodeVM item,
            ModularSelection selection, WeaponResult result, HashSet<string>? materialKeywords,
            IReadOnlyDictionary<string, ItemNodeVM>? nodesByKey = null)
        {
            if (!item.IsWeapon || string.IsNullOrWhiteSpace(item.TemplateKey)) return;
            if (string.Equals(item.TemplateKey, item.Key, StringComparison.OrdinalIgnoreCase)) return;
            if (!StateOf(main, item).IsEnchanted) return;   // unenchanted: the template only lends its model

            var variantAddons = ModularPresetEngine.WithAlsoAddons(catalog.Addons, selection.AddonNames)
                .Select(n => catalog.Addons.FirstOrDefault(a => string.Equals(a.Name, n, StringComparison.OrdinalIgnoreCase)))
                .Where(a => a != null)
                .Select(a => a!)
                .ToList();
            if (variantAddons.Any(a => a.KeepValues || a.LeaveUntouched)) return;

            // A run over thousands of items hands in its index - FindItemNode is a linear search.
            var node = nodesByKey != null
                ? (nodesByKey.TryGetValue(item.TemplateKey, out var n) ? n : null)
                : main.FindItemNode(item.TemplateKey);
            var record = main.FindWeaponRecord(item.TemplateKey);
            if (node == null && record == null) return;

            WeaponResult? computed = null;
            var templateAddons = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (node != null && node.IsWeapon && !ModularPresetApplier.IsSkipped(node))
            {
                var state = StateOf(main, node);
                var templateSelection = WeaponPrefill.Prefill(catalog, state).Selection;
                computed = WeaponPresetEngine.Compute(catalog, templateSelection, state, materialKeywords);
                // See ModularPresetApplier.TakeTemplateValues: only a template HELD by an addon took
                // none of its addons; one without material still shares them (Glenmoril pistols).
                var shared = ModularPresetEngine.WithAlsoAddons(catalog.Addons, templateSelection.AddonNames);
                bool templateHeld = shared
                    .Select(n => catalog.Addons.FirstOrDefault(a => string.Equals(a.Name, n, StringComparison.OrdinalIgnoreCase)))
                    .Any(a => a != null && (a.KeepValues || a.LeaveUntouched));
                if (!templateHeld)
                    templateAddons.UnionWith(shared);
            }

            double damage = computed?.Damage ?? node?.Damage ?? record!.Damage;
            double weight = computed?.Weight ?? node?.Weight ?? record!.Weight;
            double value = computed?.Value ?? node?.Value ?? record!.Value;
            double critDamage = computed?.CritDamage ?? node?.CritDamage ?? record!.CritDamage;

            var extra = variantAddons.Where(a => !templateAddons.Contains(a.Name)).ToList();
            foreach (var a in extra)
            {
                damage *= a.DamageFactor;
                critDamage *= a.DamageFactor;
                weight *= a.WeightFactor;
                value *= a.ValueFactor;
            }

            result.Damage = (int)Math.Round(damage, MidpointRounding.AwayFromZero);
            result.CritDamage = (int)Math.Round(critDamage, MidpointRounding.AwayFromZero);
            result.Weight = Math.Round(weight, 2);
            result.Value = (int)Math.Round(value, MidpointRounding.AwayFromZero);
            result.Speed = computed?.Speed ?? node?.Speed ?? record!.Speed;
            result.Reach = computed?.Reach ?? node?.Reach ?? record!.Reach;
            result.Stagger = computed?.Stagger ?? node?.Stagger ?? record!.Stagger;
            result.CritPercentMult = computed?.CritPercentMult ?? node?.CritPercentMult ?? record!.CritPercentMult;

            var bonus = extra.Where(a => a.DamageFactor != 1 || a.ValueFactor != 1 || a.WeightFactor != 1).Select(a => a.Name).ToList();
            result.Warnings.Add($"Values from the template weapon {(node?.Name ?? record?.Name ?? item.TemplateKey)}"
                + (bonus.Count > 0 ? $", times the variant's own addons ({string.Join(", ", bonus)})." : "."));
        }

        // The whole result onto one loaded weapon; returns the fields it touched (for persisting).
        // Weight, value, keywords, recipes, enchantment and placements go the armor way (the same
        // config shape); the weapon fields are set here, each only when it changes.
        public static List<string> ApplyResult(ItemNodeVM item, WeaponResult result)
        {
            var shared = new ModularResult
            {
                Weight = result.Weight,
                Value = result.Value,
                Keywords = result.Keywords,
                Craft = result.Craft,
                Temper = result.Temper,
                Breakdown = result.Breakdown,
                EnchantmentKey = result.EnchantmentKey,
                EnchantmentAmount = result.EnchantmentAmount,
                Containers = result.Containers,
            };
            var touched = PresetApplyService.ApplyConfig(item, ModularPresetApplier.ToConfig(shared));

            // See ModularPresetApplier.ApplyResult: a lean-loaded item takes the keyword list as it is.
            if (!item.HasKeywordVMs)
            {
                var current = new HashSet<string>(item.SelectedKeywordKeys ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
                if (!current.SetEquals(result.Keywords))
                {
                    item.SelectedKeywordKeys = result.Keywords.ToList();
                    touched.Add(nameof(ItemNodeVM.SelectedKeywordKeys));
                }
            }
            foreach (var container in result.Containers.Skip(1))
                touched.AddRange(PresetApplyService.ApplyConfig(item, ModularPresetApplier.ContainerOnly(container)));

            if (result.Damage is int damage && item.Damage != damage)
            {
                item.Damage = damage;
                touched.Add(nameof(ItemNodeVM.Damage));
            }
            if (result.CritDamage is int crit && item.CritDamage != crit)
            {
                item.CritDamage = crit;
                touched.Add(nameof(ItemNodeVM.CritDamage));
            }
            SetFloat(result.Speed, item.Speed, v => item.Speed = v, nameof(ItemNodeVM.Speed));
            SetFloat(result.Reach, item.Reach, v => item.Reach = v, nameof(ItemNodeVM.Reach));
            SetFloat(result.Stagger, item.Stagger, v => item.Stagger = v, nameof(ItemNodeVM.Stagger));
            SetFloat(result.CritPercentMult, item.CritPercentMult, v => item.CritPercentMult = v, nameof(ItemNodeVM.CritPercentMult));

            if (touched.Count > 0) item.MarkEdited();   // see ModularPresetApplier.ApplyResult
            return touched.Distinct().ToList();

            void SetFloat(double? value, float current, Action<float> set, string field)
            {
                if (value is not double v || Math.Abs(current - v) < 0.0001) return;
                set((float)v);
                touched.Add(field);
            }
        }

        // resolve: per item, from what the pre-fill detected to what is built (the dry run's cards).
        // Children and the templates added for them: see ModularPresetApplier.ApplyAsync and ChildScope.
        public static async Task<ModularPatchReport> ApplyAsync(MainContentVM main, WeaponCatalog catalog, IReadOnlyList<ItemNodeVM> items,
            Func<ItemNodeVM, ModularSelection, ModularSelection>? resolve)
        {
            var report = new ModularPatchReport();
            using var busy = BusyService.Show("Weapon preset patch…", blocksUiThread: true);
            using var bulk = main.BeginBulk();

            await main.FlushPendingSavesAsync();
            var materialKeywords = WeaponPresetEngine.MaterialKeywordsOf(main.AllAvailableKeywords);

            var families = main.WeaponFamilies;
            var scope = ChildScope.Plan(main, items, i => i.IsWeapon, families);
            report.SkippedNoName = scope.SkippedNoName;
            report.TemplatesAdded = scope.AddedTemplates.Count;

            var total = PerfClock.StartNew();
            int writes = 0;
            var selections = new Dictionary<string, ModularSelection>(StringComparer.OrdinalIgnoreCase);
            int steps = scope.Computed.Count + scope.Children.Count, step = 0;

            ModularSelection SelectionOf(ItemNodeVM item, ModularSelection detected)
                => scope.Requested.Contains(item.Key) ? resolve?.Invoke(item, detected) ?? detected : detected;

            async Task BuildAsync(ItemNodeVM item, ModularSelection selection, Action<WeaponResult>? adjust)
            {
                report.Items++;
                bool wasLoaded = item.HasLoadedDetails;
                main.EnsureItemHydrated(item, lean: true);

                var state = StateOf(main, item);
                var result = WeaponPresetEngine.Compute(catalog, selection, state, materialKeywords);
                adjust?.Invoke(result);
                if (result.Warnings.Count > 0) report.WithNotes++;

                var touched = ApplyResult(item, result);
                foreach (var field in touched)
                    await main.PersistFieldAsync(item, field);
                writes += touched.Count;
                if (touched.Count > 0) report.Changed++;

                if (!wasLoaded) main.ReleaseItemDetails(item);
            }

            foreach (var item in scope.Computed)
            {
                if (step++ % 25 == 0) BusyService.SetMessage($"Weapon preset patch: {step:N0} / {steps:N0}");
                var prefill = WeaponPrefill.Prefill(catalog, StateOf(main, item));
                var selection = SelectionOf(item, prefill.Selection);
                selections[item.Key] = selection;
                if (prefill.Notes.Count > 0) report.WithNotes++;
                await BuildAsync(item, selection, null);
            }

            var extras = new List<ChildExtras>();
            foreach (var child in scope.Children)
            {
                if (step++ % 25 == 0) BusyService.SetMessage($"Weapon preset patch: {step:N0} / {steps:N0}");
                var state = StateOf(main, child);
                var selection = SelectionOf(child, WeaponPrefill.Prefill(catalog, state).Selection);
                var x = ChildExtrasCalculator.Compute(catalog.Addons, child.Key, selection,
                    TemplateSelection(main, catalog, families.RootOf(child.Key), selections));
                extras.Add(x);
                report.ChildrenFollowing++;

                if (!scope.Requested.Contains(child.Key)) continue;
                bool valuesFollow = state.IsEnchanted && !x.KeepValues && !x.LeaveUntouched;
                await BuildAsync(child, selection, result => KeepInherited(result, state, valuesFollow));
            }
            main.SaveChildExtras(extras);

            PerfLog.Record("Weapon preset patch", total,
                $"{report.Items} item(s), {report.Changed} changed, {writes} field write(s), {report.ChildrenFollowing} children");

            // The tree catches up while the overlay is still up - see BusyService.UntilDrawnAsync.
            bulk.Dispose();
            await BusyService.UntilDrawnAsync();
            return report;
        }

        // See ModularPresetApplier.TemplateSelection.
        private static ModularSelection? TemplateSelection(MainContentVM main, WeaponCatalog catalog, string? root,
            Dictionary<string, ModularSelection> selections)
        {
            if (root == null) return null;
            if (selections.TryGetValue(root, out var known)) return known;
            var node = main.FindItemNode(root);
            if (node == null || !node.IsWeapon || ModularPresetApplier.IsSkipped(node)) return null;
            var detected = WeaponPrefill.Prefill(catalog, StateOf(main, node)).Selection;
            selections[root] = detected;
            return detected;
        }

        // See ModularPresetApplier.KeepInherited: a child's keywords are its template's, an enchanted
        // one's numbers too.
        public static void KeepInherited(WeaponResult result, WeaponItemState state, bool valuesFollow)
        {
            result.Keywords = state.Keywords.ToList();
            if (!valuesFollow) return;
            result.Damage = null;
            result.CritDamage = null;
            result.Weight = null;
            result.Value = null;
            result.Speed = null;
            result.Reach = null;
            result.Stagger = null;
            result.CritPercentMult = null;
        }
    }
}
