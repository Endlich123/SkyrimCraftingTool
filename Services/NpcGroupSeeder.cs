using System;
using System.Collections.Generic;
using System.Linq;
using SkyrimCraftingTool.Model;

namespace SkyrimCraftingTool.Services
{
    // Automatic pre-fill (G3, docs/NPC-Gruppen-Plan.md section 7): turn a scanned load order into a
    // handful of groups worth editing, so nobody has to build 6.642 NPCs' worth of filter sets by hand.
    //
    // Two stages, in this order, each taking only what the previous left:
    //
    //   1. THE EDITORID SCHEME. Vanilla names its encounter NPCs Enc<Faction><NN><Role>, and the
    //      <NN> is the tier - which is exactly the unit somebody wants to edit ("bandits, tier 3").
    //      Measured on the real load order on 2026-09-26: 2.178 NPCs carry the scheme, in 342 distinct
    //      tokens. 342 groups is not a pre-fill, it is a second problem, so only tokens of at least
    //      MinTierGroup members become groups - 23 of them at the default, covering 920 NPCs.
    //   2. THE CLASS, for whatever stage 1 left. 4.464 NPCs in 155 classes, of which the top handful
    //      carry most of the weight (1.289 and 949 in the two biggest).
    //
    // The faction is deliberately NOT a stage. It cannot be one: 1.393 NPCs have no faction and 2.986
    // have two to twelve, so a faction level would be empty for some and ambiguous for most
    // (section 1.1). It stays a filter INSIDE a group.
    //
    // THE PROPERTY THAT MATTERS MORE THAN COVERAGE: the groups must not overlap. Two rules matching
    // one NPC means the same field written twice with the winner decided by file name, which is not a
    // patch but a coin toss. Every exclude clause this class writes is there to keep that from
    // happening, and SeedResult.Overlaps is the check.
    public sealed class NpcGroupSeeder
    {
        private readonly List<NpcRecord> _npcs;
        private readonly INpcLabels _labels;
        private readonly NpcGroupSeederOptions _options;

        public NpcGroupSeeder(
            IEnumerable<NpcRecord>? npcs,
            INpcLabels? labels = null,
            NpcGroupSeederOptions? options = null)
        {
            _npcs = npcs?.ToList() ?? new List<NpcRecord>();
            _labels = labels ?? NpcLabels.Empty;
            _options = options ?? new NpcGroupSeederOptions();
        }

        // The tier token of an EditorID, or null if it does not follow the scheme: everything up to and
        // including the two digits that follow the faction name. "EncBandit03Melee1HNordM" ->
        // "EncBandit03", "DLC2EncBandit03Melee1HDarkElf01M" -> "DLC2EncBandit03".
        //
        // THE PREFIX IS PART OF THE TOKEN, not stripped: DLC2, DLC1, DA13, E3Demo, dunFortSnowhawk and
        // 00SP all appear in front of Enc on the real load order, and the NPCs behind each are a
        // separate set with separate stats. Stripping the prefix would merge Solstheim's bandits into
        // Skyrim's without anyone asking.
        //
        // The first two digits after the letters, not the last: DLC2EncBandit03Melee1HDarkElf01M has a
        // second number in it, and taking that one would produce a token per role variant.
        public static string? TierToken(string? editorId)
        {
            if (string.IsNullOrWhiteSpace(editorId)) return null;

            int enc = editorId.IndexOf("Enc", StringComparison.OrdinalIgnoreCase);
            if (enc < 0) return null;

            int i = enc + 3;
            int letterStart = i;
            while (i < editorId.Length && char.IsLetter(editorId[i])) i++;

            // "Enc" followed straight by a digit is not the scheme - there is no faction name to group
            // by, and the token would be the same for unrelated NPCs.
            if (i == letterStart) return null;

            if (i + 1 >= editorId.Length) return null;
            if (!char.IsDigit(editorId[i]) || !char.IsDigit(editorId[i + 1])) return null;

            return editorId.Substring(0, i + 2);
        }

        public NpcSeedResult Seed()
        {
            var resolver = new NpcGroupResolver(_npcs);
            var groups = new List<NpcGroup>();
            var notes = new List<string>();

            // ---- stage 1: the EditorID scheme --------------------------------------------------

            var byToken = _npcs
                .Select(n => (Npc: n, Token: TierToken(n.EditorID)))
                .Where(t => t.Token != null)
                .GroupBy(t => t.Token!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Select(t => t.Npc).ToList(), StringComparer.OrdinalIgnoreCase);

            var allTokens = byToken.Keys.ToList();

            var seededTokens = byToken
                .Where(kv => kv.Value.Count >= _options.MinTierGroup)
                .OrderByDescending(kv => kv.Value.Count)
                .ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                .Select(kv => kv.Key)
                .ToList();

            int droppedTier = 0;

            foreach (var token in seededTokens)
            {
                var group = new NpcGroup
                {
                    Name = TierName(token),
                    Origin = NpcGroupOrigin.Auto,
                    Seed = SeedTier + token,
                };

                group.Predicates.Add(Clause(NpcGroupAxis.EditorId, token, NpcGroupMode.Include));

                // The scheme is matched by SUBSTRING, because that is what filterByEditorIdContains
                // does - so "EncBandit03" also catches DLC2EncBandit03*, and on the real load order
                // that is 79 NPCs where the plan counted 54. Every longer token that contains this one
                // is excluded, which both restores the intended set and keeps the two groups apart.
                // Measured: 31 tokens need this, with prefixes DLC1, DLC2, DA13, E3Demo, 00SP,
                // dunFortSnowhawk - and one vanilla typo, DLDC2EncDragon03.
                foreach (var longer in allTokens
                             .Where(t => !string.Equals(t, token, StringComparison.OrdinalIgnoreCase)
                                         && t.Contains(token, StringComparison.OrdinalIgnoreCase))
                             .OrderBy(t => t, StringComparer.OrdinalIgnoreCase))
                {
                    group.Predicates.Add(Clause(NpcGroupAxis.EditorId, longer, NpcGroupMode.Exclude));
                }

                AddDefaultExclusions(group);

                // THE THRESHOLD IS CHECKED ON WHAT THE PREDICATE RESOLVES TO, not on how many NPCs
                // carry the token. Measured before this was added: seven class groups passed a
                // threshold of 40 raw members and then resolved to 0, 1, 2, 3, 16, 20 and 33 - the
                // exclude clauses had taken the rest. A group that promises 40 and delivers none is
                // worse than no group, because the user's next question is which of the two numbers
                // lied.
                if (!Keep(resolver, group, _options.MinTierGroup))
                {
                    droppedTier++;
                    continue;
                }

                groups.Add(group);
            }

            int belowThreshold = byToken.Where(kv => kv.Value.Count < _options.MinTierGroup).Sum(kv => kv.Value.Count);
            notes.Add($"{groups.Count} tier groups from {allTokens.Count} tokens; " +
                      $"{belowThreshold} NPCs sit in tokens below {_options.MinTierGroup} members" +
                      (droppedTier > 0 ? $", {droppedTier} tokens fell below it after the exclusions" : ""));

            // ---- stage 2: the class, for what is left -------------------------------------------

            var coveredByTier = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var tierMembersByGroup = new Dictionary<string, List<NpcRecord>>(StringComparer.OrdinalIgnoreCase);

            foreach (var group in groups)
            {
                var members = resolver.Resolve(group).Members.ToList();
                tierMembersByGroup[group.Seed] = members;
                foreach (var member in members) coveredByTier.Add(member.Key);
            }

            var rest = _npcs.Where(n => !coveredByTier.Contains(n.Key)).ToList();

            var restByClass = rest
                .Where(n => !string.IsNullOrWhiteSpace(n.ClassKey))
                .GroupBy(n => n.ClassKey, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() >= _options.MinClassGroup)
                .OrderByDescending(g => g.Count())
                .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                .ToList();

            int droppedClass = 0;
            int classGroups = 0;

            foreach (var byClass in restByClass)
            {
                var group = new NpcGroup
                {
                    Name = ClassName(byClass.Key),
                    Origin = NpcGroupOrigin.Auto,
                    Seed = SeedClass + byClass.Key,
                };

                group.Predicates.Add(Clause(NpcGroupAxis.Class, byClass.Key, NpcGroupMode.Include));

                // The class of an NPC says nothing about whether stage 1 already took it, so a plain
                // "class X" group would overlap the tier groups - EncClassBanditMelee is the class of
                // the Enc bandits AND of 201 NPCs the scheme does not name. Each tier group whose
                // members share this class is excluded by its token, which is the one thing that makes
                // "the rest" an expressible filter rather than a list.
                foreach (var seed in tierMembersByGroup
                             .Where(kv => kv.Value.Any(m =>
                                 string.Equals(m.ClassKey, byClass.Key, StringComparison.OrdinalIgnoreCase)))
                             .Select(kv => kv.Key)
                             .OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
                {
                    group.Predicates.Add(Clause(NpcGroupAxis.EditorId, seed.Substring(SeedTier.Length),
                        NpcGroupMode.Exclude));
                }

                AddDefaultExclusions(group);

                if (!Keep(resolver, group, _options.MinClassGroup))
                {
                    droppedClass++;
                    continue;
                }

                groups.Add(group);
                classGroups++;
            }

            notes.Add($"{classGroups} class groups from {rest.Count} NPCs left by stage 1" +
                      (droppedClass > 0 ? $", {droppedClass} classes fell below the threshold after the exclusions" : ""));

            // Numbered at the end so the order stays contiguous after the dropped candidates.
            for (int i = 0; i < groups.Count; i++) groups[i].SortOrder = i;

            // ---- what it came to ----------------------------------------------------------------

            // Resolved through the resolver rather than counted while building: the predicate is what
            // ships, so the predicate is what gets measured. A seeder that reported its intention
            // instead of its result would hide exactly the exclude clause that went one token too far.
            var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var group in groups)
                foreach (var member in resolver.Resolve(group).Members)
                    seen[member.Key] = seen.TryGetValue(member.Key, out var n) ? n + 1 : 1;

            var ungrouped = _npcs.Where(n => !seen.ContainsKey(n.Key)).ToList();
            int overlaps = seen.Count(kv => kv.Value > 1);

            if (_options.ExcludeUniqueEssentialProtected)
            {
                int excluded = _npcs.Count(n =>
                    ((NpcFlag)n.Flags).HasFlag(NpcFlag.Unique)
                    || ((NpcFlag)n.Flags).HasFlag(NpcFlag.Essential)
                    || ((NpcFlag)n.Flags).HasFlag(NpcFlag.Protected));
                notes.Add($"{excluded} NPCs are unique, essential or protected and excluded from every group");
            }

            return new NpcSeedResult
            {
                Groups = groups,
                Ungrouped = ungrouped,
                Covered = seen.Count,
                TierGroups = groups.Count - classGroups,
                ClassGroups = classGroups,
                Overlaps = overlaps,
                Notes = notes,
            };
        }

        // Section 2.1: named characters, quest-critical ones and the ones the game protects are out by
        // default. They are written as CLAUSES rather than filtered away silently, so the group says on
        // its face what it leaves out - and so the user can drop the clause for a group where it is
        // wrong.
        //
        // These three resolve here but do not all translate: there is no filterByUnique, so G5 has to
        // reach that one through the keyword tagger of section 5.3.
        // A candidate is kept only if its PREDICATE reaches at least the threshold, and only if the
        // predicate is clean: a warning means the resolver could not evaluate a clause, and a seeded
        // group nobody wrote by hand has no business carrying one.
        private static bool Keep(NpcGroupResolver resolver, NpcGroup group, int threshold)
        {
            var resolution = resolver.Resolve(group);
            return resolution.Warnings.Count == 0 && resolution.Count >= threshold;
        }

        private void AddDefaultExclusions(NpcGroup group)
        {
            if (!_options.ExcludeUniqueEssentialProtected) return;

            group.Predicates.Add(Clause(NpcGroupAxis.Flag, "unique", NpcGroupMode.Exclude));
            group.Predicates.Add(Clause(NpcGroupAxis.Flag, "essential", NpcGroupMode.Exclude));
            group.Predicates.Add(Clause(NpcGroupAxis.Flag, "protected", NpcGroupMode.Exclude));
        }

        private static NpcGroupPredicate Clause(string axis, string value, string mode)
            => new() { Axis = axis, Mode = mode, Value = value };

        public const string SeedTier = "tier:";
        public const string SeedClass = "class:";

        // "DLC2EncBandit03" -> "Bandit 03 (DLC2)". The token stays in the Seed column, so nothing has
        // to be parsed back out of the name.
        private static string TierName(string token)
        {
            int enc = token.IndexOf("Enc", StringComparison.OrdinalIgnoreCase);
            string prefix = enc > 0 ? token.Substring(0, enc) : "";
            string body = token.Substring(enc + 3);

            string faction = new string(body.TakeWhile(char.IsLetter).ToArray());
            string tier = body.Substring(faction.Length);

            string name = $"{faction} {tier}".Trim();
            return prefix.Length > 0 ? $"{name} ({prefix})" : name;
        }

        private string ClassName(string classKey)
        {
            var label = _labels.Class(classKey);
            return string.IsNullOrWhiteSpace(label) || label == "-" ? classKey : label;
        }
    }

    // The two thresholds and the one default exclusion, in one place because they are the knobs the
    // interface will expose (G4). The defaults land at 40 groups on the measured load order, which is
    // the 30-50 section 7 asks for.
    public sealed class NpcGroupSeederOptions
    {
        // Below this, a tier token is noise rather than a group: 231 of 342 tokens hold four NPCs or
        // fewer, and seeding those would produce hundreds of groups holding 354 NPCs between them.
        public int MinTierGroup { get; init; } = 20;

        // Same question for a class, measured on what stage 1 leaves: the 15th biggest class still has
        // 41 NPCs, the 16th has 40, and the tail below that is long and thin.
        public int MinClassGroup { get; init; } = 40;

        public bool ExcludeUniqueEssentialProtected { get; init; } = true;
    }

    public sealed class NpcSeedResult
    {
        public IReadOnlyList<NpcGroup> Groups { get; init; } = Array.Empty<NpcGroup>();

        // Every NPC no seeded group reaches. Section 7's own acceptance test is that this is
        // "plausibly small", and it is reported rather than assumed: the tail of a load order is real
        // and a seeder that claimed to cover it would be lying about the interesting part.
        public IReadOnlyList<NpcRecord> Ungrouped { get; init; } = Array.Empty<NpcRecord>();

        public int Covered { get; init; }
        public int TierGroups { get; init; }
        public int ClassGroups { get; init; }

        // NPCs reached by more than one seeded group. Must be 0: two rules on one NPC write the same
        // field twice and the winner is decided by file name.
        public int Overlaps { get; init; }

        public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();

        public int GroupCount => Groups.Count;
    }
}
