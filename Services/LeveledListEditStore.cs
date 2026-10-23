using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Data.Sqlite;
using SkyrimCraftingTool.Model;

namespace SkyrimCraftingTool.Services
{
    // One user edit to a leveled list's own properties. Null means "leave this alone", per property:
    // changing the chance must not silently also write a flag operation.
    //
    // A list whose chance comes from a GLOBAL is not editable here at all: the game reads the global
    // and ignores any number, and SkyPatcher can point a list at one but never take one away. Offering
    // that edit was built and taken back out (2026-09-16) - see LeveledListEditorVM.
    public sealed record LeveledListEdit(string ListKey, int? ChanceNone, CalcFlagMode Flags)
    {
        public bool IsEmpty => !ChanceNone.HasValue && Flags == CalcFlagMode.Unchanged;
    }

    // One edit with the load order beside it, for the overview of everything the user has changed.
    //
    // Both sides on purpose: an edit stored as "10" says nothing on its own months later. "25% -> 10%"
    // is a sentence, and it is also what the reset button undoes.
    public sealed record LeveledListEditDetail(
        LeveledListEdit Edit, string EditorId, int ScannedChanceNone, string ScannedFlags, string LastChanged)
    {
        public string ListKey => Edit.ListKey;

        public string Display => string.IsNullOrWhiteSpace(EditorId) ? Edit.ListKey : EditorId;

        public string Changes
        {
            get
            {
                var parts = new List<string>();

                if (Edit.ChanceNone.HasValue)
                    parts.Add($"chance of nothing {ScannedChanceNone}% -> {Edit.ChanceNone}%");

                if (Edit.Flags != CalcFlagMode.Unchanged)
                    parts.Add($"calculation {LeveledListFlags.Describe(LeveledListFlags.FromRecord(ScannedFlags))} " +
                              $"-> {LeveledListFlags.Describe(Edit.Flags)}");

                return string.Join(", ", parts);
            }
        }
    }

    // Reads and writes LeveledListEdit - the table that holds what the user changed about a list,
    // never what was scanned.
    //
    // Its own small store rather than another method on ItemDBHandler: the list window opens one
    // list at a time and the patch generator reads all of them at once, and neither needs the
    // snapshot machinery around items. Writes are one row.
    public static class LeveledListEditStore
    {
        private static string ItemDbPath =>
            Path.Combine(GlobalState.Tool?.InputFolder ?? "", "Item", "item.db");

        private static string Resolve(string? dbPath)
            => string.IsNullOrWhiteSpace(dbPath) ? ItemDbPath : dbPath;

        // Which lists carry an edit at all - the question every container row asks, and it has to be
        // answerable without a query per row: one container can hold dozens of lists, and the item
        // editor renders hundreds of rows.
        //
        // Cached per database, thrown away on every write below. A stale "changed" badge would be
        // exactly the kind of small lie this whole feature exists to prevent.
        private static readonly Dictionary<string, HashSet<string>> _editedKeys =
            new(StringComparer.OrdinalIgnoreCase);

        public static IReadOnlyCollection<string> EditedKeys(string? dbPath = null)
        {
            var path = Resolve(dbPath);

            lock (_editedKeys)
            {
                if (_editedKeys.TryGetValue(path, out var cached)) return cached;
            }

            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var edit in ReadAll(dbPath))
                keys.Add(edit.ListKey);

            lock (_editedKeys) _editedKeys[path] = keys;

            return keys;
        }

        public static bool IsEdited(string listKey, string? dbPath = null)
            => !string.IsNullOrWhiteSpace(listKey) && EditedKeys(dbPath).Contains(listKey);

        // Called by every write here, and by a rescan - which rebuilds the lists these edits point at.
        public static void InvalidateCache()
        {
            lock (_editedKeys) _editedKeys.Clear();
        }

        public static LeveledListEdit? Read(string listKey, string? dbPath = null)
        {
            if (string.IsNullOrWhiteSpace(listKey)) return null;

            var path = Resolve(dbPath);
            if (!File.Exists(path)) return null;

            try
            {
                using var c = new SqliteConnection($"Data Source={path};Mode=ReadOnly");
                c.Open();

                using var cmd = c.CreateCommand();
                cmd.CommandText = "SELECT ChanceNone, CalcFlagMode FROM LeveledListEdit WHERE ListKey = @k";
                cmd.Parameters.AddWithValue("@k", listKey);

                using var r = cmd.ExecuteReader();
                if (!r.Read()) return null;

                return new LeveledListEdit(
                    listKey,
                    r.IsDBNull(0) ? null : r.GetInt32(0),
                    ParseMode(r.IsDBNull(1) ? "" : r.GetString(1)));
            }
            catch (Exception ex)
            {
                AppLogger.LogError($"LeveledListEditStore.Read({listKey})", ex);
                return null;
            }
        }

        // Every edited list, for the patch generator and the report.
        //
        // Joined against the scanned lists on purpose: an edit whose list is no longer in the load
        // order must not become a rule. The row is left alone rather than deleted, because the mod
        // may well come back - the same reasoning the Active flag follows everywhere else.
        public static IReadOnlyList<LeveledListEdit> ReadAll(string? dbPath = null)
        {
            var edits = new List<LeveledListEdit>();

            var path = Resolve(dbPath);
            if (!File.Exists(path)) return edits;

            try
            {
                using var c = new SqliteConnection($"Data Source={path};Mode=ReadOnly");
                c.Open();

                using var cmd = c.CreateCommand();
                cmd.CommandText = @"
                    SELECT e.ListKey, e.ChanceNone, e.CalcFlagMode
                    FROM LeveledListEdit e
                    JOIN LeveledList l ON l.Key = e.ListKey AND l.Active = 1
                    ORDER BY e.ListKey";

                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    var edit = new LeveledListEdit(
                        r.GetString(0),
                        r.IsDBNull(1) ? null : r.GetInt32(1),
                        ParseMode(r.IsDBNull(2) ? "" : r.GetString(2)));

                    if (!edit.IsEmpty) edits.Add(edit);
                }
            }
            catch (Exception ex)
            {
                AppLogger.LogError("LeveledListEditStore.ReadAll", ex);
            }

            return edits;
        }

        // Every edit with enough context to be read back months later: the list's name, and what the
        // load order says, so the overview can put both sides next to each other ("25% -> 10%")
        // rather than only the value the user typed.
        public static IReadOnlyList<LeveledListEditDetail> ReadAllDetailed(string? dbPath = null)
        {
            var details = new List<LeveledListEditDetail>();

            var path = Resolve(dbPath);
            if (!File.Exists(path)) return details;

            try
            {
                using var c = new SqliteConnection($"Data Source={path};Mode=ReadOnly");
                c.Open();

                using var cmd = c.CreateCommand();
                cmd.CommandText = @"
                    SELECT e.ListKey, l.EditorID, e.ChanceNone, e.CalcFlagMode,
                           l.ChanceNone, COALESCE(l.Flags, ''), e.LastChanged
                    FROM LeveledListEdit e
                    JOIN LeveledList l ON l.Key = e.ListKey AND l.Active = 1
                    ORDER BY l.EditorID";

                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    var edit = new LeveledListEdit(
                        r.GetString(0),
                        r.IsDBNull(2) ? null : r.GetInt32(2),
                        ParseMode(r.IsDBNull(3) ? "" : r.GetString(3)));

                    if (edit.IsEmpty) continue;

                    details.Add(new LeveledListEditDetail(
                        edit, r.GetString(1), r.GetInt32(4), r.GetString(5),
                        r.IsDBNull(6) ? "" : r.GetString(6)));
                }
            }
            catch (Exception ex)
            {
                AppLogger.LogError("LeveledListEditStore.ReadAllDetailed", ex);
            }

            return details;
        }

        // An empty edit deletes the row instead of storing "nothing changed": otherwise the list
        // would keep showing up as edited forever, and the patch report would name lists the patch
        // does not touch.
        public static void Save(LeveledListEdit edit, string? dbPath = null)
        {
            if (edit == null || string.IsNullOrWhiteSpace(edit.ListKey)) return;
            if (KeyFactory.IsUnsetKey(edit.ListKey)) return;

            var path = Resolve(dbPath);
            if (!File.Exists(path)) return;

            try
            {
                using var c = new SqliteConnection($"Data Source={path}");
                c.Open();

                if (edit.IsEmpty)
                {
                    Delete(edit.ListKey, dbPath);
                    return;
                }

                InvalidateCache();

                using var cmd = c.CreateCommand();
                cmd.CommandText = @"
                    INSERT INTO LeveledListEdit (ListKey, ChanceNone, CalcFlagMode, LastChanged)
                    VALUES (@k, @c, @f, @now)
                    ON CONFLICT(ListKey) DO UPDATE SET
                        ChanceNone = @c, CalcFlagMode = @f, LastChanged = @now";
                cmd.Parameters.AddWithValue("@k", edit.ListKey);
                cmd.Parameters.AddWithValue("@c", (object?)edit.ChanceNone ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@f",
                    edit.Flags == CalcFlagMode.Unchanged ? (object)DBNull.Value : edit.Flags.ToString());
                cmd.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("o"));
                cmd.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                AppLogger.LogError($"LeveledListEditStore.Save({edit.ListKey})", ex);
            }
        }

        public static void Delete(string listKey, string? dbPath = null)
        {
            if (string.IsNullOrWhiteSpace(listKey)) return;
            if (KeyFactory.IsUnsetKey(listKey)) return;

            var path = Resolve(dbPath);
            if (!File.Exists(path)) return;

            try
            {
                using var c = new SqliteConnection($"Data Source={path}");
                c.Open();

                using var cmd = c.CreateCommand();
                cmd.CommandText = "DELETE FROM LeveledListEdit WHERE ListKey = @k";
                cmd.Parameters.AddWithValue("@k", listKey);
                cmd.ExecuteNonQuery();

                InvalidateCache();
            }
            catch (Exception ex)
            {
                AppLogger.LogError($"LeveledListEditStore.Delete({listKey})", ex);
            }
        }

        // An unknown or renamed mode reads as "unchanged" rather than throwing: the stored value is
        // a name, and a name that no longer exists must cost one edit, not the whole export.
        private static CalcFlagMode ParseMode(string stored)
            => Enum.TryParse<CalcFlagMode>(stored, ignoreCase: true, out var mode)
                ? mode
                : CalcFlagMode.Unchanged;
    }
}
