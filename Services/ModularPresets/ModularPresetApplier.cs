using SkyrimCraftingTool.Model;
using SkyrimCraftingTool.ViewModel;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace SkyrimCraftingTool.Services.ModularPresets
{
    public sealed class ModularPatchReport
    {
        public int Items { get; set; }
        public int Changed { get; set; }
        public int WithNotes { get; set; }
        public int SkippedNoName { get; set; }
    }

    // What the build box chose, on top of (or instead of) what the pre-fill detected per item.
    //
    // Null on a field = "(detected)": each item keeps its own pre-fill. The item-level box sets every
    // field explicitly; the multi-select and plugin boxes cannot set slots (each item has its own) and
    // usually leave type and material on "(detected)" too.
    public sealed class BuildChoice
    {
        public string? TypeName { get; set; }

        // "" = explicitly no material; null = detected.
        public string? MaterialName { get; set; }

        public List<string>? SlotNames { get; set; }

        // Null = the detected addons; a list = exactly these (item level).
        public List<string>? AddonNames { get; set; }

        // Added to whatever addon list applies (multi-select / plugin "+ addon").
        public List<string> AdditionalAddons { get; set; } = new();

        public List<string> ExtraNames { get; set; } = new();

        public ModularSelection Resolve(ModularSelection detected)
        {
            var addons = (AddonNames ?? detected.AddonNames).ToList();
            foreach (var a in AdditionalAddons)
                if (!addons.Contains(a, StringComparer.OrdinalIgnoreCase)) addons.Add(a);

            return new ModularSelection
            {
                TypeName = TypeName ?? detected.TypeName,
                MaterialName = MaterialName ?? detected.MaterialName,
                SlotNames = (SlotNames ?? detected.SlotNames).ToList(),
                AddonNames = addons,
                ExtraNames = ExtraNames.ToList(),
            };
        }
    }

    // The preset patch (docs/ItemPresets-Plan.md, P5): pre-fill + engine like the dry-run, then the
    // result is written to the item. Also what every build box's Apply runs.
    public static class ModularPresetApplier
    {
        // A safety rule (user, 2026-10-20): an item without a name is never built. Nameless
        // records are skins, inventory placeholders and other things no player picks up - and a
        // name pattern cannot have looked at them.
        public static bool IsSkipped(ItemNodeVM item) => string.IsNullOrWhiteSpace(item.Name);

        // The engine's result in the shape PresetApplyService already knows how to apply. Every
        // field it computed is Enabled; everything it left open (null values, no material -> no
        // recipe) stays disabled, so the item keeps its own.
        public static PresetSlotConfig ToConfig(ModularResult r)
        {
            var config = new PresetSlotConfig
            {
                Weight = new FieldValue<double> { Enabled = r.Weight.HasValue, Value = r.Weight ?? 0 },
                Value = new FieldValue<int> { Enabled = r.Value.HasValue, Value = r.Value ?? 0 },
                ArmorRating = new FieldValue<double> { Enabled = r.ArmorRating.HasValue, Value = r.ArmorRating ?? 0 },

                // The complete list: foreign keywords kept, managed ones recomputed - which is what
                // PresetApplyService's "union becomes the complete set" expects.
                Keywords = new FieldValue<List<string>> { Enabled = true, Value = r.Keywords.ToList() },

                Enchantment = new FieldValue<string> { Enabled = r.EnchantmentKey != null, Value = r.EnchantmentKey ?? "" },
                EnchantmentAmount = new FieldValue<int> { Enabled = r.EnchantmentAmount.HasValue, Value = r.EnchantmentAmount ?? 0 },

                // The first placement rides along here; the others go through ContainerOnly, since
                // a config holds one container string and placements add up anyway.
                Container = new FieldValue<string> { Enabled = r.Containers.Count > 0, Value = r.Containers.FirstOrDefault() ?? "{}" },
            };

            if (r.Craft != null)
            {
                config.CraftRecipe = new RecipeConfig
                {
                    WorkbenchKey = new FieldValue<string> { Enabled = r.Craft.WorkbenchKey.Length > 0, Value = r.Craft.WorkbenchKey },
                    Ingredients = new FieldValue<List<IngredientEntry>> { Enabled = true, Value = r.Craft.Ingredients },
                    Conditions = new FieldValue<List<ConditionEntry>> { Enabled = r.Craft.Conditions.Count > 0, Value = r.Craft.Conditions },
                };
            }

            // Temper: never a workbench - it is derived when the COBJ is created (see
            // PresetApplyService.ApplyRecipe).
            if (r.Temper != null)
            {
                config.TemperRecipe = new RecipeConfig
                {
                    Ingredients = new FieldValue<List<IngredientEntry>> { Enabled = true, Value = r.Temper.Ingredients },
                    Conditions = new FieldValue<List<ConditionEntry>> { Enabled = r.Temper.Conditions.Count > 0, Value = r.Temper.Conditions },
                };
            }

            if (r.Breakdown != null)
            {
                config.BreakdownRecipe = new RecipeConfig
                {
                    WorkbenchKey = new FieldValue<string> { Enabled = r.Breakdown.WorkbenchKey.Length > 0, Value = r.Breakdown.WorkbenchKey },
                    Ingredients = new FieldValue<List<IngredientEntry>> { Enabled = true, Value = r.Breakdown.Ingredients },
                    Conditions = new FieldValue<List<ConditionEntry>> { Enabled = r.Breakdown.Conditions.Count > 0, Value = r.Breakdown.Conditions },
                };
            }

            return config;
        }

        // A config that touches nothing but the placements - for the second and later extra.
        public static PresetSlotConfig ContainerOnly(string container) => new()
        {
            Container = new FieldValue<string> { Enabled = true, Value = container },
        };

        // The whole result onto one loaded item; returns the fields it touched (for persisting).
        public static List<string> ApplyResult(ItemNodeVM item, ModularResult result)
        {
            var touched = PresetApplyService.ApplyConfig(item, ToConfig(result));

            // A lean-loaded item (bulk patch) has no keyword checkboxes for ApplyConfig to tick: the
            // engine's list is the complete result already (rules, materials, addons), so it is set as
            // it is. 20 ms per item through the checkbox rules, ~0 this way.
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
                touched.AddRange(PresetApplyService.ApplyConfig(item, ContainerOnly(container)));

            // After the keywords: selecting a class keyword can itself move ArmorType
            // (ItemNodeVM.ApplyArmorTypeFromKeyword) - the type module has the last word.
            if (item.BodySlotMask != result.BodySlotMask)
            {
                item.BodySlotMask = result.BodySlotMask;
                touched.Add(nameof(ItemNodeVM.BodySlotMask));
            }
            if (!string.Equals(item.ArmorType, result.ArmorType, StringComparison.Ordinal))
            {
                item.ArmorType = result.ArmorType;
                touched.Add(nameof(ItemNodeVM.ArmorType));
            }
            // Whatever changed, by whichever path: the item is edited (see ItemNodeVM.MarkEdited).
            if (touched.Count > 0) item.MarkEdited();
            return touched.Distinct().ToList();
        }

        public static string PluginOf(string? key)
        {
            if (string.IsNullOrEmpty(key)) return "";
            int bar = key.IndexOf('|');
            return bar < 0 ? "" : key.Substring(0, bar);
        }

        // The item as the engine sees it: a loaded item's live keyword list, otherwise the cache.
        public static ModularItemState StateOf(MainContentVM main, ItemNodeVM item)
        {
            var keywords = item.HasLoadedDetails
                ? item.SelectedKeywordKeys
                : main.ArmorCache.TryGetValue(item.Key, out var rec) ? rec.Keywords ?? new List<string>() : new List<string>();
            return new ModularItemState
            {
                Name = item.Name ?? "",
                Plugin = PluginOf(item.Key),
                ArmorType = item.ArmorType ?? "",
                BodySlotMask = item.BodySlotMask,
                Keywords = keywords.ToList(),
                EditorID = item.EditorID ?? "",
                IsEnchanted = !string.IsNullOrWhiteSpace(item.HasLoadedDetails
                    ? item.ObjectEffectKey
                    : main.ArmorCache.TryGetValue(item.Key, out var r2) ? r2.ObjectEffectKey : null),
            };
        }

        public static Task<ModularPatchReport> ApplyAsync(MainContentVM main, ModularCatalog catalog, IReadOnlyList<ItemNodeVM> items,
            BuildChoice? choice = null)
            => ApplyAsync(main, catalog, items, choice == null ? null : (_, detected) => choice.Resolve(detected));

        // resolve: per item, from what the pre-fill detected to what is built (the dry run's plugin
        // and item cards each add their own choice). Null = the detection as it is.
        public static async Task<ModularPatchReport> ApplyAsync(MainContentVM main, ModularCatalog catalog, IReadOnlyList<ItemNodeVM> items,
            Func<ItemNodeVM, ModularSelection, ModularSelection>? resolve)
        {
            var report = new ModularPatchReport();
            using var busy = BusyService.Show("Preset patch…", blocksUiThread: true);
            using var bulk = main.BeginBulk();   // breakdown index + recipe keys scanned once

            // A pending debounced save must land first, or it fires afterwards with a stale value.
            await main.FlushPendingSavesAsync();
            var materialKeywords = ModularPresetEngine.MaterialKeywordsOf(main.AllAvailableKeywords);

            // Where the time goes, per phase - written to perf.log at the end (2026-10-22: a patch over
            // a whole load order "took ages and crashed"; these tell the next run what is left).
            var total = PerfClock.StartNew();
            var tHydrate = new System.Diagnostics.Stopwatch();
            var tCompute = new System.Diagnostics.Stopwatch();
            var tApply = new System.Diagnostics.Stopwatch();
            var tPersist = new System.Diagnostics.Stopwatch();
            var tRelease = new System.Diagnostics.Stopwatch();
            int writes = 0;

            for (int i = 0; i < items.Count; i++)
            {
                var item = items[i];
                if (!item.IsArmor) continue;
                if (IsSkipped(item)) { report.SkippedNoName++; continue; }
                report.Items++;
                if (i % 25 == 0) BusyService.SetMessage($"Preset patch: {i + 1:N0} / {items.Count:N0}");

                // Recipes and the keyword checkboxes only exist on a loaded item - but a loaded item
                // carries ~4,000 keyword VMs. One the patch loaded itself is released again below,
                // or a run over a whole load order would keep them all.
                bool wasLoaded = item.HasLoadedDetails;
                tHydrate.Start();
                main.EnsureItemHydrated(item, lean: true);
                tHydrate.Stop();

                tCompute.Start();
                var state = StateOf(main, item);
                var prefill = ModularPrefill.Prefill(catalog, state);
                var selection = resolve?.Invoke(item, prefill.Selection) ?? prefill.Selection;
                var result = ModularPresetEngine.Compute(catalog, selection, state, materialKeywords);
                if (prefill.Notes.Count + result.Warnings.Count > 0) report.WithNotes++;
                tCompute.Stop();

                tApply.Start();
                var touched = ApplyResult(item, result);
                tApply.Stop();

                tPersist.Start();
                foreach (var field in touched)
                    await main.PersistFieldAsync(item, field);
                tPersist.Stop();
                writes += touched.Count;
                if (touched.Count > 0) report.Changed++;

                tRelease.Start();
                if (!wasLoaded)
                    main.ReleaseItemDetails(item);
                tRelease.Stop();
            }

            PerfLog.Record("Preset patch", total,
                $"{report.Items} item(s), {report.Changed} changed, {writes} field write(s); hydrate {tHydrate.ElapsedMilliseconds} ms, compute {tCompute.ElapsedMilliseconds} ms, "
                + $"apply {tApply.ElapsedMilliseconds} ms, write {tPersist.ElapsedMilliseconds} ms, release {tRelease.ElapsedMilliseconds} ms");
            return report;
        }
    }
}
