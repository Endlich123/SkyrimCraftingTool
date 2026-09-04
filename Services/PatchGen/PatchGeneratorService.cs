using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SkyrimCraftingTool.Services.PatchGen
{
    // Orchestrates the patch export:
    //  - edited ARMO/WEAP fields  -> SkyPatcher INIs, one <Plugin>.esp.ini per source plugin
    //  - edited / created COBJ    -> one generated ESP (SkyrimCraftingTool.esp)
    // See docs/PatchGenerator-Plan.md.
    public sealed class PatchGeneratorService
    {
        private readonly PatchDataReader _itemReader;
        private readonly CobjPatchReader _cobjReader;
        private readonly EnchantmentPatchReader _enchReader;
        private readonly FormListPatchReader _formListReader;
        private readonly PatchFormIdMapStore _formIdMap;
        private readonly IReferenceResolver? _references;

        public PatchGeneratorService(
            string? connString = null,
            IReferenceResolver? references = null,
            PatchDataReader? itemReader = null,
            CobjPatchReader? cobjReader = null,
            EnchantmentPatchReader? enchReader = null,
            FormListPatchReader? formListReader = null,
            PatchFormIdMapStore? formIdMap = null)
        {
            _itemReader = itemReader ?? new PatchDataReader(connString);
            _cobjReader = cobjReader ?? new CobjPatchReader(connString);
            _enchReader = enchReader ?? new EnchantmentPatchReader(connString);
            _formListReader = formListReader ?? new FormListPatchReader(connString);
            _formIdMap = formIdMap ?? new PatchFormIdMapStore(connString);
            _references = references;
        }

        // Filled by GenerateSkyPatcher (where the enchantment diff happens) and consumed by
        // GenerateCobj, because both kinds of override share one generated ESP.
        private readonly List<CobjEspBuilder.EnchantmentEspEntry> _enchantmentEspOverrides = new();

        public PatchGenReport Generate(PatchGenOptions options)
        {
            var report = new PatchGenReport();
            _enchantmentEspOverrides.Clear();

            GenerateSkyPatcher(options, report);

            if (options.GenerateCobj)
                GenerateCobj(options, report);

            return report;
        }

        // --- SkyPatcher (Phase A) ---

        private void GenerateSkyPatcher(PatchGenOptions options, PatchGenReport report)
        {
            var armorByPlugin = new Dictionary<string, List<SkyPatcherRule>>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in _itemReader.ReadEditedArmor())
            {
                var rule = ItemRuleBuilder.BuildArmorRule(pair.Original, pair.Edited, out var skip);
                if (Accept(rule, skip, report))
                    report.ArmorRuleCount += Add(armorByPlugin, rule!);
            }

            var weaponByPlugin = new Dictionary<string, List<SkyPatcherRule>>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in _itemReader.ReadEditedWeapons())
            {
                var rule = ItemRuleBuilder.BuildWeaponRule(pair.Original, pair.Edited, out var skip);
                if (Accept(rule, skip, report))
                    report.WeaponRuleCount += Add(weaponByPlugin, rule!);
            }

            var enchByPlugin = new Dictionary<string, List<SkyPatcherRule>>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in _enchReader.ReadEditedEnchantments())
            {
                var rule = EnchantmentRuleBuilder.BuildRule(pair.Original, pair.Edited, out var skip);
                if (Accept(rule, skip, report))
                    report.EnchantmentRuleCount += Add(enchByPlugin, rule!);

                // The FLST assignment is the one enchantment edit SkyPatcher cannot express.
                // Surface it instead of dropping it on the floor - see docs/EnchantmentPatch-Plan.md (E-P4).
                if (!string.Equals(pair.Original.WornRestrictionListKey ?? "",
                                   pair.Edited.WornRestrictionListKey ?? "",
                                   StringComparison.OrdinalIgnoreCase))
                {
                    // No SkyPatcher operation exists for this field, so the generated ESP is the
                    // only route (E-P4). When ESP generation is off it cannot be patched at all -
                    // saying so is better than dropping it silently.
                    if (options.GenerateCobj)
                    {
                        var target = pair.Edited.WornRestrictionListKey ?? "";

                        // The ESP override never passes through Accept(), so its one reference has
                        // to be validated here - a list that no longer resolves would otherwise be
                        // written into the ESP silently.
                        if (target.Length > 0 && _references != null && !_references.IsActive(target))
                            report.Warnings.Add(
                                $"{pair.Edited.Key}: the new worn-restriction list {target} is not in " +
                                "the current scan (ESP override still written).");

                        _enchantmentEspOverrides.Add(
                            new CobjEspBuilder.EnchantmentEspEntry(pair.Edited.Key, target));
                    }
                    else
                    {
                        report.EnchantmentAssignmentChangesUnpatched++;
                        report.Warnings.Add(
                            $"{pair.Edited.Key}: worn-restriction list assignment changed " +
                            $"({Describe(pair.Original.WornRestrictionListKey)} -> {Describe(pair.Edited.WornRestrictionListKey)}) " +
                            "but SkyPatcher has no operation for it, and ESP generation is off - " +
                            "NOT written to the patch. Editing the list contents instead does reach the game.");
                    }
                }
            }

            var formListByPlugin = new Dictionary<string, List<SkyPatcherRule>>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in _formListReader.ReadEditedFormLists())
            {
                var rule = FormListRuleBuilder.BuildRule(pair);
                if (Accept(rule, null, report))
                    report.FormListRuleCount += Add(formListByPlugin, rule!);
            }

            if (options.DryRun) return;

            WriteCategory(options, "armor", armorByPlugin, report);
            WriteCategory(options, "weapon", weaponByPlugin, report);
            // "enchantment" is SkyPatcher's own folder name - see the patcher list in
            // docs/EnchantmentPatch-Plan.md (E-P0). Spelling matters, it is passed through verbatim.
            WriteCategory(options, "enchantment", enchByPlugin, report);
            // "formList" is camelCase in SkyPatcher's folder list - a lowercase spelling would
            // silently create a second folder next to the one other mods use.
            WriteCategory(options, "formList", formListByPlugin, report);
        }

        private static string Describe(string? listKey) =>
            string.IsNullOrWhiteSpace(listKey) ? "(none)" : listKey;

        private bool Accept(SkyPatcherRule? rule, ItemRuleBuilder.NameSkip? skip, PatchGenReport report)
        {
            if (skip != null)
                report.Warnings.Add(
                    $"Name edit skipped for {skip.Key}: \"{skip.Name}\" contains a character " +
                    "(~ : newline) SkyPatcher can't express in fullName.");

            if (rule == null) return false;

            foreach (var keyword in rule.ReferencedKeywordKeys)
                if (_references != null && !_references.IsActive(keyword))
                    report.Warnings.Add(
                        $"{rule.TargetPlugin}|{rule.TargetFormId}: reference {keyword} is not in the " +
                        "current scan (rule still written).");

            return true;
        }

        private static int Add(Dictionary<string, List<SkyPatcherRule>> byPlugin, SkyPatcherRule rule)
        {
            if (!byPlugin.TryGetValue(rule.TargetPlugin, out var list))
                byPlugin[rule.TargetPlugin] = list = new List<SkyPatcherRule>();
            list.Add(rule);
            return 1;
        }

        private static void WriteCategory(
            PatchGenOptions options, string category,
            Dictionary<string, List<SkyPatcherRule>> byPlugin, PatchGenReport report)
        {
            var dir = options.CategoryDir(category);

            // Only ever our own priority subfolder — never the shared category root where other
            // mods' SkyPatcher files live. Wipe it so a plugin whose items are all un-edited now
            // loses its stale file.
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);

            if (byPlugin.Count == 0)
                return;

            Directory.CreateDirectory(dir);

            foreach (var (plugin, rules) in byPlugin.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
            {
                var ordered = rules
                    .OrderBy(r => r.TargetFormId, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                var header =
                    "Generated by SkyrimCraftingTool. Do not edit by hand — regenerated on every export.\n" +
                    $"Source plugin: {plugin}";

                var text = SkyPatcherIniWriter.Write(ordered, header);
                var path = Path.Combine(dir, plugin + ".ini");
                File.WriteAllText(path, text);
                report.WrittenFiles.Add(path);
            }
        }

        // --- COBJ ESP (Phase B) ---

        private void GenerateCobj(PatchGenOptions options, PatchGenReport report)
        {
            var entries = _cobjReader.ReadEditedCobj();
            var enchOverrides = _enchantmentEspOverrides;

            // Both kinds of override live in the same ESP, so either one on its own is reason enough
            // to build it.
            if (entries.Count == 0 && enchOverrides.Count == 0) return;

            if (options.DryRun)
            {
                report.CobjNewCount = entries.Count(e => e.IsNew);
                report.CobjOverrideCount = entries.Count(e => !e.IsNew);
                report.EnchantmentEspOverrideCount = enchOverrides.Count;
                return;
            }

            var loadOrder = LoadOrderReader.Read();
            var builder = new CobjEspBuilder();

            // Resolve the winning record for every override up front, so the builder can deep-copy
            // it instead of assembling one from the few fields item.db tracks. Restricted to the
            // FormKeys actually being overridden - no point holding the whole load order's records.
            var wantedCobj = entries
                .Where(e => !e.IsNew)
                .Select(e => KeyFactory.ParseFormKey(e.ToolKey))
                .ToHashSet();

            var wantedEnch = enchOverrides
                .Select(e => KeyFactory.ParseFormKey(e.EnchantmentKey))
                .ToHashSet();

            // CRITICAL: never read our own output back in. The generated ESP normally sits in
            // plugins.txt, and being last it would BE the winning override for every record we
            // patch - so the builder would deep-copy the previous run's own output and treat that
            // as the original, permanently. Verified against the real load order, where exactly
            // this happened (blank EditorID, truncated conditions).
            var ownEspNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                options.EspFileName,
                KeyFactory.UserPluginName,
            };
            foreach (var e in entries) ownEspNames.Add(options.EspNameFor(e.SourcePlugin));
            foreach (var e in enchOverrides) ownEspNames.Add(options.EspNameFor(e.SourcePlugin));

            var sourcePlugins = options.PluginsInLoadOrder
                .Where(p => !ownEspNames.Contains(p.FileName))
                .ToList();

            using var resolver = WinningRecordResolver.Open(
                sourcePlugins, wantedCobj, wantedEnch, report.Warnings);

            var masters = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            bool allEsl = true;
            var espPaths = new List<string>();

            // One pass over both sources, keyed by the ESP each record belongs in. With the global
            // split mode that is a single group; with PerSourcePlugin an enchantment from plugin X
            // shares a file with X's recipes, which is what the user asked for either way.
            var cobjByEsp = entries
                .GroupBy(e => options.EspNameFor(e.SourcePlugin), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

            var enchByEsp = enchOverrides
                .GroupBy(e => options.EspNameFor(e.SourcePlugin), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

            var espNames = cobjByEsp.Keys
                .Concat(enchByEsp.Keys)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase);

            foreach (var espName in espNames)
            {
                var cobjForEsp = cobjByEsp.TryGetValue(espName, out var c)
                    ? c : new List<CobjPatchEntry>();
                var enchForEsp = enchByEsp.TryGetValue(espName, out var en)
                    ? en : new List<CobjEspBuilder.EnchantmentEspEntry>();

                var res = builder.Build(
                    cobjForEsp, _formIdMap, loadOrder, options.OutputRoot, espName, options.EslWhenPossible,
                    resolver, enchForEsp);

                report.CobjNewCount += res.NewCount;
                report.CobjOverrideCount += res.OverrideCount;
                report.CobjDeepCopiedCount += res.DeepCopiedCount;
                report.CobjFromScratchCount += res.FromScratchCount;
                report.CobjConditionRewriteSkippedCount += res.ConditionRewriteSkippedCount;
                report.StaleConditionDataCount += res.StaleConditionDataCount;
                report.EnchantmentEspOverrideCount += res.EnchantmentOverrideCount;
                report.Warnings.AddRange(res.Warnings);
                report.WrittenFiles.Add(res.OutputPath);
                espPaths.Add(res.OutputPath);
                foreach (var m in res.Masters) masters.Add(m);
                if (res.NewCount + res.OverrideCount > 0 && !res.EslFlagged) allEsl = false;
            }

            report.CobjEspPath = espPaths.Count == 1 ? espPaths[0] : null;
            report.CobjEslFlagged = espPaths.Count > 0 && allEsl;
            report.CobjMasters = masters.ToList();
        }
    }
}
