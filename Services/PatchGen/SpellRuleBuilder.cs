using System;
using System.Collections.Generic;
using System.Linq;
using SkyrimCraftingTool.Model;

namespace SkyrimCraftingTool.Services.PatchGen
{
    // Pure diff: (scanned spell, edited spell) -> one SkyPatcherRule, or null when nothing emittable
    // changed. No database, no filesystem - the same contract as ItemRuleBuilder and
    // EnchantmentRuleBuilder. Target folder is "spell". See docs/Spells-Plan.md (S-P4).
    //
    // Every operation emitted here is one docs/Spell_Patcher.txt documents. There is deliberately no
    // targetType, no spell type, no range and no cast duration: the patcher has no way to write
    // them, so the editor does not offer them and this never invents them.
    public static class SpellRuleBuilder
    {
        // Same framing problem as ItemRuleBuilder: fullName=~...~ breaks on these characters.
        private static readonly char[] NameBreakers = { '~', ':', '\r', '\n' };

        public static SkyPatcherRule? BuildRule(
            SpellRecord original, SpellRecord edited, out ItemRuleBuilder.NameSkip? nameSkip)
        {
            nameSkip = null;

            var (plugin, formId) = KeyFactory.SplitMasterKey(edited.Key);
            var ops = new List<string>();

            AppendName(ops, original.Name, edited.Name, edited.Key, ref nameSkip);
            AppendFieldOps(ops, original, edited);

            var referenced = AppendEffectOps(ops, original, edited);

            if (ops.Count == 0) return null;

            return new SkyPatcherRule
            {
                // SkyPatcher's own name for the spell patcher's filter. Spelled exactly as the
                // documentation gives it - it is passed through verbatim.
                FilterDirective = "filterBySpells",
                TargetPlugin = plugin,
                TargetFormId = formId,
                Comment = Comment(edited.EditorID, edited.Name),
                Operations = ops,
                ReferencedKeywordKeys = referenced,
            };
        }

        private static void AppendName(
            List<string> ops, string? original, string? edited, string key,
            ref ItemRuleBuilder.NameSkip? nameSkip)
        {
            edited ??= "";
            if (string.Equals(original ?? "", edited, StringComparison.Ordinal)) return;

            // Never blank a name: fullName=~~ would leave the spell nameless in the menu, which is
            // not what an empty box means.
            if (edited.Length == 0) return;

            if (edited.IndexOfAny(NameBreakers) >= 0)
            {
                nameSkip = new ItemRuleBuilder.NameSkip(key, edited);
                return;
            }

            ops.Add($"fullName=~{edited}~");
        }

        // The record's own fields, as far as the spell patcher reaches.
        private static void AppendFieldOps(List<string> ops, SpellRecord original, SpellRecord edited)
        {
            if (original.BaseCost != edited.BaseCost)
                ops.Add($"baseCost={PatchFormat.Int(edited.BaseCost)}");

            if (!NumEqual(original.ChargeTime, edited.ChargeTime))
                ops.Add($"chargeTime={PatchFormat.Num(edited.ChargeTime)}");

            if (!string.Equals(original.CastType ?? "", edited.CastType ?? "", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(edited.CastType))
            {
                // Lower case, as every example in the documentation writes it.
                ops.Add($"castType={edited.CastType.ToLowerInvariant()}");
            }

            // Only ever SET, never cleared. The documentation gives halfCostPerk a value to assign
            // and no form for "remove the perk", so an empty box is left alone rather than guessed
            // into something like halfCostPerk= - which might mean "none" or might mean nothing.
            if (!string.Equals(original.HalfCostPerkKey ?? "", edited.HalfCostPerkKey ?? "", StringComparison.OrdinalIgnoreCase)
                && !KeyFactory.IsUnsetKey(edited.HalfCostPerkKey))
            {
                ops.Add($"halfCostPerk={PatchFormat.RefKey8(edited.HalfCostPerkKey)}");
            }

            AppendFlagOps(ops, original.Flags, edited.Flags);
            AppendKeywordOps(ops, original.Keywords, edited.Keywords);
        }

        // Flags are a bitfield but the operations work by NAME, so the diff has to be per bit:
        // setFlags turns bits on, removeFlags turns them off. Sending every set bit every time would
        // fight other mods' rules over flags this user never touched.
        //
        // Bits NOTHING NAMES ARE LEFT ALONE. Three of them are in real use - 415 spells in a measured
        // load order - and there is simply no way to write them: both operations take names. The
        // editor does not offer them either, so they can never differ here.
        private static void AppendFlagOps(List<string> ops, int original, int edited)
        {
            if (original == edited) return;

            var set = new List<string>();
            var remove = new List<string>();

            foreach (var (patcherName, bit, _) in SpellFlags.Known)
            {
                bool before = (original & bit) != 0;
                bool after = (edited & bit) != 0;
                if (before == after) continue;

                (after ? set : remove).Add(patcherName);
            }

            if (set.Count > 0) ops.Add("setFlags=" + string.Join(", ", set));
            if (remove.Count > 0) ops.Add("removeFlags=" + string.Join(", ", remove));
        }

        // Add and remove separately, for the same reason as the flags: a rule that rewrote the whole
        // list would drop keywords another patch added.
        private static void AppendKeywordOps(List<string> ops, string? original, string? edited)
        {
            var before = Split(original);
            var after = Split(edited);

            var added = after.Where(k => !before.Contains(k)).ToList();
            var removed = before.Where(k => !after.Contains(k)).ToList();

            if (added.Count > 0)
                ops.Add("keywordsToAdd=" + string.Join(", ", added.Select(PatchFormat.RefKey8)));
            if (removed.Count > 0)
                ops.Add("keywordsToRemove=" + string.Join(", ", removed.Select(PatchFormat.RefKey8)));

            static HashSet<string> Split(string? csv)
                => new((csv ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                       StringComparer.OrdinalIgnoreCase);
        }

        private static string Comment(string? editorId, string? name)
        {
            editorId = (editorId ?? "").Trim();
            name = (name ?? "").Trim();
            if (editorId.Length > 0 && name.Length > 0) return $"{editorId} — {name}";
            return editorId.Length > 0 ? editorId : name;
        }

        private static bool NumEqual(double a, double b) => PatchFormat.Num(a) == PatchFormat.Num(b);

        // The per-spell effect diff.
        //
        // mgefsToChange/Add/Remove are SPELL operations here: they touch THIS spell's own effect
        // entries, never the shared MGEF record. That distinction is the whole reason the rule is
        // filtered by filterBySpells rather than filterByMgefs - the latter would change the effect
        // in every spell and every enchantment that uses it, which is a different tool.
        //
        // Returns every MGEF key the rule names, so the generator's dead-reference pass can flag an
        // effect that no longer resolves against the current scan.
        private static IReadOnlyList<string> AppendEffectOps(
            List<string> ops, SpellRecord original, SpellRecord edited)
        {
            var orig = ByMgef(original.Effects);
            var edit = ByMgef(edited.Effects);
            if (orig.Count == 0 && edit.Count == 0) return Array.Empty<string>();

            var changes = new List<string>();
            var adds = new List<string>();
            var removes = new List<string>();
            var referenced = new List<string>();

            foreach (var (mgef, e) in edit)
            {
                referenced.Add(mgef);

                if (!orig.TryGetValue(mgef, out var o))
                {
                    // Magnitude Duration Area. The optional fourth slot, sortFirst, is not used:
                    // nothing in this tool models effect order, so claiming one would be invention.
                    adds.Add($"{PatchFormat.RefKey8(mgef)}~{PatchFormat.Num(e.Magnitude)}" +
                             $"~{PatchFormat.Int(e.Duration)}~{PatchFormat.Int(e.Area)}");
                    continue;
                }

                // "null" for anything that did not change, so a rule never clobbers a value some
                // other patch set. THE FOURTH SLOT IS A MAGNITUDE MULTIPLIER, not a magnitude - the
                // tool does not model it, so it is always null rather than guessed at.
                string mag = NumEqual(o.Magnitude, e.Magnitude) ? "null" : PatchFormat.Num(e.Magnitude);
                string dur = o.Duration == e.Duration ? "null" : PatchFormat.Int(e.Duration);
                string area = o.Area == e.Area ? "null" : PatchFormat.Int(e.Area);

                if (mag == "null" && dur == "null" && area == "null") continue;

                changes.Add($"{PatchFormat.RefKey8(mgef)}~{mag}~{dur}~{area}~null");
            }

            foreach (var mgef in orig.Keys)
            {
                if (edit.ContainsKey(mgef)) continue;
                referenced.Add(mgef);
                removes.Add(PatchFormat.RefKey8(mgef));
            }

            // One fragment per operation type, entries comma-separated - the form the documentation
            // prescribes. Repeating "mgefsToChange=" within a single rule is not documented
            // behaviour and is not guessed at.
            if (changes.Count > 0) ops.Add("mgefsToChange=" + string.Join(",", changes));
            if (adds.Count > 0) ops.Add("mgefsToAdd=" + string.Join(",", adds));
            if (removes.Count > 0) ops.Add("mgefsToRemove=" + string.Join(",", removes));

            return referenced;
        }

        // First entry wins per MGEF.
        //
        // A SPEL may legitimately list the same magic effect twice with different values - measured
        // over a real load order, 938 spells do, 401 of them with genuinely differing numbers - and
        // neither SpellEffects' primary key nor mgefsToChange can express that. The patcher
        // addresses an effect BY ITS KEY, so "the second instance" is unnameable on both sides.
        // Taking the first consistently is the only honest reading; the window says so on the
        // records concerned (SpellRecord.DroppedEffectRows).
        private static Dictionary<string, SpellEffectRecord> ByMgef(IEnumerable<SpellEffectRecord>? effects)
        {
            var map = new Dictionary<string, SpellEffectRecord>(StringComparer.OrdinalIgnoreCase);
            if (effects == null) return map;

            foreach (var e in effects)
            {
                var key = (e.MagicEffectKey ?? "").Trim();
                if (key.Length == 0) continue;
                map.TryAdd(key, e);
            }

            return map;
        }
    }
}
