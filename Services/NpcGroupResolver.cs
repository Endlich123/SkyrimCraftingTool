using System;
using System.Collections.Generic;
using System.Linq;
using SkyrimCraftingTool.Model;

namespace SkyrimCraftingTool.Services
{
    // Predicate -> member list (G2, docs/NPC-Gruppen-Plan.md section 4).
    //
    // This is the half of the group model that must never be stored: a saved member list goes stale
    // on the next rescan, and a saved change then reaches NPCs nobody ever looked at - "Bandit
    // Stufe 3 has 71 members instead of 54" (section 8). So membership is recomputed from the NPC
    // records every time it is needed, and the only thing kept beside it is the snapshot the diff
    // compares against.
    //
    // WHAT IT RESOLVES AND WHAT IT DOES NOT: this answers "which NPCs does the user mean", read off
    // the scanned records. It is NOT a prediction of which NPCs SkyPatcher will touch at runtime, and
    // the two differ on purpose in two places:
    //
    //   * a rule reaches NPCs this database never saw - that is the point of a filter rule, and
    //   * some exclusions have no filter at all: filterByUnique does not exist, so the default
    //     exclusion of the 1.313 unique NPCs resolves here but needs the keyword tagger of section
    //     5.3 to reach the game.
    //
    // The rule builder (G5) closes that gap and reports it. Pretending the two are the same would put
    // a member count on screen that the patch cannot keep.
    public sealed class NpcGroupResolver
    {
        private readonly List<NpcRecord> _npcs;

        public NpcGroupResolver(IEnumerable<NpcRecord>? npcs)
            => _npcs = npcs?.ToList() ?? new List<NpcRecord>();

        public int NpcCount => _npcs.Count;

        // The axes a single NPC can hold MORE THAN ONE value of. It decides what two include clauses
        // on the same axis mean, and it is the one place where this resolver departs from a blanket
        // reading of section 4 ("clauses of the same axis and mode are ANDed"):
        //
        //   * faction and editorid are genuinely multi-valued - an NPC is in several factions, and an
        //     EditorID contains several substrings - so AND is both possible and what SkyPatcher does
        //     (filterByFactions and filterByEditorIdContains are documented as AND, with the Or
        //     variants for the other reading).
        //   * class, race, mod and gender are single-valued: an NPC has exactly one of each. ANDing
        //     two of them can only ever produce zero members, which is not a filter but a bug that
        //     looks like an empty group. Two classes therefore mean "either class" - the same reading
        //     filterByClass=A,B has, since "you can add multiple classes" would be pointless
        //     otherwise.
        //   * flag is multi-valued: the bits are independent, so "essential AND female" is a sensible
        //     filter, and it matches combining filterByEssential with filterByGender in one rule.
        private static bool MultiValued(string axis) =>
            axis is NpcGroupAxis.Faction or NpcGroupAxis.EditorId or NpcGroupAxis.Flag;

        // One group against the loaded NPCs.
        public NpcGroupResolution Resolve(NpcGroup? group)
        {
            if (group == null) return NpcGroupResolution.Empty;

            var warnings = new List<string>();
            var clauses = Validate(group, warnings);

            // A clause this resolver cannot evaluate - an unknown axis, a flag token that is not a
            // flag - resolves the PREDICATE to nothing rather than to "everyone the rest matched".
            // Dropping the clause instead would silently WIDEN the group, and a group that reaches
            // 6.642 NPCs because of a typo is exactly the failure this design exists to make
            // impossible. Add-overrides below still count: they name their NPCs outright, so there is
            // nothing ambiguous left to get wrong.
            bool predicateUsable = warnings.Count == 0;

            var members = new Dictionary<string, NpcRecord>(StringComparer.OrdinalIgnoreCase);

            // A group with no predicate at all resolves to its add-overrides and nothing else - the
            // single-NPC case of section 4.1. Not to every NPC: "no filter" as "everything" is the
            // one default that could put 6.642 NPCs in a patch by accident.
            int predicateMatches = 0;
            if (predicateUsable && clauses.Count > 0)
            {
                foreach (var npc in _npcs)
                {
                    if (!Matches(npc, clauses)) continue;
                    predicateMatches++;
                    members[npc.Key] = npc;
                }
            }

            // Hand corrections, after the predicate: remove first, then add. The order is not a
            // preference - NpcGroupMemberOverride's primary key is (GroupId, NpcKey) WITHOUT Mode, so
            // one NPC cannot carry both and the two can never meet.
            int removed = 0, added = 0;

            foreach (var over in group.Overrides)
            {
                if (string.IsNullOrWhiteSpace(over.NpcKey)) continue;

                if (string.Equals(over.Mode, NpcGroupOverrideMode.Remove, StringComparison.OrdinalIgnoreCase))
                {
                    if (members.Remove(over.NpcKey)) removed++;
                    continue;
                }

                if (!string.Equals(over.Mode, NpcGroupOverrideMode.Add, StringComparison.OrdinalIgnoreCase))
                {
                    warnings.Add($"override mode '{over.Mode}' is neither add nor remove");
                    continue;
                }

                // An add-override may name an NPC that is not in the loaded set at all - a removed
                // mod, or a group carried over from another load order. Reported rather than dropped:
                // the alternative is a member count that shrinks with no explanation.
                var record = _npcs.FirstOrDefault(n =>
                    string.Equals(n.Key, over.NpcKey, StringComparison.OrdinalIgnoreCase));

                if (record == null)
                {
                    warnings.Add($"added NPC '{over.NpcKey}' is not in the scanned data");
                    continue;
                }

                if (members.ContainsKey(record.Key)) continue;
                members[record.Key] = record;
                added++;
            }

            return new NpcGroupResolution
            {
                Members = members.Values
                    .OrderBy(n => n.EditorID, StringComparer.OrdinalIgnoreCase)
                    .ToList(),
                PredicateMatches = predicateMatches,
                AddedByOverride = added,
                RemovedByOverride = removed,
                HasPredicate = clauses.Count > 0,
                Warnings = warnings,
            };
        }

        // Every NPC no active group reaches. This is what section 7's "'without a group' is plausibly
        // small" gets measured against, and it is a resolver question rather than a seeder one: the
        // seeder writes predicates, and only resolution can say what they leave behind.
        public List<NpcRecord> Ungrouped(IEnumerable<NpcGroup>? groups)
        {
            var covered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var group in groups ?? Enumerable.Empty<NpcGroup>())
            {
                if (group == null || !group.Active) continue;
                foreach (var member in Resolve(group).Members)
                    covered.Add(member.Key);
            }

            return _npcs.Where(n => !covered.Contains(n.Key)).ToList();
        }

        // ---- predicate evaluation -------------------------------------------------------------

        // Clauses grouped by axis, because the axis is what decides how they combine.
        private sealed class AxisClauses
        {
            public string Axis = "";
            public List<string> Include = new();
            public List<string> IncludeOr = new();
            public List<string> Exclude = new();
        }

        private static List<AxisClauses> Validate(NpcGroup group, List<string> warnings)
        {
            var byAxis = new Dictionary<string, AxisClauses>(StringComparer.OrdinalIgnoreCase);

            foreach (var clause in group.Predicates)
            {
                var axis = (clause.Axis ?? "").Trim();
                var value = (clause.Value ?? "").Trim();

                if (!NpcGroupAxis.All.Contains(axis, StringComparer.OrdinalIgnoreCase))
                {
                    warnings.Add($"unknown predicate axis '{clause.Axis}'");
                    continue;
                }

                if (value.Length == 0)
                {
                    warnings.Add($"empty value on axis '{axis}'");
                    continue;
                }

                // The two axes whose values are TOKENS rather than record keys are checked here and
                // not at match time: a token that names nothing has to be a warning on the GROUP, not
                // a per-NPC mismatch that reads as "this group happens to be empty".
                if (string.Equals(axis, NpcGroupAxis.Flag, StringComparison.OrdinalIgnoreCase)
                    && !NpcFlagNames.TryParse(value, out _))
                {
                    warnings.Add($"'{value}' is not an NPC flag");
                    continue;
                }

                if (string.Equals(axis, NpcGroupAxis.Gender, StringComparison.OrdinalIgnoreCase)
                    && !IsGenderToken(value))
                {
                    warnings.Add($"'{value}' is not a gender (male, female)");
                    continue;
                }

                if (!byAxis.TryGetValue(axis, out var entry))
                {
                    entry = new AxisClauses { Axis = axis };
                    byAxis[axis] = entry;
                }

                var mode = (clause.Mode ?? "").Trim();
                if (string.Equals(mode, NpcGroupMode.Include, StringComparison.OrdinalIgnoreCase))
                    entry.Include.Add(value);
                else if (string.Equals(mode, NpcGroupMode.IncludeOr, StringComparison.OrdinalIgnoreCase))
                    entry.IncludeOr.Add(value);
                else if (string.Equals(mode, NpcGroupMode.Exclude, StringComparison.OrdinalIgnoreCase))
                    entry.Exclude.Add(value);
                else
                    warnings.Add($"unknown predicate mode '{clause.Mode}' on axis '{axis}'");
            }

            return byAxis.Values.ToList();
        }

        // Axes are ANDed with each other. Whether SkyPatcher agrees for every combination is a
        // question for the rule builder, not for this resolution: its own documentation notes that
        // filterByNpcs, filterByRaces and filterByKeywords "are not connected" to the other filters
        // (docs/NPC_Patcher.txt, first line). G5 has to check that the rule it writes expresses the
        // membership resolved here, and say so where it cannot.
        private static bool Matches(NpcRecord npc, List<AxisClauses> clauses)
        {
            foreach (var axis in clauses)
            {
                if (axis.Include.Count > 0)
                {
                    bool ok = MultiValued(axis.Axis)
                        ? axis.Include.All(v => MatchesValue(npc, axis.Axis, v))
                        : axis.Include.Any(v => MatchesValue(npc, axis.Axis, v));
                    if (!ok) return false;
                }

                if (axis.IncludeOr.Count > 0 && !axis.IncludeOr.Any(v => MatchesValue(npc, axis.Axis, v)))
                    return false;

                if (axis.Exclude.Any(v => MatchesValue(npc, axis.Axis, v)))
                    return false;
            }

            return true;
        }

        private static bool MatchesValue(NpcRecord npc, string axis, string value) => axis.ToLowerInvariant() switch
        {
            NpcGroupAxis.Class => SameKey(npc.ClassKey, value),
            NpcGroupAxis.Race => SameKey(npc.RaceKey, value),
            NpcGroupAxis.Faction => npc.Factions.Any(f => SameKey(f.FactionKey, value)),

            // CONTAINS, not "starts with" - filterByEditorIdContains is a substring match, and the
            // difference is measurable rather than academic. Measured on the real load order on
            // 2026-09-26: "EncBandit03" matches 54 records as a prefix and 79 as a substring, the
            // extra 25 being Dragonborn.esm's DLC2EncBandit03*. A prefix reading here would put 54 on
            // screen for a rule that then reaches 79 - which is the plan's own EncBandit03 figure, and
            // why section 8's rescan diff is not optional.
            NpcGroupAxis.EditorId => (npc.EditorID ?? "").Contains(value, StringComparison.OrdinalIgnoreCase),

            NpcGroupAxis.Mod => string.Equals(PluginOf(npc.Key), value, StringComparison.OrdinalIgnoreCase),
            NpcGroupAxis.Flag => HasFlag(npc, value),
            NpcGroupAxis.Gender => IsFemaleToken(value) == ((NpcFlag)npc.Flags).HasFlag(NpcFlag.Female),
            _ => false,
        };

        // The one flag that cannot be read off the Flags column. Measured on the real load order on
        // 2026-09-26: the PcLevelMult BIT is set on 0 of 6.642 NPCs, while 654 of them do use a
        // player-level multiplier - the scan reads that from the record's level field being a
        // PcLevelMult ("cfg.Level is IPcLevelMultGetter", ItemDBHandler.Scan.cs), not from a bit.
        // Testing the bit here would make filterByPCLevelMult resolve to an empty group on a load
        // order where 654 NPCs qualify.
        private static bool HasFlag(NpcRecord npc, string token)
        {
            if (!NpcFlagNames.TryParse(token, out var flag)) return false;
            if (flag == NpcFlag.PcLevelMult) return npc.UsesPcLevelMult;
            return ((NpcFlag)npc.Flags).HasFlag(flag);
        }

        private static bool IsGenderToken(string value)
            => string.Equals(value, "male", StringComparison.OrdinalIgnoreCase)
               || string.Equals(value, "female", StringComparison.OrdinalIgnoreCase);

        private static bool IsFemaleToken(string value)
            => string.Equals(value, "female", StringComparison.OrdinalIgnoreCase);

        private static string PluginOf(string? key)
        {
            if (string.IsNullOrWhiteSpace(key)) return "";
            int bar = key.IndexOf('|');
            return bar < 0 ? key.Trim() : key.Substring(0, bar).Trim();
        }

        // Two record keys naming the same record. Beyond the case-insensitivity the databases already
        // have through COLLATE NOCASE, this also ignores leading zeros in the FormID: the scan writes
        // six digits (KeyFactory.BuildMasterKey, "X6"), but a key typed off a mod page or out of the
        // SkyPatcher documentation is just as likely to read Skyrim.esm|1BCC0 or Skyrim.esm|0001BCC0.
        // All three name one faction.
        private static bool SameKey(string? a, string? b)
        {
            if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;

            int barA = a.IndexOf('|'), barB = b.IndexOf('|');
            if (barA < 0 || barB < 0)
                return string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

            if (!string.Equals(a.Substring(0, barA).Trim(), b.Substring(0, barB).Trim(),
                    StringComparison.OrdinalIgnoreCase))
                return false;

            return string.Equals(TrimId(a.Substring(barA + 1)), TrimId(b.Substring(barB + 1)),
                StringComparison.OrdinalIgnoreCase);
        }

        private static string TrimId(string id)
        {
            var trimmed = id.Trim().TrimStart('0');
            return trimmed.Length == 0 ? "0" : trimmed;
        }
    }

    // What one resolution produced. The three counts are not decoration: section 4 keeps hand
    // corrections apart from the predicate so a re-seed cannot lose them, and the interface has to be
    // able to say "54 by rule, 2 added by hand" rather than one number that hides the difference.
    public sealed class NpcGroupResolution
    {
        public static readonly NpcGroupResolution Empty = new();

        public IReadOnlyList<NpcRecord> Members { get; init; } = Array.Empty<NpcRecord>();
        public int PredicateMatches { get; init; }
        public int AddedByOverride { get; init; }
        public int RemovedByOverride { get; init; }
        public bool HasPredicate { get; init; }

        // Every reason this resolution is not simply "the predicate, applied". Non-empty means the
        // group is not trustworthy as written, and while a warning stands the predicate half resolves
        // to nothing at all - see Resolve.
        public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

        public int Count => Members.Count;
    }
}
