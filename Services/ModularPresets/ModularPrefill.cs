using SkyrimCraftingTool.Model;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace SkyrimCraftingTool.Services.ModularPresets
{
    public class ModularPrefillResult
    {
        public ModularSelection Selection { get; set; } = new();

        // Bits of the item's mask no slot definition covers. Shown in the dry-run as "not assigned".
        public uint UnassignedBits { get; set; }

        // How the material was found: "keyword", "name: <term>", or "" when none was.
        public string MaterialSource { get; set; } = "";

        // Why each addon was picked: "keyword" or "name: <term>". Shown next to it, because a name
        // pattern firing is otherwise impossible to trace.
        public Dictionary<string, string> AddonSources { get; } = new(StringComparer.OrdinalIgnoreCase);

        public List<string> Notes { get; set; } = new();
    }

    // Reads an item ONCE to pre-fill its selection. The item's keywords are only a hint here - after
    // this, the selection decides and the rules recompute every managed keyword.
    public static class ModularPrefill
    {
        public static ModularPrefillResult Prefill(ModularCatalog catalog, ModularItemState item)
        {
            var result = new ModularPrefillResult();
            var sel = result.Selection;

            // Type: straight from the item's ArmorType.
            var type = catalog.Types.FirstOrDefault(t => string.Equals(t.ArmorType, item.ArmorType, StringComparison.OrdinalIgnoreCase));
            if (type != null) sel.TypeName = type.Name;
            else result.Notes.Add($"No type for ArmorType '{item.ArmorType}'.");

            // Slots.
            var (slotNames, unassigned) = CoverMask(catalog.Slots, item.BodySlotMask);
            sel.SlotNames = slotNames;
            result.UnassignedBits = unassigned;
            if (unassigned != 0) result.Notes.Add($"Slot bits without a definition: {DescribeBits(unassigned)}.");

            // Material: keyword first, then the optional name fallback. Clothing never by keyword and
            // only name-only materials without a keyword (Silver, Gold - vanilla jewelry recipes): 1,011
            // clothing items of the user's list would otherwise have picked up a material keyword, by a
            // stray ArmorMaterial keyword or a name like "Leather Boots". Any material can be chosen by hand.
            var keywords = new HashSet<string>(item.Keywords, StringComparer.OrdinalIgnoreCase);
            bool isClothing = string.Equals(item.ArmorType, "Clothing", StringComparison.OrdinalIgnoreCase);
            //
            // Among several candidates, one with set totals for the item's own type wins over the
            // catalog's first: a Heavy and a Light tier of the same material share a name pattern
            // ("stalhrim*"), and a light piece landing on the heavy tier would just keep its values.
            var byKeyword = isClothing ? null : PreferOwnType(
                catalog.Materials.Where(m => !string.IsNullOrWhiteSpace(m.MaterialKeyword) && keywords.Contains(m.MaterialKeyword)),
                item.ArmorType);
            if (byKeyword != null)
            {
                sel.MaterialName = byKeyword.Name;
                result.MaterialSource = "keyword";
            }
            else
            {
                // A material WITHOUT keyword is a name-only one ("Silver", "Nightingale") and only counts
                // for the types it has totals for - the "Gold" of rings must not reach a golden helmet.
                // Clothing takes only those: no keyword, so nothing to break Matching Set with.
                bool NameOnly(MaterialModule m) => string.IsNullOrWhiteSpace(m.MaterialKeyword);
                var hits = catalog.Materials
                    .Where(m => NameOnly(m) ? m.Totals.ContainsKey(item.ArmorType) : !isClothing)
                    .Select(m => (m, hit: NameMatcher.Match(item.Name, m.NamePatterns, m.NameExclusions)))
                    .Where(x => x.hit != null)
                    .ToList();
                var chosen = PreferOwnType(hits.Select(x => x.m), item.ArmorType);
                if (chosen != null)
                {
                    sel.MaterialName = chosen.Name;
                    result.MaterialSource = "name: " + hits.First(x => ReferenceEquals(x.m, chosen)).hit;
                }
            }
            if (sel.MaterialName.Length == 0 && !isClothing)
                result.Notes.Add(type != null && (type.FallbackTotals != null
                                                  || type.FallbackCraft?.Materials.Count > 0
                                                  || type.FallbackTemper?.Materials.Count > 0
                                                  || type.FallbackBreakdown?.Materials.Count > 0)
                    ? "No material found (neither keyword nor name) - the type's fallback values and recipes apply."
                    : "No material found (neither keyword nor name).");

            PickAddons(catalog.Addons, item.Name, item.EditorID, item.Plugin, keywords, sel.SlotNames, sel.AddonNames, result.AddonSources);
            return result;
        }

        // Addons: the item already carries all its keywords, OR its name matches. OR, not AND -
        // "Golden Ring" is meant to GET JewelryExpensive from the "Expensive" addon, not to need it.
        //
        // Repeated until nothing new is picked, with the keywords the picked addons ADD counted as
        // carried: "Premium" adds ClothingRich, so "Rich" (triggered by ClothingRich) comes along in
        // the same pass. Checking only the item's own keywords made the first Apply pick Premium
        // and the second also Rich - a different value on every run (user, 2026-10-21).
        //
        // scope: the item's slot definitions (armor) or its weapon type (weapons) - what an addon's
        // OnlySlots is checked against. Shared by both pre-fills.
        internal static void PickAddons(IReadOnlyList<KeywordAddon> addons, string itemName, string editorId, string plugin,
            IEnumerable<string> itemKeywords, IReadOnlyCollection<string> scope, List<string> picked, Dictionary<string, string> sources)
        {
            var carried = new HashSet<string>(itemKeywords, StringComparer.OrdinalIgnoreCase);
            var addedBy = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // keyword -> addon that adds it
            bool pickedSomething = true;
            while (pickedSomething)
            {
                pickedSomething = false;
                foreach (var a in addons)
                {
                    if (picked.Contains(a.Name, StringComparer.OrdinalIgnoreCase)) continue;
                    if (a.OnlySlots is { Count: > 0 } && !a.OnlySlots.Any(s => scope.Contains(s, StringComparer.OrdinalIgnoreCase)))
                        continue;
                    if (a.OnlyPlugins is { Count: > 0 } && !a.OnlyPlugins.Any(p => PluginMatches(plugin, p)))
                        continue;
                    if (a.ExcludePlugins is { Count: > 0 } && a.ExcludePlugins.Any(p => PluginMatches(plugin, p)))
                        continue;

                    string? source = null;
                    if (a.Keywords.Count > 0 && a.Keywords.All(carried.Contains))
                    {
                        var via = a.Keywords.Select(k => addedBy.TryGetValue(k, out var by) ? by : null).FirstOrDefault(by => by != null);
                        source = via == null ? "keyword" : $"keyword via {via}";
                    }
                    else if (NameMatcher.Match(itemName, a.NamePatterns, a.NameExclusions) is string hit)
                        source = "name: " + hit;
                    else if (EditorIdMatch(editorId, a.EditorIdPatterns) is string ed)
                        source = "editor id: " + ed;
                    else if (a.Keywords.Count == 0 && string.IsNullOrWhiteSpace(a.NamePatterns) && string.IsNullOrWhiteSpace(a.EditorIdPatterns))
                    {
                        // Neither keyword nor name trigger: the whitelists ARE the trigger ("Premium"
                        // listed 40 plugins and never fired). With a keyword or a name they stay pure
                        // filters: "Bikini only from TAWOBA" must not hit every TAWOBA piece, and
                        // "Jewelry" (ArmorJewelry, only on ring/amulet slots) must not hit every gorget
                        // on slot 45 - which it did for a day, when keywords did not count here
                        // (2026-10-21, 561 armor pieces got ArmorJewelry). To chain addons use
                        // AlsoAddons, not a shared keyword. A blacklist alone triggers nothing.
                        if (a.OnlyPlugins is { Count: > 0 })
                            source = "plugin";
                        else if (a.OnlySlots is { Count: > 0 })
                            source = "slot";
                    }

                    if (source == null) continue;
                    Pick(a, source);
                    pickedSomething = true;
                }
            }

            // An addon and its companions (AlsoAddons), recorded so the build box lists them too.
            void Pick(KeywordAddon a, string source)
            {
                foreach (var name in ModularPresetEngine.WithAlsoAddons(addons, new[] { a.Name }))
                {
                    if (picked.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
                    var addon = addons.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
                    if (addon == null) continue;
                    picked.Add(addon.Name);
                    sources[addon.Name] = ReferenceEquals(addon, a) ? source : $"with {a.Name}";
                    foreach (var k in addon.Keywords)
                        if (carried.Add(k)) addedBy[k] = addon.Name;
                }
            }
        }

        // The pattern that matched the EditorID, or null. Comma-separated, "*" = any run of
        // characters, case-insensitive, the whole EditorID must match: "Skin*" takes SkinChicken,
        // "*Skin" DLC2NetchSkin - neither takes ArmorDragonSkinCuirass.
        public static string? EditorIdMatch(string? editorId, string? patterns)
        {
            if (string.IsNullOrWhiteSpace(editorId) || string.IsNullOrWhiteSpace(patterns)) return null;
            foreach (var raw in patterns.Split(','))
            {
                var p = raw.Trim();
                if (p.Length == 0) continue;
                // Without * it is a plain name - a list of several hundred vanilla uniques stays cheap.
                if (!p.Contains('*'))
                {
                    if (string.Equals(editorId, p, StringComparison.OrdinalIgnoreCase)) return p;
                    continue;
                }
                var regex = "^" + System.Text.RegularExpressions.Regex.Escape(p).Replace(@"\*", ".*") + "$";
                if (System.Text.RegularExpressions.Regex.IsMatch(editorId, regex, System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                    return p;
            }
            return null;
        }

        // "TAWOBA*" = every plugin whose file name starts with TAWOBA; otherwise the exact file name.
        public static bool PluginMatches(string plugin, string pattern)
        {
            if (string.IsNullOrWhiteSpace(pattern) || string.IsNullOrEmpty(plugin)) return false;
            var p = pattern.Trim();
            return p.EndsWith("*")
                ? plugin.StartsWith(p.TrimEnd('*'), StringComparison.OrdinalIgnoreCase)
                : string.Equals(plugin, p, StringComparison.OrdinalIgnoreCase);
        }

        private static MaterialModule? PreferOwnType(IEnumerable<MaterialModule> candidates, string armorType)
        {
            var list = candidates.ToList();
            return list.FirstOrDefault(m => m.Totals.ContainsKey(armorType)) ?? list.FirstOrDefault();
        }

        // An exact match wins. Otherwise cover the mask greedily with the largest definitions that
        // fit entirely inside what is still uncovered (catalog order breaks ties), so 31+42+43
        // becomes "Helm" (31+42) + "Ear" (43) rather than "Hair" + "Circlet" + "Ear".
        public static (List<string> Names, uint Unassigned) CoverMask(IEnumerable<SlotDefinition> definitions, uint mask)
        {
            // A record without any slot is only claimed by a definition without slots ("No slot"),
            // which the user adds on purpose. Without one, it gets no definition and the engine
            // leaves its values alone.
            if (mask == 0)
            {
                var none = definitions.FirstOrDefault(d => ToMask(d.Bits) == 0);
                return (none == null ? new List<string>() : new List<string> { none.Name }, 0);
            }

            var defs = definitions
                .Select((d, index) => (Def: d, Mask: ToMask(d.Bits), Index: index))
                .Where(x => x.Mask != 0)
                .ToList();

            var exact = defs.FirstOrDefault(x => x.Mask == mask);
            if (exact.Def != null) return (new List<string> { exact.Def.Name }, 0);

            var names = new List<string>();
            uint remaining = mask;
            foreach (var x in defs.OrderByDescending(x => BitOperations.PopCount(x.Mask)).ThenBy(x => x.Index))
            {
                if ((x.Mask & remaining) == x.Mask)
                {
                    names.Add(x.Def.Name);
                    remaining &= ~x.Mask;
                    if (remaining == 0) break;
                }
            }
            return (names, remaining);
        }

        public static uint ToMask(IEnumerable<int> bits)
        {
            uint mask = 0;
            foreach (var b in bits)
                if (b >= 0 && b < 32) mask |= 1u << b;
            return mask;
        }

        // "31, 43" - game slot numbers, the way modders and xEdit name them.
        public static string DescribeBits(uint mask)
        {
            var slots = new List<string>();
            for (int b = 0; b < 32; b++)
                if ((mask & (1u << b)) != 0) slots.Add((30 + b).ToString());
            return string.Join(", ", slots);
        }
    }
}
