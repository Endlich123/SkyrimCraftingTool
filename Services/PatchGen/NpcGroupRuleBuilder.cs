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

            var clauses = BuildFilterClauses(ruleGroup, problems, out bool anyInexpressible);

            // What those clauses alone would reach. Resolved through the same resolver the membership
            // came from, so the comparison below is between two answers to the same question.
            var reach = clauses.Count == 0
                ? new List<NpcRecord>()
                : resolver.Resolve(Expressible(ruleGroup)).Members.ToList();

            var memberKeys = members.Select(m => m.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var reachKeys = reach.Select(m => m.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);

            var tooMany = reach.Where(n => !memberKeys.Contains(n.Key)).ToList();
            var missed = members.Where(n => !reachKeys.Contains(n.Key)).ToList();

            if (tooMany.Count > 0)
            {
                clauses.Add("filterByNpcsExcluded=" + string.Join(",", tooMany.Select(n => Target(n.Key))));

                problems.Add($"{tooMany.Count} NPC(s) named in filterByNpcsExcluded - the filter reaches " +
                             "them but the group does not" +
                             (anyInexpressible ? ", because not every clause has a filter of its own" : "") +
                             ". A mod added later is only covered after the next scan.");
            }

            if (tooMany.Count > LongListThreshold)
                problems.Add($"that exclusion list is long ({tooMany.Count} NPCs) - a narrower predicate " +
                             "would produce a shorter rule.");

            // ---- the operations ---------------------------------------------------------------
            var operations = BuildOperations(group, members, stats, problems);

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
            }

            // ---- section 5.1: the second rule -------------------------------------------------
            //
            // Every filter of one rule is ANDed, so "this predicate OR these NPCs" cannot be said in
            // one line. The NPCs the filter cannot reach - hand-added members, and everything else when
            // there is no expressible predicate at all - get their own rule with the same payload.
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

                if (clauses.Count > 0)
                    problems.Add($"{missed.Count} NPC(s) need a second rule with the same values - " +
                                 "one rule cannot say 'this predicate or these NPCs' (section 5.1)");
            }

            return new NpcGroupRuleSet(rules, problems, members);
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

        private static List<string> BuildFilterClauses(
            NpcGroup group, List<string> problems, out bool anyInexpressible)
        {
            var clauses = new List<string>();
            anyInexpressible = false;

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

                clauses.Add($"{directive}={string.Join(",", pair.Select(p => p.Value))}");
            }

            foreach (var flag in group.Predicates.Where(IsFlag))
            {
                if (!FlagDirectives.TryGetValue(flag.Value ?? "", out var directive))
                {
                    anyInexpressible = true;
                    continue;
                }

                bool wanted = !string.Equals(flag.Mode, NpcGroupMode.Exclude, StringComparison.OrdinalIgnoreCase);
                clauses.Add($"{directive}={(wanted ? "true" : "false")}");
            }

            return clauses;
        }

        // The group reduced to the clauses a rule can express - the predicate the RULE stands for, as
        // opposed to the one the user wrote. Resolving this is what says how far the rule reaches.
        // THE FIX FOR THE BOUNDS THAT LEAKED, measured in game on 2026-09-27.
        //
        // Two groups scaling with the player, "Draugr 02" bounded 6-8 and "Bandit 01" bounded 1-5, both
        // came out with whichever pair SkyPatcher read LAST - in one file or in two, it made no
        // difference. Adding the OTHER group's include terms to this rule's exclusion list fixed it.
        //
        // Which says the bounds are not global after all: SkyPatcher appears to apply calcLevelMin/Max
        // to every NPC it has already switched to PcLevelMult, honouring the rule's exclusion list but
        // no longer its inclusion filter. So every scaling group has to say out loud that it does not
        // mean the other scaling groups.
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
                        if (!IsFlag(clause))
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

        private static NpcGroup Expressible(NpcGroup group)
        {
            var copy = new NpcGroup { Name = group.Name };

            foreach (var clause in group.Predicates)
            {
                bool expressible = IsFlag(clause)
                    ? FlagDirectives.ContainsKey(clause.Value ?? "")
                    : TryDirective((clause.Axis ?? "").ToLowerInvariant(), clause.Mode ?? "", out _);

                if (expressible) copy.Predicates.Add(clause);
            }

            return copy;
        }

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
            NpcGroup group, IReadOnlyList<NpcRecord> members, NpcStatResolver? stats, List<string> problems)
        {
            var operations = new List<string>();
            var changeStats = new List<string>();
            var changeSkills = new List<string>();
            var bonuses = new List<string>();

            // Computed BEFORE the loop, because it changes what each value means: a group that scales
            // with the player has no level at patch time, so anything interpolated from a level is
            // reported instead of written. See the two cases in the switch.
            bool scales = group.Values.Any(v =>
                string.Equals(v.Field, "level", StringComparison.OrdinalIgnoreCase)
                && string.Equals(v.Kind, NpcGroupValueKind.Span, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(v.Low) && !string.IsNullOrWhiteSpace(v.High));

            bool anySpan = false;
            bool needsAutoCalcOff = false;

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

                    // NOT WRITTEN AT ALL when the group scales with the player, and that is a decision
                    // rather than a limitation: a span is interpolated from the NPC's level, SkyPatcher
                    // computes it when the patch is applied, and a scaling NPC has no level yet at that
                    // point. Writing "health=150~250" without a levelRange would put a half-instruction
                    // in the INI; writing one from the scaling bounds would be a guess about what
                    // SkyPatcher does with it. Neither is worth a silent wrong number.
                    case NpcGroupValueKind.Span when scales:
                        problems.Add($"'{field}' is a span, and this group scales with the player - a " +
                                     "span is interpolated from the NPC's level, which a scaling NPC " +
                                     "does not have when the patch is applied. Not written. Use a " +
                                     "single value, or drop the level span.");
                        break;

                    case NpcGroupValueKind.Span when low.Length > 0 && high.Length > 0:
                        changeStats.Add($"{field}={low}~{high}");
                        anySpan = true;
                        needsAutoCalcOff = true;
                        break;

                    case NpcGroupValueKind.Mult when low.Length > 0:
                        changeStats.Add($"{field}Mult={low}");
                        break;

                    // The one SkyPatcher warns about in so many words: "don.t use that on Leveled NPCs,
                    // you have to disable PCLevelMult and set a level with it".
                    case NpcGroupValueKind.Calc when scales:
                        problems.Add($"'{field}' is a per-level value, and SkyPatcher documents that those " +
                                     "must not be used on NPCs that scale with the player. Not written.");
                        break;

                    case NpcGroupValueKind.Calc when low.Length > 0:
                        changeStats.Add($"calc{Capitalise(field)}={low}");
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

            // A span interpolates BETWEEN TWO LEVELS, so it is meaningless without the range it runs
            // over - "health=250~650" alone does not say which NPC gets 250. levelRange therefore comes
            // first, exactly as SkyPatcher's own example writes it
            // (docs/NPC_Patcher.txt: levelRange=1~30:changeStats=health=625~900).
            if (anySpan && !scales)
            {
                var range = LevelRange(group, members, stats);

                if (range == null)
                    problems.Add("a value span needs a level range to run over, and this group's members " +
                                 "have no level range - set a level span, or use a single value instead");
                else
                    operations.Add($"levelRange={range.Value.Low}~{range.Value.High}");

                // THE TRAP A SPAN WALKS INTO, found in game on 2026-09-27: a level span on the group
                // sets the levelRange and NOTHING ELSE - it changes no NPC's level, because level=
                // takes a single number. So a group whose members all sit on ONE level interpolates
                // every one of them to the same point, and the user sees the low end everywhere while
                // the rule looks like it should produce a range.
                //
                // "Bandit 01" is exactly that shape: 44 members, all level 1. Said out loud here,
                // because the alternative is finding out by hunting bandits.
                var actual = stats?.Spread(members, v => v.Level);

                if (actual is { Resolved: > 0 } && actual.Low == actual.High)
                    problems.Add($"every member is level {actual.Low}, so a span can only ever produce " +
                                 "its low end - the range interpolates over the NPC's OWN level, and a " +
                                 "level span on the group does not change it. Use single values here, or " +
                                 "put the span on a group whose members differ in level.");
            }

            // Before changeStats, the way the reference mod orders them.
            operations.AddRange(bonuses);

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
            if (needsAutoCalcOff || changeStats.Count > 0 || changeSkills.Count > 0 || bonuses.Count > 0)
                operations.Add("setAutoCalcStats=false");

            // The second mandatory clause of 5.2. setPcLevelMult=false takes a FALLBACK LEVEL, which is
            // a per-NPC number a group cannot know - so it is taken from the group's own level value
            // when there is one, and reported when there is not. Measured: 654 NPCs scale with the
            // player.
            if (needsAutoCalcOff)
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

            // A level SPAN no longer feeds the levelRange: since 2026-09-27 it means "scales with the
            // player between these two", which is a different instruction entirely - and one that
            // cannot be combined with a level-dependent value (see the check in BuildOperations).
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
