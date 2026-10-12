using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;
using SkyrimCraftingTool.Model;

namespace SkyrimCraftingTool.Services
{
    // One of the user's own leveled lists: a LeveledList row with Original = 0.
    public sealed record UserLeveledList(string Key, string EditorId);

    public enum UserListTargetKind
    {
        LeveledList,
        Container,
    }

    // "This own list hangs in that list / that container" - one row of UserLeveledListTarget.
    // Level is what the target list hands it out from; a container has no levels, so it is 1 there and
    // means nothing.
    public sealed record UserListTarget(string ListKey, string TargetKey, UserListTargetKind Kind, int Level, int Count);

    // Why a target was refused. None means it was written.
    public enum UserListTargetRefusal
    {
        None,
        NotAnOwnList,
        Self,
        Cycle,
        BadTarget,
    }

    // The user's own leveled lists (docs/LeveledList-Create-Plan.md, L-P2 and L-P6).
    //
    // WHAT THE TOOL OWNS HERE IS THE EXISTENCE OF THE LIST, AND WHERE IT HANGS. Nothing else: the
    // generated ESP writes an empty record, and everything that goes into it arrives the way it
    // arrives in any other list - an item's placement, a SkyPatcher rule. That is the whole design,
    // and it is why this store holds two things and not a list editor.
    //
    // Its own small store, like LeveledListEditStore and RemovalStore: one row per write, no snapshot
    // machinery, and the patch generator reads it in one go.
    public static class UserLeveledListStore
    {
        // SkyPatcher refuses to add past this since 7.1.3 and writes a line into its log instead - the
        // record format caps a leveled list at 255 entries.
        public const int MaxEntries = 255;

        public const string EditorIdPrefix = "SCT_LList_";

        // WHERE THE KEYS START, and why it is not 0x1000 like the enchantments.
        //
        // A tool key lives in ONE namespace with every other tool key: PatchFormIdMap maps
        // "SkyrimCraftingTool.esp|<hex>" to a FormID regardless of what kind of record it is. An own
        // list keyed 001001 next to an own enchantment keyed 001001 would share one FormID - the ESP
        // would write two records over each other. Enchantments count up from 0x1000 and recipes from
        // 0xDA216, so lists start far above both.
        //
        // It also stays clear of the generated ESP's own FormIDs (PatchFormIdMapStore, from 0x800),
        // which the scan reads back in once the ESP is active - see PlacementLookup.AllLeveledLists.
        internal const int KeyBaseline = 0x800000;

        // THE ITEM'S WAY IN (L-P5): AN OWN LIST INSIDE THE CONTAINER IT WAS HUNG INTO. Put SCT_LList_800001 into
        // 00SPGlacialChest4, and picking that chest on an item has to offer the list among the chest's
        // own lists - that is what "it hangs there" means to anyone placing an item (user, 2026-10-12).
        //
        // THE ONLY WAY IN. There was a second one - a pseudo-container "Your leveled lists" holding every
        // own list - and it was taken back out (user, 2026-10-12): among the containers it was simply in
        // the wrong place. So a list that hangs only in another LIST, or nowhere yet, cannot be filled
        // from an item until it also hangs in a container.
        //
        // The scan cannot know it: ContainerLVLI is what the plugins say, and the SkyPatcher rule that
        // hangs the list there only exists in the game. But the tool knows - the target is its own
        // decision, in UserLeveledListTarget - so it is merged in here, on top of the scanned rows.
        //
        // The scanned rows are never touched, only an own-list row is ever added or taken away. A new
        // list is assigned rather than the old one edited: the scanned list is ItemDBHandler's cache.
        //
        // Returns the containers whose entries changed, so the caller can rebuild only those rows.
        public static IReadOnlySet<string> MergeOwnListsIntoContainers(IEnumerable<ContainerRecord> containers, string? dbPath = null)
        {
            var changed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var names = ReadAll(dbPath).ToDictionary(l => l.Key, l => l.EditorId, StringComparer.OrdinalIgnoreCase);
            var byContainer = ReadAllTargets(dbPath)
                .Where(t => t.Kind == UserListTargetKind.Container)
                .GroupBy(t => t.TargetKey, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

            foreach (var container in containers)
            {
                if (container == null) continue;

                var current = container.LVLIEntries ?? new List<ContainerLVLIRecord>();
                bool hadOwn = current.Any(e => KeyFactory.IsUserKey(e.LVLiKey));

                byContainer.TryGetValue(container.ContainerKey, out var own);
                if (!hadOwn && own == null) continue;

                var merged = current.Where(e => !KeyFactory.IsUserKey(e.LVLiKey)).ToList();
                foreach (var t in own ?? new List<UserListTarget>())
                {
                    merged.Add(new ContainerLVLIRecord
                    {
                        ContainerKey = container.ContainerKey,
                        LVLiKey = t.ListKey,
                        LVLiName = names.TryGetValue(t.ListKey, out var n) ? n : t.ListKey,
                    });
                }

                // Same entries in the same order is no change - the row need not be rebuilt.
                bool same = merged.Count == current.Count
                            && merged.Zip(current).All(p => string.Equals(p.First.LVLiKey, p.Second.LVLiKey, StringComparison.OrdinalIgnoreCase)
                                                            && p.First.LVLiName == p.Second.LVLiName);
                if (same) continue;

                container.LVLIEntries = merged;
                changed.Add(container.ContainerKey);
            }

            return changed;
        }

        // Raised after every write here, on the caller's thread. MainContentVM re-merges the own lists
        // into the item picker's containers from it (MergeOwnListsIntoContainers).
        public static event Action? Changed;

        private static string ItemDbPath =>
            Path.Combine(GlobalState.Tool?.InputFolder ?? "", "Item", "item.db");

        private static string Resolve(string? dbPath)
            => string.IsNullOrWhiteSpace(dbPath) ? ItemDbPath : dbPath;

        private static SqliteConnection Open(string? dbPath)
        {
            var c = new SqliteConnection($"Data Source={Resolve(dbPath)}");
            c.Open();
            return c;
        }

        private static void RaiseChanged()
        {
            try { Changed?.Invoke(); }
            catch (Exception ex) { AppLogger.LogError("UserLeveledListStore.Changed handler failed", ex); }
        }

        // --------------------
        // The lists
        // --------------------

        public static UserLeveledList Create(string? editorId = null, string? dbPath = null)
        {
            using var c = Open(dbPath);
            using var tx = c.BeginTransaction();

            var key = AllocateKey(c, tx);

            // An EditorID is what the tree shows and what xEdit will list, so it is never blank.
            if (string.IsNullOrWhiteSpace(editorId))
                editorId = EditorIdPrefix + key.Substring(key.IndexOf('|') + 1);

            using (var cmd = c.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = @"
                    INSERT INTO LeveledList (Key, EditorID, ChanceNone, Flags, GlobalKey, Active, Original)
                    VALUES (@k, @e, 0, '', NULL, 1, 0);";
                cmd.Parameters.AddWithValue("@k", key);
                cmd.Parameters.AddWithValue("@e", editorId.Trim());
                cmd.ExecuteNonQuery();
            }

            tx.Commit();
            RaiseChanged();

            return new UserLeveledList(key, editorId.Trim());
        }

        // Highest key ever handed out, from two places: the lists that exist, and the FormID map -
        // which still holds the key of a list that was deleted. Counting only the first would hand a
        // deleted list's key to the next one, and with it the deleted list's FormID: a save that
        // knew the old list would find the new one in its place.
        private static string AllocateKey(SqliteConnection c, SqliteTransaction tx)
        {
            int max = KeyBaseline;

            void Scan(string sql)
            {
                using var cmd = c.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = sql;
                cmd.Parameters.AddWithValue("@prefix", KeyFactory.UserPluginName + "|%");

                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    var key = r.GetString(0);
                    int bar = key.IndexOf('|');
                    if (bar < 0) continue;

                    if (int.TryParse(key.Substring(bar + 1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v)
                        && v >= KeyBaseline && v > max)
                        max = v;
                }
            }

            Scan("SELECT Key FROM LeveledList WHERE Original = 0 AND Key LIKE @prefix");

            if (TableExists(c, tx, "PatchFormIdMap"))
                Scan("SELECT ToolKey FROM PatchFormIdMap WHERE ToolKey LIKE @prefix");

            return $"{KeyFactory.UserPluginName}|{max + 1:X6}";
        }

        public static IReadOnlyList<UserLeveledList> ReadAll(string? dbPath = null)
        {
            var lists = new List<UserLeveledList>();
            if (!File.Exists(Resolve(dbPath))) return lists;

            try
            {
                using var c = Open(dbPath);
                if (!SchemaReady(c)) return lists;

                using var cmd = c.CreateCommand();
                cmd.CommandText = @"
                    SELECT Key, EditorID FROM LeveledList
                    WHERE Original = 0 AND Active = 1
                    ORDER BY EditorID COLLATE NOCASE";

                using var r = cmd.ExecuteReader();
                while (r.Read())
                    lists.Add(new UserLeveledList(r.GetString(0), r.IsDBNull(1) ? "" : r.GetString(1)));
            }
            catch (Exception ex)
            {
                AppLogger.LogError("UserLeveledListStore.ReadAll", ex);
            }

            return lists;
        }

        public static bool IsOwn(string? key, string? dbPath = null)
            => KeyFactory.IsUserKey(key)
               && ReadAll(dbPath).Any(l => string.Equals(l.Key, key, StringComparison.OrdinalIgnoreCase));

        // The EditorID is all there is to rename. Refused for a blank one - see Create.
        public static bool Rename(string key, string editorId, string? dbPath = null)
        {
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(editorId)) return false;

            using var c = Open(dbPath);
            using var cmd = c.CreateCommand();
            cmd.CommandText = "UPDATE LeveledList SET EditorID = @e WHERE Key = @k AND Original = 0";
            cmd.Parameters.AddWithValue("@k", key);
            cmd.Parameters.AddWithValue("@e", editorId.Trim());

            bool changed = cmd.ExecuteNonQuery() > 0;
            if (changed) RaiseChanged();
            return changed;
        }

        // Gone means gone - the list, where it hangs, what hangs in it, and its edited properties.
        // Only ever for Original = 0: a scanned list cannot be deleted, no plugin can be un-shipped.
        //
        // The FormID map row stays, on purpose: see AllocateKey.
        //
        // Placements INTO the list are left in the items' ContainerStrings. They stop resolving, and
        // ContainerEntryVM.ApplyPlacements already drops a list it cannot find - rewriting every
        // item's string here would be a second write path into data this store does not own.
        public static bool Delete(string key, string? dbPath = null)
        {
            if (!KeyFactory.IsUserKey(key)) return false;

            using var c = Open(dbPath);
            using var tx = c.BeginTransaction();

            int deleted;
            using (var cmd = c.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "DELETE FROM LeveledList WHERE Key = @k AND Original = 0";
                cmd.Parameters.AddWithValue("@k", key);
                deleted = cmd.ExecuteNonQuery();
            }

            if (deleted == 0)
            {
                tx.Rollback();
                return false;
            }

            foreach (var sql in new[]
                     {
                         "DELETE FROM UserLeveledListTarget WHERE ListKey = @k OR TargetKey = @k",
                         "DELETE FROM LeveledListEdit WHERE ListKey = @k",
                     })
            {
                if (!TableExists(c, tx, sql.Split(' ')[2])) continue;

                using var cmd = c.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = sql;
                cmd.Parameters.AddWithValue("@k", key);
                cmd.ExecuteNonQuery();
            }

            tx.Commit();

            LeveledListEditStore.InvalidateCache();
            RaiseChanged();
            return true;
        }

        // --------------------
        // Where a list hangs (L-P6)
        // --------------------

        public static IReadOnlyList<UserListTarget> ReadTargets(string listKey, string? dbPath = null)
            => ReadTargetRows("WHERE t.ListKey = @k", listKey, dbPath);

        // Every target of every own list that still exists, for the patch generator.
        public static IReadOnlyList<UserListTarget> ReadAllTargets(string? dbPath = null)
            => ReadTargetRows("", null, dbPath);

        private static IReadOnlyList<UserListTarget> ReadTargetRows(string where, string? listKey, string? dbPath)
        {
            var rows = new List<UserListTarget>();
            if (!File.Exists(Resolve(dbPath))) return rows;

            try
            {
                using var c = Open(dbPath);
                if (!SchemaReady(c) || !TableExists(c, null, "UserLeveledListTarget")) return rows;

                using var cmd = c.CreateCommand();

                // Joined against the list: a target row whose list was deleted outside this store
                // must not become a rule for a record nobody writes.
                cmd.CommandText = $@"
                    SELECT t.ListKey, t.TargetKey, t.TargetKind, t.Level, t.Count
                    FROM UserLeveledListTarget t
                    JOIN LeveledList l ON l.Key = t.ListKey AND l.Original = 0 AND l.Active = 1
                    {where}
                    ORDER BY t.ListKey COLLATE NOCASE, t.TargetKey COLLATE NOCASE";
                if (listKey != null) cmd.Parameters.AddWithValue("@k", listKey);

                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    var kind = string.Equals(r.GetString(2), nameof(UserListTargetKind.Container), StringComparison.OrdinalIgnoreCase)
                        ? UserListTargetKind.Container
                        : UserListTargetKind.LeveledList;

                    rows.Add(new UserListTarget(r.GetString(0), r.GetString(1), kind, r.GetInt32(3), r.GetInt32(4)));
                }
            }
            catch (Exception ex)
            {
                AppLogger.LogError("UserLeveledListStore.ReadTargets", ex);
            }

            return rows;
        }

        // Writes (or replaces) one target. Checked before it is written, not left to SkyPatcher: since
        // 7.1.3 SkyPatcher refuses a self or circular reference too, but it says so in its own log,
        // which nobody reads while placing a list.
        public static UserListTargetRefusal SetTarget(
            string listKey, string targetKey, UserListTargetKind kind, int level = 1, int count = 1, string? dbPath = null)
        {
            if (string.IsNullOrWhiteSpace(targetKey) || KeyFactory.IsUnsetKey(targetKey))
                return UserListTargetRefusal.BadTarget;

            if (!IsOwn(listKey, dbPath))
                return UserListTargetRefusal.NotAnOwnList;

            if (kind == UserListTargetKind.LeveledList)
            {
                if (string.Equals(listKey, targetKey, StringComparison.OrdinalIgnoreCase))
                    return UserListTargetRefusal.Self;

                if (WouldCreateCycle(listKey, targetKey, dbPath))
                    return UserListTargetRefusal.Cycle;
            }

            using var c = Open(dbPath);
            using var cmd = c.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO UserLeveledListTarget (ListKey, TargetKey, TargetKind, Level, Count)
                VALUES (@l, @t, @kind, @lvl, @cnt)
                ON CONFLICT(ListKey, TargetKey) DO UPDATE SET
                    TargetKind = excluded.TargetKind, Level = excluded.Level, Count = excluded.Count;";
            cmd.Parameters.AddWithValue("@l", listKey);
            cmd.Parameters.AddWithValue("@t", targetKey);
            cmd.Parameters.AddWithValue("@kind", kind.ToString());
            // A level of 0 is how the item editor says "not placed" - here it would be a list that
            // never comes out. And at least one of it, or the entry hands out nothing.
            cmd.Parameters.AddWithValue("@lvl", Math.Max(1, level));
            cmd.Parameters.AddWithValue("@cnt", Math.Max(1, count));
            cmd.ExecuteNonQuery();

            RaiseChanged();
            return UserListTargetRefusal.None;
        }

        public static bool RemoveTarget(string listKey, string targetKey, string? dbPath = null)
        {
            using var c = Open(dbPath);
            using var cmd = c.CreateCommand();
            cmd.CommandText = "DELETE FROM UserLeveledListTarget WHERE ListKey = @l AND TargetKey = @t";
            cmd.Parameters.AddWithValue("@l", listKey);
            cmd.Parameters.AddWithValue("@t", targetKey);

            bool removed = cmd.ExecuteNonQuery() > 0;
            if (removed) RaiseChanged();
            return removed;
        }

        // Would putting `listKey` into `targetKey` make a list contain itself?
        //
        // Only the user's own lists can close a loop. A scanned list is never inside an own list -
        // nothing puts one there: own lists are filled by item placements and by other own lists - so
        // walking down from `listKey` only ever meets own lists, and the question reduces to: is the
        // target already somewhere inside the list being placed?
        internal static bool WouldCreateCycle(string listKey, string targetKey, string? dbPath = null)
        {
            // child -> the lists it hangs in, read once
            var hangsIn = ReadAllTargets(dbPath)
                .Where(t => t.Kind == UserListTargetKind.LeveledList)
                .ToList();

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { listKey };
            var stack = new Stack<string>();
            stack.Push(listKey);

            while (stack.Count > 0)
            {
                var parent = stack.Pop();

                // Everything that hangs in `parent` is below it.
                foreach (var child in hangsIn.Where(t => string.Equals(t.TargetKey, parent, StringComparison.OrdinalIgnoreCase)))
                {
                    if (string.Equals(child.ListKey, targetKey, StringComparison.OrdinalIgnoreCase))
                        return true;

                    if (seen.Add(child.ListKey))
                        stack.Push(child.ListKey);
                }
            }

            return false;
        }

        // A database the startup schema pass has not reached yet - or one another tool built - has no
        // own lists by definition. Answered quietly: it is not an error to have none.
        private static bool SchemaReady(SqliteConnection c)
        {
            if (!TableExists(c, null, "LeveledList")) return false;

            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('LeveledList') WHERE name = 'Original'";
            return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
        }

        private static bool TableExists(SqliteConnection c, SqliteTransaction? tx, string table)
        {
            using var cmd = c.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=@t";
            cmd.Parameters.AddWithValue("@t", table);
            return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
        }
    }
}
