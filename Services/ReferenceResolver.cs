using System;
using System.Collections.Generic;
using SkyrimCraftingTool.Model;

namespace SkyrimCraftingTool.Services
{
    public sealed class ReferenceResolver : IReferenceResolver
    {
        // Vanilla workbench keywords that are always present in Skyrim.esm but get filtered out of
        // MainContentVM.AllAvailableWorkbenches on purpose, so register them here so their recipes'
        // workbench never looks like a dead reference.
        //
        // The two Temper ones have been here from the start. CraftingSmelter joined them when the
        // Breakdown section took it over: crafting an armour in a furnace is not a thing, so it has
        // no business in the general workbench picker - but a smelter recipe still has to resolve.
        // CraftingTanningRack is deliberately NOT in this list: it stays in the general picker,
        // because unlike the furnace it really is a crafting bench.
        private static readonly (string Key, string Name)[] VanillaTemperWorkbenches =
        {
            ("Skyrim.esm|0ADB78", "CraftingSmithingArmorTable"),
            ("Skyrim.esm|088108", "CraftingSmithingSharpeningWheel"),
            ("Skyrim.esm|0A5CCE", "CraftingSmelter"),
        };

        // Case-insensitive, to match the databases: Skyrim ignores case in plugin filenames, so the
        // same plugin can reach a key as "ccBGSSSE001-Fish.esm" from one mod and "ccbgssse001-fish.esm"
        // from another. With an ordinal comparer, a perfectly valid reference whose key happened to
        // arrive in the other spelling resolved to nothing and was drawn with the red dead-reference
        // border. (The indexer is used below rather than Add, so the two spellings collapsing into one
        // entry is a no-op, not a duplicate-key throw.)
        private Dictionary<string, ReferenceLookup> _byKey = new(StringComparer.OrdinalIgnoreCase);

        // Called after every scan, from MainContentVM.ApplyCacheSnapshot, with the freshly rebuilt
        // catalogs. Later kinds win on a key collision (workbenches are a naming subset of keywords,
        // so a "Crafting*" key resolves as Workbench, not Keyword).
        public void Rebuild(
            IEnumerable<FormIDRecord>? keywords,
            IEnumerable<FormIDRecord>? materials,
            IEnumerable<FormIDRecord>? workbenches,
            IEnumerable<FormIDRecord>? perks,
            IEnumerable<FormIDRecord>? quests,
            IEnumerable<ContainerRecord>? containers)
        {
            var map = new Dictionary<string, ReferenceLookup>(StringComparer.OrdinalIgnoreCase);

            Add(map, keywords, ReferenceKind.Keyword);
            Add(map, materials, ReferenceKind.Material);
            Add(map, perks, ReferenceKind.Perk);
            Add(map, quests, ReferenceKind.Quest);

            if (containers != null)
                foreach (var c in containers)
                    if (!string.IsNullOrEmpty(c?.ContainerKey))
                        map[c.ContainerKey] = new ReferenceLookup(true, c.Name, ReferenceKind.Container);

            Add(map, workbenches, ReferenceKind.Workbench);

            foreach (var (key, name) in VanillaTemperWorkbenches)
                map[key] = new ReferenceLookup(true, name, ReferenceKind.Workbench);

            _byKey = map;
        }

        private static void Add(Dictionary<string, ReferenceLookup> map, IEnumerable<FormIDRecord>? records, ReferenceKind kind)
        {
            if (records == null) return;
            foreach (var r in records)
                if (!string.IsNullOrEmpty(r?.Key))
                    map[r.Key] = new ReferenceLookup(true, r.Name, kind);
        }

        public ReferenceLookup Resolve(string? key)
        {
            if (string.IsNullOrEmpty(key)) return ReferenceLookup.Miss;
            return _byKey.TryGetValue(key, out var hit) ? hit : ReferenceLookup.Miss;
        }

        public ReferenceLookup Resolve(string? key, ReferenceKind expected)
        {
            var r = Resolve(key);
            return r.Found && r.Kind == expected ? r : r with { Found = false };
        }

        public bool IsActive(string? key) => Resolve(key).Found;

        public string DisplayName(string? key, string fallback = "")
        {
            var name = Resolve(key).Name;
            if (!string.IsNullOrEmpty(name)) return name!;
            if (!string.IsNullOrEmpty(fallback)) return fallback;
            return key ?? "";
        }
    }
}
