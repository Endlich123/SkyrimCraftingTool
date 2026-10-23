using SkyrimCraftingTool.Model;
using System;
using System.Collections.Generic;
using System.Linq;

namespace SkyrimCraftingTool.Services
{
    // Carrying an effect change from a base enchantment over to the tier variants that point at it.
    //
    // WHY A FACTOR AND NOT A COPY: the tiers are a ladder of their own values, not copies of the
    // base. Measured across the real load order, 388 of 432 shared effect pairs differ in magnitude
    // from their base - EnchArmorFortifyBlock runs 15/20/25/30/35/40 against a base of 13, and
    // EnchWeaponFireDamage01 sits at half the base. Writing the base's number into every tier would
    // flatten exactly what makes them tiers.
    //
    // WHAT SCALES, measured over the same 432 pairs:
    //   Magnitude  the ladder            (388 of 432 differ)
    //   Duration   copied                (419 of 432 identical) - EXCEPT for effects that have no
    //              magnitude at all, where the duration IS the ladder: EnchWeaponSoulTrap runs
    //              3/5/7/10/15/20 seconds against a base of 4, with magnitude 0 throughout.
    //   Area       copied                (432 of 432 identical)
    //
    // The tool already knows which case an effect is in: MagicEffects.HasMagnitude comes from the
    // scan.
    public sealed class FamilyEffectChange
    {
        public EnchantmentRecord Child { get; init; } = null!;

        public string MagicEffectKey { get; init; } = "";
        public string Label { get; init; } = "";

        // Remove this effect from the child (the base no longer carries it) instead of adding it.
        public bool IsRemoval { get; init; }

        public float Magnitude { get; set; }
        public int Duration { get; set; }
        public int Area { get; set; }

        // What the child's own effects say about where it sits relative to the base. Null when
        // nothing could be derived - then the values above are the base's, unchanged.
        public double? Factor { get; init; }

        // Set when the row needs a human: no factor at all, or several shared effects that disagree
        // about it. Empty means the proposal stands on its own.
        public string Note { get; init; } = "";

        public bool NeedsAttention => !string.IsNullOrEmpty(Note);
    }

    public static class EnchantmentFamily
    {
        // Two factors count as the same up to this much - vanilla ladders are clean multiples, so
        // anything past it is a genuine disagreement, not rounding.
        private const double FactorTolerance = 0.01;

        public static List<EnchantmentRecord> ChildrenOf(
            EnchantmentRecord parent, IEnumerable<EnchantmentRecord> all)
        {
            if (parent == null || string.IsNullOrEmpty(parent.Key)) return new List<EnchantmentRecord>();

            return (all ?? Enumerable.Empty<EnchantmentRecord>())
                .Where(e => e != null
                            && !ReferenceEquals(e, parent)
                            && string.Equals(e.BaseEnchantmentKey, parent.Key, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        // "This child sits at x times the base." Read from the effects the two already share:
        // magnitude first, duration second for the magnitude-less effects. Returns null when there
        // is nothing to read it from - 27 of 387 children share no effect with their base at all,
        // and another 10 have a base magnitude of 0.
        //
        // Several shared effects can disagree (55 of the 67 children that have more than one do:
        // EnchRobesCollegeIllusion05 reads 4.40 from one and 15.00 from the other). The median is
        // taken and the caller is told, rather than picking one silently.
        public static double? DeriveFactor(
            IEnumerable<EnchantmentEffectRecord> parentEffects,
            IEnumerable<EnchantmentEffectRecord> childEffects,
            out bool factorsDisagree)
        {
            factorsDisagree = false;

            var parentByMgef = ByMgef(parentEffects);
            var childByMgef = ByMgef(childEffects);

            var ratios = Ratios(parentByMgef, childByMgef, e => e.Magnitude);
            if (ratios.Count == 0)
                ratios = Ratios(parentByMgef, childByMgef, e => e.Duration);

            if (ratios.Count == 0) return null;

            factorsDisagree = ratios.Max() - ratios.Min() > FactorTolerance;
            return Median(ratios);
        }

        // What applying the base's effect list to its children would change, child by child.
        // hasMagnitude answers "does this MGEF carry a magnitude at all" - the scan's flag.
        public static List<FamilyEffectChange> Plan(
            EnchantmentRecord parent,
            IEnumerable<EnchantmentRecord> children,
            Func<string, bool> hasMagnitude,
            Func<string, string>? label = null)
        {
            var plan = new List<FamilyEffectChange>();
            if (parent == null) return plan;

            var parentEffects = parent.Effects?.ToList() ?? new List<EnchantmentEffectRecord>();

            foreach (var child in children ?? Enumerable.Empty<EnchantmentRecord>())
            {
                var childEffects = child.Effects?.ToList() ?? new List<EnchantmentEffectRecord>();
                var factor = DeriveFactor(parentEffects, childEffects, out bool disagree);

                var childKeys = new HashSet<string>(
                    childEffects.Select(e => e.MagicEffectKey ?? ""), StringComparer.OrdinalIgnoreCase);

                foreach (var effect in parentEffects)
                {
                    if (childKeys.Contains(effect.MagicEffectKey ?? "")) continue;
                    plan.Add(Propose(child, effect, factor, disagree, hasMagnitude, label));
                }

                var parentKeys = new HashSet<string>(
                    parentEffects.Select(e => e.MagicEffectKey ?? ""), StringComparer.OrdinalIgnoreCase);

                foreach (var effect in childEffects)
                {
                    if (parentKeys.Contains(effect.MagicEffectKey ?? "")) continue;

                    plan.Add(new FamilyEffectChange
                    {
                        Child = child,
                        MagicEffectKey = effect.MagicEffectKey ?? "",
                        Label = label?.Invoke(effect.MagicEffectKey ?? "") ?? Describe(effect),
                        IsRemoval = true,
                        Magnitude = effect.Magnitude,
                        Duration = effect.Duration,
                        Area = effect.Area,
                    });
                }
            }

            return plan;
        }

        // The variant's new effect list: what it has today, minus the planned removals, plus the
        // planned additions with the values the preview ended up with. Pure, so the numbers can be
        // checked without a database behind them - the caller writes the result.
        //
        // SaveEnchantmentEffects replaces a record's whole list, so every change for one variant has
        // to go through here together.
        public static List<EnchantmentEffectRecord> ApplyTo(
            string childKey,
            IEnumerable<EnchantmentEffectRecord> existing,
            IEnumerable<(FamilyEffectChange Change, float Magnitude, int Duration, int Area)> changes,
            Func<string, (string EditorId, string Name)>? describe = null)
        {
            var result = (existing ?? Enumerable.Empty<EnchantmentEffectRecord>()).ToList();

            foreach (var (change, magnitude, duration, area) in changes ?? Enumerable.Empty<(FamilyEffectChange, float, int, int)>())
            {
                if (change.IsRemoval)
                {
                    result.RemoveAll(e => string.Equals(e.MagicEffectKey, change.MagicEffectKey,
                                                        StringComparison.OrdinalIgnoreCase));
                    continue;
                }

                // Never twice: PRIMARY KEY(EnchantmentKey, MagicEffectKey). A plan built against a
                // stale list could otherwise insert a duplicate that the save would collapse.
                result.RemoveAll(e => string.Equals(e.MagicEffectKey, change.MagicEffectKey,
                                                    StringComparison.OrdinalIgnoreCase));

                var (editorId, name) = describe?.Invoke(change.MagicEffectKey) ?? ("", "");

                result.Add(new EnchantmentEffectRecord
                {
                    EnchantmentKey = childKey,
                    MagicEffectKey = change.MagicEffectKey,
                    EditorID = editorId,
                    Name = name,
                    Magnitude = magnitude,
                    Duration = duration,
                    Area = area,
                });
            }

            return result;
        }

        private static FamilyEffectChange Propose(
            EnchantmentRecord child, EnchantmentEffectRecord source, double? factor, bool disagree,
            Func<string, bool> hasMagnitude, Func<string, string>? label)
        {
            string mgef = source.MagicEffectKey ?? "";
            bool carriesMagnitude = hasMagnitude?.Invoke(mgef) ?? true;

            float magnitude = source.Magnitude;
            int duration = source.Duration;
            string note = "";

            if (factor == null)
            {
                note = "no factor could be derived — the base's values were copied";
            }
            else if (carriesMagnitude && source.Magnitude != 0)
            {
                magnitude = (float)Math.Round(source.Magnitude * factor.Value, 2);
            }
            else if (!carriesMagnitude && source.Duration != 0)
            {
                duration = (int)Math.Round(source.Duration * factor.Value, MidpointRounding.AwayFromZero);
            }
            else
            {
                // An effect with nothing to scale - a magnitude-less, duration-less one, or a value
                // of 0 that multiplying would leave at 0 anyway.
                note = "nothing to scale — the base's values were copied";
            }

            if (note.Length == 0 && disagree)
                note = "the child's own effects disagree about the factor — the median was used";

            return new FamilyEffectChange
            {
                Child = child,
                MagicEffectKey = mgef,
                Label = label?.Invoke(mgef) ?? Describe(source),
                Magnitude = magnitude,
                Duration = duration,
                Area = source.Area,
                Factor = factor,
                Note = note,
            };
        }

        private static List<double> Ratios(
            Dictionary<string, EnchantmentEffectRecord> parent,
            Dictionary<string, EnchantmentEffectRecord> child,
            Func<EnchantmentEffectRecord, double> value)
        {
            var ratios = new List<double>();
            foreach (var (mgef, p) in parent)
            {
                if (!child.TryGetValue(mgef, out var c)) continue;

                double basis = value(p);
                if (Math.Abs(basis) < 0.0001) continue;

                ratios.Add(value(c) / basis);
            }
            return ratios;
        }

        private static double Median(List<double> values)
        {
            var sorted = values.OrderBy(v => v).ToList();
            int mid = sorted.Count / 2;

            return sorted.Count % 2 == 1
                ? sorted[mid]
                : (sorted[mid - 1] + sorted[mid]) / 2.0;
        }

        private static Dictionary<string, EnchantmentEffectRecord> ByMgef(
            IEnumerable<EnchantmentEffectRecord>? effects)
        {
            var map = new Dictionary<string, EnchantmentEffectRecord>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in effects ?? Enumerable.Empty<EnchantmentEffectRecord>())
            {
                if (e?.MagicEffectKey == null) continue;
                map[e.MagicEffectKey] = e;   // first-wins is not possible here: PK(Ench, Mgef)
            }
            return map;
        }

        private static string Describe(EnchantmentEffectRecord e)
        {
            var editorId = (e.EditorID ?? "").Trim();
            var name = (e.Name ?? "").Trim();

            if (editorId.Length > 0 && name.Length > 0) return $"{editorId} | {name}";
            if (editorId.Length > 0) return editorId;
            if (name.Length > 0) return name;

            return e.MagicEffectKey ?? "";
        }
    }
}
