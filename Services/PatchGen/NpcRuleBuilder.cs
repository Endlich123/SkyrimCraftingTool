using System;
using System.Collections.Generic;
using System.Linq;
using SkyrimCraftingTool.Model;

namespace SkyrimCraftingTool.Services.PatchGen
{
    // Pure diff: one scanned-versus-edited NPC -> one SkyPatcherRule, or null when nothing changed.
    // No database, no filesystem. Same shape as ItemRuleBuilder - see docs/NPC-Plan.md §3 and §10.
    //
    // The filter is always filterByNpcs with this record's own FormID. That is the decision from
    // NPC-Plan.md §10: a rule that names FormIDs hits exactly the NPCs it names, today and after any
    // future mod install. A filterByFactions rule would reach further, and that is exactly why the
    // editor never produced one behind the user.s back - the faction view that could has been gone
    // since 2026-09-17 (NPC-Plan.md §12).
    public static class NpcRuleBuilder
    {
        // An NPC that inherits its stats from a template and gets its stats patched anyway. The
        // patch is written as asked - the user may well be clearing the flag by other means - but it
        // is reported, because 61,4 % of NPCs are in this state and the edit silently does nothing
        // for them (NPC-Plan.md §4).
        public sealed record InheritedStatsWarning(string Key, string Name, string TemplateKey);

        public static SkyPatcherRule? Build(NpcRecord npc, out InheritedStatsWarning? warning)
        {
            warning = null;
            if (npc == null || string.IsNullOrWhiteSpace(npc.Key)) return null;

            var (plugin, formId) = KeyFactory.SplitMasterKey(npc.Key);
            var ops = new List<string>();

            // The order matters and is deliberate - see AppendTemplateFlagOps.
            AppendTemplateFlagOps(ops, npc);
            AppendFlagOps(ops, npc);
            AppendLevelOps(ops, npc);
            bool statsChanged = AppendStatOps(ops, npc);
            AppendSkillOps(ops, npc);
            AppendKeywordOps(ops, npc);
            AppendFactionOps(ops, npc);
            AppendSpellOps(ops, npc);
            AppendPerkOps(ops, npc);
            AppendItemOps(ops, npc);
            AppendLinkOps(ops, npc);
            AppendBodyOps(ops, npc);
            AppendAiOps(ops, npc);

            if (ops.Count == 0) return null;

            if (statsChanged && InheritsStats(npc))
                warning = new InheritedStatsWarning(npc.Key, Describe(npc), npc.TemplateKey);

            return new SkyPatcherRule
            {
                FilterDirective = "filterByNpcs",
                TargetPlugin = plugin,
                TargetFormId = formId,
                Comment = Describe(npc),
                Operations = ops,
            };
        }

        // Rules that say exactly the same thing about different NPCs become one line.
        //
        // WHY: setting one value on every bandit in a class node is 405 separate edits, and one rule
        // each would be 405 lines that differ only in a FormID. filterByNpcs takes a comma-separated
        // list, which is what NPC-Plan.md §10 says the multi-selection produces - and a file with one
        // line per intent is a file a person can still read.
        //
        // Only rules with the SAME operations in the SAME order merge. Two NPCs whose operations
        // merely overlap stay apart: reordering or trimming them would change what each one gets.
        internal static List<SkyPatcherRule> MergeIdenticalRules(IReadOnlyList<SkyPatcherRule> rules)
        {
            var merged = new List<SkyPatcherRule>();
            var byOperations = new Dictionary<string, List<SkyPatcherRule>>(StringComparer.Ordinal);

            foreach (var rule in rules)
            {
                // A GROUP RULE IS NEVER MERGED (G5, docs/NPC-Gruppen-Plan.md section 5). This merge
                // works by collecting several targets behind one filter directive, and a group rule has
                // no target - its identity is the list of filter clauses it carries. Two groups that
                // happen to set the same value would otherwise produce one signature here, and the
                // merged rule would keep the first group's filters and silently drop the second's.
                if (rule.FilterClauses.Count > 0)
                {
                    merged.Add(rule);
                    continue;
                }

                // The directive and the file both belong in the identity: two rules in different
                // plugin files cannot share a line, and two rules that say the same thing through
                // different filters are not the same instruction - merging them would silently turn
                // one into the other. Only filterByNpcs is produced today (the faction rules that
                // shared this merge are gone, NPC-Plan.md §12), but the guard costs one string and
                // the failure it prevents is silent.
                //
                // Joined with a character no operation can contain. A digit run would not do:
                // "0000" appears inside every padded FormID, so two rules saying different things
                // could produce one signature and quietly merge.
                const char Separator = '\u0001';

                string signature = rule.FilterDirective
                    + Separator + rule.FileNamePlugin
                    + Separator + string.Join(Separator, rule.Operations);

                if (!byOperations.TryGetValue(signature, out var group))
                    byOperations[signature] = group = new List<SkyPatcherRule>();

                group.Add(rule);
            }

            foreach (var group in byOperations.Values)
            {
                if (group.Count == 1)
                {
                    merged.Add(group[0]);
                    continue;
                }

                // FormIDs in the order the rules arrived, which is the order the tree showed them -
                // so a regenerated file diffs cleanly against the one before it.
                var targets = group
                    .Select(r => $"{r.TargetPlugin}|{PatchFormat.FormId8(r.TargetFormId)}")
                    .ToList();

                merged.Add(new SkyPatcherRule
                {
                    FilterDirective = group[0].FilterDirective,
                    TargetPlugin = group[0].TargetPlugin,
                    TargetFormId = group[0].TargetFormId,
                    FilePlugin = group[0].FileNamePlugin,

                    // The comment names how many, not all of them: 405 names would not be a comment.
                    Comment = $"{group.Count} NPCs",
                    Operations = group[0].Operations,

                    // What the writer actually puts after "filterByNpcs=".
                    ExplicitTargets = targets,
                });
            }

            return merged;
        }

        private static bool InheritsStats(NpcRecord npc) =>
            !string.IsNullOrWhiteSpace(npc.TemplateKey)
            && (npc.TemplateFlags & (uint)NpcTemplateFlag.Stats) != 0;

        private static string Describe(NpcRecord npc)
        {
            string name = !string.IsNullOrWhiteSpace(npc.Name) ? npc.Name : npc.EditorID;
            return string.IsNullOrWhiteSpace(npc.EditorID) || npc.EditorID == name
                ? name
                : $"{npc.EditorID} \"{name}\"";
        }

        // --------------------
        // Level
        // --------------------

        private static void AppendLevelOps(List<string> ops, NpcRecord npc)
        {
            bool multChanged = npc.UsesPcLevelMult != npc.Scanned.UsesPcLevelMult;
            bool levelChanged = npc.Level != npc.Scanned.Level;

            // setPcLevelMult=false=<level> in one operation: the second half is the level the NPC
            // falls back to, and without it the documented failure is a level of 1000. So turning the
            // multiplier off ALWAYS carries a level, whether or not the level field itself was
            // touched - a rule that switches the mode and leaves the level to chance is a trap.
            if (multChanged)
            {
                ops.Add(npc.UsesPcLevelMult
                    ? "setPcLevelMult=true"
                    : $"setPcLevelMult=false={PatchFormat.Int(npc.Level)}");
            }
            else if (levelChanged)
            {
                // Mode unchanged, so the plain level operation is enough. Emitted even for an NPC
                // that scales, because that is the level the game falls back to.
                ops.Add($"level={PatchFormat.Int(npc.Level)}");
            }

            if (npc.CalcMinLevel != npc.Scanned.CalcMinLevel)
                ops.Add($"calcLevelMin={PatchFormat.Int(npc.CalcMinLevel)}");

            if (npc.CalcMaxLevel != npc.Scanned.CalcMaxLevel)
                ops.Add($"calcLevelMax={PatchFormat.Int(npc.CalcMaxLevel)}");
        }

        // --------------------
        // Stats
        // --------------------

        // One changeStats operation with the changed values in it, not three operations - that is
        // the documented shape ("changeStats=health=250, magicka=150, stamina=50").
        //
        // Only the changed ones: writing all three would patch two values the user never touched,
        // and on an NPC whose magicka the game recalculates that is a visible difference.
        private static bool AppendStatOps(List<string> ops, NpcRecord npc)
        {
            var parts = new List<string>();

            if (npc.Health != npc.Scanned.Health) parts.Add($"health={PatchFormat.Int(npc.Health)}");
            if (npc.Magicka != npc.Scanned.Magicka) parts.Add($"magicka={PatchFormat.Int(npc.Magicka)}");
            if (npc.Stamina != npc.Scanned.Stamina) parts.Add($"stamina={PatchFormat.Int(npc.Stamina)}");

            if (parts.Count == 0) return false;

            ops.Add("changeStats=" + string.Join(", ", parts));
            return true;
        }

        // Spells, shouts and leveled spells. One list in the record, three operations in the patch -
        // so the entries are split by their scanned kind. An entry whose kind is blank resolved to
        // none of the three catalogues: a dead reference, and there is no operation that could carry
        // it, so it is left out rather than sent as a guess.
        private static void AppendSpellOps(List<string> ops, NpcRecord npc)
        {
            var scanned = npc.ScannedSpells ?? new List<NpcSpellRecord>();
            var current = npc.Spells ?? new List<NpcSpellRecord>();

            var scannedKeys = new HashSet<string>(scanned.Select(s => s.SpellKey), StringComparer.OrdinalIgnoreCase);
            var currentKeys = new HashSet<string>(current.Select(s => s.SpellKey), StringComparer.OrdinalIgnoreCase);

            void Emit(string kind, string addOp, string removeOp)
            {
                var added = current
                    .Where(s => Kind(s, kind) && !scannedKeys.Contains(s.SpellKey))
                    .Select(s => PatchFormat.RefKey8(s.SpellKey))
                    .ToList();

                var removed = scanned
                    .Where(s => Kind(s, kind) && !currentKeys.Contains(s.SpellKey))
                    .Select(s => PatchFormat.RefKey8(s.SpellKey))
                    .ToList();

                if (added.Count > 0) ops.Add($"{addOp}=" + string.Join(",", added));
                if (removed.Count > 0) ops.Add($"{removeOp}=" + string.Join(",", removed));
            }

            Emit("spell", "spellsToAdd", "spellsToRemove");
            Emit("shout", "shoutsToAdd", "shoutsToRemove");
            Emit("levspell", "levSpellsToAdd", "levSpellsToRemove");
        }

        private static bool Kind(NpcSpellRecord spell, string kind) =>
            string.Equals(spell.Kind, kind, StringComparison.OrdinalIgnoreCase);

        // Perks, added only. SkyPatcher has perksToAdd and no perksToRemove, so a perk the plugins
        // gave an NPC cannot be taken away by any rule this tool writes - the editor does not offer
        // it, and this would silently drop it if it somehow arrived here anyway.
        private static void AppendPerkOps(List<string> ops, NpcRecord npc)
        {
            var scanned = new HashSet<string>(
                (npc.ScannedPerks ?? new List<NpcPerkRecord>()).Select(p => p.PerkKey),
                StringComparer.OrdinalIgnoreCase);

            var added = (npc.Perks ?? new List<NpcPerkRecord>())
                .Where(p => !scanned.Contains(p.PerkKey))
                .Select(p => PatchFormat.RefKey8(p.PerkKey))
                .ToList();

            if (added.Count > 0) ops.Add("perksToAdd=" + string.Join(",", added));
        }

        // Inventory. objectsToAdd carries a count, objectsToRemove does not.
        //
        // A CHANGED count comes out as a remove followed by an add, and in that order: objectsToAdd
        // adds, so sending it alone for an item the NPC already carries would give them both lots.
        private static void AppendItemOps(List<string> ops, NpcRecord npc)
        {
            var scanned = (npc.ScannedItems ?? new List<NpcItemRecord>())
                .ToDictionary(i => i.ItemKey, i => i.Count, StringComparer.OrdinalIgnoreCase);
            var current = npc.Items ?? new List<NpcItemRecord>();
            var currentKeys = new HashSet<string>(current.Select(i => i.ItemKey), StringComparer.OrdinalIgnoreCase);

            var added = current.Where(i => !scanned.ContainsKey(i.ItemKey)).ToList();
            var changed = current.Where(i => scanned.TryGetValue(i.ItemKey, out int c) && c != i.Count).ToList();
            var removed = scanned.Keys.Where(k => !currentKeys.Contains(k)).ToList();

            var toRemove = removed.Concat(changed.Select(i => i.ItemKey)).ToList();
            if (toRemove.Count > 0)
                ops.Add("objectsToRemove=" + string.Join(",", toRemove.Select(PatchFormat.RefKey8)));

            var toAdd = added.Concat(changed).ToList();
            if (toAdd.Count > 0)
            {
                ops.Add("objectsToAdd=" + string.Join(",",
                    toAdd.Select(i => $"{PatchFormat.RefKey8(i.ItemKey)}={PatchFormat.Int(i.Count)}")));
            }
        }

        // --------------------
        // Flags, links and AI (N-P4)
        // --------------------

        // Template flags first in the whole rule, and that is a decision rather than an accident:
        // clearing the Stats flag is what makes a stat edit take effect at all, so it has to be said
        // before the stats are. The documentation's own examples read left to right
        // ("healthBonus=50:changeStats=calcHealth=10"), so that is the order assumed here - and it is
        // on the list for the game test (NPC-Plan.md §7, Frage 2).
        private static void AppendTemplateFlagOps(List<string> ops, NpcRecord npc)
        {
            uint added = npc.TemplateFlags & ~npc.Scanned.TemplateFlags;
            uint removed = npc.Scanned.TemplateFlags & ~npc.TemplateFlags;

            if (removed != 0)
                ops.Add("removeTemplateFlags=" + string.Join(", ", NpcTemplateFlagNames.Describe(removed)));
            if (added != 0)
                ops.Add("setTemplateFlags=" + string.Join(", ", NpcTemplateFlagNames.Describe(added)));
        }

        // Then the ACBS flags, for the same reason one step down: autocalcstats recalculates values
        // when certain things happen, so turning it off belongs before the values are set.
        //
        // Removed before added, so "off then on" cannot end up meaning "on then off".
        private static void AppendFlagOps(List<string> ops, NpcRecord npc)
        {
            uint added = npc.Flags & ~npc.Scanned.Flags;
            uint removed = npc.Scanned.Flags & ~npc.Flags;

            // A bit this tool cannot name must not be written: Describe() reports it as
            // "unknown(0x...)", which is fine on screen and would be nonsense in a patch string.
            var removedNames = NpcFlagNames.Describe(removed);
            var addedNames = NpcFlagNames.Describe(added);

            removedNames = removedNames.Where(n => !n.StartsWith("unknown")).ToList();
            addedNames = addedNames.Where(n => !n.StartsWith("unknown")).ToList();

            if (removedNames.Count > 0) ops.Add("removeFlags=" + string.Join(", ", removedNames));
            if (addedNames.Count > 0) ops.Add("setFlags=" + string.Join(", ", addedNames));
        }

        // The plain links. "null" is SkyPatcher's documented way of clearing deathItem and skin; the
        // others have no documented clear, so an emptied field is skipped rather than guessed at -
        // the same rule the item enchantment field follows.
        private static void AppendLinkOps(List<string> ops, NpcRecord npc)
        {
            Link(ops, "class", npc.ClassKey, npc.Scanned.ClassKey, clearable: false);
            Link(ops, "race", npc.RaceKey, npc.Scanned.RaceKey, clearable: false);
            Link(ops, "voiceType", npc.VoiceKey, npc.Scanned.VoiceKey, clearable: false);
            Link(ops, "outfitDefault", npc.DefaultOutfitKey, npc.Scanned.DefaultOutfitKey, clearable: false);
            Link(ops, "outfitSleep", npc.SleepOutfitKey, npc.Scanned.SleepOutfitKey, clearable: false);
            Link(ops, "deathItem", npc.DeathItemKey, npc.Scanned.DeathItemKey, clearable: true);
            Link(ops, "skin", npc.SkinKey, npc.Scanned.SkinKey, clearable: true);
        }

        private static void Link(List<string> ops, string op, string edited, string scanned, bool clearable)
        {
            edited ??= "";
            scanned ??= "";
            if (string.Equals(edited, scanned, StringComparison.OrdinalIgnoreCase)) return;

            if (edited.Length == 0)
            {
                if (clearable) ops.Add($"{op}=null");
                return;
            }

            ops.Add($"{op}={PatchFormat.RefKey8(edited)}");
        }

        private static void AppendBodyOps(List<string> ops, NpcRecord npc)
        {
            if (Math.Abs(npc.Weight - npc.Scanned.Weight) > 0.0001f)
                ops.Add($"weight={PatchFormat.Num(npc.Weight)}");
            if (Math.Abs(npc.Height - npc.Scanned.Height) > 0.0001f)
                ops.Add($"height={PatchFormat.Num(npc.Height)}");
        }

        // One operation each, and only for a value that is actually in the documented list: an AI
        // token the patcher does not know would be a line that silently does nothing.
        private static void AppendAiOps(List<string> ops, NpcRecord npc)
        {
            Ai(ops, "setAggression", npc.Aggression, npc.Scanned.Aggression, NpcAiTokens.Aggression);
            Ai(ops, "setConfidence", npc.Confidence, npc.Scanned.Confidence, NpcAiTokens.Confidence);
            Ai(ops, "setAssistance", npc.Assistance, npc.Scanned.Assistance, NpcAiTokens.Assistance);
            Ai(ops, "setMorality", npc.Morality, npc.Scanned.Morality, NpcAiTokens.Morality);
            Ai(ops, "setMood", npc.Mood, npc.Scanned.Mood, NpcAiTokens.Mood);
        }

        private static void Ai(List<string> ops, string op, string edited, string scanned,
                               IReadOnlyList<string> allowed)
        {
            edited ??= "";
            if (string.Equals(edited, scanned ?? "", StringComparison.OrdinalIgnoreCase)) return;
            if (!allowed.Contains(edited, StringComparer.OrdinalIgnoreCase)) return;

            ops.Add($"{op}={edited}");
        }

        // --------------------
        // Lists (N-P3)
        // --------------------

        // Keywords: a plain set, so the diff is "what is new" and "what is gone". Same shape as the
        // item keyword operations, and the same reason for a diff rather than a full list: these
        // operations are additive, so sending the whole set would re-add what is already there and
        // never remove anything.
        private static void AppendKeywordOps(List<string> ops, NpcRecord npc)
        {
            var scanned = new HashSet<string>(npc.ScannedKeywords ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
            var current = new HashSet<string>(npc.Keywords ?? new List<string>(), StringComparer.OrdinalIgnoreCase);

            var added = (npc.Keywords ?? new List<string>()).Where(k => !scanned.Contains(k)).ToList();
            var removed = (npc.ScannedKeywords ?? new List<string>()).Where(k => !current.Contains(k)).ToList();

            if (added.Count > 0)
                ops.Add("keywordsToAdd=" + string.Join(",", added.Select(PatchFormat.RefKey8)));
            if (removed.Count > 0)
                ops.Add("keywordsToRemove=" + string.Join(",", removed.Select(PatchFormat.RefKey8)));
        }

        // Factions: a set with a rank attached, which makes three cases rather than two. A rank
        // CHANGE is emitted as an add, because factionsToAdd carries the rank and there is no
        // separate "set the rank" operation - adding a faction the NPC is already in is how the rank
        // gets written.
        private static void AppendFactionOps(List<string> ops, NpcRecord npc)
        {
            var scanned = (npc.ScannedFactions ?? new List<NpcFactionRecord>())
                .ToDictionary(f => f.FactionKey, f => f.Rank, StringComparer.OrdinalIgnoreCase);
            var current = npc.Factions ?? new List<NpcFactionRecord>();
            var currentKeys = new HashSet<string>(current.Select(f => f.FactionKey), StringComparer.OrdinalIgnoreCase);

            var added = current
                .Where(f => !scanned.TryGetValue(f.FactionKey, out int rank) || rank != f.Rank)
                .ToList();

            var removed = scanned.Keys.Where(k => !currentKeys.Contains(k)).ToList();

            if (added.Count > 0)
            {
                ops.Add("factionsToAdd=" + string.Join(",",
                    added.Select(f => $"{PatchFormat.RefKey8(f.FactionKey)}={PatchFormat.Int(f.Rank)}")));
            }

            if (removed.Count > 0)
                ops.Add("factionsToRemove=" + string.Join(",", removed.Select(PatchFormat.RefKey8)));
        }

        // --------------------
        // Skills
        // --------------------

        // Same shape as changeStats: one operation, the changed skills in it, in the order the
        // editor shows them so a regenerated file diffs cleanly against the previous one.
        private static void AppendSkillOps(List<string> ops, NpcRecord npc)
        {
            var changed = npc.Skills
                .Where(s => s.Value != s.ScannedValue && !string.IsNullOrWhiteSpace(s.Skill))
                .OrderBy(s => IndexOf(s.Skill))
                .Select(s => $"{s.Skill.ToLowerInvariant()}={PatchFormat.Int(s.Value)}")
                .ToList();

            if (changed.Count == 0) return;

            ops.Add("changeSkills=" + string.Join(",", changed));
        }

        private static int IndexOf(string skill)
        {
            for (int i = 0; i < NpcSkillNames.All.Count; i++)
            {
                if (string.Equals(NpcSkillNames.All[i], skill, StringComparison.OrdinalIgnoreCase))
                    return i;
            }
            return int.MaxValue;
        }
    }
}
