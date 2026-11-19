using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Data.Sqlite;
using SkyrimCraftingTool.Model;

namespace SkyrimCraftingTool.Services
{
    public sealed record RemovedEntry(string OwnerKey, string Reference);

    // Which of the two kinds of owner a decision belongs to. The two tables are identical in shape
    // and differ only in the name of their owner column, so one store serves both rather than two
    // copies of the same file drifting apart - the six copied condition templates are the standing
    // reminder of how that ends.
    public enum RemovalScope
    {
        Container,
        LeveledList,

        // "Take this recipe out of the game." Only ever a MOD's recipe: one the tool created is
        // deleted outright, row and all, because no plugin would bring it back. A row here becomes
        // an ESP override carrying the Deleted record flag.
        Recipe,
    }

    public static class RemovalStore
    {
        private static string ItemDbPath =>
            Path.Combine(GlobalState.Tool?.InputFolder ?? "", "Item", "item.db");

        private static string Resolve(string? dbPath)
            => string.IsNullOrWhiteSpace(dbPath) ? ItemDbPath : dbPath;

        internal static string TableOf(RemovalScope scope) => scope switch
        {
            RemovalScope.Container => "ContainerRemovedEntry",
            RemovalScope.LeveledList => "LeveledListRemovedEntry",
            _ => "RecipeRemovedEntry",
        };

        internal static string OwnerColumnOf(RemovalScope scope) => scope switch
        {
            RemovalScope.Container => "ContainerKey",
            RemovalScope.LeveledList => "ListKey",
            _ => "ItemKey",
        };

        private static bool TableExists(SqliteConnection c, RemovalScope scope)
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = @n";
            cmd.Parameters.AddWithValue("@n", TableOf(scope));
            return cmd.ExecuteScalar() != null;
        }

        public static IReadOnlyCollection<string> RemovedKeys(RemovalScope scope, string ownerKey, string? dbPath = null)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(ownerKey)) return result;

            var path = Resolve(dbPath);
            if (!File.Exists(path)) return result;

            try
            {
                using var c = new SqliteConnection($"Data Source={path}");
                c.Open();
                if (!TableExists(c, scope)) return result;

                using var cmd = c.CreateCommand();
                cmd.CommandText = $"SELECT Reference FROM {TableOf(scope)} WHERE {OwnerColumnOf(scope)} = @k";
                cmd.Parameters.AddWithValue("@k", ownerKey);
                using var r = cmd.ExecuteReader();
                while (r.Read()) result.Add(r.GetString(0));
            }
            catch (Exception ex)
            {
                AppLogger.LogError($"RemovalStore.RemovedKeys({scope}, {ownerKey})", ex);
            }

            return result;
        }

        public static IReadOnlyList<RemovedEntry> ReadAll(RemovalScope scope, string? dbPath = null)
        {
            var result = new List<RemovedEntry>();

            var path = Resolve(dbPath);
            if (!File.Exists(path)) return result;

            try
            {
                using var c = new SqliteConnection($"Data Source={path}");
                c.Open();
                if (!TableExists(c, scope)) return result;

                string owner = OwnerColumnOf(scope);
                using var cmd = c.CreateCommand();
                cmd.CommandText = $"SELECT {owner}, Reference FROM {TableOf(scope)} ORDER BY {owner}, Reference";
                using var r = cmd.ExecuteReader();
                while (r.Read()) result.Add(new RemovedEntry(r.GetString(0), r.GetString(1)));
            }
            catch (Exception ex)
            {
                AppLogger.LogError($"RemovalStore.ReadAll({scope})", ex);
            }

            return result;
        }

        // How many decisions exist in total, for the "you are changing things for every mod" notice.
        // Counted rather than read: the notice needs a number, not the rows.
        public static int Count(RemovalScope scope, string? dbPath = null)
        {
            var path = Resolve(dbPath);
            if (!File.Exists(path)) return 0;

            try
            {
                using var c = new SqliteConnection($"Data Source={path}");
                c.Open();
                if (!TableExists(c, scope)) return 0;

                using var cmd = c.CreateCommand();
                cmd.CommandText = $"SELECT COUNT(*) FROM {TableOf(scope)}";
                return Convert.ToInt32(cmd.ExecuteScalar());
            }
            catch (Exception ex)
            {
                AppLogger.LogError($"RemovalStore.Count({scope})", ex);
                return 0;
            }
        }

        public static void Add(RemovalScope scope, RemovedEntry entry, string? dbPath = null)
        {
            if (entry == null) return;
            if (KeyFactory.IsUnsetKey(entry.OwnerKey) || KeyFactory.IsUnsetKey(entry.Reference)) return;

            var path = Resolve(dbPath);
            if (!File.Exists(path)) return;

            try
            {
                using var c = new SqliteConnection($"Data Source={path}");
                c.Open();

                using var cmd = c.CreateCommand();
                cmd.CommandText = $@"
                    INSERT INTO {TableOf(scope)} ({OwnerColumnOf(scope)}, Reference, LastChanged)
                    VALUES (@k, @r, @now)
                    ON CONFLICT({OwnerColumnOf(scope)}, Reference) DO UPDATE SET LastChanged = @now";
                cmd.Parameters.AddWithValue("@k", entry.OwnerKey);
                cmd.Parameters.AddWithValue("@r", entry.Reference);
                cmd.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("o"));
                cmd.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                AppLogger.LogError($"RemovalStore.Add({scope}, {entry.OwnerKey}, {entry.Reference})", ex);
            }
        }

        // Undo. Deleting the decision is the whole of it - nothing else recorded it.
        public static void Remove(RemovalScope scope, string ownerKey, string reference, string? dbPath = null)
        {
            if (string.IsNullOrWhiteSpace(ownerKey) || string.IsNullOrWhiteSpace(reference)) return;

            var path = Resolve(dbPath);
            if (!File.Exists(path)) return;

            try
            {
                using var c = new SqliteConnection($"Data Source={path}");
                c.Open();
                if (!TableExists(c, scope)) return;

                using var cmd = c.CreateCommand();
                cmd.CommandText =
                    $"DELETE FROM {TableOf(scope)} WHERE {OwnerColumnOf(scope)} = @k AND Reference = @r";
                cmd.Parameters.AddWithValue("@k", ownerKey);
                cmd.Parameters.AddWithValue("@r", reference);
                cmd.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                AppLogger.LogError($"RemovalStore.Remove({scope}, {ownerKey}, {reference})", ex);
            }
        }
    }
}
