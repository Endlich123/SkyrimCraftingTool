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
        string ContainerKey = "",
        string ContainerName = "");

    // The same list reached through two different containers, with two different levels on its
    // sliders. Surfaced as a warning; the rule uses the lowest level (see BuildRules).
    public sealed record LeveledListLevelConflict(
        string LvliKey,
        string ItemKey,
        int UsedLevel,
        IReadOnlyList<int> AllLevels);

    // "This item goes straight into that container", i.e. a container the user selected while every
    // one of its LVLi sliders stayed at 0.
    public sealed record ContainerPlacement(string ItemKey, string ItemEditorId, string ContainerKey, string ContainerName = "");

    // item -> leveled-list placements  =>  one filterByLLs rule per leveled list.
    //
    // WHY THIS IS THE LEVELED-LIST PATCHER AND NOT THE CONTAINER ONE: the Container tab does not
    // put items into a CONT. The scan only records a container's entries when the entry itself is
    // an LVLI (see ItemDBHandler.Scan.cs, "CONTAINER + LVLI"), and an item's ContainerString stores
    // "{ContainerKey: {LVLiKey,Level; ...}}" - the container is the grouping the user browses by,
    // the leveled list with its level is the actual placement. So the operation is
    // addOnceToLLs under filterByLLs, which also stays clear of SkyPatcher's CTD disclaimer on
    // addToContainers (that one is about putting leveled LISTS into containers - something this
    // tool never produces).
    //
    // THE TWO PLACEMENT KINDS. A container row carries its own leveled lists, each with a level
    // slider, and which one the user meant is decided by those sliders:
    //   every slider at 0 ("{ContainerKey: {}}")  -> the item goes into the CONTAINER itself
    //                                                -> filterByContainers + addOnceToContainers
    //   any slider above 0                        -> the item goes into THOSE leveled lists
    //                                                -> filterByLLs + addOnceToLLs
    // So both patchers are in play, and the sliders are the switch between them. SkyPatcher's CTD
    // disclaimer on addToContainers does not apply either way: it is about putting a leveled LIST
    // into a container as the object, and the object here is always the ARMO/WEAP itself.
    //
    // ADDITIVE ONLY, BY CONSTRUCTION: the scan never fills Armor/Weapons.ContainerString (see
    // ArmorParamNames/WeaponParamNames), so the pristine value is always empty and every placement
    // is something the user added. There is no "was in that list, now removed" state to express,
    // which is why nothing here emits removeFromLLs or removeFromContainers.
    //
    // addOnce, not add: the patch is regenerated from scratch on every export and SkyPatcher INIs
    // stack with whatever else touches the list, so a plain addToLLs would keep appending the same
    // item on every game start.
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
                var containerName = containerNames != null && containerNames.TryGetValue(container.ContainerKey, out var cn) ? cn : "";

                foreach (var (lvliKey, level) in container.Levels)
                {
                    // Level 0 means "slider off". ContainerStringBuilder already drops those on
                    // write; enforcing it again here keeps a hand-edited or legacy string from
                    // smuggling one in as if it were a real placement.
                    if (level <= 0) continue;
                    if (string.IsNullOrWhiteSpace(lvliKey)) continue;

                    var name = lvliNames != null && lvliNames.TryGetValue(lvliKey, out var n) ? n : "";
                    lists.Add(new LeveledListPlacement(itemKey, itemEditorId, lvliKey, name, level,
                        container.ContainerKey, containerName));
                    anyLevel = true;
                }

                // No slider on this container was raised: the item belongs in the container itself.
                if (!anyLevel && !string.IsNullOrWhiteSpace(container.ContainerKey))
                    containers.Add(new ContainerPlacement(itemKey, itemEditorId, container.ContainerKey, containerName));
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

                    // One list can sit in several containers, each with its own slider. Lowest level
                    // wins: it is the earliest point the user asked to see the item, and any higher
                    // setting is already covered from there on. Reported so it is a decision the
                    // user can see rather than a silent pick.
                    int level = levels[0];
                    if (levels.Count > 1)
                        conflictList.Add(new LeveledListLevelConflict(list.Key, item.Key, level, levels));

                    entries.Add($"{PatchFormat.RefKey8(item.Key)}~{PatchFormat.Int(level)}~{EntryCount}");
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
    }
}
