using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Data.Sqlite;
using SkyrimCraftingTool.Model;

namespace SkyrimCraftingTool.Services
{
    // Everything the user decided about ONE container or leveled list on the Container/LeveledList
    // tab, seen as a whole - for that tab's "only edited" filter and its reset (user, 2026-10-17).
    //
    //   container      objects taken out                 (ContainerRemovedEntry)
    //   leveled list   objects taken out                 (LeveledListRemovedEntry)
    //                  entries an override dropped, put back (LeveledListRestoredEntry)
    //                  chance of nothing / calc flags    (LeveledListEdit)
    //
    // NOT the placements of items into it. Those are decisions about the ITEM, stored on the item,
    // and they have their own "only edited" and reset on the placement sub-tabs. Resetting a chest
    // here must not quietly un-place every armor the user put into it.
    //
    // Reads and deletes the same rows RemovalStore, LeveledListRestoreStore and LeveledListEditStore
    // write - a reset is "every one of those decisions taken back", which is exactly deleting the
    // rows: in all three stores the decision IS the row.
    public static class OwnerDecisionStore
    {
        private static string ItemDbPath =>
            Path.Combine(GlobalState.Tool?.InputFolder ?? "", "Item", "item.db");

        private static string Resolve(string? dbPath) =>
            string.IsNullOrWhiteSpace(dbPath) ? ItemDbPath : dbPath;

        // (table, owner column) per scope.
        private static (string Table, string OwnerColumn)[] TablesOf(RemovalScope scope) => scope switch
        {
            RemovalScope.Container => new[] { ("ContainerRemovedEntry", "ContainerKey") },
            RemovalScope.LeveledList => new[]
            {
                ("LeveledListRemovedEntry", "ListKey"),
                ("LeveledListRestoredEntry", "ListKey"),
                ("LeveledListEdit", "ListKey"),
            },
            _ => Array.Empty<(string, string)>(),
        };

        // Every owner with at least one decision.
        public static HashSet<string> EditedOwners(RemovalScope scope, string? dbPath = null)
        {
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var path = Resolve(dbPath);
            if (!File.Exists(path)) return keys;

            try
            {
                using var c = new SqliteConnection($"Data Source={path}");
                c.Open();
                foreach (var (table, owner) in TablesOf(scope))
                {
                    if (!UserRecords.TableExists(c, table)) continue;
                    using var cmd = c.CreateCommand();
                    cmd.CommandText = $"SELECT DISTINCT {owner} FROM {table}";
                    using var r = cmd.ExecuteReader();
                    while (r.Read()) keys.Add(r.GetString(0));
                }
            }
            catch (Exception ex)
            {
                AppLogger.LogError($"OwnerDecisionStore.EditedOwners({scope})", ex);
            }
            return keys;
        }

        // Takes every decision about this owner back, in one transaction. Returns how many rows went.
        public static int Reset(RemovalScope scope, string ownerKey, string? dbPath = null)
        {
            if (string.IsNullOrWhiteSpace(ownerKey)) return 0;
            var path = Resolve(dbPath);
            if (!File.Exists(path)) return 0;

            int removed = 0;
            try
            {
                using var c = new SqliteConnection($"Data Source={path}");
                c.Open();
                using var tx = c.BeginTransaction();
                foreach (var (table, owner) in TablesOf(scope))
                {
                    if (!UserRecords.TableExists(c, table)) continue;
                    using var cmd = c.CreateCommand();
                    cmd.Transaction = tx;
                    cmd.CommandText = $"DELETE FROM {table} WHERE {owner} = @k";
                    cmd.Parameters.AddWithValue("@k", ownerKey);
                    removed += cmd.ExecuteNonQuery();
                }
                tx.Commit();
            }
            catch (Exception ex)
            {
                AppLogger.LogError($"OwnerDecisionStore.Reset({scope}, {ownerKey})", ex);
            }

            // The chance edit is cached per database (LeveledListEditStore.EditedKeys).
            if (scope == RemovalScope.LeveledList) LeveledListEditStore.InvalidateCache();
            return removed;
        }
    }
}
