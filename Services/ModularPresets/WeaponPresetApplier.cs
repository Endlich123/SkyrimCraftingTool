using SkyrimCraftingTool.Model;
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
        public static async Task<ModularPatchReport> ApplyAsync(MainContentVM main, WeaponCatalog catalog, IReadOnlyList<ItemNodeVM> items,
            Func<ItemNodeVM, ModularSelection, ModularSelection>? resolve)
        {
            var report = new ModularPatchReport();
            using var busy = BusyService.Show("Weapon preset patch…", blocksUiThread: true);
            using var bulk = main.BeginBulk();

            await main.FlushPendingSavesAsync();
            var materialKeywords = WeaponPresetEngine.MaterialKeywordsOf(main.AllAvailableKeywords);

            var total = PerfClock.StartNew();
            int writes = 0;
            for (int i = 0; i < items.Count; i++)
            {
                var item = items[i];
                if (!item.IsWeapon) continue;
                if (ModularPresetApplier.IsSkipped(item)) { report.SkippedNoName++; continue; }
                report.Items++;
                if (i % 25 == 0) BusyService.SetMessage($"Weapon preset patch: {i + 1:N0} / {items.Count:N0}");

                bool wasLoaded = item.HasLoadedDetails;
                main.EnsureItemHydrated(item, lean: true);

                var state = StateOf(main, item);
                var prefill = WeaponPrefill.Prefill(catalog, state);
                var selection = resolve?.Invoke(item, prefill.Selection) ?? prefill.Selection;
                var result = WeaponPresetEngine.Compute(catalog, selection, state, materialKeywords);
                if (prefill.Notes.Count + result.Warnings.Count > 0) report.WithNotes++;

                var touched = ApplyResult(item, result);
                foreach (var field in touched)
                    await main.PersistFieldAsync(item, field);
                writes += touched.Count;
                if (touched.Count > 0) report.Changed++;

                if (!wasLoaded) main.ReleaseItemDetails(item);
            }

            PerfLog.Record("Weapon preset patch", total, $"{report.Items} item(s), {report.Changed} changed, {writes} field write(s)");
            return report;
        }
    }
}
