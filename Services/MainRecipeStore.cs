using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Data.Sqlite;
using SkyrimCraftingTool.Model;

namespace SkyrimCraftingTool.Services
{
    // The three recipe kinds an item can have. Crafting and Temper are told apart by the workbench
    // keyword, Breakdown by the item being on the ingredient side - see BreakdownRecipeVM.
    public enum RecipeKind
    {
        Crafting,
        Temper,
        Breakdown,
    }

    // Which recipe of a kind the item's editor shows.
    //
    // WHY THIS EXISTS, and it is not what it looks like. The order recipes come out of the database
    // in is stable in practice: "SELECT ... FROM COBJ WHERE Active = 1" with no ORDER BY returns
    // rowid order, the rescan upserts rather than reinserting, and MarkInactiveExcept sets
    // Active = 0 instead of deleting - so rowids survive. This is NOT a guard against drift.
    //
    // It is here because a "make this the main one" button without stored state does not survive a
    // restart. Absent means "the first one", which is what every item that was never decided about
    // gets, and which is exactly what the editor did before this existed.
    //
    // Shaped like RemovalStore on purpose: the decision IS the row, there is no history log, and
    // deleting the row is the whole of the undo.
    public static class MainRecipeStore
    {
        private static string ItemDbPath =>
            Path.Combine(GlobalState.Tool?.InputFolder ?? "", "Item", "item.db");

        private static string Resolve(string dbPath) =>
            string.IsNullOrWhiteSpace(dbPath) ? ItemDbPath : dbPath;

        private static bool TableExists(SqliteConnection c)
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'MainRecipe'";
            return cmd.ExecuteScalar() != null;
        }

        // The COBJ the user picked, or "" when they never picked one.
        public static string MainFor(string itemKey, RecipeKind kind, string dbPath = null)
        {
            if (string.IsNullOrWhiteSpace(itemKey)) return "";

            var path = Resolve(dbPath);
            if (!File.Exists(path)) return "";

            try
            {
                using var c = new SqliteConnection($"Data Source={path}");
                c.Open();
                if (!TableExists(c)) return "";

                using var cmd = c.CreateCommand();
                cmd.CommandText = "SELECT CobjKey FROM MainRecipe WHERE ItemKey = @item AND Kind = @kind";
                cmd.Parameters.AddWithValue("@item", itemKey);
                cmd.Parameters.AddWithValue("@kind", kind.ToString());

                return cmd.ExecuteScalar() as string ?? "";
            }
            catch (Exception ex)
            {
                AppLogger.LogError($"MainRecipeStore.MainFor({itemKey}, {kind})", ex);
                return "";
            }
        }

        public static void Set(string itemKey, RecipeKind kind, string cobjKey, string dbPath = null)
        {
            if (KeyFactory.IsUnsetKey(itemKey) || KeyFactory.IsUnsetKey(cobjKey)) return;

            var path = Resolve(dbPath);
            if (!File.Exists(path)) return;

            try
            {
                using var c = new SqliteConnection($"Data Source={path}");
                c.Open();

                using var cmd = c.CreateCommand();
                // Upsert: picking the same recipe twice is the user saying the same thing twice,
                // not an error worth a message.
                cmd.CommandText = @"
                    INSERT INTO MainRecipe (ItemKey, Kind, CobjKey, LastChanged)
                    VALUES (@item, @kind, @cobj, @now)
                    ON CONFLICT(ItemKey, Kind) DO UPDATE SET CobjKey = @cobj, LastChanged = @now";
                cmd.Parameters.AddWithValue("@item", itemKey);
                cmd.Parameters.AddWithValue("@kind", kind.ToString());
                cmd.Parameters.AddWithValue("@cobj", cobjKey);
                cmd.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("o"));
                cmd.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                AppLogger.LogError($"MainRecipeStore.Set({itemKey}, {kind}, {cobjKey})", ex);
            }
        }

        // Back to "the first one". Also what has to happen when the chosen recipe is deleted -
        // leaving the row would point the editor at a COBJ that no longer exists.
        public static void Clear(string itemKey, RecipeKind kind, string dbPath = null)
        {
            if (string.IsNullOrWhiteSpace(itemKey)) return;

            var path = Resolve(dbPath);
            if (!File.Exists(path)) return;

            try
            {
                using var c = new SqliteConnection($"Data Source={path}");
                c.Open();
                if (!TableExists(c)) return;

                using var cmd = c.CreateCommand();
                cmd.CommandText = "DELETE FROM MainRecipe WHERE ItemKey = @item AND Kind = @kind";
                cmd.Parameters.AddWithValue("@item", itemKey);
                cmd.Parameters.AddWithValue("@kind", kind.ToString());
                cmd.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                AppLogger.LogError($"MainRecipeStore.Clear({itemKey}, {kind})", ex);
            }
        }

        // Drops any decision that names this COBJ, whichever item and kind it was made for. Called
        // when a recipe is removed, so no decision is left pointing at a record that is gone.
        public static void ForgetRecipe(string cobjKey, string dbPath = null)
        {
            if (string.IsNullOrWhiteSpace(cobjKey)) return;

            var path = Resolve(dbPath);
            if (!File.Exists(path)) return;

            try
            {
                using var c = new SqliteConnection($"Data Source={path}");
                c.Open();
                if (!TableExists(c)) return;

                using var cmd = c.CreateCommand();
                cmd.CommandText = "DELETE FROM MainRecipe WHERE CobjKey = @cobj";
                cmd.Parameters.AddWithValue("@cobj", cobjKey);
                cmd.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                AppLogger.LogError($"MainRecipeStore.ForgetRecipe({cobjKey})", ex);
            }
        }

        // Picks the recipe the editor should show: the stored decision when it is still among the
        // candidates, the first one otherwise. The fallback matters - a decision can outlive its
        // recipe (a mod was removed and the next scan dropped the row), and pointing the editor at
        // nothing would make the item look like it had no recipe at all.
        public static T Choose<T>(IEnumerable<T> candidates, Func<T, string> keyOf, string storedMainKey)
            where T : class
        {
            T first = null;

            foreach (var candidate in candidates ?? Array.Empty<T>())
            {
                first ??= candidate;

                if (!string.IsNullOrEmpty(storedMainKey)
                    && string.Equals(keyOf(candidate), storedMainKey, StringComparison.OrdinalIgnoreCase))
                {
                    return candidate;
                }
            }

            return first;
        }
    }
}
