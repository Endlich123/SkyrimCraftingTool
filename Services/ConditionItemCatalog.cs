using SkyrimCraftingTool.Model;
using System;
using System.Collections.Generic;
using System.Linq;

namespace SkyrimCraftingTool.Services
{
    // What a GetItemCount condition can point at: armor, weapons, every world item (misc, books,
    // food, ingredients, keys, ammo, scrolls, soul gems) and form lists. Measured on the real modlist
    // (2026-10-24) that covers all but 15 of 3,880 GetItemCount targets; misc items alone are 2,098.
    //
    // A static catalogue rather than one more list threaded through every condition owner
    // (ItemNodeVM, MultiSelectDetailVM, BreakdownRecipeVM, the preset editors) and every
    // ConditionMapper.ToViewModel call: it is ~54,000 records, the same for all of them, and is
    // replaced as a whole whenever MainContentVM applies a cache snapshot.
    public static class ConditionItemCatalog
    {
        private static IReadOnlyList<FormIDRecord> _all = Array.Empty<FormIDRecord>();
        private static Dictionary<string, FormIDRecord> _byKey = new(StringComparer.OrdinalIgnoreCase);

        public static IReadOnlyList<FormIDRecord> All => _all;

        public static void Set(IEnumerable<FormIDRecord> records)
        {
            var list = records.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
            var byKey = new Dictionary<string, FormIDRecord>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in list) byKey.TryAdd(r.Key, r);   // first wins; armor and weapons never share a key
            _all = list;
            _byKey = byKey;
        }

        // Case-insensitive: a Plugin|FormID key can arrive in two casings (see key-case notes).
        public static FormIDRecord? Find(string? key) =>
            !string.IsNullOrEmpty(key) && _byKey.TryGetValue(key, out var r) ? r : null;

        // The first `max` records whose display name contains the text. A ComboBox over all 54,000
        // is not usable even virtualized; the editor shows these and the user narrows by typing.
        public static List<FormIDRecord> Search(string? text, int max = 100)
        {
            if (string.IsNullOrWhiteSpace(text)) return _all.Take(max).ToList();
            var t = text.Trim();
            return _all.Where(r => r.Name.Contains(t, StringComparison.OrdinalIgnoreCase)).Take(max).ToList();
        }

        // "Iron Ingot (IngotIron) · Misc". The kind matters: a key, a book and a misc item with the
        // same name are common, and the EditorID tells mod copies apart.
        public static FormIDRecord Entry(string key, string name, string editorId, string kind)
        {
            var display = string.IsNullOrWhiteSpace(name) || name == editorId
                ? editorId
                : $"{name} ({editorId})";
            return new FormIDRecord { Key = key, Name = $"{display} · {kind}", Type = kind };
        }
    }
}
