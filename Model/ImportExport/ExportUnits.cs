using Microsoft.Data.Sqlite;
using System.Collections.Generic;

namespace SkyrimCraftingTool.Model
{
    // The export units that exist, and the registry every tab module picks from.
    // docs/TabExport-Plan.md §3.1 / §4.
    //
    // The five units below are the ones that existed before TE-1, moved into unit form unchanged:
    // their SQL still lives in ItemDBHandler.ImportExport.cs, the units only route to it.
    public static class ExportUnits
    {
        public static readonly ExportUnit Armor = new ArmorOrWeaponsUnit("Armor", ItemDBHandler.ArmorShadowColumns);
        public static readonly ExportUnit Weapons = new ArmorOrWeaponsUnit("Weapons", ItemDBHandler.WeaponShadowColumns);
        public static readonly ExportUnit Cobj = new CobjUnit();
        public static readonly ExportUnit Enchantments = new EnchantmentsUnit();
        public static readonly ExportUnit WornRestrictionList = new WornRestrictionListUnit();

        // TE-2: the per-item recipe decisions (RecipeDecisionUnits.cs).
        public static readonly ExportUnit MainRecipe = new MainRecipeUnit();
        public static readonly ExportUnit RecipeRemoved = new RecipeRemovedUnit();

        // TE-3.
        public static readonly ExportUnit Spells = new SpellsUnit();

        // TE-4.
        public static readonly ExportUnit Npc = new NpcUnit();

        // TE-5 (NpcGroupUnit.cs).
        public static readonly ExportUnit NpcGroup = new NpcGroupUnit();

        // TE-6 (ContainerUnits.cs).
        public static readonly ExportUnit Placement = new PlacementUnit();
        public static readonly ExportUnit LeveledListEdit = new LeveledListEditUnit();
        public static readonly ExportUnit LeveledListRemoved = new OwnerSetUnit(
            "LeveledListRemoved", "LeveledListRemovedEntry", "ListKey", new[] { "Reference" }, "LeveledList", "Key", "The removed entries");
        public static readonly ExportUnit LeveledListRestored = new OwnerSetUnit(
            "LeveledListRestored", "LeveledListRestoredEntry", "ListKey", new[] { "Reference", "Level", "Count" }, "LeveledList", "Key", "The restored entries");
        public static readonly ExportUnit ContainerRemoved = new OwnerSetUnit(
            "ContainerRemoved", "ContainerRemovedEntry", "ContainerKey", new[] { "Reference" }, "Container", "ContainerKey", "The removed entries");
        public static readonly ExportUnit UserLeveledList = new UserLeveledListUnit();

        // Every unit - the first five in the order the old GetEditedItems produced them. What the ItemDBHandler
        // facade (GetEditedItems/PreviewImport/ApplyImport without a unit list) works on - kept so
        // the round-trip suites and any whole-database caller see exactly the old behaviour.
        public static readonly IReadOnlyList<ExportUnit> All = new[]
        {
            Armor, Weapons, Cobj, Enchantments, WornRestrictionList, MainRecipe, RecipeRemoved, Spells, Npc, NpcGroup,
            Placement, LeveledListEdit, LeveledListRemoved, LeveledListRestored, ContainerRemoved, UserLeveledList,
        };

        // Shared Probe for every unit keyed by one row in one table with a LastChanged column.
        // ExecuteScalar returns C# null only when no row matched; a row that exists but was never
        // edited comes back as DBNull (LastChanged IS NULL). Treating both alike once sent every
        // never-edited target - the normal case for a first import - into ToSkipMissing.
        internal static ImportTarget ProbeKeyedRow(SqliteConnection connection, string table, string key)
        {
            if (!UserRecords.TableExists(connection, table)) return ImportTarget.Missing;

            using var cmd = connection.CreateCommand();
            cmd.CommandText = $"SELECT LastChanged FROM {table} WHERE Key = @key";
            cmd.Parameters.AddWithValue("@key", key);
            var local = cmd.ExecuteScalar();
            return local == null ? ImportTarget.Missing : ImportTarget.FromLastChanged(local as string);
        }

        // A field that may point at one of the user's own records, and where that record lives.
        // Label and Tab only word the note when the record is missing here.
        internal sealed record OwnRecordRef(string Field, string TargetTable, string Label, string Tab);

        // At export: the Uid of every user-created record a DTO's fields point at (UserRecords §3).
        internal static void AnnotateRefs(SqliteConnection connection, IEnumerable<EditedItemDto> items, IReadOnlyList<OwnRecordRef> refs)
        {
            foreach (var item in items)
                foreach (var r in refs)
                {
                    if (!item.Fields.TryGetValue(r.Field, out var value)) continue;
                    var uid = UserRecords.UidForReference(connection, r.TargetTable, value);
                    if (uid.Length == 0) continue;
                    (item.Refs ??= new Dictionary<string, string>())[r.Field] = uid;
                }
        }

        // At import: point every annotated field at this machine's copy of the record. Found by Uid
        // first (imported earlier, maybe under another Key), then through this import's own key
        // map. Not found means the record is not here yet - the field is NOT applied, because the
        // file's Key may well name a different record of this machine's, and saying so beats
        // silently assigning the wrong one.
        internal static void FollowRefs(SqliteConnection connection, IEnumerable<EditedItemDto> items, IReadOnlyList<OwnRecordRef> refs,
            ImportKeyMap map, List<string> notes)
        {
            foreach (var item in items)
            {
                if (item.Refs == null) continue;
                foreach (var r in refs)
                {
                    if (!item.Refs.TryGetValue(r.Field, out var uid) || !item.Fields.TryGetValue(r.Field, out var value)) continue;

                    var local = UserRecords.ResolveReference(connection, r.TargetTable, uid, map);

                    if (local != null)
                    {
                        item.Fields[r.Field] = local;
                        continue;
                    }

                    item.Fields.Remove(r.Field);
                    var who = string.IsNullOrEmpty(item.DisplayName) ? item.Key : item.DisplayName;
                    notes.Add($"⚠ {who}: its {r.Label} is one of your own records that this setup does not have yet. " +
                              $"Import the {r.Tab} tab first, then this one again - until then that field was left as it is.");
                }
            }
        }

        private sealed class ArmorOrWeaponsUnit : ExportUnit
        {
            // The item's enchantment may be one the user created (TE-3).
            private static readonly OwnRecordRef[] Refs =
            {
                new("IsEditedObjectEffectKey", "Enchantments", "enchantment", "Enchantments"),
            };

            private readonly string _table;
            private readonly string[] _shadowColumns;

            public ArmorOrWeaponsUnit(string table, string[] shadowColumns)
            {
                _table = table;
                _shadowColumns = shadowColumns;
            }

            public override string Table => _table;

            // The placement is stored on the item but exported by the Container/LeveledList tab
            // (PlacementUnit, TE-6 / plan §6.1) - books and the like have no Items tab to travel with. An
            // item whose only edit was its placement therefore has nothing left here and is not
            // exported by this unit at all. Importing an OLD file that still carries the placement on
            // the item keeps working: the whitelist (ArmorShadowColumns) still has the column.
            internal const string PlacementColumn = "IsEditedContainerString";

            internal override List<EditedItemDto> Collect(SqliteConnection connection, ExportScope scope, string? scopeValue)
            {
                var items = ItemDBHandler.GetEditedArmorOrWeapons(connection, _table, _shadowColumns, scope, scopeValue);
                foreach (var item in items) item.Fields.Remove(PlacementColumn);
                items.RemoveAll(i => i.Fields.Count == 0);
                AnnotateRefs(connection, items, Refs);
                return items;
            }

            internal override void FollowKeys(SqliteConnection connection, IReadOnlyList<EditedItemDto> items, ImportKeyMap map, List<string> notes)
                => FollowRefs(connection, items, Refs, map, notes);

            internal override ImportTarget Probe(SqliteConnection connection, EditedItemDto item)
                => ProbeKeyedRow(connection, _table, item.Key);

            internal override bool Apply(SqliteConnection connection, SqliteTransaction transaction, EditedItemDto item)
                => ItemDBHandler.ApplySimpleFieldItem(connection, transaction, _table, item);
        }

        private sealed class CobjUnit : ExportUnit
        {
            public override string Table => "COBJ";

            internal override List<EditedItemDto> Collect(SqliteConnection connection, ExportScope scope, string? scopeValue)
                => ItemDBHandler.GetEditedCOBJ(connection, scope, scopeValue);

            // A user-created (Original=0) recipe carries its own full data and needs no scanned base
            // row, so a missing one is created rather than skipped.
            internal override ImportTarget Probe(SqliteConnection connection, EditedItemDto item)
            {
                var target = ProbeKeyedRow(connection, Table, item.Key);
                return target.State == ImportTargetState.Missing && item.Original == 0
                    ? ImportTarget.Create
                    : target;
            }

            internal override bool Apply(SqliteConnection connection, SqliteTransaction transaction, EditedItemDto item)
                => ItemDBHandler.ApplyCobjItem(connection, transaction, item);

            // A user-created recipe's Key is local; its Uid is what identifies it (TE-2).
            internal override void ResolveKeys(SqliteConnection connection, IReadOnlyList<EditedItemDto> items, ImportKeyMap map)
                => ItemDBHandler.ResolveUserCobjKeys(connection, items, map);
        }

        private sealed class EnchantmentsUnit : ExportUnit
        {
            public override string Table => "Enchantments";

            internal override List<EditedItemDto> Collect(SqliteConnection connection, ExportScope scope, string? scopeValue)
                => ItemDBHandler.GetEditedEnchantments(connection, scope, scopeValue);

            // A user-created enchantment carries its whole row (TE-3), so a missing one is created.
            internal override ImportTarget Probe(SqliteConnection connection, EditedItemDto item)
            {
                var target = ProbeKeyedRow(connection, Table, item.Key);
                return target.State == ImportTargetState.Missing && item.Original == 0
                    ? ImportTarget.Create
                    : target;
            }

            internal override bool Apply(SqliteConnection connection, SqliteTransaction transaction, EditedItemDto item)
                => ItemDBHandler.ApplyEnchantmentItem(connection, transaction, item);

            internal override void ResolveKeys(SqliteConnection connection, IReadOnlyList<EditedItemDto> items, ImportKeyMap map)
                => UserRecords.ResolveRecordKeys(connection, Table, items, map);
        }

        private sealed class SpellsUnit : ExportUnit
        {
            public override string Table => "Spells";

            internal override List<EditedItemDto> Collect(SqliteConnection connection, ExportScope scope, string? scopeValue)
                => ItemDBHandler.GetEditedSpells(connection, scope, scopeValue);

            // A user-created spell carries its whole row, so a missing one is created.
            internal override ImportTarget Probe(SqliteConnection connection, EditedItemDto item)
            {
                var target = ProbeKeyedRow(connection, Table, item.Key);
                return target.State == ImportTargetState.Missing && item.Original == 0
                    ? ImportTarget.Create
                    : target;
            }

            internal override bool Apply(SqliteConnection connection, SqliteTransaction transaction, EditedItemDto item)
                => ItemDBHandler.ApplySpellItem(connection, transaction, item);

            internal override void ResolveKeys(SqliteConnection connection, IReadOnlyList<EditedItemDto> items, ImportKeyMap map)
                => UserRecords.ResolveRecordKeys(connection, Table, items, map);
        }

        // An NPC is always a scanned record; its spell list may name spells the user created.
        private sealed class NpcUnit : ExportUnit
        {
            public override string Table => "Npc";

            internal override List<EditedItemDto> Collect(SqliteConnection connection, ExportScope scope, string? scopeValue)
                => ItemDBHandler.GetEditedNpcs(connection, scope, scopeValue);

            internal override ImportTarget Probe(SqliteConnection connection, EditedItemDto item)
                => ProbeKeyedRow(connection, Table, item.Key);

            internal override bool Apply(SqliteConnection connection, SqliteTransaction transaction, EditedItemDto item)
                => ItemDBHandler.ApplyNpcItem(connection, transaction, item);

            internal override void FollowKeys(SqliteConnection connection, IReadOnlyList<EditedItemDto> items, ImportKeyMap map, List<string> notes)
                => ItemDBHandler.FollowNpcRefs(connection, items, map, notes);
        }

        // E3: an FLST's edited contents, keyed by ListKey in its own state table.
        private sealed class WornRestrictionListUnit : ExportUnit
        {
            public override string Table => "WornRestrictionList";

            internal override List<EditedItemDto> Collect(SqliteConnection connection, ExportScope scope, string? scopeValue)
                => ItemDBHandler.GetEditedWornRestrictionLists(connection, scope, scopeValue);

            // "Exists locally" = the FLST has member rows or a state row; otherwise the list isn't in
            // this load order and the edit has nothing to attach to. A state row means it has been
            // edited here (conflict check on its LastChanged); none means never edited.
            internal override ImportTarget Probe(SqliteConnection connection, EditedItemDto item)
            {
                string? localLastChanged = null;
                bool stateExists, contentExists;

                using (var cmd = connection.CreateCommand())
                {
                    cmd.CommandText = "SELECT LastChanged FROM WornRestrictionListState WHERE ListKey = @key";
                    cmd.Parameters.AddWithValue("@key", item.Key);
                    var res = cmd.ExecuteScalar();
                    stateExists = res != null;
                    localLastChanged = res as string;
                }
                using (var cmd = connection.CreateCommand())
                {
                    cmd.CommandText = "SELECT 1 FROM WornRestrictionKeywords WHERE ListKey = @key LIMIT 1";
                    cmd.Parameters.AddWithValue("@key", item.Key);
                    contentExists = cmd.ExecuteScalar() != null;
                }

                if (!stateExists && !contentExists)
                    return ImportTarget.Missing;
                return ImportTarget.FromLastChanged(localLastChanged);
            }

            internal override bool Apply(SqliteConnection connection, SqliteTransaction transaction, EditedItemDto item)
                => ItemDBHandler.ApplyWornRestrictionListItem(connection, transaction, item);
        }
    }
}
