using System;
using System.Collections.Generic;
using SkyrimCraftingTool.Model;

namespace SkyrimCraftingTool.Services.PatchGen
{
    // Pure diff: (original scanned enchantment, user-edited enchantment) -> one SkyPatcherRule, or
    // null when nothing emittable changed. No DB, no filesystem — same contract as ItemRuleBuilder.
    // Target folder is "enchantment". See docs/EnchantmentPatch-Plan.md (E-P1).
    public static class EnchantmentRuleBuilder
    {
        // Same framing problem as ItemRuleBuilder: fullName=~...~ breaks on these characters.
        private static readonly char[] NameBreakers = { '~', ':', '\r', '\n' };

        public static SkyPatcherRule? BuildRule(
            EnchantmentRecord original, EnchantmentRecord edited, out ItemRuleBuilder.NameSkip? nameSkip)
        {
            nameSkip = null;
            var (plugin, formId) = KeyFactory.SplitMasterKey(edited.Key);
            var ops = new List<string>();

            AppendName(ops, original.Name, edited.Name, edited.Key, ref nameSkip);

            // baseCost alone is NOT enough. Without the ENCH ENIT "Manual Calc" flag the game
            // AUTO-CALCULATES the enchantment cost from its effects and ignores the stored value,
            // so the edit would silently do nothing. SkyPatcher exposes that flag as
            // "costoverride" - the official example pairs the two for exactly this reason.
            //
            // Side effect the user is opting into by editing the cost at all: the enchantment
            // stops auto-scaling with its effects. Only ever emitted together with baseCost.
            if (!NumEqual(original.EnchantmentCost, edited.EnchantmentCost))
            {
                ops.Add($"baseCost={PatchFormat.Num(edited.EnchantmentCost)}");
                ops.Add("setFlags=costoverride");
            }

            var refKeys = AppendEffectOps(ops, original, edited);

            if (ops.Count == 0) return null;
            return new SkyPatcherRule
            {
                FilterDirective = "filterByEnchs",
                TargetPlugin = plugin,
                TargetFormId = formId,
                Comment = Comment(edited.EditorID, edited.Name),
                Operations = ops,
                ReferencedKeywordKeys = refKeys,
            };
        }

        private static void AppendName(
            List<string> ops, string? original, string? edited, string key,
            ref ItemRuleBuilder.NameSkip? nameSkip)
        {
            edited ??= "";
            if (string.Equals(original ?? "", edited, StringComparison.Ordinal)) return;
            if (edited.Length == 0) return; // never blank a name
            if (edited.IndexOfAny(NameBreakers) >= 0)
            {
                nameSkip = new ItemRuleBuilder.NameSkip(key, edited);
                return;
            }
            ops.Add($"fullName=~{edited}~");
        }

        private static bool NumEqual(double a, double b) => PatchFormat.Num(a) == PatchFormat.Num(b);

        private static string Comment(string? editorId, string? name)
        {
            editorId = (editorId ?? "").Trim();
            name = (name ?? "").Trim();
            if (editorId.Length > 0 && name.Length > 0) return $"{editorId} — {name}";
            return editorId.Length > 0 ? editorId : name;
        }

        // Per-enchantment effect diff. mgefsToChange/Add/Remove are ENCHANTMENT operations: they
        // touch this enchantment's own effect entries, never the shared MGEF record — which is
        // exactly what EnchantmentEffects PRIMARY KEY(EnchantmentKey, MagicEffectKey) models.
        //
        // Returns every referenced MGEF key so the generator's dead-reference pass can flag effects
        // that no longer resolve against the current scan.
        private static IReadOnlyList<string> AppendEffectOps(
            List<string> ops, EnchantmentRecord original, EnchantmentRecord edited)
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
                    // Magnitude Duration Area (sortFirst is optional and not used).
                    adds.Add($"{PatchFormat.RefKey8(mgef)}~{PatchFormat.Num(e.Magnitude)}" +
                             $"~{PatchFormat.Int(e.Duration)}~{PatchFormat.Int(e.Area)}");
                    continue;
                }

                // "null" for anything that didn't change, so we never clobber a value some other
                // patch set. The 4th slot is the magnitude multiplier, which the tool doesn't track.
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

            // One fragment per operation type, entries comma-separated — the format the docs
            // prescribe. Repeating "mgefsToChange=" in a single rule is not documented behaviour.
            if (changes.Count > 0) ops.Add("mgefsToChange=" + string.Join(",", changes));
            if (adds.Count > 0) ops.Add("mgefsToAdd=" + string.Join(",", adds));
            if (removes.Count > 0) ops.Add("mgefsToRemove=" + string.Join(",", removes));

            return referenced;
        }

        // First entry wins per MGEF. EnchantmentEffects has PRIMARY KEY(EnchantmentKey,
        // MagicEffectKey) so live rows can't collide, but EnchantmentEffects_Original has no key at
        // all — and a plugin may legitimately list the same MGEF twice with different values, which
        // neither this schema nor mgefsToChange can express. Consistent limitation, documented.
        private static Dictionary<string, EnchantmentEffectRecord> ByMgef(
            IEnumerable<EnchantmentEffectRecord>? effects)
        {
            var map = new Dictionary<string, EnchantmentEffectRecord>(StringComparer.OrdinalIgnoreCase);
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
