using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using SkyrimCraftingTool.Model;
using SkyrimCraftingTool.Services;

namespace SkyrimCraftingTool.ViewModel
{
    // THE ROWS OF THE ITEM TAB'S ENCHANTMENT PICKER (2026-11-08).
    //
    // Each enchantment becomes a FormIDRecord, so the shared RecordChoiceTemplate and PickerFilter
    // work on it unchanged:
    //   Name        - "EditorID | Name" (see MainContentVM.EnchantmentLabel for why both halves)
    //   FullName    - the in-game name, first line of the tooltip
    //   Kind        - Armor / Weapon / Staff / Other, the same split as the Enchantments tab's tree
    //                 (EnchantmentCategoryHelper). Grey after the label, and the chips.
    //   Description - what the item card says: each effect's in-game text with its numbers filled in.
    //
    // The chips are a filter, as in the Enchantments tab's effect picker: nothing is preselected from
    // the item's type.
    public static class EnchantmentChoices
    {
        public static readonly IReadOnlyList<string> GroupOrder = new[]
        {
            nameof(EnchantmentCategory.Armor),
            nameof(EnchantmentCategory.Weapon),
            nameof(EnchantmentCategory.Staff),
            nameof(EnchantmentCategory.Other),
        };

        public static List<FormIDRecord> Build(
            IEnumerable<EnchantmentRecord> enchantments,
            IEnumerable<MagicEffectsRecords>? magicEffects)
        {
            var list = enchantments.ToList();

            var effectsByKey = new Dictionary<string, MagicEffectsRecords>(StringComparer.OrdinalIgnoreCase);
            foreach (var m in magicEffects ?? Enumerable.Empty<MagicEffectsRecords>())
                if (!string.IsNullOrEmpty(m.Key)) effectsByKey[m.Key] = m;

            // Two records can still share an EditorID across plugins - measured, exactly two pairs
            // do, each a Skyrim.esm record and a Dawnguard.esm one. Only those get their plugin
            // appended: putting it on all 632 would be noise for the 630 that read fine without it.
            var ambiguous = list
                .GroupBy(MainContentVM.EnchantmentLabel, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var result = new List<FormIDRecord> { MainContentVM.NoEnchantmentChoice() };
            result.AddRange(
                list.Select(e =>
                    {
                        var label = MainContentVM.EnchantmentLabel(e);
                        if (ambiguous.Contains(label))
                            label += $"  [{KeyFactory.SplitMasterKey(e.Key).master}]";

                        return new FormIDRecord
                        {
                            Key = e.Key,
                            Name = label,
                            FullName = (e.Name ?? "").Trim(),
                            Kind = EnchantmentCategoryHelper.Classify(e).ToString(),
                            Description = Describe(e, effectsByKey),
                        };
                    })
                    .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase));

            return result;
        }

        // One line per effect. An effect without a text of its own (or before the scan that writes
        // MagicEffects.Description) shows its name and numbers instead, so tiers stay apart.
        public static string Describe(EnchantmentRecord ench, IReadOnlyDictionary<string, MagicEffectsRecords> effectsByKey)
        {
            var lines = new List<string>();

            foreach (var eff in ench.Effects)
            {
                effectsByKey.TryGetValue(eff.MagicEffectKey ?? "", out var mgef);

                var text = mgef?.Description;
                if (!string.IsNullOrWhiteSpace(text))
                {
                    lines.Add(FillPlaceholders(text.Trim(), eff.Magnitude, eff.Duration, eff.Area));
                    continue;
                }

                var name = FirstNonEmpty(mgef?.Name, eff.Name, mgef?.EditorID, eff.EditorID, eff.MagicEffectKey);
                var numbers = new List<string>();
                if (eff.Magnitude != 0) numbers.Add(Number(eff.Magnitude));
                if (eff.Duration != 0) numbers.Add($"{eff.Duration} s");
                if (eff.Area != 0) numbers.Add($"{eff.Area} ft");
                lines.Add(numbers.Count > 0 ? $"{name}: {string.Join(", ", numbers)}" : name);
            }

            return string.Join("\n", lines);
        }

        private static readonly Regex Placeholder = new(@"<\s*(mag|dur|area|\d+(?:\.\d+)?)\s*>",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // The game's placeholders: <mag>, <dur>, <area> take the effect's values, a bare number in
        // brackets ("<25>% more effective") is shown without them. Anything else - <Global=...>, or
        // a mod that brackets a word for emphasis - stays as written.
        public static string FillPlaceholders(string text, float magnitude, int duration, int area)
            => Placeholder.Replace(text, m => m.Groups[1].Value.ToLowerInvariant() switch
            {
                "mag" => Number(magnitude),
                "dur" => duration.ToString(CultureInfo.InvariantCulture),
                "area" => area.ToString(CultureInfo.InvariantCulture),
                var n => n,
            });

        private static string Number(float value) => value.ToString("0.#", CultureInfo.InvariantCulture);

        private static string FirstNonEmpty(params string?[] values)
            => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim() ?? "";
    }
}
