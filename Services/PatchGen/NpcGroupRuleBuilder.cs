using System;
using System.Collections.Generic;
using System.Linq;
using SkyrimCraftingTool.Model;

namespace SkyrimCraftingTool.Services.PatchGen
{
    // Group -> SkyPatcher rules (G5, docs/NPC-Gruppen-Plan.md section 5).
    //
    // ONE BUILDER FOR THE PATCH AND FOR THE PREVIEW. The preview of section 6 is the rule text this
    // produces, run through the same writer the patch uses - so the two cannot drift apart. A preview
    // built by a second code path would eventually promise something the patch does not do, and that
    // is the one failure this screen exists to prevent.
    //
    // THE IDEA THAT MAKES IT EXACT: a rule's filters are what SkyPatcher can express, and they rarely
    // cover a group exactly. So the builder compares two sets - what the group MEANS (the resolver's
    // membership) against what the filter would REACH - and writes the difference out by name:
    //
    //   reach \ members  ->  filterByNpcsExcluded   (named NPCs the filter would wrongly include)
    //   members \ reach  ->  filterByNpcs           (a second rule, section 5.1)
    //
    // Every clause SkyPatcher has no filter for then costs names rather than correctness, and the
    // report says how many. Measured on the real load order on 2026-09-26, the default exclusion of
    // unique NPCs costs 272 names across all 40 seeded groups, and nothing at all for the 23 tier
    // groups - which is why this tool does NOT build the keyword tagger of section 5.3. That tagger is
    // for the case where the predicate cannot be expressed otherwise; here it can, exactly, and a
    // keyword would mean a new KYWD record, a second INI and a dependency on file name ordering.
    //
    // THE ONE THING THIS BUYS AT A PRICE: a named exclusion list is built from the scan, so a mod that
    // adds a unique NPC matching the predicate is not covered until the next scan. That is precisely
    // what the rescan diff of section 8 reports, and it applies to a tagged keyword list just as much.
    public static class NpcGroupRuleBuilder
    {
        // The axis/mode pairs that have a filter, exactly as section 5 lists them. A pair that is not
        // in here costs names instead - see the class note.
        private static readonly Dictionary<(string Axis, string Mode), string> Directives = new()
        {
            [(NpcGroupAxis.Class, NpcGroupMode.Include)] = "filterByClass",
            [(NpcGroupAxis.Class, NpcGroupMode.Exclude)] = "filterByClassExclude",
            [(NpcGroupAxis.Race, NpcGroupMode.Include)] = "filterByRaces",
            [(NpcGroupAxis.Faction, NpcGroupMode.Include)] = "filterByFactions",
            [(NpcGroupAxis.Faction, NpcGroupMode.IncludeOr)] = "filterByFactionsOr",
            [(NpcGroupAxis.Faction, NpcGroupMode.Exclude)] = "filterByFactionsExcluded",
            [(NpcGroupAxis.EditorId, NpcGroupMode.Include)] = "filterByEditorIdContains",
            [(NpcGroupAxis.EditorId, NpcGroupMode.IncludeOr)] = "filterByEditorIdContainsOr",
            [(NpcGroupAxis.EditorId, NpcGroupMode.Exclude)] = "filterByEditorIdContainsExcluded",
            [(NpcGroupAxis.Mod, NpcGroupMode.Include)] = "filterByModNames",
            [(NpcGroupAxis.Gender, NpcGroupMode.Include)] = "filterByGender",
        };

        // The flag axis is one directive per flag, and only these four exist. filterByUnique does not -
        // so excluding the 1.313 unique NPCs is the clause that costs names.
        private static readonly Dictionary<string, string> FlagDirectives = new(StringComparer.OrdinalIgnoreCase)
        {
            ["essential"] = "filterByEssential",
            ["protected"] = "filterByProtected",
            ["autocalcstats"] = "filterByAutoCalc",
            ["pclevelmult"] = "filterByPCLevelMult",
        };

        // A named list longer than this is reported. Not refused: the user may well mean it, and a rule
        // that names 300 NPCs still works. Measured worst case on the seeded groups: 103.
        private const int LongListThreshold = 150;

        public static NpcGroupRuleSet BuildRules(
            NpcGroup? group,
            IEnumerable<NpcRecord>? allNpcs = null,
            NpcStatResolver? stats = null,
            IEnumerable<NpcGroup>? allGroups = null)
        {
            var rules = new List<SkyPatcherRule>();
            var problems = new List<string>();

            if (group == null) return new NpcGroupRuleSet(rules, problems, Array.Empty<NpcRecord>());

            var npcs = allNpcs?.ToList() ?? new List<NpcRecord>();
            var resolver = new NpcGroupResolver(npcs);

            var resolution = resolver.Resolve(group);
            foreach (var warning in resolution.Warnings) problems.Add(warning);

            var members = resolution.Members;

            // ---- the filter half -------------------------------------------------------------
            // The rule filters on more than the group says, when other groups scale too - see
            // ScalingCounterExclusions. Membership is unaffected: that is still group.Predicates alone.
            var ruleGroup = WithScalingCounterExclusions(group, allGroups, problems, members);

            // The reach is resolved as SkyPatcher sees the NPCs: its EditorID filters match the EditorID
            // of the DEFINING plugin, and an override may have renamed it (measured 2026-10-31 - 43 of
            // 113 renamed bandits were missed). Membership above stays on the name the user sees.
            var reachResolver = new NpcGroupResolver(npcs, asSkyPatcherSees: true);

            var clauses = BuildFilterClauses(ruleGroup, reachResolver, problems, out var ruleFilter, out bool anyInexpressible);

            // What those clauses alone would reach. Resolved through the same resolver the membership
            // came from, so the comparison below is between two answers to the same question - and
            // with exactly the predicates the rule carries, one inclusion and the exclusions, where
            // AND and SkyPatcher's OR give the same answer.
            var reach = clauses.Count == 0
                ? new List<NpcRecord>()
                : reachResolver.Resolve(ruleFilter).Members.ToList();

            var memberKeys = members.Select(m => m.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var reachKeys = reach.Select(m => m.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);

            var tooMany = reach.Where(n => !memberKeys.Contains(n.Key)).ToList();
            var missed = members.Where(n => !reachKeys.Contains(n.Key)).ToList();

            if (tooMany.Count > 0)
            {
                clauses.Add("filterByNpcsExcluded=" + string.Join(",", tooMany.Select(n => Target(n.Key))));

                problems.Add($"{tooMany.Count} NPC(s) named in filterByNpcsExcluded - the filter reaches " +
                             "them but the group does not" +
                             (anyInexpressible ? ", because not every clause can be written as a filter" : "") +
                             ". A mod added later is only covered after the next scan.");
            }

            if (tooMany.Count > LongListThreshold)
                problems.Add($"that exclusion list is long ({tooMany.Count} NPCs) - a narrower predicate " +
                             "would produce a shorter rule.");

            // ---- the operations ---------------------------------------------------------------
            var operations = BuildOperations(group, members, stats, problems, out var afterLevel);

            if (operations.Count == 0)
            {
                if (group.Values.Count > 0 || group.Lists.Count > 0)
                    problems.Add("nothing to patch - every value on this group is incomplete");

                return new NpcGroupRuleSet(rules, problems, members);
            }

            if (clauses.Count > 0)
            {
                rules.Add(new SkyPatcherRule
                {
                    FilterClauses = clauses,
                    Operations = operations,
                    Comment = $"{group.Name} - {members.Count} NPC(s)",
                    FilePlugin = FileName,
                });

                AddAfterLevelRule(rules, clauses, afterLevel, group.Name);
            }

            // ---- section 5.1: the second rule -------------------------------------------------
            //
            // The NPCs the filter cannot reach - hand-added members, and everything else when there is
            // no expressible predicate at all - get their own rule with the same payload. SkyPatcher
            // joins different filters with OR (see BuildFilterClauses), so a filterByNpcs inside the
            // rule above might do the same - but that one is not measured, and a rule of its own is
            // certain.
            if (missed.Count > 0)
            {
                rules.Add(new SkyPatcherRule
                {
                    FilterClauses = new[] { "filterByNpcs=" + string.Join(",", missed.Select(n => Target(n.Key))) },
                    Operations = operations,
                    Comment = clauses.Count > 0
                        ? $"{group.Name} - {missed.Count} NPC(s) the filter above cannot reach (section 5.1)"
                        : $"{group.Name} - {missed.Count} NPC(s) named directly",
                    FilePlugin = FileName,
                });

                AddAfterLevelRule(rules, new[] { "filterByNpcs=" + string.Join(",", missed.Select(n => Target(n.Key))) },
                    afterLevel, group.Name);

                if (clauses.Count > 0)
                {
                    int renamed = missed.Count(n => n.IsEditorIdRenamed);
                    problems.Add($"{missed.Count} NPC(s) need a second rule with the same values - " +
                                 "the filter above cannot reach them" +
                                 (renamed > 0
                                     ? $" ({renamed} because an override renamed their EditorID, and SkyPatcher matches the original one)"
                                     : ""));
                }
            }

            return new NpcGroupRuleSet(rules, problems, members);
        }

        // The per-level values of a group that sets a fixed level, on a line of their own BEHIND the line
        // with level=. changeStats reads the level from before level= of the same line, so on one line
        // calc works on the old level. Measured 2026-11-04: level=10 and calcMagicka=200 on one line left
        // all 237 NPCs of a level 1 group at Magicka 0; on two lines 396 of 396 NPCs got 1350.
        private static void AddAfterLevelRule(
            List<SkyPatcherRule> rules, IReadOnlyList<string> clauses, IReadOnlyList<string> afterLevel, string name)
        {
            if (afterLevel.Count == 0) return;

            rules.Add(new SkyPatcherRule
            {
                FilterClauses = clauses,
                Operations = afterLevel,
                Comment = $"{name} - second line, values that must not share the line above",
                FilePlugin = FileName,
            });
        }

        // MEASURED IN GAME ON 2026-09-27, and documented nowhere: calcLevelMin and calcLevelMax are NOT
        // scoped to the rule that carries them. The last pair SkyPatcher reads wins for every NPC that
        // gets PcLevelMult - across rules AND across files.
        //
        // The test that settled it: two groups, "Draugr 02" bounded 6-8 and "Bandit 01" bounded 1-5.
        // With the bandit rule last, both came out 1-5. With the draugr rule last, both came out 6-8.
        // Splitting them into two files changed nothing.
        //
        // Kept as a safety net for the case the counter-exclusions cannot cover: groups that scale to
        // different bounds AND select on an axis with no exclusion filter. Everything else is handled
        // by WithScalingCounterExclusions, so this no longer fires for the ordinary case.
        public static IReadOnlyList<string> ScalingCollisions(IEnumerable<NpcGroup>? groups)
        {
            var scaling = (groups ?? Enumerable.Empty<NpcGroup>())
                .Where(g => g != null && g.Active && Scales(g))
                .ToList();

            if (scaling.Count < 2) return Array.Empty<string>();

            var uncounterable = scaling
                .SelectMany(g => g.Predicates
                    .Where(p => !IsFlag(p)
                        && !string.Equals(p.Axis, NpcGroupAxis.Gender, StringComparison.OrdinalIgnoreCase)
                        && (string.Equals(p.Mode, NpcGroupMode.Include, StringComparison.OrdinalIgnoreCase)
                            || string.Equals(p.Mode, NpcGroupMode.IncludeOr, StringComparison.OrdinalIgnoreCase))
                        && !Directives.ContainsKey(((p.Axis ?? "").ToLowerInvariant(), NpcGroupMode.Exclude)))
                    .Select(p => (Group: g.Name, Axis: (p.Axis ?? "").ToLowerInvariant())))
                .Distinct()
                .ToList();

            if (uncounterable.Count == 0) return Array.Empty<string>();

            return new[]
            {
                $"{scaling.Count} groups scale with the player level, and " +
                string.Join(", ", uncounterable.Select(u => $"'{u.Group}' selects by {u.Axis}")) +
                " - that axis has no exclusion filter, so their level bounds can leak into each other. " +
                "Give the scaling groups the same bounds, or select them by class, faction or EditorID.",
            };
        }

        // The file every group rule is written into. Not a plugin name: a group filters across the whole
        // load order, so there is no plugin whose presence should decide whether the rule is read.
        //
        // THE LEADING "0 " IS THE PRECEDENCE. Group rules and the single-NPC rules land in the same
        // folder, and section 5.3 states what SkyPatcher does with that: alphabetical order is execution
        // order. A file read later therefore has the last word on a field both of them write - so the
        // group file is named to sort FIRST, which makes a hand edit on one NPC beat the group it happens
        // to fall into. The other way round, editing a single NPC would look broken for every NPC that is
        // also in a group.
        //
        // Not verified in game: that alphabetical order decides, section 5.3 says; that the later file
        // wins a collision is the obvious reading of it and nothing more.
        public const string FileName = "0 SkyrimCraftingTool - NPC groups";

        // The preview of section 6: the rules as the writer will put them in the INI, plus what they
        // cost. Same builder, so the screen cannot promise something the patch does not do.
        public static NpcGroupRulePreview Build(
            NpcGroup? group,
            IEnumerable<NpcRecord>? allNpcs = null,
            NpcStatResolver? stats = null,
            IEnumerable<NpcGroup>? allGroups = null)
        {
            var set = BuildRules(group, allNpcs, stats, allGroups);

            return new NpcGroupRulePreview
            {
                Text = SkyPatcherIniWriter.Write(set.Rules).TrimEnd('\n'),
                Problems = set.Problems,
                RuleCount = set.Rules.Count,
            };
        }

        // ---- the filter clauses ------------------------------------------------------------------

        // ONE INCLUSION FILTER PER RULE. Measured in game on 2026-10-31 (runtime dump, five test rules
        // each marking its matches with its own skill value): SkyPatcher joins DIFFERENT filters of
        // one rule with OR, not AND.
        //
        //   filterByEditorIdContains=OREO_EncBandit10                        192 of 192 expected
        //   filterByClass=02F202                                             441 of 445
        //   filterByEditorIdContains=OREO_EncBandit10:filterByProtected=false  24 386 - every unprotected NPC
        //   filterByRaces=NordRace:filterByEditorIdContains=OREO_EncBandit10    3 960 - every Nord, plus the bandits
        //   filterByRaces=NordRace:filterByClass=02F202                         4 224 - every Nord, plus the class
        //
        // Only the ...Excluded filters subtract (filterByNpcsExcluded and filterByEditorIdContainsExcluded
        // measured). The docs hint at it in their first line - the filters "work independent from
        // each other". The rules this wrote before reached about 23 800 NPCs outside their groups.
        //
        // So the rule carries the one inclusion that reaches fewest NPCs beside the exclusions, and every
        // other inclusion and every flag (filterByEssential=false selects all non-essential NPCs - it
        // does not narrow) is paid for by names, the way clauses with no filter already are.
        private static List<string> BuildFilterClauses(
            NpcGroup group, NpcGroupResolver resolver, List<string> problems,
            out NpcGroup ruleFilter, out bool anyInexpressible)
        {
            var clauses = new List<string>();
            ruleFilter = new NpcGroup { Name = group.Name };
            anyInexpressible = false;

            var inclusions = new List<(string Text, List<NpcGroupPredicate> Clauses)>();
            var exclusions = new List<(string Text, List<NpcGroupPredicate> Clauses)>();

            // Keyed by directive: include and includeOr can share one (an axis an NPC holds once).
            var byDirective = new Dictionary<string, List<NpcGroupPredicate>>();
            var order = new List<string>();

            foreach (var pair in group.Predicates
                         .Where(p => !IsFlag(p))
                         .GroupBy(p => (Axis: (p.Axis ?? "").ToLowerInvariant(), Mode: p.Mode ?? "")))
            {
                if (!TryDirective(pair.Key.Axis, pair.Key.Mode, out var directive))
                {
                    anyInexpressible = true;
                    problems.Add($"'{pair.Key.Axis} {pair.Key.Mode}' has no SkyPatcher filter of its own");
                    continue;
                }

                if (!byDirective.TryGetValue(directive, out var list))
                {
                    byDirective[directive] = list = new List<NpcGroupPredicate>();
                    order.Add(directive);
                }
                list.AddRange(pair);
            }

            foreach (var directive in order)
            {
                var predicates = byDirective[directive];
                var text = $"{directive}={string.Join(",", predicates.Select(p => p.Value))}";
                bool exclude = predicates.All(p => string.Equals(p.Mode, NpcGroupMode.Exclude, StringComparison.OrdinalIgnoreCase));

                (exclude ? exclusions : inclusions).Add((text, predicates));
            }

            var flags = new List<(string Text, List<NpcGroupPredicate> Clauses)>();
            foreach (var flag in group.Predicates.Where(IsFlag))
            {
                if (!FlagDirectives.TryGetValue(flag.Value ?? "", out var directive))
                {
                    anyInexpressible = true;
                    continue;
                }

                bool wanted = !string.Equals(flag.Mode, NpcGroupMode.Exclude, StringComparison.OrdinalIgnoreCase);
                flags.Add(($"{directive}={(wanted ? "true" : "false")}", new List<NpcGroupPredicate> { flag }));
            }

            // A flag is a selector too, so it can carry the rule - but only when nothing else can.
            var candidates = inclusions.Count > 0 ? inclusions : flags;

            // No inclusion at all: a rule of exclusions alone says nothing SkyPatcher documents, so
            // there is no filter line - the members are named instead (the second rule).
            if (candidates.Count == 0)
            {
                if (exclusions.Count > 0) anyInexpressible = true;
                return clauses;
            }

            var exclusionPredicates = exclusions.SelectMany(e => e.Clauses).ToList();
            var chosen = candidates
                .Select(c =>
                {
                    var probe = new NpcGroup { Name = group.Name };
                    probe.Predicates.AddRange(c.Clauses);
                    probe.Predicates.AddRange(exclusionPredicates);
                    return (Candidate: c, Probe: probe, Reach: resolver.Resolve(probe).Count);
                })
                .OrderBy(x => x.Reach)
                .First();

            foreach (var dropped in inclusions.Concat(flags).Where(c => !ReferenceEquals(c.Clauses, chosen.Candidate.Clauses)))
            {
                anyInexpressible = true;

                // Flags, gender and mod narrow since SkyPatcher 8.0.1 (measured 2026-11-04). They stay out
                // of the rule because 8.0.0 read a flag differently, and a name works on every version.
                string axis = (dropped.Clauses[0].Axis ?? "").ToLowerInvariant();
                bool wouldNarrow = axis is NpcGroupAxis.Flag or NpcGroupAxis.Gender or NpcGroupAxis.Mod;

                problems.Add(wouldNarrow
                    ? $"'{dropped.Text}' is not written as a filter - this rule filters by " +
                      $"'{chosen.Candidate.Text}' and the difference is named, which works on every " +
                      "SkyPatcher version"
                    : $"'{dropped.Text}' is not written as a filter - SkyPatcher joins the filters " +
                      $"of one rule with OR, so this rule filters by '{chosen.Candidate.Text}' alone " +
                      "and the difference is named");
            }

            clauses.Add(chosen.Candidate.Text);
            clauses.AddRange(exclusions.Select(e => e.Text));
            ruleFilter = chosen.Probe;
            return clauses;
        }

        // THE FIX FOR THE BOUNDS THAT LEAKED, measured in game on 2026-09-27.
        //
        // Most likely the same OR as in BuildFilterClauses: the seeded groups carried flag filters
        // (filterByEssential=false, filterByProtected=false), and each of those alone reached nearly
        // every NPC - so the last rule's bounds landed on everybody. With one inclusion per rule the
        // counter-exclusions should no longer be needed; they stay until a dump shows it, since an
        // exclusion that bites nobody costs nothing.
        //
        // Two groups scaling with the player, "Draugr 02" bounded 6-8 and "Bandit 01" bounded 1-5, both
        // came out with whichever pair SkyPatcher read LAST - in one file or in two, it made no
        // difference. Adding the OTHER group's include terms to this rule's exclusion list fixed it.
        //
        // The reading back then: SkyPatcher applies the bounds honouring the rule's exclusion list but
        // not its inclusion filter - which is the OR above, seen through one field only.
        //
        // Only between groups that BOTH scale - a group with fixed levels never enters that pass - and
        // only on the three axes that have an exclusion directive. Race, mod and gender have none, and
        // an include on one of those is reported rather than silently left to leak.
        private static NpcGroup WithScalingCounterExclusions(
            NpcGroup group, IEnumerable<NpcGroup>? allGroups, List<string> problems,
            IReadOnlyList<NpcRecord>? members = null)
        {
            if (!Scales(group) || allGroups == null) return group;

            var others = allGroups
                .Where(g => g != null && g.Active && !ReferenceEquals(g, group) && Scales(g))
                .ToList();

            if (others.Count == 0) return group;

            var copy = new NpcGroup { Id = group.Id, Name = group.Name };
            copy.Predicates.AddRange(group.Predicates);
            copy.Overrides.AddRange(group.Overrides);
            copy.Values.AddRange(group.Values);
            copy.Lists.AddRange(group.Lists);

            foreach (var other in others)
            {
                foreach (var clause in other.Predicates)
                {
                    if (!string.Equals(clause.Mode, NpcGroupMode.Include, StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(clause.Mode, NpcGroupMode.IncludeOr, StringComparison.OrdinalIgnoreCase))
                        continue;

                    var axis = (clause.Axis ?? "").ToLowerInvariant();

                    if (!Directives.ContainsKey((axis, NpcGroupMode.Exclude)))
                    {
                        // Gender keeps the bounds apart: 19 and 730 NPCs of one class, each on its own
                        // pair (measured 2026-11-04).
                        if (!IsFlag(clause) && axis != NpcGroupAxis.Gender)
                            problems.Add($"'{other.Name}' also scales and selects by {axis}, which has no " +
                                         "exclusion filter - its level bounds may leak into this group. " +
                                         "Give both groups the same bounds to be safe.");
                        continue;
                    }

                    bool already = copy.Predicates.Any(p =>
                        string.Equals(p.Axis, clause.Axis, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(p.Mode, NpcGroupMode.Exclude, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(p.Value, clause.Value, StringComparison.OrdinalIgnoreCase));

                    if (already) continue;

                    // THE CLAUSE MUST NOT COST THIS GROUP ITS OWN MEMBERS. Two groups can be disjoint in
                    // membership and still overlap in TERMS: "Bandit 03" selects by EditorID, and the
                    // class group that holds the other bandits selects by a class those same NPCs have.
                    // Excluding that class here would cut the rule off from nearly everyone it means.
                    //
                    // So the clause is tried against the members first, and dropped if it would bite.
                    // Then the bounds can still leak, which is worth saying - but a rule that patches
                    // the wrong NPCs is worse than one that patches the right NPCs with a wrong bound.
                    if (WouldHit(clause, members))
                    {
                        problems.Add($"'{other.Name}' also scales and selects by {axis} in a way that " +
                                     "covers members of this group, so it cannot be excluded here - " +
                                     "their level bounds may end up shared. Give both groups the same " +
                                     "bounds if that matters.");
                        continue;
                    }

                    copy.Predicates.Add(new NpcGroupPredicate
                    {
                        Axis = clause.Axis,
                        Mode = NpcGroupMode.Exclude,
                        Value = clause.Value,
                    });
                }
            }

            return copy;
        }

        // Does this one clause match any of the members? Resolved against the members alone, which is
        // the only universe that matters here: the question is not what the clause means in general but
        // whether excluding it would throw out somebody this group is for.
        private static bool WouldHit(NpcGroupPredicate clause, IReadOnlyList<NpcRecord>? members)
        {
            if (members == null || members.Count == 0) return false;

            var probe = new NpcGroup { Name = "probe" };
            probe.Predicates.Add(new NpcGroupPredicate
            {
                Axis = clause.Axis,
                Mode = NpcGroupMode.Include,
                Value = clause.Value,
            });

            return new NpcGroupResolver(members).Resolve(probe).Count > 0;
        }

        private static bool Scales(NpcGroup group) => group.Values.Any(v =>
            string.Equals(v.Field, "level", StringComparison.OrdinalIgnoreCase)
            && string.Equals(v.Kind, NpcGroupValueKind.Span, StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(v.Low) && !string.IsNullOrWhiteSpace(v.High));

        private static bool IsFlag(NpcGroupPredicate p)
            => string.Equals(p.Axis, NpcGroupAxis.Flag, StringComparison.OrdinalIgnoreCase);

        // Whether an EXCLUDE on this clause's axis becomes a filter of its own, or has to be paid for by
        // naming NPCs in filterByNpcsExcluded instead. Both reach the game; the difference is the length
        // of the rule and whether a mod installed later is covered without a rescan.
        //
        // Public because NpcGroupDeduplicator has to choose between candidate clauses, and which filters
        // SkyPatcher has is knowledge that belongs here - a second copy of this table would be a second
        // thing to keep in step with docs/NPC_Patcher.txt.
        public static bool ExcludeHasFilter(NpcGroupPredicate? clause)
        {
            if (clause == null) return false;

            return IsFlag(clause)
                ? FlagDirectives.ContainsKey(clause.Value ?? "")
                : Directives.ContainsKey(((clause.Axis ?? "").ToLowerInvariant(), NpcGroupMode.Exclude));
        }

        private static bool TryDirective(string axis, string mode, out string directive)
        {
            if (Directives.TryGetValue((axis, mode), out directive!)) return true;

            // includeOr on an axis whose value an NPC holds exactly once is the same thing as include:
            // "either of these" IS the list filter there.
            if (string.Equals(mode, NpcGroupMode.IncludeOr, StringComparison.OrdinalIgnoreCase)
                && Directives.TryGetValue((axis, NpcGroupMode.Include), out directive!))
                return true;

            directive = "";
            return false;
        }

        // ---- the operations (section 5.2) --------------------------------------------------------

        private static List<string> BuildOperations(
            NpcGroup group, IReadOnlyList<NpcRecord> members, NpcStatResolver? stats, List<string> problems,
            out List<string> afterLevel)
        {
            var operations = new List<string>();
            afterLevel = new List<string>();
            var changeStats = new List<string>();
            var changeSkills = new List<string>();
            var bonuses = new List<string>();

            // Computed BEFORE the loop: a per-level value is refused on a group that scales, and the
            // fallback level of setPcLevelMult=false must not reach a rule that switches the flag on.
            bool scales = group.Values.Any(v =>
                string.Equals(v.Field, "level", StringComparison.OrdinalIgnoreCase)
                && string.Equals(v.Kind, NpcGroupValueKind.Span, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(v.Low) && !string.IsNullOrWhiteSpace(v.High));

            bool anySpan = false;
            bool needsAutoCalcOff = false;

            // A fixed level on this group. Spans stay on the first line, they are meant to read the level
            // the members had. Calc values must see the new one, so they move behind it.
            bool hasFixedLevel = group.Values.Any(v =>
                string.Equals(v.Field, "level", StringComparison.OrdinalIgnoreCase)
                && string.Equals(v.Kind, NpcGroupValueKind.Direct, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(v.Low));

            var behindFirstLine = new List<string>();

            // FOLLOW: AutoCalc stays on, so the game keeps calculating these stats from the level the NPC
            // has in game, and the value of the rule is added on top. The only form that grows with the
            // player. Measured 2026-11-04 at levels 5 to 25: no value 67, 114, 160, 206, 253; health=300~500
            // the same plus 304 at every level. levelRange, calc and AutoCalc off all stay constant.
            bool follows = group.Values.Any(v =>
                Stats.Contains((v.Field ?? "").Trim())
                && string.Equals(v.Kind, NpcGroupValueKind.Follow, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(v.Low));

            // levelRange belongs to the whole changeStats of its line, so a random value beside a span goes
            // on a line of its own (measured 2026-11-04: 396 of 396 NPCs right on both). Computed before the
            // loop: the order of the rows must not decide it. Members on one level give no range.
            bool ruleHasLevelRange = !follows && LevelRange(group, members, stats) != null && group.Values.Any(v =>
                Stats.Contains((v.Field ?? "").Trim())
                && string.Equals(v.Kind, NpcGroupValueKind.Span, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(v.Low) && !string.IsNullOrWhiteSpace(v.High));

            foreach (var value in group.Values.OrderBy(v => v.Field, StringComparer.OrdinalIgnoreCase))
            {
                string field = (value.Field ?? "").Trim();
                if (field.Length == 0) continue;

                string low = (value.Low ?? "").Trim();
                string high = (value.High ?? "").Trim();

                // LEVEL IS NOT A STAT. "level=30" is an operation of its own - the same one the
                // single-NPC builder writes - and changeStats knows only health, magicka and stamina
                // (docs/NPC_Patcher.txt: changeStats lists the three, plus calcX and xMult; level sits
                // in the operation list beside weight and height). Writing it into changeStats produced
                // "changeStats=calcHealth=12,level=30", which SkyPatcher has no reading for.
                if (string.Equals(field, "level", StringComparison.OrdinalIgnoreCase))
                {
                    AppendLevel(value, low, high, operations, problems);
                    continue;
                }

                // Skills are their own operation, and a different one: changeSkills takes plain
                // "skill=value" pairs and nothing else - no range, no multiplier, no per-level value
                // (docs/NPC_Patcher.txt documents exactly "changeSkills=twohanded=45,onehanded=50").
                if (NpcSkillNames.All.Contains(field, StringComparer.OrdinalIgnoreCase))
                {
                    if (follows)
                        problems.Add($"skill '{field}' is not written - with follow the game keeps calculating " +
                                     "the skills as well and would replace it");
                    else
                        AppendSkill(value, field, low, changeSkills, problems);

                    continue;
                }

                if (!Stats.Contains(field))
                {
                    problems.Add($"there is no SkyPatcher operation for '{field}' - it is not written");
                    continue;
                }

                switch ((value.Kind ?? "").ToLowerInvariant())
                {
                    case NpcGroupValueKind.Direct when low.Length > 0:
                        changeStats.Add($"{field}={low}");
                        break;

                    // Also on a group that scales. changeStats reads the level the NPC has BEFORE level=
                    // of the same line. Measured 2026-11-04 (SkyPatcher 8.0.1, 234 NPCs, levelRange=10~50,
                    // health=150~250, beside setPcLevelMult=true and level=1000): all 196 NPCs on a fixed
                    // level landed on the line, and all 234 scaled. The value is a fixed number per NPC.
                    case NpcGroupValueKind.Span or NpcGroupValueKind.Mult or NpcGroupValueKind.Calc when follows:
                        problems.Add($"'{field}' is set to {value.Kind}, which is not measured beside follow. " +
                                     "Not written. Use follow, direct or random on this group.");
                        break;

                    case NpcGroupValueKind.Follow when low.Length > 0:
                        changeStats.Add(high.Length > 0 ? $"{field}={low}~{high}" : $"{field}={low}");
                        break;

                    case NpcGroupValueKind.Span when low.Length > 0 && high.Length > 0:
                        changeStats.Add($"{field}={low}~{high}");
                        anySpan = true;
                        needsAutoCalcOff = true;
                        break;

                    case NpcGroupValueKind.Random when ruleHasLevelRange && low.Length > 0 && high.Length > 0:
                        behindFirstLine.Add($"{field}={low}~{high}");
                        break;

                    // The same text as a span, but without levelRange: each NPC draws its own number,
                    // so the level does not matter and a scaling group can use it too.
                    case NpcGroupValueKind.Random when low.Length > 0 && high.Length > 0:
                        changeStats.Add($"{field}={low}~{high}");
                        break;

                    case NpcGroupValueKind.Mult when low.Length > 0:
                        changeStats.Add($"{field}Mult={low}");
                        break;

                    // What the docs warn about ("do not use the calc entries on NPCs with PCLevelMult"), and
                    // it is about the MEMBERS, not the group. An NPC that scales stores a multiplier as its
                    // level, and calc takes that for a level: measured 2026-11-04, Health up to 20 564. A
                    // member on a fixed level is fine beside scaling (138 of 138 in range), because calc
                    // reads the level from before the rule. A fixed level on the group switches the flag
                    // off first, so that case is fine too.
                    case NpcGroupValueKind.Calc when !hasFixedLevel && members.Any(m => m.UsesPcLevelMult):
                        problems.Add($"'{field}' is a per-level value, and {members.Count(m => m.UsesPcLevelMult)} " +
                                     "member(s) already scale with the player - their level is a multiplier, " +
                                     "so the result would be far too high. Not written. Set a fixed level on " +
                                     "this group, or take those NPCs out.");
                        break;

                    case NpcGroupValueKind.Calc when low.Length > 0:
                        (hasFixedLevel ? behindFirstLine : changeStats).Add($"calc{Capitalise(field)}={low}");
                        needsAutoCalcOff = true;
                        break;

                    // A flat amount on top, and an operation of its own rather than part of
                    // changeStats: "staminaBonus=100:changeStats=health=250~650,calcStamina=10" is how
                    // the reference mod writes it (docs/Referenz, 64 of its 101 NPC rules).
                    case NpcGroupValueKind.Bonus when low.Length > 0:
                        bonuses.Add($"{field}Bonus={low}");
                        break;

                    default:
                        problems.Add($"'{field}' is set to kind '{value.Kind}' but has no complete value");
                        break;
                }
            }

            // A span interpolates BETWEEN TWO LEVELS, so it needs the range it runs over - without one,
            // "health=250~650" is a random value since SkyPatcher 8.0.1 (NpcGroupValueKind.Random).
            // levelRange therefore comes first, exactly as SkyPatcher's own example writes it
            // (docs/NPC_Patcher.txt: levelRange=1~30:changeStats=health=625~900).
            if (anySpan)
            {
                var range = LevelRange(group, members, stats);

                // No range, because the members do not differ in level. The values are still written:
                // without levelRange SkyPatcher 8.0.1 gives each NPC a random number (its changelog,
                // and measured on Bandit 01: 113 NPCs, all level 1, 76 different values). The result is
                // the same as the kind random, so that is what the user is told.
                if (range == null)
                {
                    var actual = stats?.Spread(members, v => v.Level);

                    problems.Add((actual is { Resolved: > 0 } && actual.Low == actual.High
                                     ? $"every member is level {actual.Low}, so this span has no level range to run over"
                                     : "this span has no level range to run over") +
                                 " - each NPC gets a random value instead, drawn anew at every game start, " +
                                 "the same as the kind random");
                }
                else
                {
                    operations.Add($"levelRange={range.Value.Low}~{range.Value.High}");
                }

                // An NPC that scales already stores its multiplier in the level field, so it sits above
                // any range: all 38 of them got the high end in the measurement above. Without scaling
                // on the group, setPcLevelMult=false gives them a fixed level instead.
                int alreadyScaling = scales ? members.Count(m => m.UsesPcLevelMult) : 0;

                if (alreadyScaling > 0)
                    problems.Add($"{alreadyScaling} member(s) already scale with the player, so a span " +
                                 "gives them its high end");
            }

            // Before changeStats, the way the reference mod orders them. A bonus adds to the result of
            // calc, so it goes wherever calc goes.
            if (behindFirstLine.Count > 0)
            {
                afterLevel.AddRange(bonuses);
                afterLevel.Add("changeStats=" + string.Join(",", behindFirstLine));
                afterLevel.Add("setAutoCalcStats=false");
            }
            else
            {
                operations.AddRange(bonuses);
            }

            if (changeStats.Count > 0) operations.Add("changeStats=" + string.Join(",", changeStats));

            // Ordered the way the skill menu shows them rather than the way the rows were typed, so two
            // groups that set the same skills produce the same line - which is what lets a diff of two
            // generated INIs mean something.
            if (changeSkills.Count > 0)
                operations.Add("changeSkills=" + string.Join(",", changeSkills
                    .OrderBy(s => SkillOrder(s.Split('=')[0]))));

            // WITHOUT THIS THE ENGINE TAKES EVERYTHING BACK, and it took three rounds of testing to see
            // why. Measured on 2026-09-27: a group set to scale with the player, with a plain
            // "changeStats=stamina=40", came out of the game with stamina 90, health 71, magicka 114 -
            // and with SKILLS the rule never mentioned changed too (One-Handed 15 -> 13, Destruction
            // 15 -> 18). Both records carry the AutoCalcStats flag, so the engine recomputed the lot
            // from race, class and level when the actor spawned.
            //
            // It used to be written only for a span or a per-level value, where the plan names it as
            // mandatory (section 5.2). That was too narrow: it is mandatory for ANY value this rule
            // sets, because the flag does not care which of them the patch wrote. The reference mod
            // says the same by example - all 101 of its NPC rules carry it (docs/Referenz).
            if (follows)
            {
                int fixedStats = members.Count(m => (m.Flags & (uint)NpcFlag.AutoCalcStats) == 0);

                if (fixedStats > 0)
                    problems.Add($"{fixedStats} member(s) do not have AutoCalc, so the game does not calculate " +
                                 "their stats - they get the value, but it does not grow with the level");
            }
            else if (needsAutoCalcOff || changeStats.Count > 0 || changeSkills.Count > 0 || bonuses.Count > 0)
            {
                operations.Add("setAutoCalcStats=false");
            }

            // The second mandatory clause of 5.2. setPcLevelMult=false takes a FALLBACK LEVEL, which is
            // a per-NPC number a group cannot know - so it is taken from the group's own level value
            // when there is one, and reported when there is not. Measured: 654 NPCs scale with the
            // player. Not on a group that scales: its rule switches the flag on.
            //
            // A fixed level needs it too: with the flag on, level=30 is a multiplier of x0.03. Together
            // the two give exactly that level, in either order (measured 2026-11-04, 63 of 63 and 45 of
            // 45). setPcLevelMult=false=30 alone does not: it is only an upper limit (95 of 190 on 30).
            if ((needsAutoCalcOff || hasFixedLevel) && !scales)
            {
                int scaling = members.Count(m => m.UsesPcLevelMult);
                if (scaling > 0)
                {
                    string? fallback = LevelValue(group);

                    if (fallback != null)
                        operations.Add($"setPcLevelMult=false={fallback}");
                    else
                        problems.Add($"{scaling} member(s) scale with the player level - " +
                                     "setPcLevelMult=false needs a fallback level, so set a level on this " +
                                     "group or those NPCs keep scaling");
                }
            }

            AppendLists(group, operations, problems);
            return operations;
        }

        // The three attributes changeStats knows. Anything else is reported rather than guessed at - the
        // editor offers four fields today, the data model allows any string, and a rule that carries
        // "changeStats=speed=1" would look perfectly fine and do nothing.
        private static readonly HashSet<string> Stats = new(StringComparer.OrdinalIgnoreCase)
        {
            "health", "magicka", "stamina",
        };

        // A skill takes ONE number too. changeSkills is documented as plain pairs and nothing else, so a
        // span, a multiplier or a per-level value has no spelling here - reported rather than written as
        // one of its ends.
        private static void AppendSkill(
            NpcGroupValue value, string field, string low, List<string> changeSkills, List<string> problems)
        {
            if (string.Equals(value.Kind, NpcGroupValueKind.Direct, StringComparison.OrdinalIgnoreCase)
                && low.Length > 0)
            {
                changeSkills.Add($"{field.ToLowerInvariant()}={low}");
                return;
            }

            problems.Add(low.Length == 0
                ? $"skill '{field}' has no value"
                : $"skill '{field}' cannot be set as '{value.Kind}' - changeSkills takes a single number");
        }

        // x1.0 - the NPC follows the player level exactly. The record stores the multiplier times
        // 1000, so 1250 would be x1.25. Not exposed as a setting yet: every measured use wants the
        // plain "same as the player, bounded", and an unlabelled 1000 in a text box invites a 5.
        private const int PlayerLevelMultiplier = 1000;

        private static int SkillOrder(string skill)
        {
            for (int i = 0; i < NpcSkillNames.All.Count; i++)
                if (string.Equals(NpcSkillNames.All[i], skill, StringComparison.OrdinalIgnoreCase)) return i;

            return int.MaxValue;
        }

        // level takes ONE number, so only a direct value becomes an operation. A level SPAN is not a
        // change the patch can make - it is the range the other spans interpolate over (see LevelRange),
        // and saying so is better than writing a level nobody asked for. There is no levelMult and no
        // calcLevel either; calcLevelMin / calcLevelMax exist but bound a player-level multiplier, which
        // is a different thing.
        private static void AppendLevel(
            NpcGroupValue value, string low, string high, List<string> operations, List<string> problems)
        {
            switch ((value.Kind ?? "").ToLowerInvariant())
            {
                case NpcGroupValueKind.Direct when low.Length > 0:
                    operations.Add($"level={low}");
                    break;

                // A LEVEL SPAN MEANS "SCALES WITH THE PLAYER, BETWEEN THESE TWO", and since
                // 2026-09-27 that is something the patch can actually say. Measured in game on the 44
                // EncBandit01 records - all of which inherit their stats from a template:
                //
                //     setPcLevelMult=true=<max>:calcLevelMin=<low>:calcLevelMax=<high>:level=1000
                //
                // Two details that are in no documentation, neither SkyPatcher's own nor its INI:
                // "true" takes a second parameter exactly as "false=50" does, and with the flag on,
                // LEVEL IS THE MULTIPLIER TIMES 1000 - 1000 is x1.0, 1250 is x1.25. A plain "5" there
                // would mean x0.005. That is why this is built from a span rather than typed by hand.
                case NpcGroupValueKind.Span when low.Length > 0 && high.Length > 0:
                    operations.Add($"setPcLevelMult=true={high}");
                    operations.Add($"calcLevelMin={low}");
                    operations.Add($"calcLevelMax={high}");
                    operations.Add($"level={PlayerLevelMultiplier}");
                    break;

                default:
                    problems.Add($"level cannot be set as '{value.Kind}' - only a single value works");
                    break;
            }
        }

        // The range a span interpolates over: what the user asked the levels to become if a level span
        // is set, otherwise what the members' levels ARE. The second is the honest default - it is the
        // same number the editor shows in its "is" column.
        private static (string Low, string High)? LevelRange(
            NpcGroup group, IReadOnlyList<NpcRecord> members, NpcStatResolver? stats)
        {
            var level = group.Values.FirstOrDefault(v =>
                string.Equals(v.Field, "level", StringComparison.OrdinalIgnoreCase));

            // A level SPAN does not feed the levelRange: since 2026-09-27 it means "scales with the
            // player between these two", a different instruction. The range is always the members' own
            // levels, which is what changeStats reads. The resolver has no level for an NPC that scales
            // already, so those do not stretch it.
            if (stats == null) return null;

            var spread = stats.Spread(members, v => v.Level);
            if (spread.Low == null || spread.High == null || spread.Low == spread.High) return null;

            return (spread.Low.Value.ToString(), spread.High.Value.ToString());
        }

        private static string? LevelValue(NpcGroup group)
        {
            var level = group.Values.FirstOrDefault(v =>
                string.Equals(v.Field, "level", StringComparison.OrdinalIgnoreCase));

            return string.IsNullOrWhiteSpace(level?.Low) ? null : level!.Low.Trim();
        }

        private static void AppendLists(NpcGroup group, List<string> operations, List<string> problems)
        {
            foreach (var entry in group.Lists)
            {
                bool add = !string.Equals(entry.Mode, NpcGroupListMode.Remove, StringComparison.OrdinalIgnoreCase);
                string? extra = string.IsNullOrWhiteSpace(entry.Extra) ? null : entry.Extra.Trim();

                string? op = (entry.Kind ?? "").ToLowerInvariant() switch
                {
                    "perk" => add ? "perksToAdd" : null,
                    "spell" => add ? "spellsToAdd" : "spellsToRemove",
                    "item" => add ? "objectsToAdd" : "objectsToRemove",
                    "faction" => add ? "factionsToAdd" : "factionsToRemove",
                    "keyword" => add ? "keywordsToAdd" : "keywordsToRemove",
                    _ => null,
                };

                // An entry with no operation behind it is DROPPED, and until now silently. The one that
                // can actually occur is a perk removal: SkyPatcher has perksToAdd and no perksToRemove,
                // so the entry stores, resolves and previews perfectly and then reaches nothing. The
                // editor does not offer it; a group carried in from elsewhere still can.
                if (op == null)
                {
                    problems.Add(string.Equals(entry.Kind, "perk", StringComparison.OrdinalIgnoreCase)
                        ? $"'{Target(entry.TargetKey)}' is listed as a perk to REMOVE - SkyPatcher has " +
                          "perksToAdd and no perksToRemove, so that line cannot be patched."
                        : $"'{entry.Kind} {entry.Mode}' has no SkyPatcher operation, so that line is " +
                          "not in the rule.");
                    continue;
                }

                // factionsToAdd leaves a member that is already in the faction on its old rank, so a
                // rank is only certain after a remove (measured 2026-11-04, see
                // NpcRuleBuilder.AppendFactionOps). Without a rank there is nothing to enforce, and a
                // remove would reset the ranks members already have.
                if (extra != null && op == "factionsToAdd")
                    operations.Add($"factionsToRemove={Target(entry.TargetKey)}");

                // A rank or a count is part of the operation where it takes one - factionsToAdd and
                // objectsToAdd do, perksToAdd does not.
                operations.Add(extra != null && (op == "factionsToAdd" || op == "objectsToAdd")
                    ? $"{op}={Target(entry.TargetKey)}={extra}"
                    : $"{op}={Target(entry.TargetKey)}");
            }
        }

        // Plugin|FormID as SkyPatcher wants it: the FormID padded to eight digits, the same as every
        // other rule this tool writes.
        private static string Target(string key)
        {
            if (string.IsNullOrWhiteSpace(key)) return "";

            int bar = key.IndexOf('|');
            if (bar < 0) return key;

            return key.Substring(0, bar) + "|" + PatchFormat.FormId8(key.Substring(bar + 1));
        }

        private static string Capitalise(string field)
            => field.Length == 0 ? field : char.ToUpperInvariant(field[0]) + field.Substring(1);
    }

    public sealed record NpcGroupRuleSet(
        IReadOnlyList<SkyPatcherRule> Rules,
        IReadOnlyList<string> Problems,
        IReadOnlyList<NpcRecord> Members)
    {
        public bool HasRules => Rules.Count > 0;
    }

    public sealed class NpcGroupRulePreview
    {
        // The rule text exactly as the INI will carry it - produced by the writer the patch uses.
        public string Text { get; init; } = "";

        // Everything the rules cost or cannot do. This is the half of the preview that earns its place
        // on screen.
        public IReadOnlyList<string> Problems { get; init; } = Array.Empty<string>();

        public int RuleCount { get; init; }

        public bool HasProblems => Problems.Count > 0;
    }
}
