using Microsoft.Data.Sqlite;
using SkyrimCraftingTool.Services;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace SkyrimCraftingTool.Model
{
    internal static class UserRecords
    {
        // Bookkeeping, never record data: the import writes these itself or leaves them alone.
        private static readonly HashSet<string> MetaColumns = new(StringComparer.OrdinalIgnoreCase)
        {
            "Key", "Original", "Active", "IsEdited", "EffectsEdited", "KeywordsEdited", "ConditionsEdited",
            "LastChanged", "LastPatched", "Uid", "DroppedEffectRows",
        };

        internal static bool TableExists(SqliteConnection connection, string table)
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = @n";
            cmd.Parameters.AddWithValue("@n", table);
            return cmd.ExecuteScalar() != null;
        }

        internal static bool HasColumn(SqliteConnection connection, string table, string column)
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = $"SELECT 1 FROM pragma_table_info('{table}') WHERE name = @c";
            cmd.Parameters.AddWithValue("@c", column);
            return cmd.ExecuteScalar() != null;
        }

        private static List<string> DataColumns(SqliteConnection connection, SqliteTransaction? transaction, string table)
        {
            var columns = new List<string>();
            using var cmd = connection.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = $"SELECT name FROM pragma_table_info('{table}')";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var name = r.GetString(0);
                if (!MetaColumns.Contains(name)) columns.Add(name);
            }
            return columns;
        }

        // ---- identity ----

        // "" when the database predates the column (EnsureSchema adds it on the next start).
        internal static string EnsureUid(SqliteConnection connection, string table, string key)
        {
            if (!HasColumn(connection, table, "Uid")) return "";

            using (var read = connection.CreateCommand())
            {
                read.CommandText = $"SELECT Uid FROM {table} WHERE Key = @k";
                read.Parameters.AddWithValue("@k", key);
                if (read.ExecuteScalar() is string existing && existing.Length > 0)
                    return existing;
            }

            var uid = Guid.NewGuid().ToString("N");
            using var write = connection.CreateCommand();
            write.CommandText = $"UPDATE {table} SET Uid = @u WHERE Key = @k AND (Uid IS NULL OR Uid = '')";
            write.Parameters.AddWithValue("@u", uid);
            write.Parameters.AddWithValue("@k", key);
            write.ExecuteNonQuery();
            return uid;
        }

        internal static string? FindKeyByUid(SqliteConnection connection, string table, string uid)
        {
            if (string.IsNullOrEmpty(uid) || !HasColumn(connection, table, "Uid")) return null;
            using var cmd = connection.CreateCommand();
            cmd.CommandText = $"SELECT Key FROM {table} WHERE Uid = @u LIMIT 1";
            cmd.Parameters.AddWithValue("@u", uid);
            return cmd.ExecuteScalar() as string;
        }

        internal static bool IsUserKey(string? key) =>
            !string.IsNullOrEmpty(key) && key.StartsWith(KeyFactory.UserPluginName + "|", StringComparison.OrdinalIgnoreCase);

        private static bool RowExists(SqliteConnection connection, string table, string key)
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = $"SELECT 1 FROM {table} WHERE Key = @k";
            cmd.Parameters.AddWithValue("@k", key);
            return cmd.ExecuteScalar() != null;
        }

        internal static void ResolveKeys(SqliteConnection connection, string table, IEnumerable<EditedItemDto> items,
            ImportKeyMap map, IReadOnlyList<string> namespaceTables, Func<string> allocate)
        {
            if (!HasColumn(connection, table, "Uid")) return;

            var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            bool Taken(string key) =>
                taken.Contains(key)
                || namespaceTables.Any(t => TableExists(connection, t) && RowExists(connection, t, key))
                || InFormIdMap(connection, key);

            foreach (var item in items)
            {
                if (item.Original != 0 || string.IsNullOrEmpty(item.Uid)) continue;

                string original = item.Key;
                string? resolved = FindKeyByUid(connection, table, item.Uid);

                if (resolved == null && Taken(original))
                {
                    do { resolved = allocate(); }
                    while (Taken(resolved));
                }

                resolved ??= original;
                map.RememberUid(table, item.Uid, resolved);
                taken.Add(resolved);
                if (!string.Equals(resolved, original, StringComparison.OrdinalIgnoreCase))
                {
                    map.Add(table, original, resolved);
                    item.Key = resolved;
                }
            }
        }

        // Recipes, enchantments and spells: one namespace, one counter (UserKeyAllocator).
        internal static readonly string[] RecordTables = { "COBJ", "Enchantments", "Spells" };

        internal static void ResolveRecordKeys(SqliteConnection connection, string table, IEnumerable<EditedItemDto> items, ImportKeyMap map)
            => ResolveKeys(connection, table, items, map, RecordTables, () => UserKeyAllocator.NextRecordKey(connection));

        private static bool InFormIdMap(SqliteConnection connection, string key)
        {
            if (!TableExists(connection, "PatchFormIdMap")) return false;
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT 1 FROM PatchFormIdMap WHERE ToolKey = @k";
            cmd.Parameters.AddWithValue("@k", key);
            return cmd.ExecuteScalar() != null;
        }

        // ---- the whole row ----

        // Every data column that has a value, base and shadow alike, as the export's Fields.
        internal static Dictionary<string, string> ReadRow(SqliteConnection connection, string table, string key)
        {
            var fields = new Dictionary<string, string>();
            var columns = DataColumns(connection, null, table);
            if (columns.Count == 0) return fields;

            using var cmd = connection.CreateCommand();
            cmd.CommandText = $"SELECT {string.Join(", ", columns)} FROM {table} WHERE Key = @k";
            cmd.Parameters.AddWithValue("@k", key);
            using var r = cmd.ExecuteReader();
            if (!r.Read()) return fields;

            for (int i = 0; i < columns.Count; i++)
                if (!r.IsDBNull(i))
                    fields[columns[i]] = Convert.ToString(r.GetValue(i), CultureInfo.InvariantCulture) ?? "";
            return fields;
        }

        internal static bool WriteRow(SqliteConnection connection, SqliteTransaction transaction, string table,
            EditedItemDto item, string? lastChanged = null)
        {
            var columns = DataColumns(connection, transaction, table);
            var allowed = new HashSet<string>(columns, StringComparer.OrdinalIgnoreCase);
            var values = item.Fields.Where(kv => allowed.Contains(kv.Key))
                .ToDictionary(kv => columns.First(c => string.Equals(c, kv.Key, StringComparison.OrdinalIgnoreCase)), kv => kv.Value);
            if (values.Count == 0) return false;

            bool hasUid = !string.IsNullOrEmpty(item.Uid) && HasColumn(connection, table, "Uid");
            string stamp = string.IsNullOrEmpty(lastChanged) ? ItemDBHandler.NowIso() : lastChanged!;

            bool exists;
            using (var check = connection.CreateCommand())
            {
                check.Transaction = transaction;
                check.CommandText = $"SELECT 1 FROM {table} WHERE Key = @k";
                check.Parameters.AddWithValue("@k", item.Key);
                exists = check.ExecuteScalar() != null;
            }

            using var cmd = connection.CreateCommand();
            cmd.Transaction = transaction;
            int p = 0;
            string Bind(object? v) { var name = $"@p{p++}"; cmd.Parameters.AddWithValue(name, v ?? DBNull.Value); return name; }

            if (!exists)
            {
                var names = new List<string> { "Key", "Original", "Active", "IsEdited", "LastChanged" };
                var binds = new List<string> { Bind(item.Key), "0", "1", "1", Bind(stamp) };
                foreach (var (column, value) in values) { names.Add(column); binds.Add(Bind(value)); }
                if (hasUid) { names.Add("Uid"); binds.Add(Bind(item.Uid)); }
                cmd.CommandText = $"INSERT INTO {table} ({string.Join(", ", names)}) VALUES ({string.Join(", ", binds)})";
            }
            else
            {
                var sets = columns
                    .Where(c => values.ContainsKey(c) || c.StartsWith("IsEdited", StringComparison.Ordinal))
                    .Select(c => $"{c} = {(values.TryGetValue(c, out var v) ? Bind(v) : "NULL")}")
                    .ToList();
                sets.Add("IsEdited = 1");
                sets.Add($"LastChanged = {Bind(stamp)}");
                if (hasUid) sets.Add($"Uid = {Bind(item.Uid)}");
                // Original = 0 in the WHERE: whatever the file says, an import never turns a
                // plugin's record into a tool-created one.
                cmd.CommandText = $"UPDATE {table} SET {string.Join(", ", sets)} WHERE Key = {Bind(item.Key)} AND Original = 0";
            }
            return cmd.ExecuteNonQuery() > 0;
        }

        // ---- references to them ----

        internal static string UidForReference(SqliteConnection connection, string table, string? value)
            => IsUserKey(value) && RowExists(connection, table, value!) ? EnsureUid(connection, table, value!) : "";

        // At import: the local Key for a referenced user record, by its Uid - from this very import
        // first (a record it creates is not in the database yet), then from the database. Null when
        // this setup does not have it at all; the caller decides what that means.
        internal static string? ResolveReference(SqliteConnection connection, string table, string uid, ImportKeyMap map)
            => map.KeyForUid(table, uid) ?? FindKeyByUid(connection, table, uid);
    }
}
