using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Data.Sqlite;
using SkyrimCraftingTool.Model;

namespace SkyrimCraftingTool.Services
{
    public sealed record RestoredEntry(string ListKey, string Reference, int Level, int Count);

    public static class LeveledListRestoreStore
    {
        private static string ItemDbPath =>
            Path.Combine(GlobalState.Tool?.InputFolder ?? "", "Item", "item.db");

        private static string Resolve(string? dbPath)
            => string.IsNullOrWhiteSpace(dbPath) ? ItemDbPath : dbPath;

        private static bool TableExists(SqliteConnection c)
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText =
                "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'LeveledListRestoredEntry'";
            return cmd.ExecuteScalar() != null;
        }

        public static IReadOnlyCollection<string> RestoredKeys(string listKey, string? dbPath = null)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(listKey)) return result;

            var path = Resolve(dbPath);
            if (!File.Exists(path)) return result;

            try
            {
                using var c = new SqliteConnection($"Data Source={path}");
                c.Open();
                if (!TableExists(c)) return result;

                using var cmd = c.CreateCommand();
                cmd.CommandText = "SELECT Reference FROM LeveledListRestoredEntry WHERE ListKey = @k";
                cmd.Parameters.AddWithValue("@k", listKey);
                using var r = cmd.ExecuteReader();
                while (r.Read()) result.Add(r.GetString(0));
            }
            catch (Exception ex)
            {
                AppLogger.LogError($"LeveledListRestoreStore.RestoredKeys({listKey})", ex);
            }

            return result;
        }

        public static IReadOnlyList<RestoredEntry> ReadAll(string? dbPath = null)
        {
            var result = new List<RestoredEntry>();

            var path = Resolve(dbPath);
            if (!File.Exists(path)) return result;

            try
            {
                using var c = new SqliteConnection($"Data Source={path}");
                c.Open();
                if (!TableExists(c)) return result;

                using var cmd = c.CreateCommand();
                cmd.CommandText =
                    "SELECT ListKey, Reference, Level, Count FROM LeveledListRestoredEntry ORDER BY ListKey, Reference";
                using var r = cmd.ExecuteReader();
                while (r.Read())
                    result.Add(new RestoredEntry(r.GetString(0), r.GetString(1), r.GetInt32(2), r.GetInt32(3)));
            }
            catch (Exception ex)
            {
                AppLogger.LogError("LeveledListRestoreStore.ReadAll", ex);
            }

            return result;
        }

        public static void Add(RestoredEntry entry, string? dbPath = null)
        {
            if (entry == null) return;
            if (KeyFactory.IsUnsetKey(entry.ListKey) || KeyFactory.IsUnsetKey(entry.Reference)) return;

            var path = Resolve(dbPath);
            if (!File.Exists(path)) return;

            try
            {
                using var c = new SqliteConnection($"Data Source={path}");
                c.Open();

                using var cmd = c.CreateCommand();
                cmd.CommandText = @"
                    INSERT INTO LeveledListRestoredEntry (ListKey, Reference, Level, Count, LastChanged)
                    VALUES (@k, @r, @l, @c, @now)
                    ON CONFLICT(ListKey, Reference) DO UPDATE SET
                        Level = @l, Count = @c, LastChanged = @now";
                cmd.Parameters.AddWithValue("@k", entry.ListKey);
                cmd.Parameters.AddWithValue("@r", entry.Reference);
                cmd.Parameters.AddWithValue("@l", entry.Level);
                cmd.Parameters.AddWithValue("@c", entry.Count);
                cmd.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("o"));
                cmd.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                AppLogger.LogError($"LeveledListRestoreStore.Add({entry.ListKey}, {entry.Reference})", ex);
            }
        }

        public static void Remove(string listKey, string reference, string? dbPath = null)
        {
            if (string.IsNullOrWhiteSpace(listKey) || string.IsNullOrWhiteSpace(reference)) return;

            var path = Resolve(dbPath);
            if (!File.Exists(path)) return;

            try
            {
                using var c = new SqliteConnection($"Data Source={path}");
                c.Open();
                if (!TableExists(c)) return;

                using var cmd = c.CreateCommand();
                cmd.CommandText =
                    "DELETE FROM LeveledListRestoredEntry WHERE ListKey = @k AND Reference = @r";
                cmd.Parameters.AddWithValue("@k", listKey);
                cmd.Parameters.AddWithValue("@r", reference);
                cmd.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                AppLogger.LogError($"LeveledListRestoreStore.Remove({listKey}, {reference})", ex);
            }
        }
    }
}
