using System;
using System.Collections.Generic;
using System.Linq;
using SkyrimCraftingTool.Model;

namespace SkyrimCraftingTool.Services
{
    // The rescan diff (G6, docs/NPC-Gruppen-Plan.md section 8).
    //
    // WHY THIS IS NOT OPTIONAL. A group is a predicate, so its membership is whatever the load order
    // happens to match - install one mod and "Bandit tier 3" holds 71 NPCs where it held 54. The saved
    // change then reaches 17 NPCs nobody ever looked at, and nothing about the group looks different.
    //
    // That is the same danger that took the faction view out in September (_alt/NPC-Plan.md section 12),
    // and the answer settled on then was visibility rather than giving the mechanism up: the membership
    // the user CONFIRMED is kept (NpcGroupMemberSnapshot), the fresh membership is held against it, and
    // the difference is reported BY NAME. Until it is confirmed again the group counts as unchecked, and
    // the patch report says so.
    //
    // A count on its own would not do the job. "54 -> 71" tells you something changed; "17 new from
    // Bandit Overhaul.esp" tells you whether you meant it.
    public static class NpcGroupDiffService
    {
        public static NpcGroupDrift Compare(
            NpcGroup? group,
            IReadOnlyList<NpcRecord>? members,
            IReadOnlyCollection<string>? snapshot)
        {
            var current = members ?? Array.Empty<NpcRecord>();
            var confirmed = new HashSet<string>(
                snapshot ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);

            var currentKeys = new HashSet<string>(
                current.Select(m => m.Key), StringComparer.OrdinalIgnoreCase);

            // An EMPTY snapshot is not "confirmed as empty" - it is "never confirmed". The two look the
            // same in the table, and this is the one place the difference matters: a group nobody has
            // confirmed has no baseline, so there is nothing to compare and everything to check.
            bool hasSnapshot = confirmed.Count > 0;

            var added = hasSnapshot
                ? current.Where(m => !confirmed.Contains(m.Key)).ToList()
                : new List<NpcRecord>();

            var removed = hasSnapshot
                ? confirmed.Where(k => !currentKeys.Contains(k)).OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList()
                : new List<string>();

            return new NpcGroupDrift
            {
                GroupId = group?.Id ?? 0,
                GroupName = group?.Name ?? "",
                HasSnapshot = hasSnapshot,
                SnapshotCount = confirmed.Count,
                CurrentCount = current.Count,
                Added = added,
                RemovedKeys = removed,
            };
        }

        // Every group against its own snapshot. The snapshots come in as one dictionary rather than one
        // query per group: NpcGroupStore.LoadSnapshots reads them in a single pass, and 40 groups would
        // otherwise be 40 round trips for a screen that opens on a click.
        public static List<NpcGroupDrift> CompareAll(
            IEnumerable<NpcGroup>? groups,
            NpcGroupResolver resolver,
            IReadOnlyDictionary<long, List<string>>? snapshots)
        {
            var result = new List<NpcGroupDrift>();

            foreach (var group in groups ?? Enumerable.Empty<NpcGroup>())
            {
                if (group == null) continue;

                List<string>? snapshot = null;
                snapshots?.TryGetValue(group.Id, out snapshot);

                result.Add(Compare(group, resolver.Resolve(group).Members, snapshot ?? new List<string>()));
            }

            return result;
        }
    }

    public sealed class NpcGroupDrift
    {
        public long GroupId { get; init; }
        public string GroupName { get; init; } = "";

        // False means the group was never confirmed - no baseline, so Added and Removed are empty and
        // the group still needs looking at. See Compare.
        public bool HasSnapshot { get; init; }

        public int SnapshotCount { get; init; }
        public int CurrentCount { get; init; }

        // The NPCs that are in the group now and were not when it was confirmed - the 17 from section 8.
        // Whole records, because the report names them and says which plugin they came from.
        public IReadOnlyList<NpcRecord> Added { get; init; } = Array.Empty<NpcRecord>();

        // Keys only: a member that is gone may have gone with its plugin, in which case there is no
        // record left to take a name from.
        public IReadOnlyList<string> RemovedKeys { get; init; } = Array.Empty<string>();

        public bool Changed => Added.Count > 0 || RemovedKeys.Count > 0;

        // What the patch report marks and the editor highlights: either nobody has checked this group, or
        // it has moved since they did.
        public bool NeedsAttention => !HasSnapshot || Changed;

        // "Bandit 03: 54 -> 71, 17 new from Bandit Overhaul.esp" - the line section 8 asks for.
        public string Headline => $"{GroupName}: {Detail}";

        // The same without the group's name, for the places that print the name themselves - the patch
        // report already names every group it lists. One property so the two cannot word it differently.
        public string Detail
        {
            get
            {
                if (!HasSnapshot) return $"{CurrentCount} member(s), never confirmed";
                if (!Changed) return $"{CurrentCount} member(s), unchanged";

                var parts = new List<string>();

                if (Added.Count > 0)
                    parts.Add($"{Added.Count} new from {PluginList(Added)}");

                if (RemovedKeys.Count > 0)
                    parts.Add($"{RemovedKeys.Count} gone");

                return $"{SnapshotCount} -> {CurrentCount}, {string.Join(", ", parts)}";
            }
        }

        // The names themselves, because that is what makes the difference judgeable. Capped, since a
        // class group can gain hundreds at once and a message box with 300 EditorIDs helps nobody - the
        // count in the headline stays exact either way.
        public IReadOnlyList<string> AddedNames => Added
            .Take(NameLimit)
            .Select(n => string.IsNullOrWhiteSpace(n.EditorID) ? n.Key : n.EditorID)
            .ToList();

        public int AddedNamesNotShown => Math.Max(0, Added.Count - NameLimit);

        private const int NameLimit = 10;

        // Which plugins the new members came from, biggest first: "Bandit Overhaul.esp" or
        // "Bandit Overhaul.esp (12), Skyrim.esm (5)". The plugin is the half of the answer that says
        // whether the change was somebody's mod install or a change to the group itself.
        private static string PluginList(IReadOnlyList<NpcRecord> npcs)
        {
            var byPlugin = npcs
                .GroupBy(n => PluginOf(n.Key), StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(g => g.Count())
                .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (byPlugin.Count == 1) return byPlugin[0].Key;

            return string.Join(", ", byPlugin.Select(g => $"{g.Key} ({g.Count()})"));
        }

        private static string PluginOf(string? key)
        {
            if (string.IsNullOrWhiteSpace(key)) return "?";
            int bar = key.IndexOf('|');
            return bar < 0 ? key : key.Substring(0, bar);
        }
    }
}
