using System;
using System.Collections.Generic;
using System.Linq;
using SkyrimCraftingTool.Model;

namespace SkyrimCraftingTool.Services
{
    // Which numbers an NPC actually has (G1, docs/NPC-Gruppen-Plan.md section 2).
    //
    // The stored Health/Magicka/Stamina/Level on a record is not automatically the NPC's: 61,4 % of
    // records take their stats from a template, and for those the stored field is one the game never
    // reads. Every NPC falls into exactly one of four cases, and the plan's numbers are per case:
    //
    //   A   757 own value
    //   B 3.331 AutoCalcStats flag - AFTER THE MEASUREMENT OF 2026-09-25 THIS IS THE SAME AS A:
    //           three bandits read with getbaseav matched their stored values on six of six
    //           numbers, so the flag does not replace the record (section 3). No prediction, no
    //           marking - which is why this class has no autocalc case.
    //   C 1.970 inherits from another NPC - follow the chain, it ends in A or B
    //   D   584 inherits from a LEVELLED CHARACTER LIST - there is no single value at all, because
    //           the game draws one of N candidates when it spawns
    //
    // So 91,2 % resolve to one number and the rest must say so instead of showing the field that is
    // lying. That is the whole job of this class, and the reason the group editor can show a spread
    // at all: a spread over unresolved records would be a spread over dead fields.
    public sealed class NpcStatResolver
    {
        // The chain is short in practice - measured depth 5 at the deepest, 1.706 NPCs at depth 1 -
        // but a plugin can point two records at each other, and an unbounded walk would hang the
        // interface rather than report a broken record.
        private const int MaxHops = 16;

        private readonly Dictionary<string, NpcRecord> _byKey;

        // Levelled character list -> what it holds. Empty when the database was scanned before the LVLN
        // tables existed, and then case D stays what it was: "no single value" (G8).
        private readonly Dictionary<string, List<string>> _leveledNpcs;

        public NpcStatResolver(
            IEnumerable<NpcRecord>? npcs,
            IReadOnlyDictionary<string, List<string>>? leveledNpcs = null)
        {
            _byKey = new Dictionary<string, NpcRecord>(StringComparer.OrdinalIgnoreCase);
            foreach (var npc in npcs ?? Enumerable.Empty<NpcRecord>())
                if (!string.IsNullOrWhiteSpace(npc.Key)) _byKey[npc.Key] = npc;

            _leveledNpcs = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in leveledNpcs ?? new Dictionary<string, List<string>>())
                _leveledNpcs[pair.Key] = pair.Value;
        }

        // True when this record's stats come from its template rather than from its own fields. Both
        // halves are needed: the template flags are a mask over a link, so the mask alone says nothing
        // and the link alone does not say WHICH areas are inherited.
        //
        // Level is part of the Stats area, not of BaseData - the ACBS block the flag covers holds the
        // level, the three attribute offsets and the skills together.
        public static bool InheritsStats(NpcRecord npc)
            => npc != null
               && !string.IsNullOrWhiteSpace(npc.TemplateKey)
               && ((NpcTemplateFlag)npc.TemplateFlags).HasFlag(NpcTemplateFlag.Stats);

        public NpcStatView Resolve(NpcRecord? npc)
        {
            if (npc == null) return NpcStatView.Nothing;

            var current = npc;
            var walked = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { npc.Key };
            int hops = 0;

            while (InheritsStats(current))
            {
                if (hops >= MaxHops)
                    return NpcStatView.Unresolved(current.TemplateKey, hops,
                        $"the template chain is more than {MaxHops} records deep");

                // The one thing a record browser cannot tell you: a template key that resolves to no
                // NPC record is a LEVELLED CHARACTER LIST (299 of them in the real load order). The
                // game picks one of its entries at spawn, so there is no number to show - and showing
                // the record's own field instead would be showing a field the game ignores.
                if (!_byKey.TryGetValue(current.TemplateKey, out var template))
                {
                    // Not an NPC record, so it is a levelled character list. Since G8 those are
                    // scanned, and the list's contents turn "no single value" into a real range: the
                    // game draws one of N candidates at spawn, so the honest answer is all of them.
                    var candidates = Candidates(current.TemplateKey);

                    return candidates.Count > 0
                        ? NpcStatView.FromCandidates(current.TemplateKey, hops, candidates)
                        : NpcStatView.Unresolved(current.TemplateKey, hops,
                            "inherits from a levelled character list");
                }

                if (!walked.Add(template.Key))
                    return NpcStatView.Unresolved(template.Key, hops,
                        "the template chain points back at itself");

                current = template;
                hops++;
            }

            return new NpcStatView
            {
                Source = hops == 0 ? NpcStatSource.Own : NpcStatSource.Inherited,
                SourceKey = current.Key,
                Hops = hops,
                Level = current.UsesPcLevelMult ? null : current.Level,
                LevelMult = current.UsesPcLevelMult ? current.LevelMult : null,
                Health = Normalise(current.Health),
                Magicka = Normalise(current.Magicka),
                Stamina = Normalise(current.Stamina),

                // Skills ride the same chain as the attributes: the ACBS block the Stats template flag
                // covers holds the level, the three attributes AND the eighteen skills together. An NPC
                // that takes its health from a template takes its One-Handed from there too, so
                // resolving one without the other would show a real health next to a dead skill.
                Skills = Lookup(current.Skills),
            };
        }

        // Everything a levelled character list can draw, flattened. The lists NEST - an entry may be
        // another LVLN, the same way the item lists do - so this walks them, with the same cycle guard
        // the template chain uses: a plugin can point two lists at each other, and an unbounded walk
        // would hang the interface rather than report a broken record.
        //
        // Each candidate is resolved in full, so a candidate that inherits from a template contributes
        // its template's numbers, and a candidate that draws from another list contributes that list's.
        private List<NpcStatView> Candidates(string listKey)
        {
            var views = new List<NpcStatView>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void Walk(string key, int depth)
            {
                if (depth > MaxHops || !seen.Add(key)) return;
                if (!_leveledNpcs.TryGetValue(key, out var entries)) return;

                foreach (var entry in entries)
                {
                    if (_byKey.TryGetValue(entry, out var npc))
                    {
                        var view = Resolve(npc);

                        // A candidate that is itself a list contributes ITS candidates, not a nested
                        // view - the caller wants leaves, not a tree.
                        if (view.Candidates.Count > 0) views.AddRange(view.Candidates);
                        else if (view.HasNumbers) views.Add(view);

                        continue;
                    }

                    Walk(entry, depth + 1);
                }
            }

            Walk(listKey, 0);
            return views;
        }

        private static Dictionary<string, int> Lookup(IEnumerable<NpcSkillRecord>? skills)
        {
            var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (var skill in skills ?? Enumerable.Empty<NpcSkillRecord>())
                if (!string.IsNullOrWhiteSpace(skill.Skill)) map[skill.Skill] = skill.Value;

            return map;
        }

        // A base stat the record stores as a WRAPPED NEGATIVE number. Measured on the real load order
        // on 2026-09-26: 39 of 6.642 records carry a value above 60.000 in one of the three fields, and
        // the pattern leaves no doubt - Magicka 65511 with MagickaOffset -25 in the same row
        // (65536 - 25), Magicka 65534 where the offset is -2. The field is unsigned in the record
        // layout, so whatever wrote those rows put a negative number into it and Mutagen reads back
        // exactly what is there.
        //
        // Read as the signed 16-bit value it was written as, and floored at 0 because a base attribute
        // below zero is not a thing the engine has. Without this, one such member turns a group's whole
        // spread into "0 - 65511" and the column that this editor is built around says nothing at all.
        //
        // THE SCAN IS THE PROPER PLACE FOR THIS - see the note in docs/NPC-Gruppen-Plan.md section 10.3.
        // Once it normalises on the way in, this method simply never triggers.
        private static int Normalise(int stored)
            => Math.Max(0, stored > short.MaxValue ? unchecked((short)stored) : stored);

        // The spread of one field over a set of NPCs - the "ist" column of section 6. This is what
        // makes "too coarse" visible instead of a guess: Health 222-238 is a group worth editing,
        // Health 35-497 is four tiers in one bucket.
        //
        // NPCs whose value does not resolve are counted, not folded in: a range that quietly included
        // 584 dead fields would read as wider than the group really is, and the number of unresolved
        // members is exactly what tells you whether the range can be trusted.
        public NpcStatSpread Spread(IEnumerable<NpcRecord>? npcs, Func<NpcStatView, int?> field)
        {
            int? low = null, high = null;
            int resolved = 0, unresolved = 0;

            foreach (var npc in npcs ?? Enumerable.Empty<NpcRecord>())
            {
                // Leaves, not the view itself: an NPC drawn from a levelled character list contributes
                // every candidate the list can produce, because the game will pick one of them and a
                // group that covers only one of the ends would be a group that lies (G8).
                bool any = false;

                foreach (var leaf in Resolve(npc).Leaves)
                {
                    var value = field(leaf);
                    if (value == null) continue;

                    any = true;
                    if (low == null || value < low) low = value;
                    if (high == null || value > high) high = value;
                }

                if (any) resolved++;
                else unresolved++;
            }

            return new NpcStatSpread
            {
                Low = low,
                High = high,
                Resolved = resolved,
                Unresolved = unresolved,
            };
        }
    }

    public enum NpcStatSource
    {
        // A and B of section 2: the record's own fields, which after the measurement of 2026-09-25 is
        // also what an AutoCalcStats record has.
        Own,

        // C: the numbers come from a template, hops records up the chain.
        Inherited,

        // D: drawn from a levelled character list. There is no single value, but there is a range over
        // the candidates the list can produce - which is what the lists were scanned for (G8).
        Candidates,

        // A list that was not scanned, and the two broken shapes: no number and no range either.
        Unresolved,
    }

    public sealed class NpcStatView
    {
        public static readonly NpcStatView Nothing = new() { Source = NpcStatSource.Unresolved, Reason = "no record" };

        public static NpcStatView Unresolved(string sourceKey, int hops, string reason) => new()
        {
            Source = NpcStatSource.Unresolved,
            SourceKey = sourceKey ?? "",
            Hops = hops,
            Reason = reason,
        };

        // Case D of section 2, once the levelled character lists are scanned: the game draws one of
        // these at spawn, so there is no single value - but there IS a range, and that is worth more
        // than the shrug this used to return.
        public static NpcStatView FromCandidates(string listKey, int hops, IReadOnlyList<NpcStatView> candidates)
            => new()
            {
                Source = NpcStatSource.Candidates,
                SourceKey = listKey ?? "",
                Hops = hops,
                Candidates = candidates,
                Reason = $"drawn from a levelled character list ({candidates.Count} candidates)",
            };

        public NpcStatSource Source { get; init; }

        // The record the numbers were read from: the NPC itself for Own, the end of the chain for
        // Inherited, and the link that could not be followed for Unresolved.
        public string SourceKey { get; init; } = "";
        public int Hops { get; init; }

        // Null for an NPC that scales with the player (654 of them) - there is no fixed level, and
        // LevelMult carries the multiplier instead.
        public int? Level { get; init; }
        public float? LevelMult { get; init; }

        public int? Health { get; init; }
        public int? Magicka { get; init; }
        public int? Stamina { get; init; }

        // The eighteen skills of the record the numbers came from, by SkyPatcher's own spelling
        // ("twohanded", "heavyarmor"). Empty for an NPC whose values do not resolve at all.
        public IReadOnlyDictionary<string, int> Skills { get; init; }
            = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        // One skill, or null when this NPC has no value for it - which is the same "no number" the
        // attributes use, so the spread treats both the same way.
        public int? Skill(string? name)
            => !string.IsNullOrWhiteSpace(name) && Skills.TryGetValue(name, out var value) ? value : null;

        // For case D: every NPC the levelled character list can draw, already resolved. Empty for every
        // other case.
        public IReadOnlyList<NpcStatView> Candidates { get; init; } = Array.Empty<NpcStatView>();

        // What a spread is actually built from: this view, or - for an NPC drawn from a list - the
        // candidates behind it. One NPC can therefore contribute several numbers to a group's range,
        // which is correct: the game will pick one of them and the group has to cover all.
        public IEnumerable<NpcStatView> Leaves => Candidates.Count > 0 ? Candidates : new[] { this };

        // Why there is no number, for the cases that have none.
        public string Reason { get; init; } = "";

        public bool HasNumbers => Source is NpcStatSource.Own or NpcStatSource.Inherited;
    }

    public sealed class NpcStatSpread
    {
        public int? Low { get; init; }
        public int? High { get; init; }
        public int Resolved { get; init; }
        public int Unresolved { get; init; }

        public bool IsEmpty => Resolved == 0;
        public bool IsSingle => Resolved > 0 && Low == High;

        // What the "ist" column prints. Deliberately not a bare number when the group is not
        // homogeneous - "222 - 238" and "35 - 497" have to look different at a glance, and the tail
        // says how many members the range does not cover.
        public string Text
        {
            get
            {
                if (IsEmpty)
                    return Unresolved > 0 ? $"- ({Unresolved} unresolved)" : "-";

                string body = IsSingle ? $"{Low}" : $"{Low} - {High}";
                return Unresolved > 0 ? $"{body}  (+{Unresolved} unresolved)" : body;
            }
        }
    }
}
