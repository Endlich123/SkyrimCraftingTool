using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using SkyrimCraftingTool.Model;

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

        // NPC records that need an ESP override (N-P6): combat style, crime faction, removed perks.
        // Filled in GenerateSkyPatcher, where the edited NPCs are already read.
        private readonly List<NpcEspEntry> _npcEspOverrides = new();

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

        // SkyPatcher's category folders for the two Container-tab rule kinds. Named constants rather
        // than literals because they are magic strings with a silent failure mode: a wrong spelling
        // writes a folder nobody reads instead of raising anything. Both verified against a real
        // SkyPatcher install and confirmed working in game (2026-09-07) - do not "tidy" the casing.
        public const string LeveledListFolder = "leveledList";

        // The "all sliders at 0 -> the item goes into the container itself" half.
        public const string ContainerFolder = "container";

        // Filled by GenerateSkyPatcher (where the enchantment diff happens) and consumed by
        // GenerateCobj, because both kinds of override share one generated ESP.
        private readonly List<CobjEspBuilder.EnchantmentEspEntry> _enchantmentEspOverrides = new();

        public PatchGenReport Generate(PatchGenOptions options)
        {
            var report = new PatchGenReport { DryRun = options.DryRun };
            _enchantmentEspOverrides.Clear();

            GenerateSkyPatcher(options, report);

            if (options.GenerateCobj)
                GenerateCobj(options, report);

            return report;
        }

        // --- SkyPatcher (Phase A) ---

        private void GenerateSkyPatcher(PatchGenOptions options, PatchGenReport report)
        {
            // Container placements are collected across BOTH item tables and only turned into rules
            // afterwards, because the rules are keyed by the LEVELED LIST, not by the item - one
            // list usually collects items from several plugins (see LeveledListRuleBuilder).
            var lvliNames = _itemReader.ReadLeveledListNames();
            var containerNames = _itemReader.ReadContainerNames();
            var placements = new List<LeveledListPlacement>();
            var containerPlacements = new List<ContainerPlacement>();

            // An item can now wear an enchantment the user created here. Its key inside the tool
            // ("SkyrimCraftingTool.esp|001001") is NOT the FormID the record ends up with - the ESP
            // hands out its own, from the persistent map. The rule has to name that one, so the
            // allocation happens before any rule is built.
            var userEnchantments = ResolveUserEnchantmentKeys(options, report);

            var armorByPlugin = new Dictionary<string, List<SkyPatcherRule>>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in _itemReader.ReadEditedArmor())
            {
                pair.Edited.ObjectEffectKey =
                    MapEnchantmentKey(pair.Edited.ObjectEffectKey, pair.Original.ObjectEffectKey,
                                      pair.Edited.Key, userEnchantments, options, report);

                var rule = ItemRuleBuilder.BuildArmorRule(pair.Original, pair.Edited, out var skip);
                if (Accept(rule, skip, report))
                    report.ArmorRuleCount += Add(armorByPlugin, rule!);

                LeveledListRuleBuilder.ParsePlacements(
                    pair.Edited.Key, pair.Edited.EditorID, pair.Edited.ContainerString,
                    placements, containerPlacements, lvliNames, containerNames);
            }

            var weaponByPlugin = new Dictionary<string, List<SkyPatcherRule>>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in _itemReader.ReadEditedWeapons())
            {
                pair.Edited.ObjectEffectKey =
                    MapEnchantmentKey(pair.Edited.ObjectEffectKey, pair.Original.ObjectEffectKey,
                                      pair.Edited.Key, userEnchantments, options, report);

                var rule = ItemRuleBuilder.BuildWeaponRule(pair.Original, pair.Edited, out var skip);
                if (Accept(rule, skip, report))
                    report.WeaponRuleCount += Add(weaponByPlugin, rule!);

                LeveledListRuleBuilder.ParsePlacements(
                    pair.Edited.Key, pair.Edited.EditorID, pair.Edited.ContainerString,
                    placements, containerPlacements, lvliNames, containerNames);
            }

            // Books, scrolls, misc, soul gems, ammo, food, ingredients, keys. They have no stats to
            // patch, so they produce no rule of their own - only placements, which join the same two
            // lists the armor and weapon loops above fill.
            foreach (var (key, editorId, containerString) in _itemReader.ReadEditedWorldItems())
            {
                LeveledListRuleBuilder.ParsePlacements(
                    key, editorId, containerString,
                    placements, containerPlacements, lvliNames, containerNames);
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

            // NPCs (Prio 8 / N-P2). One rule per edited NPC, filtered by its own FormID - see
            // NPC-Plan.md §10 for why the single editor never emits a faction rule.
            var npcByPlugin = new Dictionary<string, List<SkyPatcherRule>>(StringComparer.OrdinalIgnoreCase);
            var editedNpcs = _itemReader.ReadEditedNpcs();

            // The three edits no rule can carry. Collected here because the rows are already read,
            // and handed to the ESP builder at the end of the run.
            _npcEspOverrides.AddRange(NpcEspBuilder.EntriesFor(editedNpcs));

            foreach (var npc in editedNpcs)
            {
                var rule = NpcRuleBuilder.Build(npc, out var inherited);
                if (!Accept(rule, null, report)) continue;

                report.NpcRuleCount += Add(npcByPlugin, rule!);

                // Written as asked, but said out loud: 61,4 % of NPCs take their stats from a
                // template, and for those the game never reads the value this rule sets. Clearing
                // the flag is a separate operation the user has to add deliberately (N-P4), so the
                // patch is not silently "fixed" here - it is reported.
                if (inherited != null)
                {
                    report.Warnings.Add(
                        $"{inherited.Key}: stats patched, but this NPC inherits its stats from " +
                        $"{inherited.TemplateKey} - the game will not read them. The Stats template " +
                        "flag has to be cleared as well (removeTemplateFlags=stats).");
                }
            }

            // NPC GROUPS (G5, docs/NPC-Gruppen-Plan.md section 5). A different kind of rule from the one
            // above: that one names a record, this one names a filter and reaches whatever matches it at
            // runtime - including NPCs from mods this database has never seen. That reach is the point,
            // and it is why every group in the patch is listed in the report by name and member count.
            //
            // Read once for all groups: resolving membership walks every NPC, and 40 groups over 6.642
            // records is 40 walks, not 40 reads.
            var groups = _itemReader.ReadNpcGroups().Where(g => g.Active).ToList();

            if (groups.Count > 0)
            {
                // Checked over ALL groups, because the thing it catches is not a property of any one of
                // them: SkyPatcher keeps only the last calcLevelMin/Max pair it reads (see
                // NpcGroupRuleBuilder.ScalingCollisions). A group rule alone cannot see the collision.
                foreach (var collision in NpcGroupRuleBuilder.ScalingCollisions(groups))
                    report.Warnings.Add(collision);

                var allNpcs = _itemReader.ReadAllNpcs();
                var groupStats = new NpcStatResolver(allNpcs, _itemReader.ReadLeveledNpcs());
                var snapshots = _itemReader.ReadGroupSnapshots();

                foreach (var group in groups.OrderBy(g => g.SortOrder).ThenBy(g => g.Id))
                {
                    var set = NpcGroupRuleBuilder.BuildRules(group, allNpcs, groupStats, groups);

                    foreach (var problem in set.Problems)
                        report.Warnings.Add($"group '{group.Name}': {problem}");

                    if (!set.HasRules) continue;

                    foreach (var rule in set.Rules)
                    {
                        if (!Accept(rule, null, report)) continue;
                        report.NpcGroupRuleCount += Add(npcByPlugin, rule);
                    }

                    // Section 8: a group may have grown members since anybody last looked at it - a mod
                    // install is enough. Marked rather than withheld: the user asked for the patch, and
                    // the honest answer is to name which groups moved and by how much.
                    snapshots.TryGetValue(group.Id, out var snapshot);
                    var drift = NpcGroupDiffService.Compare(group, set.Members, snapshot);

                    report.NpcGroupSummaries.Add(
                        $"{group.Name}: {set.Members.Count} NPC(s), {set.Rules.Count} rule(s)" +
                        (drift.NeedsAttention ? $"  ** UNCHECKED: {drift.Detail}" : ""));

                    // In the warnings too, and with names: the summaries block is a list of what the
                    // patch did, and a group that silently took in 17 new NPCs is not that - it is
                    // something to look at before playing.
                    if (drift.Changed)
                        report.Warnings.Add(
                            $"group '{group.Name}' changed since it was confirmed: {drift.Detail}" +
                            (drift.AddedNames.Count > 0 ? $" - {string.Join(", ", drift.AddedNames)}" : "") +
                            (drift.AddedNamesNotShown > 0 ? $" and {drift.AddedNamesNotShown} more" : ""));
                }
            }

            foreach (var pair in _formListReader.ReadEditedFormLists())
            {
                var rule = FormListRuleBuilder.BuildRule(pair);
                if (Accept(rule, null, report))
                    report.FormListRuleCount += Add(formListByPlugin, rule!);
            }

            // Container tab -> leveled lists. Grouped by the LIST's plugin, like formList above:
            // the rule targets the list, not the item that goes into it.
            var lvliByPlugin = new Dictionary<string, List<SkyPatcherRule>>(StringComparer.OrdinalIgnoreCase);
            var lvliRules = LeveledListRuleBuilder.BuildRules(placements, out var levelConflicts);
            foreach (var rule in lvliRules)
            {
                if (Accept(rule, null, report))
                    report.LeveledListRuleCount += Add(lvliByPlugin, rule);
            }

            // Entries the user put back after an override dropped them. Additive like the placements
            // above, so this can only ever add to a list, never take from it.
            foreach (var rule in LeveledListRuleBuilder.BuildRestoreRules(LeveledListRestoreStore.ReadAll(), lvliNames))
            {
                if (Accept(rule, null, report))
                    report.LeveledListRuleCount += Add(lvliByPlugin, rule);
            }

            // The list's own properties, if the user changed any. Filed under the LIST's plugin
            // rather than an item's, because these rules edit the list itself and reference nothing
            // foreign - see LeveledListRuleBuilder.BuildPropertyRules.
            foreach (var edit in LeveledListEditStore.ReadAll())
            {
                var rule = LeveledListRuleBuilder.BuildPropertyRules(new[] { edit }, lvliNames).FirstOrDefault();
                if (rule == null || !Accept(rule, null, report)) continue;

                report.LeveledListPropertyRuleCount += Add(lvliByPlugin, rule);

                var name = lvliNames != null && lvliNames.TryGetValue(edit.ListKey, out var n) && !string.IsNullOrWhiteSpace(n)
                    ? $"{n} ({edit.ListKey})"
                    : edit.ListKey;

                // Not a warning - the user asked for this. It is in the report because a change to a
                // list's own properties affects every mod feeding that list, and that belongs on the
                // record of what this patch does.
                report.LeveledListPropertyEdits.Add($"{name}: {string.Join(", ", rule.Operations)}");
            }

            foreach (var c in levelConflicts)
                report.Warnings.Add(
                    $"{c.ItemKey} sits in leveled list {c.LvliKey} through more than one container, " +
                    $"with different levels ({string.Join(", ", c.AllLevels)}) - patched at {c.UsedLevel}.");

            // The other half of the Container tab: a container whose sliders all stayed at 0 means
            // the item goes into the container itself. Grouped by the container's plugin.
            var contByPlugin = new Dictionary<string, List<SkyPatcherRule>>(StringComparer.OrdinalIgnoreCase);
            foreach (var rule in LeveledListRuleBuilder.BuildContainerRules(containerPlacements))
            {
                if (Accept(rule, null, report))
                    report.ContainerRuleCount += Add(contByPlugin, rule);
            }

            // What the user decided to take OUT, from the Container/LeveledList tab.
            //
            // EVERY OTHER RULE THIS METHOD WRITES ONLY ADDS. These delete content other mods put
            // there, which is what makes the patch stop being safe-by-construction. They are counted
            // apart and listed one by one in the report - not as a warning, because the user asked
            // for them, but on the record, the same way a list-property edit is.
            foreach (var rule in LeveledListRuleBuilder.BuildRemovalRules(
                         RemovalStore.ReadAll(RemovalScope.LeveledList), RemovalScope.LeveledList, lvliNames))
            {
                if (!Accept(rule, null, report)) continue;

                report.RemovalRuleCount += Add(lvliByPlugin, rule);
                report.Removals.Add(rule.Comment);
            }

            foreach (var rule in LeveledListRuleBuilder.BuildRemovalRules(
                         RemovalStore.ReadAll(RemovalScope.Container), RemovalScope.Container, containerNames))
            {
                if (!Accept(rule, null, report)) continue;

                report.RemovalRuleCount += Add(contByPlugin, rule);
                report.Removals.Add(rule.Comment);
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
            // Both spellings verified against a real SkyPatcher install and confirmed working
            // in game (2026-09-07). Same camelCase rule as formList above.
            WriteCategory(options, LeveledListFolder, lvliByPlugin, report);
            WriteCategory(options, ContainerFolder, contByPlugin, report);

            // "npc" is SkyPatcher.s own folder name, same rule as the others: passed through verbatim.
            //
            // Merged first: a bulk edit produces one rule per NPC, and hundreds of lines differing
            // only in a FormID are what filterByNpcs.s comma-separated list exists to avoid.
            foreach (var plugin in npcByPlugin.Keys.ToList())
                npcByPlugin[plugin] = NpcRuleBuilder.MergeIdenticalRules(npcByPlugin[plugin]);

            WriteCategory(options, "npc", npcByPlugin, report);
        }

        // The readers take a path; the service is built with a connection string.
        private static string DbPathFrom(string connString) =>
            connString.StartsWith("Data Source=", StringComparison.OrdinalIgnoreCase)
                ? connString.Substring("Data Source=".Length)
                : connString;

        private static string Describe(string? listKey) =>
            string.IsNullOrWhiteSpace(listKey) ? "(none)" : listKey;

        private bool Accept(SkyPatcherRule? rule, ItemRuleBuilder.NameSkip? skip, PatchGenReport report)
        {
            if (skip != null)
                report.Warnings.Add(
                    $"Name edit skipped for {skip.Key}: \"{skip.Name}\" contains a character " +
                    "(~ : newline) SkyPatcher can't express in fullName.");

            if (rule == null) return false;

            // Taking an enchantment OFF still gets said out loud, but no longer because the syntax is
            // a guess - "objectEffect=null" was confirmed in game on 2026-09-30 (see
            // ItemRuleBuilder.ObjectEffectRemovalOp). What remains is the part the rule cannot
            // promise: the patch changes the BASE record, and by the user's own experience an
            // enchantment that has already been put on an item is not reliably gone from copies a
            // save has already handed out. So the warning now points at the save, not at the syntax.
            if (rule.Operations.Any(op => op == ItemRuleBuilder.ObjectEffectRemovalOp))
                report.Warnings.Add(
                    $"{rule.TargetPlugin}|{rule.TargetFormId}: removes the item's enchantment " +
                    "(objectEffect=null). The base record loses it, but a copy your save already " +
                    "carries may keep it - judge this one on a freshly spawned item.");

            foreach (var keyword in rule.ReferencedKeywordKeys)
                if (_references != null && !_references.IsActive(keyword))
                    report.Warnings.Add(
                        $"{rule.TargetPlugin}|{rule.TargetFormId}: reference {keyword} is not in the " +
                        "current scan (rule still written).");

            return true;
        }

        private static int Add(Dictionary<string, List<SkyPatcherRule>> byPlugin, SkyPatcherRule rule)
        {
            // FileNamePlugin, not TargetPlugin: they are the same for every rule that edits the
            // record it filters on, and deliberately differ for the Container-tab rules - see
            // SkyPatcherRule.FilePlugin.
            if (!byPlugin.TryGetValue(rule.FileNamePlugin, out var list))
                byPlugin[rule.FileNamePlugin] = list = new List<SkyPatcherRule>();
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

        // toolKey -> the key the record will really have in the generated ESP. Allocating here (and
        // not in the ESP phase, which runs later) is what lets an item rule name the right FormID;
        // PatchFormIdMapStore never reassigns, so the ESP phase gets the same number back.
        //
        // A dry run only PEEKS: it must not leave an allocation behind for a patch that was never
        // written. An enchantment seen for the first time therefore has no id yet in a dry run, and
        // the rule for it is withheld with a warning rather than guessed at.
        private Dictionary<string, string> ResolveUserEnchantmentKeys(
            PatchGenOptions options, PatchGenReport report)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (!options.GenerateCobj) return map;

            string esp = options.EspNameFor(KeyFactory.UserPluginName);

            foreach (var e in _enchReader.ReadNewEnchantments())
            {
                // The builder refuses an effectless enchantment, so there would be nothing to point
                // at - leaving it out of the map makes the rule withhold itself below.
                if (e.Effects.Count == 0) continue;

                uint? id = options.DryRun ? _formIdMap.Peek(e.ToolKey) : _formIdMap.Allocate(e.ToolKey, esp);
                if (id == null) continue;

                map[e.ToolKey] = $"{esp}|{id.Value:X6}";
            }

            return map;
        }

        // Rewrites an item's enchantment key when it points at one of the user's own enchantments.
        // Everything else passes through untouched.
        private static string MapEnchantmentKey(
            string? edited, string? original, string itemKey,
            IReadOnlyDictionary<string, string> userEnchantments,
            PatchGenOptions options, PatchGenReport report)
        {
            var key = (edited ?? "").Trim();
            if (key.Length == 0) return key;

            bool isOwn = key.StartsWith(KeyFactory.UserPluginName + "|", StringComparison.OrdinalIgnoreCase);
            if (!isOwn) return key;

            if (userEnchantments.TryGetValue(key, out var realKey))
                return realKey;

            // Its own enchantment, but nothing will exist for the rule to point at: either ESP
            // generation is off, or the enchantment has no effects and is not being written.
            // Falling back to the item's previous enchantment means no objectEffect op is emitted at
            // all - better than a rule aimed at a record that does not exist.
            report.Warnings.Add(
                $"{itemKey}: wears {key}, an enchantment created in this tool that is not being " +
                (options.GenerateCobj
                    ? "written (it has no effects yet) — the assignment was left out of the patch."
                    : "written because ESP generation is off — the assignment was left out of the patch."));

            return original ?? "";
        }

        // --- COBJ ESP (Phase B) ---

        private void GenerateCobj(PatchGenOptions options, PatchGenReport report)
        {
            var entries = _cobjReader.ReadEditedCobj();
            var enchOverrides = _enchantmentEspOverrides;
            var newEnchantments = _enchReader.ReadNewEnchantments();

            // Recipes, overridden enchantments and the user's own enchantments share the ESP, so any
            // one of them on its own is reason enough to build it.
            if (entries.Count == 0 && enchOverrides.Count == 0 && newEnchantments.Count == 0) return;

            // A user-created enchantment's worn-restriction list goes straight into the ESP without
            // passing through Accept(), the same blind spot the override path guards above. A list
            // from a mod that has since left the scan would otherwise be written without a word.
            foreach (var e in newEnchantments)
            {
                var list = e.WornRestrictionListKey ?? "";
                if (!KeyFactory.IsUnsetKey(list) && _references != null && !_references.IsActive(list))
                    report.Warnings.Add(
                        $"{e.EditorId}: the worn-restriction list {list} is not in the current scan " +
                        "(still written to the ESP).");
            }

            if (options.DryRun)
            {
                report.CobjNewCount = entries.Count(e => e.IsNew);
                report.CobjOverrideCount = entries.Count(e => !e.IsNew);
                report.EnchantmentEspOverrideCount = enchOverrides.Count;
                report.NewEnchantmentCount = newEnchantments.Count;
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

            // The user's own enchantments have no source plugin to split by - they belong to the
            // tool's pseudo-plugin, which maps to the same ESP its recipes go to.
            var newEnchByEsp = newEnchantments
                .GroupBy(_ => options.EspNameFor(KeyFactory.UserPluginName), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

            var espNames = cobjByEsp.Keys
                .Concat(enchByEsp.Keys)
                .Concat(newEnchByEsp.Keys)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase);

            foreach (var espName in espNames)
            {
                var cobjForEsp = cobjByEsp.TryGetValue(espName, out var c)
                    ? c : new List<CobjPatchEntry>();
                var enchForEsp = enchByEsp.TryGetValue(espName, out var en)
                    ? en : new List<CobjEspBuilder.EnchantmentEspEntry>();

                var newEnchForEsp = newEnchByEsp.TryGetValue(espName, out var ne)
                    ? ne : new List<CobjEspBuilder.NewEnchantmentEspEntry>();

                var res = builder.Build(
                    cobjForEsp, _formIdMap, loadOrder, options.OutputRoot, espName, options.EslWhenPossible,
                    resolver, enchForEsp, newEnchForEsp);

                report.CobjNewCount += res.NewCount;
                report.CobjOverrideCount += res.OverrideCount;
                report.CobjDeepCopiedCount += res.DeepCopiedCount;
                report.CobjFromScratchCount += res.FromScratchCount;
                report.CobjConditionRewriteSkippedCount += res.ConditionRewriteSkippedCount;
                report.CobjDeletedCount += res.DeletedCount;
                report.StaleConditionDataCount += res.StaleConditionDataCount;
                report.EnchantmentEspOverrideCount += res.EnchantmentOverrideCount;
                report.NewEnchantmentCount += res.NewEnchantmentCount;
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
