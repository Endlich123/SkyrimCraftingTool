using SkyrimCraftingTool.Model;
using System;
using System.Collections.Generic;
using System.Linq;

namespace SkyrimCraftingTool.Services
{
    // What one tab exports and imports (docs/TabExport-Plan.md §3.2): its export units, the folder
    // its files go to, and the word its messages use. Everything else - file I/O, preview, the
    // conflict window, apply, the dialogs - is shared (ImportExportFlow, TabImportExportVM), so a
    // tab is described here and nowhere else.
    public sealed class TabExportModule
    {
        public TabExportModule(string folder, string noun, params ExportUnit[] units)
        {
            Folder = folder;
            Noun = noun;
            Units = units;
        }

        // Subfolder under Output\Exports\. Part of the on-disk layout - do not rename casually.
        public string Folder { get; }

        // Singular, lower case: "item", "enchantment". Messages add "(s)".
        public string Noun { get; }

        public IReadOnlyList<ExportUnit> Units { get; }

        // A DTO belongs to this tab when one of its units wrote it. The import filters on this before
        // anything else, so a tab can never apply another tab's record - not even from a legacy file
        // that mixes both.
        public bool Accepts(EditedItemDto? dto) =>
            dto != null && Units.Any(u => string.Equals(u.Table, dto.Table, StringComparison.Ordinal));
    }

    // The registry. A unit appears in exactly one module - that is what "export is per tab" means.
    public static class TabExportModules
    {
        // Items + their recipes, and the two per-item recipe decisions (which one is main, which of a
        // mod's were taken out). NOT the placement: that is the Container/LeveledList tab's (TE-6).
        public static readonly TabExportModule Items =
            new("Items", "item", ExportUnits.Armor, ExportUnits.Weapons, ExportUnits.Cobj,
                ExportUnits.MainRecipe, ExportUnits.RecipeRemoved);

        public static readonly TabExportModule Enchantments =
            new("Enchantments", "enchantment", ExportUnits.Enchantments, ExportUnits.WornRestrictionList);

        // TE-3. Scanned spells' field and effect edits, and spells the user created.
        public static readonly TabExportModule Spells =
            new("Spells", "spell", ExportUnits.Spells);

        // TE-4. Field edits, skills, factions and the spell/perk/item lists of single NPCs. NPC
        // groups are their own tab and their own module.
        public static readonly TabExportModule Npcs =
            new("NPCs", "NPC", ExportUnits.Npc);

        // TE-5. Every group, auto and user alike - the definition, not the members.
        public static readonly TabExportModule NpcGroups =
            new("NpcGroups", "NPC group", ExportUnits.NpcGroup);

        // TE-6. Everything this tab decides: where items are placed (armor, weapons AND the world
        // items that have no other tab), list chances, removed and restored entries, and the user's
        // own lists with where they hang. One module, so an own list and the placements into it
        // travel - and are matched - together.
        public static readonly TabExportModule Containers =
            new("Containers", "container/list change",
                ExportUnits.UserLeveledList, ExportUnits.Placement, ExportUnits.LeveledListEdit,
                ExportUnits.LeveledListRemoved, ExportUnits.LeveledListRestored, ExportUnits.ContainerRemoved);

        public static readonly IReadOnlyList<TabExportModule> All = new[] { Items, Enchantments, Spells, Npcs, NpcGroups, Containers };

        // Every unit of every tab - what "Import all" in Settings previews in ONE pass over the files
        // of all tab folders (user, 2026-10-18: Settings uses the tabs' exports, it is not a second
        // system). One pass means one ImportKeyMap: an own enchantment that gets a new key on a
        // collision is remapped for the items and NPCs pointing at it in the same import.
        public static readonly IReadOnlyList<ExportUnit> AllUnits = All.SelectMany(m => m.Units).ToArray();
    }
}
