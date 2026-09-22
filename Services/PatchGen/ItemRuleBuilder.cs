using System;
using System.Collections.Generic;
using System.Linq;
using SkyrimCraftingTool.Model;

namespace SkyrimCraftingTool.Services.PatchGen
{
    // Pure diff: (original scanned record, user-edited record) -> one SkyPatcherRule, or null when
    // nothing emittable changed. No DB, no filesystem. See docs/PatchGenerator-Plan.md §2.
    public static class ItemRuleBuilder
    {
        // Names containing any of these can't be expressed as fullName=~...~ without breaking the
        // rule's ':' / '~' framing. We skip the name op and let the caller surface a warning.
        private static readonly char[] NameBreakers = { '~', ':', '\r', '\n' };

        public sealed record NameSkip(string Key, string Name);

        // Mutagen ArmorType enum -> SkyPatcher spelling. Anything else (including the empty string
        // a record without BOD2 scans as) yields null and is skipped rather than guessed at.
        private static string? SkyPatcherArmorType(string? armorType) => armorType?.Trim().ToLowerInvariant() switch
        {
            "lightarmor" => "lightarmor",
            "heavyarmor" => "heavyarmor",
            "clothing" => "clothing",
            _ => null,
        };

        public static SkyPatcherRule? BuildArmorRule(ArmorRecord original, ArmorRecord edited, out NameSkip? nameSkip)
        {
            nameSkip = null;
            var (plugin, formId) = KeyFactory.SplitMasterKey(edited.Key);
            var ops = new List<string>();

            AppendName(ops, original.Name, edited.Name, edited.Key, ref nameSkip);

            if (!NumEqual(original.ArmorRating, edited.ArmorRating))
                ops.Add($"damageResist={PatchFormat.Num(edited.ArmorRating)}");
            if (original.Value != edited.Value)
                ops.Add($"value={PatchFormat.Int(edited.Value)}");
            if (!NumEqual(original.Weight, edited.Weight))
                ops.Add($"weight={PatchFormat.Num(edited.Weight)}");

            // BOD2 ArmorType. This - not the ArmorLight keyword - is what decides whether the game
            // treats the record as armor or clothing, so it governs the inventory category, whether
            // an armor rating is shown at all, and whether the item can be tempered. A tester set
            // every armor keyword on a clothing item, saw the patched rating only in Modex and
            // nothing in game, and could not temper it: all one missing field.
            if (!string.Equals(original.ArmorType, edited.ArmorType, StringComparison.OrdinalIgnoreCase)
                && SkyPatcherArmorType(edited.ArmorType) is { } armorType)
            {
                ops.Add($"armorType={armorType}");
            }

            var refKeys = AppendKeywordOps(ops, original.Keywords, edited.Keywords).ToList();
            AppendBipedSlotOps(ops, original.BodySlotMask, edited.BodySlotMask);
            AppendObjectEffectOp(ops, refKeys, original.ObjectEffectKey, edited.ObjectEffectKey);

            if (ops.Count == 0) return null;
            return new SkyPatcherRule
            {
                FilterDirective = "filterByArmors",
                TargetPlugin = plugin,
                TargetFormId = formId,
                Comment = Comment(edited.EditorID, edited.Name),
                Operations = ops,
                ReferencedKeywordKeys = refKeys,
            };
        }

        public static SkyPatcherRule? BuildWeaponRule(WeaponRecord original, WeaponRecord edited, out NameSkip? nameSkip)
        {
            nameSkip = null;
            var (plugin, formId) = KeyFactory.SplitMasterKey(edited.Key);
            var ops = new List<string>();

            AppendName(ops, original.Name, edited.Name, edited.Key, ref nameSkip);

            if (original.Damage != edited.Damage)
                ops.Add($"attackDamage={PatchFormat.Int(edited.Damage)}");
            if (!NumEqual(original.Speed, edited.Speed))
                ops.Add($"speed={PatchFormat.Num(edited.Speed)}");
            if (!NumEqual(original.Reach, edited.Reach))
                ops.Add($"reach={PatchFormat.Num(edited.Reach)}");
            if (!NumEqual(original.Stagger, edited.Stagger))
                ops.Add($"stagger={PatchFormat.Num(edited.Stagger)}");
            if (original.Value != edited.Value)
                ops.Add($"value={PatchFormat.Int(edited.Value)}");
            if (!NumEqual(original.Weight, edited.Weight))
                ops.Add($"weight={PatchFormat.Num(edited.Weight)}");

            var refKeys = AppendKeywordOps(ops, original.Keywords, edited.Keywords).ToList();
            AppendObjectEffectOp(ops, refKeys, original.ObjectEffectKey, edited.ObjectEffectKey);

            if (ops.Count == 0) return null;
            return new SkyPatcherRule
            {
                FilterDirective = "filterByWeapons",
                TargetPlugin = plugin,
                TargetFormId = formId,
                Comment = Comment(edited.EditorID, edited.Name),
                Operations = ops,
                ReferencedKeywordKeys = refKeys,
            };
        }

        // WHICH enchantment the item wears. One operation, and the only one here that can also take
        // something AWAY: an empty value means "no enchantment any more".
        //
        // THE REMOVAL FORM IS NOT CONFIRMED. Assigning an effect is documented
        // (objectEffect=<Plugin>|<FormID>); clearing one with an empty value is the obvious reading
        // and nothing more - the generator flags it in the report so a patch that relies on it is
        // never silently trusted. Assignment is unaffected either way.
        internal const string ObjectEffectRemovalOp = "objectEffect=";

        private static void AppendObjectEffectOp(
            List<string> ops, List<string> refKeys, string? original, string? edited)
        {
            var from = (original ?? "").Trim();
            var to = (edited ?? "").Trim();

            if (string.Equals(from, to, StringComparison.OrdinalIgnoreCase)) return;

            if (to.Length == 0)
            {
                ops.Add(ObjectEffectRemovalOp);
                return;
            }

            ops.Add("objectEffect=" + PatchFormat.RefKey8(to));

            // Into the reference list so the dead-reference pass covers it: an enchantment from a
            // plugin that is no longer in the scan would otherwise be written without a word.
            refKeys.Add(to);
        }

        // --- helpers ---

        private static void AppendName(List<string> ops, string? original, string? edited, string key, ref NameSkip? nameSkip)
        {
            edited ??= "";
            if (string.Equals(original ?? "", edited, StringComparison.Ordinal)) return;
            if (edited.Length == 0) return; // never blank a name
            if (edited.IndexOfAny(NameBreakers) >= 0)
            {
                nameSkip = new NameSkip(key, edited);
                return;
            }
            ops.Add($"fullName=~{edited}~");
        }

        // Returns the raw (6-hex) keyword keys referenced, for the dead-ref validation pass.
        private static IReadOnlyList<string> AppendKeywordOps(List<string> ops, IEnumerable<string>? original, IEnumerable<string>? edited)
        {
            var orig = Normalize(original);
            var ed = Normalize(edited);
            var origSet = new HashSet<string>(orig, StringComparer.Ordinal);
            var edSet = new HashSet<string>(ed, StringComparer.Ordinal);

            var added = ed.Where(k => !origSet.Contains(k)).ToList();
            var removed = orig.Where(k => !edSet.Contains(k)).ToList();

            if (added.Count > 0)
                ops.Add("keywordsToAdd=" + string.Join(",", added.Select(PatchFormat.RefKey8)));
            if (removed.Count > 0)
                ops.Add("keywordsToRemove=" + string.Join(",", removed.Select(PatchFormat.RefKey8)));

            if (added.Count == 0 && removed.Count == 0) return Array.Empty<string>();
            return added.Concat(removed).ToList();
        }

        private static void AppendBipedSlotOps(List<string> ops, uint original, uint edited)
        {
            uint added = edited & ~original;
            uint removed = original & ~edited;
            if (added != 0) ops.Add("bipedSlotsToAdd=" + string.Join(",", SetBits(added)));
            if (removed != 0) ops.Add("bipedSlotsToRemove=" + string.Join(",", SetBits(removed)));
        }

        private static IEnumerable<int> SetBits(uint mask)
        {
            for (int i = 0; i < 32; i++)
                if ((mask & (1u << i)) != 0)
                    yield return i;
        }

        // Ordered, de-duplicated, trimmed, empties dropped.
        private static List<string> Normalize(IEnumerable<string>? keys)
        {
            var list = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            if (keys != null)
                foreach (var raw in keys)
                {
                    var k = raw?.Trim() ?? "";
                    if (k.Length == 0) continue;
                    if (seen.Add(k)) list.Add(k);
                }
            return list;
        }

        // Compare via the emitted representation so float round-trip noise ("1.5" vs "1.4999999")
        // never produces a phantom op.
        private static bool NumEqual(double a, double b) => PatchFormat.Num(a) == PatchFormat.Num(b);

        private static string Comment(string? editorId, string? name)
        {
            editorId = (editorId ?? "").Trim();
            name = (name ?? "").Trim();
            if (editorId.Length > 0 && name.Length > 0) return $"{editorId} — {name}";
            return editorId.Length > 0 ? editorId : name;
        }
    }
}
