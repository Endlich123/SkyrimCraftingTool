using System;
using System.Collections.Generic;
using System.Linq;
using SkyrimCraftingTool.Model;
using SkyrimCraftingTool.Services.PatchGen;

namespace SkyrimCraftingTool.Services.TemplateChildren
{
    // The Kinder-Patcher's rule (docs/Plan_Aktuell/KinderPatcher-Plan.md): a template child gets
    // everything its template gets.
    //
    // Measured in the game's memory (2026-10-27/28): on load a child copies slots, type and keywords
    // (replacing its own) from the template's PLUGIN state, and a SkyPatcher change to the template
    // never reaches it. So the child's rule is built here, as a pair the ordinary ItemRuleBuilder
    // diffs:
    // - slots and keywords exist only as +/- in SkyPatcher: the target is the template's edited state,
    //   the base is the template's plugin state (what the child holds in game), not the child's own;
    // - type and the numbers are absolute setters: written whenever the template changed them or an
    //   addon of the child's own multiplies them (ChildExtras);
    // - name, enchantment, charge and placements stay the child's own edits.
    // The child's own edits of the inherited fields are ignored - they were made against the wrong base.
    public static class TemplateInheritance
    {
        // The top of the template chain, or null when the item is no child: no template, a template
        // that is not scanned, a self-reference or a loop. templateOf answers null for an unknown key
        // and "" for a record without a template. A chain is followed to its root (6 weapons in
        // NEFARAM are made from a template that has one itself), as ArmorTemplateSlots does for slots.
        public static string? RootOf(string key, Func<string, string?> templateOf)
        {
            var first = templateOf(key);
            if (string.IsNullOrWhiteSpace(first) || string.Equals(first, key, StringComparison.OrdinalIgnoreCase))
                return null;
            if (templateOf(first) == null) return null;   // the template itself is not scanned

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { key };
            var current = first;
            while (seen.Add(current))
            {
                var next = templateOf(current);
                if (string.IsNullOrWhiteSpace(next) || templateOf(next) == null) return current;
                current = next;
            }
            return null;   // a loop: nothing the game could resolve either
        }

        // An unenchanted child only borrows the template's model (WindhelmSSE's "Leather Hood" on the
        // Thieves Guild Hood): the standard system computes its numbers like any other item. Its slots,
        // type and keywords still come from the template - the game copies those regardless.
        public static bool ValuesFollow(string? ownEnchantment, ChildExtras extras)
            => !string.IsNullOrWhiteSpace(ownEnchantment) && !extras.KeepValues && !extras.LeaveUntouched;

        public static ArmorPatchPair Armor(ArmorPatchPair child, ArmorPatchPair root, ChildExtras? extras)
        {
            var x = extras ?? ChildExtras.None;
            var o = PatchDataReader.CopyOf(child.Original);
            var e = PatchDataReader.CopyOf(child.Edited);

            o.BodySlotMask = root.Original.BodySlotMask;
            o.ArmorType = root.Original.ArmorType;
            o.Keywords = root.Original.Keywords.ToList();
            e.BodySlotMask = o.BodySlotMask;
            e.ArmorType = o.ArmorType;
            e.Keywords = o.Keywords.ToList();
            if (!x.LeaveUntouched)
            {
                e.BodySlotMask = root.Edited.BodySlotMask;
                e.ArmorType = root.Edited.ArmorType;
                if (!x.KeepKeywords) e.Keywords = root.Edited.Keywords.ToList();
            }

            if (ValuesFollow(child.Edited.ObjectEffectKey, x))
            {
                (o.ArmorRating, e.ArmorRating) = Follow(root.Original.ArmorRating, root.Edited.ArmorRating,
                    x.ArmorRatingFactor, child.Original.ArmorRating, v => (float)Math.Round(v, 2));
                (o.Weight, e.Weight) = Follow(root.Original.Weight, root.Edited.Weight,
                    x.WeightFactor, child.Original.Weight, v => (float)Math.Round(v, 2));
                (o.Value, e.Value) = Follow(root.Original.Value, root.Edited.Value,
                    x.ValueFactor, child.Original.Value, v => (int)Math.Round(v));
            }
            return new ArmorPatchPair(o, e);
        }

        public static WeaponPatchPair Weapon(WeaponPatchPair child, WeaponPatchPair root, ChildExtras? extras)
        {
            var x = extras ?? ChildExtras.None;
            var o = PatchDataReader.CopyOf(child.Original);
            var e = PatchDataReader.CopyOf(child.Edited);

            o.Keywords = root.Original.Keywords.ToList();
            e.Keywords = x.LeaveUntouched || x.KeepKeywords ? o.Keywords.ToList() : root.Edited.Keywords.ToList();

            if (ValuesFollow(child.Edited.ObjectEffectKey, x))
            {
                int Whole(double v) => (int)Math.Round(v, MidpointRounding.AwayFromZero);
                (o.Damage, e.Damage) = Follow(root.Original.Damage, root.Edited.Damage, x.DamageFactor, child.Original.Damage, Whole);
                (o.CritDamage, e.CritDamage) = Follow(root.Original.CritDamage, root.Edited.CritDamage, x.DamageFactor, child.Original.CritDamage, Whole);
                (o.Weight, e.Weight) = Follow(root.Original.Weight, root.Edited.Weight, x.WeightFactor, child.Original.Weight, v => (float)Math.Round(v, 2));
                (o.Value, e.Value) = Follow(root.Original.Value, root.Edited.Value, x.ValueFactor, child.Original.Value, Whole);
                (o.Speed, e.Speed) = Follow(root.Original.Speed, root.Edited.Speed, 1, child.Original.Speed, v => (float)v);
                (o.Reach, e.Reach) = Follow(root.Original.Reach, root.Edited.Reach, 1, child.Original.Reach, v => (float)v);
                (o.Stagger, e.Stagger) = Follow(root.Original.Stagger, root.Edited.Stagger, 1, child.Original.Stagger, v => (float)v);
                (o.CritPercentMult, e.CritPercentMult) = Follow(root.Original.CritPercentMult, root.Edited.CritPercentMult, 1, child.Original.CritPercentMult, v => (float)v);
            }
            return new WeaponPatchPair(o, e);
        }

        // One number of the template. Unchanged template and no factor: nothing to follow, the child's
        // own plugin number on both sides (its own edit is ignored). Otherwise the child gets the
        // template's edited number times its factor - written unless the child's own number AND the
        // template's plugin number already are that, since it is not measured which of the two the game
        // starts the child with (armor rating and weight never differed in NEFARAM).
        private static (T Original, T Edited) Follow<T>(double templateOriginal, double templateEdited, double factor,
            T childOriginal, Func<double, T> round) where T : IConvertible
        {
            bool changed = !Same(templateOriginal, templateEdited) || factor != 1;
            if (!changed) return (childOriginal, childOriginal);

            var target = round(templateEdited * factor);
            double t = Convert.ToDouble(target);
            return Same(t, Convert.ToDouble(childOriginal))
                ? (round(templateOriginal), target)
                : (childOriginal, target);
        }

        private static bool Same(double a, double b) => PatchFormat.Num(a) == PatchFormat.Num(b);
    }
}
