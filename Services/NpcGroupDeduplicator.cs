using System;
using System.Collections.Generic;
using System.Linq;
using SkyrimCraftingTool.Model;
using SkyrimCraftingTool.Services.PatchGen;

namespace SkyrimCraftingTool.Services
{
    // "Exclude what other groups already cover" (docs/NPC-Gruppen-Plan.md sections 3.0.3 and 7).
    //
    // This is the move the pre-fill already makes by hand: the class group EncClassBanditMelee carries
    // twelve `editorid exclude EncBandit0N` lines, because the tier groups own those NPCs. Doing it for
    // a group somebody built themselves is the same operation, and it was the missing half - the
    // overlap block could say "177 members, 165 of them elsewhere" and there was nothing to press.
    //
    // IT WRITES CLAUSES, NEVER A MEMBER LIST. The obvious implementation - one remove-override per
    // shared NPC - would be a stored member list in disguise: correct the day it is written and wrong
    // after the next mod install, which is the single failure this whole design exists to prevent
    // (section 8). A clause keeps meaning the same thing when the load order changes.
    //
    // THE ONE RULE IT WILL NOT BREAK: a clause may only remove NPCs the other group ALSO has. Two
    // groups can overlap in members and still be defined in terms that are far wider - "Bandit 03"
    // selects by EditorID, and the group it shares eight NPCs with selects by a class those eight and
    // 169 others have. Excluding that class would cut the group off from nearly everyone it means, so
    // the clause is tried against the real membership first and dropped if it bites. Same test
    // NpcGroupRuleBuilder.WithScalingCounterExclusions makes, for the same reason.
    public sealed class NpcGroupDeduplicator
    {
        private readonly NpcGroupResolver _resolver;

        public NpcGroupDeduplicator(IEnumerable<NpcRecord>? npcs)
            => _resolver = new NpcGroupResolver(npcs);

        // Works out what to add without touching the group. The caller applies it - so the plan can be
        // reported, and so a group is never rewritten by something that only wanted to look.
        public NpcGroupDedupePlan Plan(NpcGroup? group, IEnumerable<NpcGroup>? others)
        {
            var added = new List<NpcGroupPredicate>();
            var notes = new List<string>();
            var problems = new List<string>();

            if (group == null) return new NpcGroupDedupePlan(added, notes, problems, 0, 0);

            var working = Copy(group);
            int before = Members(working).Count;

            if (before == 0)
            {
                problems.Add("This group has no members, so there is nothing to separate.");
                return new NpcGroupDedupePlan(added, notes, problems, 0, 0);
            }

            var candidates = (others ?? Enumerable.Empty<NpcGroup>())
                .Where(o => o != null && o.Active && !ReferenceEquals(o, group))
                .ToList();

            // How many NPCs each other group ended up keeping, summed over the passes below.
            var leftTo = new List<(string Name, int Count)>();

            // MORE THAN ONE PASS, and this is not a refinement - it is half the result. Measured on the
            // real load order on 2026-09-28: one pass wrote six lines and left 48 of 96 duplicates in
            // place, because `editorid exclude EncBandit01` is a SUBSTRING and also catches
            // DLC2EncBandit01*. Those eight belonged to a different group, so the line was refused as
            // collateral - correctly. Once the DLC2 line is in, the same clause takes only its own eight
            // and is safe. A second pass finds it; the first cannot.
            //
            // It terminates because every pass either adds a clause that AlreadyExcluded then blocks, or
            // adds nothing and stops.
            int maxPasses = 1 + candidates.Sum(o => CandidateClauses(o).Count());

            for (int pass = 0; pass < maxPasses; pass++)
            {
                // Only the last pass's REASONS survive: a clause refused as collateral in pass one and
                // accepted in pass two must not still be listed as impossible. What each group was left
                // is the opposite - it accumulates, because the last pass is by definition the one that
                // did nothing.
                problems.Clear();

                var passAdded = RunPass(working, candidates, leftTo, problems);
                if (passAdded.Count == 0) break;

                added.AddRange(passAdded);
            }

            foreach (var (name, count) in leftTo
                         .GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                         .Select(g => (g.Key, g.Sum(x => x.Count))))
            {
                notes.Add($"'{name}': {count} NPC(s) left to it.");
            }

            int after = Members(working).Count;

            if (added.Count == 0 && problems.Count == 0)
                notes.Add("This group shares no NPC with any other group - nothing to exclude.");

            // Said once, at the end, rather than per clause: a rule whose exclusion has no filter of its
            // own still works - it names the NPCs instead - and the difference only matters the next time
            // a mod adds one.
            int named = added.Count(c => !NpcGroupRuleBuilder.ExcludeHasFilter(c));
            if (named > 0)
            {
                problems.Add($"{named} of the added lines have no SkyPatcher filter of their own, so the " +
                             "rule will name those NPCs instead. They are only covered again after the " +
                             "next scan.");
            }

            return new NpcGroupDedupePlan(added, notes, problems, before, after);
        }

        // One sweep over the other groups. Appends to working, and says what it could not do.
        private List<NpcGroupPredicate> RunPass(
            NpcGroup working,
            List<NpcGroup> candidates,
            List<(string Name, int Count)> leftTo,
            List<string> problems)
        {
            var added = new List<NpcGroupPredicate>();
            var current = Members(working);

            // Biggest overlap first: the largest duplicate is the one worth removing, and removing it
            // often takes the smaller ones with it - the twelve tier groups share their NPCs with the
            // same class group, not with each other.
            var ranked = candidates
                .Select(o => (Group: o, Keys: Members(o)))
                .Select(x => (x.Group, x.Keys, Shared: x.Keys.Count(current.Contains)))
                .Where(x => x.Shared > 0)
                .OrderByDescending(x => x.Shared)
                .ThenBy(x => x.Group.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var other in ranked)
            {
                var shared = other.Keys.Where(current.Contains).ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (shared.Count == 0) continue;

                // Nothing to write: separating the two would leave this group empty, which is not a
                // filter but a deletion. It is the shape of the real case - a hand-made group that is
                // the pre-filled one again without its exclusions - and the answer is a decision, not a
                // clause.
                if (shared.Count == current.Count)
                {
                    problems.Add($"Every one of these {current.Count} NPCs is also in '{other.Group.Name}' - " +
                                 "this group is contained in that one. Excluding it would leave nothing, " +
                                 "so delete one of the two or narrow this one first.");
                    continue;
                }

                int removedHere = 0;

                foreach (var clause in CandidateClauses(other.Group))
                {
                    if (AlreadyExcluded(working, clause)) continue;

                    var trial = Copy(working);
                    trial.Predicates.Add(new NpcGroupPredicate
                    {
                        Axis = clause.Axis,
                        Mode = NpcGroupMode.Exclude,
                        Value = clause.Value,

                        // Stamped, so one press can be taken back as one press and a second press can
                        // replace its own answer instead of piling a new one on top.
                        Origin = NpcGroupPredicateOrigin.AutoExclude,
                    });

                    var after = Members(trial);

                    var removed = current.Where(k => !after.Contains(k)).ToList();
                    if (removed.Count == 0) continue;

                    if (after.Count == 0)
                    {
                        problems.Add($"Excluding '{Describe(clause)}' would empty this group, so it was " +
                                     "left out.");
                        continue;
                    }

                    // THE TEST. Everything this clause takes away has to be somebody else's too.
                    var collateral = removed.Where(k => !shared.Contains(k)).ToList();
                    if (collateral.Count > 0)
                    {
                        problems.Add($"'{other.Group.Name}' selects by {Describe(clause)}, which also covers " +
                                     $"{collateral.Count} NPC(s) that are only in this group - so it cannot " +
                                     "be excluded as a filter line.");
                        continue;
                    }

                    var clauseAdded = trial.Predicates[^1];
                    working.Predicates.Add(clauseAdded);
                    added.Add(clauseAdded);

                    current = after;
                    removedHere += removed.Count;

                    shared = other.Keys.Where(current.Contains).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    if (shared.Count == 0) break;
                }

                if (removedHere > 0) leftTo.Add((other.Group.Name, removedHere));

                // Not gated on having removed something: a group whose every candidate clause was already
                // in place and that still shares NPCs would otherwise pass in silence, which is the one
                // outcome this button must never produce.
                if (shared.Count > 0)
                {
                    problems.Add($"'{other.Group.Name}' still shares {shared.Count} NPC(s) with this group, " +
                                 "which no filter line can separate. Take them out by hand if it matters.");
                }
            }

            return added;
        }

        // The other group's include terms, the ones that say what it IS. Its own exclusions are its
        // business, and copying them here would import a decision rather than avoid a collision.
        //
        // Ordered so the clauses that become a real SkyPatcher filter are tried first: two clauses may
        // separate the same NPCs, and the one that patches as a filter is worth more than the one that
        // patches as a list of names.
        private static IEnumerable<NpcGroupPredicate> CandidateClauses(NpcGroup other)
            => other.Predicates
                .Where(p => !string.Equals(p.Mode, NpcGroupMode.Exclude, StringComparison.OrdinalIgnoreCase)
                            && !string.IsNullOrWhiteSpace(p.Value))
                .OrderByDescending(NpcGroupRuleBuilder.ExcludeHasFilter)
                .ToList();

        private static bool AlreadyExcluded(NpcGroup group, NpcGroupPredicate clause)
            => group.Predicates.Any(p =>
                string.Equals(p.Axis, clause.Axis, StringComparison.OrdinalIgnoreCase)
                && string.Equals(p.Mode, NpcGroupMode.Exclude, StringComparison.OrdinalIgnoreCase)
                && string.Equals(p.Value, clause.Value, StringComparison.OrdinalIgnoreCase));

        private static string Describe(NpcGroupPredicate clause)
            => $"{(clause.Axis ?? "").ToLowerInvariant()} {clause.Value}";

        // Resolved through the real resolver rather than re-implemented here. The seven axes do not all
        // combine the same way (NpcGroupResolver.MultiValued), and a second reading of them would
        // disagree with the member count on screen sooner or later.
        private HashSet<string> Members(NpcGroup group)
            => _resolver.Resolve(group).Members
                .Select(m => m.Key)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // The overrides come along: a hand-added NPC is a member, and a clause that appears to remove one
        // in fact does not - the override puts it back. Leaving them out would make the test above
        // reject clauses that are perfectly safe.
        private static NpcGroup Copy(NpcGroup group)
        {
            var copy = new NpcGroup { Id = group.Id, Name = group.Name };
            copy.Predicates.AddRange(group.Predicates);
            copy.Overrides.AddRange(group.Overrides);
            return copy;
        }
    }

    // What the auto-check would do, and what it could not do. Both halves matter: a button that silently
    // solved eleven of thirteen overlaps and said nothing about the other two would be worse than no
    // button, because the two left over are the ones that need a decision.
    public sealed class NpcGroupDedupePlan
    {
        public NpcGroupDedupePlan(
            IReadOnlyList<NpcGroupPredicate> added,
            IReadOnlyList<string> notes,
            IReadOnlyList<string> problems,
            int membersBefore,
            int membersAfter)
        {
            Added = added;
            Notes = notes;
            Problems = problems;
            MembersBefore = membersBefore;
            MembersAfter = membersAfter;
        }

        public IReadOnlyList<NpcGroupPredicate> Added { get; }
        public IReadOnlyList<string> Notes { get; }
        public IReadOnlyList<string> Problems { get; }

        public int MembersBefore { get; }
        public int MembersAfter { get; }

        public int Removed => Math.Max(0, MembersBefore - MembersAfter);

        public bool AnythingToDo => Added.Count > 0;

        public string Headline => Added.Count == 0
            ? (Problems.Count > 0
                ? "Nothing could be excluded as a filter line."
                : "Nothing to exclude - this group shares no NPC with another.")
            : $"{Added.Count} filter line(s) added, {Removed} NPC(s) left to the groups that already " +
              $"had them: {MembersBefore} -> {MembersAfter} members.";
    }
}
