using System;
using System.Collections.Generic;
using System.Linq;
using SkyrimCraftingTool.Model;

namespace SkyrimCraftingTool.Services.PatchGen
{
    // One "this item appears in that leveled list from level N" placement, flattened out of an
    // item's ContainerString.
    // ContainerKey/ContainerName are carried purely so the rule's comment can say which container
    // the placement was made through. They are NOT part of the rule: one leveled list can be reached
    // from several containers, so a single rule may well have come through more than one of them -
    // see the comment assembly in BuildRules, which lists them all rather than picking one.
    public sealed record LeveledListPlacement(
        string ItemKey,
        string ItemEditorId,
        string LvliKey,
        string LvliName,
        int Level,
        int Count = 1,
        string ContainerKey = "",
        string ContainerName = "");

    // The same list reached through two different containers, with two different levels on its
    // sliders. Surfaced as a warning; the rule uses the lowest level (see BuildRules).
    public sealed record LeveledListLevelConflict(
        string LvliKey,
        string ItemKey,
        int UsedLevel,
        IReadOnlyList<int> AllLevels);

    public sealed record ContainerPlacement(string ItemKey, string ItemEditorId, string ContainerKey, string ContainerName = "");

    public static class LeveledListRuleBuilder
    {
        // The Container tab models a level per list, never a count - one entry per placement.
        private const string EntryCount = "1";

        // The plugin that owns the item being placed. Decides the .ini file name, NOT the filter.
        private static string SourcePlugin(string itemKey)
        {
            int bar = itemKey.IndexOf('|');
            return bar > 0 ? itemKey.Substring(0, bar) : itemKey;
        }

        // "which container did this come from" for the rule's comment. One leveled list can hang in
        // several containers, and a rule bundles every item of one plugin that lands in that list -
        // so the honest answer is often more than one, and naming just the first would be a lie in
        // exactly the case that is most confusing to read back. Lists them all, capped so a list
        // reachable from a dozen merchant chests doesn't produce an unreadable comment line.
        private const int MaxNamedContainers = 3;

        private static string ContainerLabel(IEnumerable<ContainerPlacement> placements)
        {
            var p = placements.First();
            return string.IsNullOrWhiteSpace(p.ContainerName)
                ? $"container {p.ContainerKey}"
                : $"container {p.ContainerName} ({p.ContainerKey})";
        }

        private static string ViaContainers(IEnumerable<LeveledListPlacement> placements)
        {
            var containers = placements
                .Where(p => !string.IsNullOrWhiteSpace(p.ContainerKey))
                .Select(p => string.IsNullOrWhiteSpace(p.ContainerName) ? p.ContainerKey : $"{p.ContainerName} ({p.ContainerKey})")
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (containers.Count == 0) return "";
            if (containers.Count <= MaxNamedContainers)
                return " via " + string.Join(", ", containers);

            return " via " + string.Join(", ", containers.Take(MaxNamedContainers))
                 + $" and {containers.Count - MaxNamedContainers} more container(s)";
        }

        // Splits one item's ContainerString into the two placement kinds described above.
        public static void ParsePlacements(
            string itemKey, string itemEditorId, string? containerString,
            List<LeveledListPlacement> lists, List<ContainerPlacement> containers,
            IReadOnlyDictionary<string, string>? lvliNames = null,
            IReadOnlyDictionary<string, string>? containerNames = null)
        {
            if (string.IsNullOrWhiteSpace(containerString)) return;
            if (string.IsNullOrWhiteSpace(itemKey)) return;

            List<ParsedContainerEntry> parsed;
            try
            {
                parsed = ContainerStringParser.Parse(containerString);
            }
            catch (Exception ex)
            {
                // A malformed string must not take the whole export down - the item just
                // contributes no placements. Same spirit as CobjEspBuilder's per-entry try/catch.
                AppLogger.LogError($"LeveledListRuleBuilder: unparseable ContainerString on {itemKey}", ex);
                return;
            }

            foreach (var container in parsed)
            {
                bool anyLevel = false;

                // The tool creates no containers, so a container key under its own plugin can only be a
                // leftover: the "Your leveled lists" pseudo-container that was offered for a while and
                // taken back out (docs/LeveledList-Create-Plan.md, L-P5). Its lists are still real and
                // still placed; the container is never named, and never becomes a filterByContainers
                // target - there is no such container to put anything into.
                bool notAContainer = KeyFactory.IsUserKey(container.ContainerKey);
                var containerKey = notAContainer ? "" : container.ContainerKey;
                var containerName = !notAContainer && containerNames != null
                                    && containerNames.TryGetValue(container.ContainerKey, out var cn) ? cn : "";

                foreach (var (lvliKey, placement) in container.Placements)
                {
                    var level = placement.Level;
                    // Level 0 means "slider off". ContainerStringBuilder already drops those on
                    // write; enforcing it again here keeps a hand-edited or legacy string from
                    // smuggling one in as if it were a real placement.
                    if (level <= 0) continue;
                    if (string.IsNullOrWhiteSpace(lvliKey)) continue;

                    var name = lvliNames != null && lvliNames.TryGetValue(lvliKey, out var n) ? n : "";
                    lists.Add(new LeveledListPlacement(itemKey, itemEditorId, lvliKey, name, level,
                        placement.Count, containerKey, containerName));
                    anyLevel = true;
                }

                // No slider on this container was raised: the item belongs in the container itself.
                if (!anyLevel && !string.IsNullOrWhiteSpace(containerKey))
                    containers.Add(new ContainerPlacement(itemKey, itemEditorId, containerKey, containerName));
            }
        }

        public static IReadOnlyList<SkyPatcherRule> BuildRules(
            IEnumerable<LeveledListPlacement> placements,
            out IReadOnlyList<LeveledListLevelConflict> conflicts)
        {
            var rules = new List<SkyPatcherRule>();
            var conflictList = new List<LeveledListLevelConflict>();
            conflicts = conflictList;

            // Two levels of grouping, and they are not the same thing:
            //   outer = the ITEMS' plugin, which decides the .ini file name (SkyPatcher's
            //           conditional load - see SkyPatcherRule.FilePlugin). One file per mod that
            //           contributes items, so a rule only exists while that mod does.
            //   inner = the leveled LIST, which is what the rule filters on.
            // Two mods feeding the same list therefore produce two rules in two files rather than
            // one merged rule in the list's own (usually always-loaded) file.
            var byFile = (placements ?? Enumerable.Empty<LeveledListPlacement>())
                .Where(p => p.Level > 0 && !string.IsNullOrWhiteSpace(p.LvliKey) && !string.IsNullOrWhiteSpace(p.ItemKey))
                .GroupBy(p => SourcePlugin(p.ItemKey), StringComparer.OrdinalIgnoreCase)
                .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase);

            foreach (var file in byFile)
            foreach (var list in file
                .GroupBy(p => p.LvliKey, StringComparer.OrdinalIgnoreCase)
                .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
            {
                var entries = new List<string>();
                var referenced = new List<string> { list.Key };

                var byItem = list
                    .GroupBy(p => p.ItemKey, StringComparer.OrdinalIgnoreCase)
                    .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase);

                foreach (var item in byItem)
                {
                    var levels = item.Select(p => p.Level).Distinct().OrderBy(l => l).ToList();

                    int level = levels[0];
                    if (levels.Count > 1)
                        conflictList.Add(new LeveledListLevelConflict(list.Key, item.Key, level, levels));

                    int count = item.Where(p => p.Level == level)
                                    .Select(p => p.Count)
                                    .DefaultIfEmpty(1)
                                    .Max();

                    entries.Add($"{PatchFormat.RefKey8(item.Key)}~{PatchFormat.Int(level)}~{PatchFormat.Int(count)}");
                    referenced.Add(item.Key);
                }

                if (entries.Count == 0) continue;

                var (plugin, formId) = KeyFactory.SplitMasterKey(list.Key);
                var listName = list.Select(p => p.LvliName).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n));

                rules.Add(new SkyPatcherRule
                {
                    FilterDirective = "filterByLLs",
                    TargetPlugin = plugin,
                    TargetFormId = formId,
                    FilePlugin = file.Key,
                    Comment = (string.IsNullOrWhiteSpace(listName)
                            ? $"leveled list {list.Key}: {entries.Count} item(s)"
                            : $"leveled list {listName} ({list.Key}): {entries.Count} item(s)")
                        + ViaContainers(list),
                    Operations = new[] { "addOnceToLLs=" + string.Join(",", entries) },
                    ReferencedKeywordKeys = referenced,
                });
            }

            return rules;
        }

        public static IReadOnlyList<SkyPatcherRule> BuildRestoreRules(
            IEnumerable<RestoredEntry> restored,
            IReadOnlyDictionary<string, string>? lvliNames = null)
        {
            var rules = new List<SkyPatcherRule>();

            foreach (var byList in (restored ?? Enumerable.Empty<RestoredEntry>())
                         .Where(e => e != null
                                     && !string.IsNullOrWhiteSpace(e.ListKey)
                                     && !string.IsNullOrWhiteSpace(e.Reference))
                         .GroupBy(e => e.ListKey, StringComparer.OrdinalIgnoreCase)
                         .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
            {
                // One rule per (list, owning plugin): the filter names the list, but the FILE a rule
                // goes into is decided by what it references, and one list can get entries back from
                // several mods at once.
                foreach (var byFile in byList
                             .GroupBy(e => KeyFactory.SplitMasterKey(e.Reference).master, StringComparer.OrdinalIgnoreCase)
                             .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
                {
                    var entries = byFile
                        .OrderBy(e => e.Reference, StringComparer.OrdinalIgnoreCase)
                        .Select(e => $"{PatchFormat.RefKey8(e.Reference)}~{PatchFormat.Int(e.Level)}~{PatchFormat.Int(e.Count)}")
                        .ToList();

                    if (entries.Count == 0) continue;

                    var (plugin, formId) = KeyFactory.SplitMasterKey(byList.Key);
                    var listName = lvliNames != null && lvliNames.TryGetValue(byList.Key, out var n) && !string.IsNullOrWhiteSpace(n)
                        ? $"{n} ({byList.Key})"
                        : byList.Key;

                    rules.Add(new SkyPatcherRule
                    {
                        FilterDirective = "filterByLLs",
                        TargetPlugin = plugin,
                        TargetFormId = formId,
                        FilePlugin = byFile.Key,
                        Comment = $"leveled list {listName}: {entries.Count} entry(s) put back after an override dropped them",
                        Operations = new[] { "addOnceToLLs=" + string.Join(",", entries) },
                        ReferencedKeywordKeys = byFile.Select(e => e.Reference).ToList(),
                    });
                }
            }

            return rules;
        }

        public static IReadOnlyList<SkyPatcherRule> BuildRemovalRules(
            IEnumerable<RemovedEntry> removed,
            RemovalScope scope,
            IReadOnlyDictionary<string, string>? ownerNames = null)
        {
            var rules = new List<SkyPatcherRule>();

            string filter = scope == RemovalScope.Container ? "filterByContainers" : "filterByLLs";
            string op = scope == RemovalScope.Container ? "removeFromContainers" : "removeFromLLs";
            string what = scope == RemovalScope.Container ? "container" : "leveled list";

            foreach (var byOwner in (removed ?? Enumerable.Empty<RemovedEntry>())
                         .Where(e => e != null
                                     && !string.IsNullOrWhiteSpace(e.OwnerKey)
                                     && !string.IsNullOrWhiteSpace(e.Reference))
                         .GroupBy(e => e.OwnerKey, StringComparer.OrdinalIgnoreCase)
                         .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
            {
                foreach (var byFile in byOwner
                             .GroupBy(e => KeyFactory.SplitMasterKey(e.Reference).master, StringComparer.OrdinalIgnoreCase)
                             .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
                {
                    var entries = byFile
                        .Select(e => e.Reference)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
                        .Select(PatchFormat.RefKey8)
                        .ToList();

                    if (entries.Count == 0) continue;

                    var (plugin, formId) = KeyFactory.SplitMasterKey(byOwner.Key);
                    var ownerName = ownerNames != null && ownerNames.TryGetValue(byOwner.Key, out var n) && !string.IsNullOrWhiteSpace(n)
                        ? $"{n} ({byOwner.Key})"
                        : byOwner.Key;

                    rules.Add(new SkyPatcherRule
                    {
                        FilterDirective = filter,
                        TargetPlugin = plugin,
                        TargetFormId = formId,
                        FilePlugin = byFile.Key,
                        // Says "every" out loud in the file itself. Someone reading the INI a year
                        // from now has to be able to see that this is not a per-entry removal.
                        Comment = $"{what} {ownerName}: removes every occurrence of {entries.Count} object(s)",
                        Operations = new[] { op + "=" + string.Join(",", entries) },
                        ReferencedKeywordKeys = byFile.Select(e => e.Reference).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                    });
                }
            }

            return rules;
        }

        public static IReadOnlyList<SkyPatcherRule> BuildPropertyRules(
            IEnumerable<LeveledListEdit> edits,
            IReadOnlyDictionary<string, string>? lvliNames = null)
        {
            var rules = new List<SkyPatcherRule>();

            foreach (var edit in (edits ?? Enumerable.Empty<LeveledListEdit>())
                         .Where(e => e != null && !string.IsNullOrWhiteSpace(e.ListKey) && !e.IsEmpty)
                         .OrderBy(e => e.ListKey, StringComparer.OrdinalIgnoreCase))
            {
                var operations = new List<string>();

                // Only what actually differs from the scan. An edit that writes the value a list
                // already has would make this tool the owner of a property nobody changed.
                if (edit.ChanceNone.HasValue)
                    operations.Add("chanceNone=" + PatchFormat.Int(Math.Clamp(edit.ChanceNone.Value, 0, 100)));

                var flagOperation = LeveledListFlags.ToOperation(edit.Flags);
                if (!string.IsNullOrEmpty(flagOperation))
                    operations.Add(flagOperation);

                if (operations.Count == 0) continue;

                var (plugin, formId) = KeyFactory.SplitMasterKey(edit.ListKey);
                var name = lvliNames != null && lvliNames.TryGetValue(edit.ListKey, out var n) ? n : "";

                rules.Add(new SkyPatcherRule
                {
                    FilterDirective = "filterByLLs",
                    TargetPlugin = plugin,
                    TargetFormId = formId,
                    Comment = (string.IsNullOrWhiteSpace(name)
                            ? $"leveled list {edit.ListKey}"
                            : $"leveled list {name} ({edit.ListKey})")
                        + ": list properties changed by you - affects every item in this list",
                    Operations = operations,
                    ReferencedKeywordKeys = new[] { edit.ListKey },
                });
            }

            return rules;
        }

        // The container half: one filterByContainers rule per container, listing every item that
        // goes into it directly. Same shape as BuildRules above - grouped by the TARGET (the
        // container), deterministic order, addOnce so re-running the export can't stack duplicates.
        public static IReadOnlyList<SkyPatcherRule> BuildContainerRules(IEnumerable<ContainerPlacement> placements)
        {
            var rules = new List<SkyPatcherRule>();

            // Same two-level grouping as BuildRules: file by the ITEMS' plugin, rule by the target.
            var byFile = (placements ?? Enumerable.Empty<ContainerPlacement>())
                .Where(p => !string.IsNullOrWhiteSpace(p.ContainerKey) && !string.IsNullOrWhiteSpace(p.ItemKey))
                .GroupBy(p => SourcePlugin(p.ItemKey), StringComparer.OrdinalIgnoreCase)
                .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase);

            foreach (var file in byFile)
            foreach (var container in file
                .GroupBy(p => p.ContainerKey, StringComparer.OrdinalIgnoreCase)
                .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
            {
                var items = container
                    .Select(p => p.ItemKey)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (items.Count == 0) continue;

                var (plugin, formId) = KeyFactory.SplitMasterKey(container.Key);

                rules.Add(new SkyPatcherRule
                {
                    FilterDirective = "filterByContainers",
                    TargetPlugin = plugin,
                    TargetFormId = formId,
                    FilePlugin = file.Key,
                    Comment = ContainerLabel(container) + $": {items.Count} item(s)",
                    // <object>~<count>; the Container tab models no count, so one each.
                    Operations = new[]
                    {
                        "addOnceToContainers=" + string.Join(",",
                            items.Select(k => $"{PatchFormat.RefKey8(k)}~{EntryCount}"))
                    },
                    ReferencedKeywordKeys = items.Prepend(container.Key).ToList(),
                });
            }

            return rules;
        }

        public sealed record OwnListPlacement(
            string ListKey,
            string ListName,
            string TargetKey,
            string TargetName,
            UserListTargetKind Kind,
            int Level,
            int Count,
            string FilePlugin);

        // One rule per (target, file), every own list that goes into it on that one line - the same
        // shape BuildRules gives items. addOnce for the same reason as there: the patch is regenerated
        // from scratch and SkyPatcher INIs stack, so a plain add would grow the list on every start.
        //
        // A list into a list:   filterByLLs=<target>:addOnceToLLs=<list>~<level>~<count>
        // A list into a chest:  filterByContainers=<target>:addOnceToContainers=<list>~<count>
        //
        // The second one is the reason SkyPatcher 7.1.3 matters: before it, leveled lists in
        // containers were behind iAllowLeveledListsAddedToContainers (off by default) and carried a
        // CTD warning. The generator says so in the report whenever such a rule is written.
        public static IReadOnlyList<SkyPatcherRule> BuildOwnListRules(IEnumerable<OwnListPlacement> placements)
        {
            var rules = new List<SkyPatcherRule>();

            foreach (var group in (placements ?? Enumerable.Empty<OwnListPlacement>())
                         .Where(p => !string.IsNullOrWhiteSpace(p.ListKey) && !string.IsNullOrWhiteSpace(p.TargetKey))
                         .GroupBy(p => (p.Kind, Target: p.TargetKey.ToUpperInvariant(), File: p.FilePlugin.ToUpperInvariant()))
                         .OrderBy(g => g.Key.Kind)
                         .ThenBy(g => g.Key.Target, StringComparer.OrdinalIgnoreCase)
                         .ThenBy(g => g.Key.File, StringComparer.OrdinalIgnoreCase))
            {
                var first = group.First();
                bool intoList = first.Kind == UserListTargetKind.LeveledList;

                // One entry per own list, even if two target rows somehow named the same pair:
                // addOnce would drop the second at runtime anyway.
                var entries = group
                    .GroupBy(p => p.ListKey, StringComparer.OrdinalIgnoreCase)
                    .Select(g => g.First())
                    .OrderBy(p => p.ListKey, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                var ops = intoList
                    ? "addOnceToLLs=" + string.Join(",", entries.Select(p =>
                          $"{PatchFormat.RefKey8(p.ListKey)}~{PatchFormat.Int(Math.Max(1, p.Level))}~{PatchFormat.Int(Math.Max(1, p.Count))}"))
                    : "addOnceToContainers=" + string.Join(",", entries.Select(p =>
                          $"{PatchFormat.RefKey8(p.ListKey)}~{PatchFormat.Int(Math.Max(1, p.Count))}"));

                var (plugin, formId) = KeyFactory.SplitMasterKey(first.TargetKey);
                var what = intoList ? "leveled list" : "container";
                var target = string.IsNullOrWhiteSpace(first.TargetName)
                    ? $"{what} {first.TargetKey}"
                    : $"{what} {first.TargetName} ({first.TargetKey})";
                var names = string.Join(", ", entries.Select(p => string.IsNullOrWhiteSpace(p.ListName) ? p.ListKey : p.ListName));

                rules.Add(new SkyPatcherRule
                {
                    FilterDirective = intoList ? "filterByLLs" : "filterByContainers",
                    TargetPlugin = plugin,
                    TargetFormId = formId,
                    FilePlugin = first.FilePlugin,
                    Comment = $"{target}: your own leveled list(s) {names}",
                    Operations = new[] { ops },

                    // Only a CONTAINER target is offered to the dead-reference check. The resolver
                    // knows containers; it does not know leveled lists at all, so a list target
                    // would be reported as missing on every single rule - and the own lists are
                    // translated keys the scan has never seen either.
                    ReferencedKeywordKeys = intoList ? Array.Empty<string>() : new[] { first.TargetKey },
                });
            }

            return rules;
        }
    }
}
