using Microsoft.Data.Sqlite;
using SkyrimCraftingTool.Services;
using System;
using System.Collections.Generic;
using System.Linq;

namespace SkyrimCraftingTool.Model
{
    // TE-2: the two per-item recipe decisions of the Items tab, which no export carried before.
    //
    //   MainRecipe     which recipe of a kind the editor shows (MainRecipeStore)
    //   RecipeRemoved  which of a mod's recipes the user took out of the game (RemovalStore, Recipe)
    //
    // Both are keyed by the ITEM, not by a recipe, and both are a SET of rows per item - so one DTO
    // carries an item's whole set and an import replaces it. That is what "use the file version" in
    // the conflict window means for every other unit too: the file's state wins, not a merge of it.
    //
    // Neither has an IsEdited flag. The decision IS the row (see RemovalStore): no row, no edit, and
    // nothing exported. Undoing every decision on one machine therefore does not undo them on another
    // by import - the same limit every unit has with a reset.
    internal abstract class ItemDecisionUnit : ExportUnit
    {
        protected abstract string DbTable { get; }

        private static bool TableExists(SqliteConnection connection, string table)
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = @n";
            cmd.Parameters.AddWithValue("@n", table);
            return cmd.ExecuteScalar() != null;
        }

        // ItemKey -> its rows, within the scope. Item scope matches the item key, Plugin scope its
        // plugin - the same reading BuildScopeWhere gives "Key" on the record tables.
        protected Dictionary<string, (string LastChanged, List<SqliteRow> Rows)> ReadPerItem(
            SqliteConnection connection, string columns, ExportScope scope, string? scopeValue)
        {
            var result = new Dictionary<string, (string, List<SqliteRow>)>(StringComparer.OrdinalIgnoreCase);
            if (!TableExists(connection, DbTable)) return result;

            using var cmd = connection.CreateCommand();
            string where = scope switch
            {
                ExportScope.Item => " WHERE ItemKey = @scope",
                ExportScope.Plugin => " WHERE ItemKey LIKE @scope",
                _ => "",
            };
            if (scope == ExportScope.Item) cmd.Parameters.AddWithValue("@scope", scopeValue ?? "");
            if (scope == ExportScope.Plugin) cmd.Parameters.AddWithValue("@scope", (scopeValue ?? "") + "|%");
            cmd.CommandText = $"SELECT ItemKey, LastChanged, {columns} FROM {DbTable}{where} ORDER BY ItemKey";

            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                string itemKey = r.GetString(0);
                string changed = r.IsDBNull(1) ? "" : r.GetString(1);
                var row = new SqliteRow();
                for (int i = 2; i < r.FieldCount; i++)
                    row[r.GetName(i)] = r.IsDBNull(i) ? "" : r.GetValue(i).ToString() ?? "";

                if (!result.TryGetValue(itemKey, out var entry))
                    entry = ("", new List<SqliteRow>());
                // ISO-8601 "o" strings sort as they compare; the newest row dates the whole set.
                if (string.CompareOrdinal(changed, entry.Item1) > 0) entry.Item1 = changed;
                entry.Item2.Add(row);
                result[itemKey] = entry;
            }
            return result;
        }

        protected static string ItemName(SqliteConnection connection, string itemKey)
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT Name FROM Armor WHERE Key = @k UNION ALL SELECT Name FROM Weapons WHERE Key = @k LIMIT 1";
            cmd.Parameters.AddWithValue("@k", itemKey);
            return cmd.ExecuteScalar() as string ?? itemKey;
        }

        // A decision about an item that isn't in this load order has nothing to attach to. Present
        // with rows -> edited here at the newest row's time; present without -> untouched.
        internal override ImportTarget Probe(SqliteConnection connection, EditedItemDto item)
        {
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "SELECT 1 FROM Armor WHERE Key = @k UNION ALL SELECT 1 FROM Weapons WHERE Key = @k LIMIT 1";
                cmd.Parameters.AddWithValue("@k", item.Key);
                if (cmd.ExecuteScalar() == null) return ImportTarget.Missing;
            }

            if (!TableExists(connection, DbTable)) return ImportTarget.Untouched;

            using var last = connection.CreateCommand();
            last.CommandText = $"SELECT MAX(LastChanged) FROM {DbTable} WHERE ItemKey = @k";
            last.Parameters.AddWithValue("@k", item.Key);
            return ImportTarget.FromLastChanged(last.ExecuteScalar() as string);
        }

        protected static void DeleteRowsOf(SqliteConnection connection, SqliteTransaction transaction, string table, string itemKey)
        {
            using var cmd = connection.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = $"DELETE FROM {table} WHERE ItemKey = @k";
            cmd.Parameters.AddWithValue("@k", itemKey);
            cmd.ExecuteNonQuery();
        }

        protected static string Stamp(EditedItemDto item) =>
            string.IsNullOrEmpty(item.LastChanged) ? ItemDBHandler.NowIso() : item.LastChanged;
    }

    internal sealed class SqliteRow : Dictionary<string, string>
    {
        public SqliteRow() : base(StringComparer.Ordinal) { }
    }

    // Fields: RecipeKind name -> CobjKey ("Crafting" -> "Skyrim.esm|00012345").
    internal sealed class MainRecipeUnit : ItemDecisionUnit
    {
        public override string Table => "MainRecipe";
        protected override string DbTable => "MainRecipe";

        internal override List<EditedItemDto> Collect(SqliteConnection connection, ExportScope scope, string? scopeValue)
        {
            var result = new List<EditedItemDto>();
            foreach (var (itemKey, (lastChanged, rows)) in ReadPerItem(connection, "Kind, CobjKey", scope, scopeValue))
            {
                var dto = new EditedItemDto
                {
                    Table = Table,
                    Key = itemKey,
                    LastChanged = lastChanged,
                    DisplayName = ItemName(connection, itemKey),
                };
                foreach (var row in rows)
                    dto.Fields[row["Kind"]] = row["CobjKey"];
                result.Add(dto);
            }
            return result;
        }

        // The decision may name one of the user's own recipes, whose Key the same import just
        // changed (ResolveUserCobjKeys).
        internal override void FollowKeys(SqliteConnection connection, IReadOnlyList<EditedItemDto> items, ImportKeyMap map, List<string> notes)
        {
            foreach (var item in items)
                foreach (var kind in item.Fields.Keys.ToList())
                    item.Fields[kind] = map.Resolve("COBJ", item.Fields[kind]);
        }

        internal override bool Apply(SqliteConnection connection, SqliteTransaction transaction, EditedItemDto item)
        {
            // Kind is a column VALUE, not SQL text, but only the three real kinds mean anything; a
            // hand-edited file's "Crafitng" would be a decision nothing ever reads.
            var decisions = item.Fields
                .Where(kv => Enum.TryParse<RecipeKind>(kv.Key, ignoreCase: false, out _) && !KeyFactory.IsUnsetKey(kv.Value))
                .ToList();
            if (decisions.Count == 0) return false;

            DeleteRowsOf(connection, transaction, DbTable, item.Key);
            foreach (var (kind, cobjKey) in decisions)
            {
                using var cmd = connection.CreateCommand();
                cmd.Transaction = transaction;
                cmd.CommandText = "INSERT INTO MainRecipe (ItemKey, Kind, CobjKey, LastChanged) VALUES (@i, @k, @c, @t)";
                cmd.Parameters.AddWithValue("@i", item.Key);
                cmd.Parameters.AddWithValue("@k", kind);
                cmd.Parameters.AddWithValue("@c", cobjKey);
                cmd.Parameters.AddWithValue("@t", Stamp(item));
                cmd.ExecuteNonQuery();
            }
            return true;
        }
    }

    // Rows["Removed"]: one { Reference = CobjKey } per recipe taken out.
    internal sealed class RecipeRemovedUnit : ItemDecisionUnit
    {
        internal const string RowsName = "Removed";

        public override string Table => "RecipeRemoved";
        protected override string DbTable => "RecipeRemovedEntry";

        internal override List<EditedItemDto> Collect(SqliteConnection connection, ExportScope scope, string? scopeValue)
        {
            var result = new List<EditedItemDto>();
            foreach (var (itemKey, (lastChanged, rows)) in ReadPerItem(connection, "Reference", scope, scopeValue))
            {
                result.Add(new EditedItemDto
                {
                    Table = Table,
                    Key = itemKey,
                    LastChanged = lastChanged,
                    DisplayName = ItemName(connection, itemKey),
                    Rows = new Dictionary<string, List<Dictionary<string, string>>>
                    {
                        [RowsName] = rows.Select(r => new Dictionary<string, string> { ["Reference"] = r["Reference"] }).ToList(),
                    },
                });
            }
            return result;
        }

        internal override bool Apply(SqliteConnection connection, SqliteTransaction transaction, EditedItemDto item)
        {
            if (item.Rows == null || !item.Rows.TryGetValue(RowsName, out var rows) || rows == null)
                return false;

            var references = rows
                .Select(r => r.GetValueOrDefault("Reference", ""))
                .Where(r => !KeyFactory.IsUnsetKey(r))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            bool hasMainRecipe = HasMainRecipeTable(connection, transaction);

            DeleteRowsOf(connection, transaction, DbTable, item.Key);
            foreach (var reference in references)
            {
                using var cmd = connection.CreateCommand();
                cmd.Transaction = transaction;
                cmd.CommandText = "INSERT INTO RecipeRemovedEntry (ItemKey, Reference, LastChanged) VALUES (@i, @r, @t)";
                cmd.Parameters.AddWithValue("@i", item.Key);
                cmd.Parameters.AddWithValue("@r", reference);
                cmd.Parameters.AddWithValue("@t", Stamp(item));
                cmd.ExecuteNonQuery();

                // Same rule as removing one in the editor (MainRecipeStore.ForgetRecipe): no
                // decision is left pointing at a recipe that is now out of the game.
                if (!hasMainRecipe) continue;
                using var forget = connection.CreateCommand();
                forget.Transaction = transaction;
                forget.CommandText = "DELETE FROM MainRecipe WHERE CobjKey = @r";
                forget.Parameters.AddWithValue("@r", reference);
                forget.ExecuteNonQuery();
            }
            return true;
        }

        private static bool HasMainRecipeTable(SqliteConnection connection, SqliteTransaction transaction)
        {
            using var cmd = connection.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'MainRecipe'";
            return cmd.ExecuteScalar() != null;
        }
    }
}
