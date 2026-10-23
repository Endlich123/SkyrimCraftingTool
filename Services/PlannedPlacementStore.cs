using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Data.Sqlite;
using SkyrimCraftingTool.Model;

namespace SkyrimCraftingTool.Services
{
    // Taking back a placement the user made, from the pane that shows it.
    //
    // THE DECISION LIVES IN THE OTHER RECORD. A "+ new" row in a container's contents is not the
    // container's state at all - it is a line in some item's IsEditedContainerString, and taking it
    // out means editing that item. The pane does it anyway, because the row is where the user sees
    // the placement and "undo it where you made it" was a fair description of a detour nobody should
    // have to walk.
    //
    // THE LAST RESORT, NOT THE FIRST. Where the item has a live view model, OwnerContentsVM goes
    // through that instead - an ARMO/WEAP is owned by the item tree's ItemNodeVM, which carries its
    // own dirty state and save pipeline, and writing the row behind its back would let it save the
    // old placement back later. This is for the case where nothing is loaded.
    //
    // Three tables because a placement can live in any of them: armor, weapons and everything the
    // world-item tabs place (books, scrolls, misc, soul gems, ammo, food, ingredients, keys).
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
