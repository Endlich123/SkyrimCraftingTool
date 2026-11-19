using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Data.Sqlite;
using SkyrimCraftingTool.Model;

namespace SkyrimCraftingTool.Services
{
    public static class PlannedPlacementStore
    {
        private static readonly string[] Tables = { "Armor", "Weapons", "WorldItem" };

        private static string Resolve(string? dbPath)
            => string.IsNullOrWhiteSpace(dbPath)
                ? Path.Combine(GlobalState.Tool?.InputFolder ?? "", "Item", "item.db")
                : dbPath;

        // Returns true when a row was found and rewritten.
        public static bool RemovePlacement(
            string itemKey, string ownerKey, bool ownerIsContainer, string? dbPath = null)
        {
            if (string.IsNullOrWhiteSpace(itemKey) || string.IsNullOrWhiteSpace(ownerKey)) return false;

            var path = Resolve(dbPath);
            if (!File.Exists(path)) return false;

            try
            {
                using var c = new SqliteConnection($"Data Source={path}");
                c.Open();

                foreach (var table in Tables)
                {
                    if (!TableExists(c, table)) continue;

                    var current = ReadPlacement(c, table, itemKey);
                    if (current == null) continue;

                    var updated = ContainerPlacementEditor.WithoutPlacement(current, ownerKey, ownerIsContainer);
                    if (string.Equals(updated, current, StringComparison.Ordinal)) return true;

                    Write(c, table, itemKey, updated);
                    return true;
                }
            }
            catch (Exception ex)
            {
                AppLogger.LogError($"PlannedPlacementStore.RemovePlacement({itemKey}, {ownerKey})", ex);
            }

            return false;
        }

        private static bool TableExists(SqliteConnection c, string table)
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = @t";
            cmd.Parameters.AddWithValue("@t", table);
            return cmd.ExecuteScalar() != null;
        }

        private static string? ReadPlacement(SqliteConnection c, string table, string itemKey)
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = $"SELECT IsEditedContainerString FROM {table} WHERE Key = @k";
            cmd.Parameters.AddWithValue("@k", itemKey);

            using var r = cmd.ExecuteReader();
            if (!r.Read()) return null;

            return r.IsDBNull(0) ? "" : r.GetString(0);
        }

        // IsEdited stays 1 and LastChanged moves, exactly as ItemDBHandler.UpdateField does it: the
        // row still carries user edits - possibly only this one, now emptied - and the shadow column
        // is what the patch reads. Clearing IsEdited here would hide the item's other edits.
        private static void Write(SqliteConnection c, string table, string itemKey, string value)
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText =
                $"UPDATE {table} SET IsEditedContainerString = @v, IsEdited = 1, LastChanged = @now WHERE Key = @k";
            cmd.Parameters.AddWithValue("@v", value);
            cmd.Parameters.AddWithValue("@k", itemKey);
            cmd.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("o"));
            cmd.ExecuteNonQuery();
        }
    }
}
