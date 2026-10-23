using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Data.Sqlite;
using SkyrimCraftingTool.Model;

namespace SkyrimCraftingTool.Services
{
    // One entry the user decided to put back into a leveled list after an override dropped it.
    //
    // Level and Count are the values it had where it was last seen, so putting one back restores it
    // as it was rather than at some invented default.
    public sealed record RestoredEntry(string ListKey, string Reference, int Level, int Count);

    // The user's answers to the lost-entry report.
    //
    // WHY A STORE OF ITS OWN rather than reusing item placements, which say the same thing: a
    // placement lives in the ITEM's ContainerString, and only Armor and Weapons have one. Measured
    // against the real load order, of 340 lost entries 12 are armor and 78 are weapons - the other
    // 250 are nested lists, books and scrolls, record types the scan does not carry. Built on
    // placements, this would have worked for a quarter of the cases and silently done nothing for
    // the rest. SkyPatcher needs only the FormID, so the decision is kept on the LIST, where every
    // type fits.
    //
    // Everything here is additive at runtime: the patch emits filterByLLs + addOnceToLLs, which
    // supplements the list the game loaded rather than replacing the record. Nothing in this store
    // can therefore clobber another mod - which is what made it safe to offer a restore at all.
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

        // Which references the user put back into this one list. Read when the list window opens, so
        // the History block can show which rows are already dealt with.
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

        // Everything the user put back, for the patch run.
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
                // Upsert rather than insert: pressing the button twice is the user saying the same
                // thing twice, not an error worth showing them.
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
