using SkyrimCraftingTool.Model;
using SkyrimCraftingTool.Services.TemplateChildren;
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

        // Kinder-Patcher (K3): templates the run patched because a chosen child follows them, and the
        // children whose extras were refreshed - their template part is written by the patch itself.
        public int TemplatesAdded { get; set; }
        public int ChildrenFollowing { get; set; }

        // " 3 template(s) added …, 120 template children follow their template." - empty when none.
        public string ChildText
        {
            get
            {
                var parts = new List<string>();
                if (TemplatesAdded > 0) parts.Add($"{TemplatesAdded} template(s) added because chosen children follow them");
                if (ChildrenFollowing > 0) parts.Add($"{ChildrenFollowing} template children follow their template");
                return parts.Count == 0 ? "" : " " + string.Join(", ", parts) + ".";
            }
        }
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
            // Never on a variant: its slots are its template's (the result carries those, not a change).
            if (!item.SlotsInherited && item.BodySlotMask != result.BodySlotMask)
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
                BodySlotMask = item.EffectiveBodySlotMask,
                SlotsInherited = item.SlotsInherited,
                Keywords = keywords.ToList(),
                EditorID = item.EditorID ?? "",
                IsEnchanted = !string.IsNullOrWhiteSpace(item.HasLoadedDetails
                    ? item.ObjectEffectKey
                    : main.ArmorCache.TryGetValue(item.Key, out var r2) ? r2.ObjectEffectKey : null),
            };
        }

        // DISPLAY ONLY since the Kinder-Patcher (2026-10-28): the dry run's card shows what the child
        // will hold in game. The patch no longer writes these numbers into the child - it takes them
        // from the template's saved state times the child's extras (TemplateInheritance,
        // ChildExtrasCalculator - the same arithmetic).
        //
        // An armor made from a template (TNAM) - every enchanted variant of Thaumaturgy and vanilla -
        // gets the template's armor rating, value and weight (user, 2026-10-25: "the same item plus a
        // magic effect"). Its own type and keywords stay as computed - its slots are the template's
        // anyway (ArmorTemplateSlots, StateOf) - and being enchanted it
        // gets no new recipe anyway. Held back when the variant itself keeps its values (an addon
        // with KeepValues or LeaveUntouched, e.g. the vanilla uniques) and when the template is not
        // in the scan.
        //
        // The template is computed from its own detection, without the dry run's plugin or item
        // choices: the template usually sits in another plugin than the variant (vanilla vs.
        // Thaumaturgy), and the dry run only knows the choices of the items it lists.
        public static void TakeTemplateValues(MainContentVM main, ModularCatalog catalog, ItemNodeVM item,
            ModularSelection selection, ModularResult result, HashSet<string>? materialKeywords,
            IReadOnlyDictionary<string, ItemNodeVM>? nodesByKey = null)
        {
            if (!item.IsArmor || string.IsNullOrWhiteSpace(item.TemplateKey)) return;
            if (string.Equals(item.TemplateKey, item.Key, StringComparison.OrdinalIgnoreCase)) return;

            // Only an ENCHANTED piece is "its template plus a magic effect". An unenchanted one with a
            // template only borrows its model - WindhelmSSE's "Leather Hood" is built on the Thieves
            // Guild Hood and took its value of 0 (2026-10-25). 18 such pieces in a real modlist; they
            // are computed like any other item.
            if (!StateOf(main, item).IsEnchanted) return;

            var variantAddons = ModularPresetEngine.WithAlsoAddons(catalog, selection.AddonNames)
                .Select(n => catalog.Addons.FirstOrDefault(a => string.Equals(a.Name, n, StringComparison.OrdinalIgnoreCase)))
                .Where(a => a != null)
                .Select(a => a!)
                .ToList();
            if (variantAddons.Any(a => a.KeepValues || a.LeaveUntouched)) return;

            // A run over thousands of items hands in its index - FindItemNode is a linear search.
            var node = nodesByKey != null
                ? (nodesByKey.TryGetValue(item.TemplateKey, out var n) ? n : null)
                : main.FindItemNode(item.TemplateKey);
            var record = main.FindArmorRecord(item.TemplateKey);
            if (node == null && record == null) return;

            ModularResult? computed = null;
            var templateAddons = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (node != null && !IsSkipped(node))
            {
                var state = StateOf(main, node);
                var templateSelection = ModularPrefill.Prefill(catalog, state).Selection;
                computed = ModularPresetEngine.Compute(catalog, templateSelection, state, materialKeywords);
                // A template HELD by an addon (KeepValues / LeaveUntouched - the vanilla uniques:
                // "TemplateClothesRobesMageMaster" is named "Master Robes" and keeps its 5 gold) took
                // none of its addons, so the variant's all count. A template that merely has no
                // material shares them all the same: a plugin tier on both a Glenmoril pistol and its
                // "+1" must not count for the variant alone (2026-10-26).
                var shared = ModularPresetEngine.WithAlsoAddons(catalog, templateSelection.AddonNames);
                bool templateHeld = shared
                    .Select(n => catalog.Addons.FirstOrDefault(a => string.Equals(a.Name, n, StringComparison.OrdinalIgnoreCase)))
                    .Any(a => a != null && (a.KeepValues || a.LeaveUntouched));
                if (!templateHeld)
                    templateAddons.UnionWith(shared);
            }

            // Null from the engine = the template keeps its own value, so the variant takes that.
            double armorRating = computed?.ArmorRating ?? node?.ArmorRating ?? record!.ArmorRating;
            double value = computed?.Value ?? node?.Value ?? record!.Value;
            double weight = computed?.Weight ?? node?.Weight ?? record!.Weight;

            // What the variant has and its plain piece has not, on top (user, 2026-10-25: enchanted
            // robes get a bonus): "Master Robes" (x512) picks an enchanted master robe by its name,
            // while its template is the plain robe. An addon both share is in the template's numbers
            // already and is not counted twice.
            var extra = variantAddons.Where(a => !templateAddons.Contains(a.Name)).ToList();
            foreach (var a in extra)
            {
                armorRating *= a.ArmorRatingFactor;
                value *= a.ValueFactor;
                weight *= a.WeightFactor;
            }

            result.ArmorRating = Math.Round(armorRating, 2);
            result.Value = (int)Math.Round(value);
            result.Weight = Math.Round(weight, 2);
            var bonus = extra.Where(a => a.ArmorRatingFactor != 1 || a.ValueFactor != 1 || a.WeightFactor != 1).Select(a => a.Name).ToList();
            result.Warnings.Add($"Values from the template armor {(node?.Name ?? record?.Name ?? item.TemplateKey)}"
                + (bonus.Count > 0 ? $", times the variant's own addons ({string.Join(", ", bonus)})." : "."));
        }

        public static Task<ModularPatchReport> ApplyAsync(MainContentVM main, ModularCatalog catalog, IReadOnlyList<ItemNodeVM> items,
            BuildChoice? choice = null)
            => ApplyAsync(main, catalog, items, choice == null ? null : (_, detected) => choice.Resolve(detected));

        // resolve: per item, from what the pre-fill detected to what is built (the dry run's plugin
        // and item cards each add their own choice). Null = the detection as it is. A template the run
        // adds for its children (ChildScope) is built from its detection: the cards never showed it.
        public static async Task<ModularPatchReport> ApplyAsync(MainContentVM main, ModularCatalog catalog, IReadOnlyList<ItemNodeVM> items,
            Func<ItemNodeVM, ModularSelection, ModularSelection>? resolve)
        {
            var report = new ModularPatchReport();
            using var busy = BusyService.Show("Preset patch…", blocksUiThread: true);
            using var bulk = main.BeginBulk();   // breakdown index + recipe keys scanned once

            // A pending debounced save must land first, or it fires afterwards with a stale value.
            await main.FlushPendingSavesAsync();
            var materialKeywords = ModularPresetEngine.MaterialKeywordsOf(main.AllAvailableKeywords);

            var families = main.ArmorFamilies;
            var scope = ChildScope.Plan(main, items, i => i.IsArmor, families);
            report.SkippedNoName = scope.SkippedNoName;
            report.TemplatesAdded = scope.AddedTemplates.Count;

            // Where the time goes, per phase - written to perf.log at the end (2026-10-22: a patch over
            // a whole load order "took ages and crashed"; these tell the next run what is left).
            var total = PerfClock.StartNew();
            var tHydrate = new System.Diagnostics.Stopwatch();
            var tCompute = new System.Diagnostics.Stopwatch();
            var tApply = new System.Diagnostics.Stopwatch();
            var tPersist = new System.Diagnostics.Stopwatch();
            var tRelease = new System.Diagnostics.Stopwatch();
            int writes = 0;

            ModularSelection SelectionOf(ItemNodeVM item, ModularSelection detected)
                => scope.Requested.Contains(item.Key) ? resolve?.Invoke(item, detected) ?? detected : detected;

            // The selection each computed item was built with - a child's extras compare against it.
            var selections = new Dictionary<string, ModularSelection>(StringComparer.OrdinalIgnoreCase);
            int steps = scope.Computed.Count + scope.Children.Count, step = 0;

            async Task BuildAsync(ItemNodeVM item, ModularSelection selection, ModularItemState state, Action<ModularResult>? adjust)
            {
                report.Items++;

                // Recipes and the keyword checkboxes only exist on a loaded item - but a loaded item
                // carries ~4,000 keyword VMs. One the patch loaded itself is released again below,
                // or a run over a whole load order would keep them all.
                bool wasLoaded = item.HasLoadedDetails;
                tHydrate.Start();
                main.EnsureItemHydrated(item, lean: true);
                tHydrate.Stop();

                tCompute.Start();
                var result = ModularPresetEngine.Compute(catalog, selection, state, materialKeywords);
                adjust?.Invoke(result);
                if (result.Warnings.Count > 0) report.WithNotes++;
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

            foreach (var item in scope.Computed)
            {
                if (step++ % 25 == 0) BusyService.SetMessage($"Preset patch: {step:N0} / {steps:N0}");
                var state = StateOf(main, item);
                var prefill = ModularPrefill.Prefill(catalog, state);
                var selection = SelectionOf(item, prefill.Selection);
                selections[item.Key] = selection;
                if (prefill.Notes.Count > 0) report.WithNotes++;
                await BuildAsync(item, selection, state, null);
            }

            // Children: the extras for the patch, and for a chosen child its own fields (recipes of an
            // unenchanted one, placements, enchantment) - never slots, type, keywords, nor the numbers
            // it takes from its template (TemplateInheritance).
            var extras = new List<ChildExtras>();
            foreach (var child in scope.Children)
            {
                if (step++ % 25 == 0) BusyService.SetMessage($"Preset patch: {step:N0} / {steps:N0}");
                var state = StateOf(main, child);
                var prefill = ModularPrefill.Prefill(catalog, state);
                var selection = SelectionOf(child, prefill.Selection);
                var x = ChildExtrasCalculator.Compute(catalog.Addons, child.Key, selection,
                    TemplateSelection(main, catalog, families.RootOf(child.Key), selections));
                extras.Add(x);
                report.ChildrenFollowing++;

                if (!scope.Requested.Contains(child.Key)) continue;
                bool valuesFollow = state.IsEnchanted && !x.KeepValues && !x.LeaveUntouched;
                await BuildAsync(child, selection, state, result => KeepInherited(result, state, valuesFollow));
            }
            main.SaveChildExtras(extras);

            PerfLog.Record("Preset patch", total,
                $"{report.Items} item(s), {report.Changed} changed, {writes} field write(s), {report.ChildrenFollowing} children; hydrate {tHydrate.ElapsedMilliseconds} ms, compute {tCompute.ElapsedMilliseconds} ms, "
                + $"apply {tApply.ElapsedMilliseconds} ms, write {tPersist.ElapsedMilliseconds} ms, release {tRelease.ElapsedMilliseconds} ms");

            // The tree catches up (filter, edited dots) while the overlay is still up - see BusyService.UntilDrawnAsync.
            bulk.Dispose();
            await BusyService.UntilDrawnAsync();
            return report;
        }

        // The template's selection for a child's extras: from this run, else its own detection;
        // null when the template is not in the tree or has no name.
        private static ModularSelection? TemplateSelection(MainContentVM main, ModularCatalog catalog, string? root,
            Dictionary<string, ModularSelection> selections)
        {
            if (root == null) return null;
            if (selections.TryGetValue(root, out var known)) return known;
            var node = main.FindItemNode(root);
            if (node == null || IsSkipped(node)) return null;
            var detected = ModularPrefill.Prefill(catalog, StateOf(main, node)).Selection;
            selections[root] = detected;
            return detected;
        }

        // A child's result without what it inherits: its slots, type and keywords are the template's,
        // and an enchanted one's numbers too (the patch writes them from the template).
        public static void KeepInherited(ModularResult result, ModularItemState state, bool valuesFollow)
        {
            result.Keywords = state.Keywords.ToList();
            result.ArmorType = state.ArmorType;
            result.BodySlotMask = state.BodySlotMask;
            if (!valuesFollow) return;
            result.ArmorRating = null;
            result.Value = null;
            result.Weight = null;
        }
    }
}
