using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Data.Sqlite;
using SkyrimCraftingTool.Model;

namespace SkyrimCraftingTool.Services
{
    // Where does something already sit, and what is in a leveled list.
    //
    // Read-only, and deliberately so: this answers questions about the load order as scanned. It has
    // nothing to do with the placements the user configures in the Container tab - those live in
    // ContainerString and are what the patch WILL add. Mixing the two would be the most misleading
    // thing this feature could do: "already in the game" and "you asked for it" look identical in a
    // list and mean opposite things.
    public enum PlacementKind { LeveledList, Container }

    // One place a thing was found. Level/Count are only meaningful for a leveled list.
    public sealed record Placement(
        PlacementKind Kind, string Key, string Name, int Level, int Count);

    // One entry of a leveled list. IsList marks an entry that points at another leveled list rather
    // than at an item - a third of all entries do. Those are shown as a link, not resolved: with a
    // nesting depth of up to 9, expanding everything can mean thousands of rows, and the interesting
    // answer is almost always the next hop.
    // IsPlanned marks a row the PATCH will add - it is not in the list yet. Shown alongside the
    // scanned contents rather than in a separate box, because the question the contents answer is
    // "what will come out of this list", and a placement the user has already made is part of that
    // answer. Marked, never mixed silently: what the game has and what the patch will add must stay
    // tellable apart.
    // IsRestored narrows IsPlanned: both are rows the patch will add, but one is something the user
    // chose to put into the list and the other is something that USED to be in it until an override
    // dropped it. Same effect on the game, different thing to read months later - "+ new" on a
    // vanilla item that was always meant to be there would be a small lie.
    public sealed record LeveledListEntryInfo(
        int Ordinal, string Reference, string Name, int Level, int Count, bool IsList,
        bool IsPlanned = false, bool IsRestored = false);

    // An item that used to be in the list and is not in the winning version any more. Name is
    // resolved the same way an entry's is; LostFrom is the plugin it was last seen in, which is the
    // first thing anyone asks once they see something is missing.
    public sealed record LeveledListLostEntryInfo(
        string Reference, string Name, int Level, int Count, string LostFrom, bool IsList,
        bool Ambiguous = false);

    // One line of the overview: a list that lost entries, and how many.
    public sealed record LostListSummary(
        string ListKey, string EditorId, int LostCount);

    public sealed record LeveledListInfo(
        string Key, string EditorId, int ChanceNone, string Flags, string GlobalKey,
        IReadOnlyList<LeveledListEntryInfo> Entries,
        IReadOnlyList<LeveledListLostEntryInfo> LostEntries);

    public static class PlacementLookup
    {
        private static string ItemDbPath =>
            Path.Combine(GlobalState.Tool?.InputFolder ?? "", "Item", "item.db");

        // Both entry points take an optional path so they can be pointed at a prepared database in
        // tests. Defaulting to GlobalState keeps every real call site unchanged.
        private static SqliteConnection? TryOpen(string? dbPath)
        {
            var path = string.IsNullOrWhiteSpace(dbPath) ? ItemDbPath : dbPath;
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;

            var c = new SqliteConnection($"Data Source={path};Mode=ReadOnly");
            c.Open();
            return c;
        }

        // The first column that actually holds something, else the raw key.
        //
        // The key is a deliberate last resort, not an empty cell: about 7.7% of entries point at
        // record types the scan does not cover (books, scrolls, keys, ...), and "Skyrim.esm|0ED03A"
        // at least says which record it is. A blank would read as "nothing here".
        private static string FirstNonEmpty(SqliteDataReader r, string fallback, params int[] columns)
        {
            foreach (var column in columns)
            {
                if (r.IsDBNull(column)) continue;

                var value = r.GetString(column);
                if (!string.IsNullOrWhiteSpace(value)) return value;
            }

            return fallback;
        }

        // An item.db from before a table existed is a normal thing to meet: the schema is brought up
        // to date on launch, but this reader runs against whatever file it is pointed at, including
        // in tests. A missing table degrades to "nothing to report" rather than throwing and taking
        // the whole list window with it.
        private static bool HasTable(SqliteConnection c, string table)
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = @n";
            cmd.Parameters.AddWithValue("@n", table);
            return cmd.ExecuteScalar() != null;
        }

        private const string FormIdAlias = "formids";

        // Where formid.db sits relative to the database being read. Next to item.db in tests (so the
        // attach path can actually be exercised), in its own folder under Input in the real layout.
        private static string FormIdPathFor(string? dbPath)
            => string.IsNullOrWhiteSpace(dbPath)
                ? Path.Combine(GlobalState.Tool?.InputFolder ?? "", "FormID", "formid.db")
                : Path.Combine(Path.GetDirectoryName(dbPath) ?? "", "formid.db");

        // formid.db holds the reference tables, above all Materials. Attaching it costs one statement
        // and saves a lookup per row; if it is missing the query runs without those names rather than
        // failing, because a missing reference database must not cost the whole list view.
        //
        // THE ALREADY-ATTACHED CHECK IS NOT BELT AND BRACES. Microsoft.Data.Sqlite pools connections:
        // disposing one hands the underlying handle back to the pool WITH this database still
        // attached, and the next open gets that same handle. A second ATTACH then fails with
        // "database formids is already in use" - so the first list a user opened showed names and
        // every one after it showed raw keys, until the pool happened to hand out a fresh handle.
        private static bool TryAttachFormIds(SqliteConnection c, string? dbPath)
        {
            var path = FormIdPathFor(dbPath);
            if (!File.Exists(path)) return false;

            try
            {
                // Attached ALREADY IS NOT ENOUGH - it has to be attached to THIS file. A pooled
                // handle can come back carrying `formids` pointing at a different formid.db, and
                // the name check alone accepted that and then read names out of the wrong database.
                // Found as a flaky test (one run green, the next red) once enough test classes ran
                // in parallel to make the pool hand such a handle over.
                var attached = AttachedPath(c);

                if (attached != null)
                {
                    if (SamePath(attached, path)) return true;

                    using var detach = c.CreateCommand();
                    detach.CommandText = $"DETACH DATABASE {FormIdAlias}";
                    detach.ExecuteNonQuery();
                }

                using var cmd = c.CreateCommand();
                cmd.CommandText = $"ATTACH DATABASE @p AS {FormIdAlias}";
                cmd.Parameters.AddWithValue("@p", path);
                cmd.ExecuteNonQuery();
                return true;
            }
            catch (Exception ex)
            {
                AppLogger.LogWarning($"PlacementLookup: formid.db could not be attached ({ex.Message})");
                return false;
            }
        }

        // The file `formids` currently points at on this connection, or null when nothing is
        // attached under that name.
        private static string? AttachedPath(SqliteConnection c)
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT file FROM pragma_database_list WHERE name = @a";
            cmd.Parameters.AddWithValue("@a", FormIdAlias);

            var value = cmd.ExecuteScalar();
            return value == null || value == DBNull.Value ? null : Convert.ToString(value);
        }

        internal static bool SamePath(string? a, string? b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;

            try
            {
                return string.Equals(
                    Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar),
                    Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                // An unusable path is not the same path, and comparing them must not throw in the
                // middle of opening a list.
                return false;
            }
        }

        // Every leveled list and container that already holds this key, straight from the scanned
        // load order. Direct placements only - no walking up through parent lists. In the item view
        // the question is "is this already in the game somewhere", and a chain of six lists answers
        // a different question than the one being asked.
        public static IReadOnlyList<Placement> WhereIs(string itemKey, string? dbPath = null)
        {
            var found = new List<Placement>();
            if (string.IsNullOrWhiteSpace(itemKey)) return found;

            try
            {
                using var c = TryOpen(dbPath);
                if (c == null) return found;

                using (var cmd = c.CreateCommand())
                {
                    cmd.CommandText = @"
                        SELECT l.Key, l.EditorID, e.Level, e.Count
                        FROM LeveledListEntry e
                        JOIN LeveledList l ON l.Key = e.ListKey
                        WHERE e.Reference = @k AND l.Active = 1
                        ORDER BY l.EditorID";
                    cmd.Parameters.AddWithValue("@k", itemKey);
                    using var r = cmd.ExecuteReader();
                    while (r.Read())
                        found.Add(new Placement(PlacementKind.LeveledList,
                            r.GetString(0), r.GetString(1), r.GetInt32(2), r.GetInt32(3)));
                }

                using (var cmd = c.CreateCommand())
                {
                    cmd.CommandText = @"
                        SELECT k.ContainerKey, k.Name, e.Count
                        FROM ContainerEntry e
                        JOIN Container k ON k.ContainerKey = e.ContainerKey
                        WHERE e.Reference = @k AND k.Active = 1
                        ORDER BY k.Name";
                    cmd.Parameters.AddWithValue("@k", itemKey);
                    using var r = cmd.ExecuteReader();
                    while (r.Read())
                        found.Add(new Placement(PlacementKind.Container,
                            r.GetString(0), r.GetString(1), 0, r.GetInt32(2)));
                }
            }
            catch (Exception ex)
            {
                // A lookup that fails costs an informational panel, never the editor around it.
                AppLogger.LogError($"PlacementLookup.WhereIs({itemKey})", ex);
            }

            return found;
        }

        // A placement the patch WILL make into this list, read from another item's ContainerString.
        // Level/Count are what the rule will carry.
        public sealed record PlannedPlacement(string ItemKey, string Name, int Level, int Count);

        // Everything the user has lined up for this list, across ALL items - not just the one whose
        // window is open.
        //
        // Without this the list view shows the load order as scanned and nothing else, so ten items
        // placed into the same list are invisible until the patch is generated. They also change the
        // odds: an item joining a list of twelve is one in thirteen, but if nine more are waiting in
        // other items' settings it is one in twenty-two. That is the difference between a calculator
        // that answers the question and one that flatters it.
        //
        // The same "lowest level wins" rule as the patch (see LeveledListRuleBuilder.BuildRules): one
        // list can be reached through several containers with different levels, and the shown value
        // has to be the one that will actually be written.
        public static IReadOnlyList<PlannedPlacement> PlannedFor(string listKey, string? dbPath = null)
        {
            var planned = new List<PlannedPlacement>();
            if (string.IsNullOrWhiteSpace(listKey)) return planned;

            try
            {
                using var c = TryOpen(dbPath);
                if (c == null) return planned;

                foreach (var table in new[] { "Armor", "Weapons" })
                {
                    using var cmd = c.CreateCommand();

                    // The shadow column, not the base one: the scan never writes ContainerString, so
                    // every placement is a user edit and lives in IsEditedContainerString. LIKE is a
                    // cheap pre-filter - the string is parsed properly below, because a key can also
                    // appear as the container's own key.
                    cmd.CommandText = $@"
                        SELECT Key, COALESCE(NULLIF(Name, ''), EditorID), IsEditedContainerString
                        FROM {table}
                        WHERE IsEdited = 1 AND Active = 1
                          AND IsEditedContainerString IS NOT NULL
                          AND IsEditedContainerString LIKE @like
                        ORDER BY 2";
                    cmd.Parameters.AddWithValue("@like", "%" + listKey + "%");

                    using var r = cmd.ExecuteReader();
                    while (r.Read())
                    {
                        var itemKey = r.GetString(0);
                        var name = r.IsDBNull(1) ? itemKey : r.GetString(1);
                        var containerString = r.IsDBNull(2) ? "" : r.GetString(2);

                        var best = LowestPlacementIn(containerString, listKey);
                        if (best != null)
                            planned.Add(new PlannedPlacement(itemKey, name, best.Value.Level, best.Value.Count));
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.LogError($"PlacementLookup.PlannedFor({listKey})", ex);
            }

            return planned;
        }

        private static (int Level, int Count)? LowestPlacementIn(string containerString, string listKey)
        {
            List<ParsedContainerEntry> parsed;
            try
            {
                parsed = ContainerStringParser.Parse(containerString);
            }
            catch (Exception)
            {
                // A malformed string costs this one item's row, never the window.
                return null;
            }

            (int Level, int Count)? best = null;

            foreach (var container in parsed)
                foreach (var (key, placement) in container.Placements)
                {
                    if (placement.Level <= 0) continue;
                    if (!string.Equals(key, listKey, StringComparison.OrdinalIgnoreCase)) continue;

                    if (best == null || placement.Level < best.Value.Level)
                        best = (placement.Level, Math.Max(placement.Count, 1));
                }

            return best;
        }

        // A container that reaches this list, and through which of its entries.
        //
        // One container can hold several lists that all lead here, which is why Entries is a list:
        // each one is an independent chance at the same restock, and reporting only the first would
        // understate it.
        public sealed record ContainerReach(
            string ContainerKey, string Name, IReadOnlyList<(string ListKey, int Count)> Entries);

        // Which containers roll this list - directly or through any chain of parent lists.
        //
        // The other direction from everything else here, and the one that answers "where would I
        // actually run into this". A list is not rolled by itself: it is rolled because a chest, a
        // merchant or a corpse holds something that leads to it, possibly six levels up. Measured on
        // the real load order, LItemArmorBootsHeavyBlacksmith has 62 ancestors over 6 levels and is
        // reached by 120 containers.
        //
        // Ancestors are walked in SQL rather than in memory: the edge table is 28,475 rows, and a
        // recursive CTE with UNION handles both the diamond shapes and the cycles.
        public static IReadOnlyList<ContainerReach> ContainersReaching(string listKey, string? dbPath = null)
        {
            var found = new List<ContainerReach>();
            if (string.IsNullOrWhiteSpace(listKey)) return found;

            try
            {
                using var c = TryOpen(dbPath);
                if (c == null) return found;

                using var cmd = c.CreateCommand();
                cmd.CommandText = @"
                    WITH RECURSIVE up(Key) AS (
                        SELECT Key FROM LeveledList WHERE Key = @root AND Active = 1
                        UNION
                        SELECT parent.Key
                        FROM LeveledListEntry e
                        JOIN up ON up.Key = e.Reference
                        JOIN LeveledList parent ON parent.Key = e.ListKey AND parent.Active = 1
                    )
                    SELECT k.ContainerKey, k.Name, ce.Reference, ce.Count
                    FROM ContainerEntry ce
                    JOIN Container k ON k.ContainerKey = ce.ContainerKey AND k.Active = 1
                    WHERE ce.Reference IN (SELECT Key FROM up)
                    ORDER BY k.Name, ce.Ordinal";
                cmd.Parameters.AddWithValue("@root", listKey);

                var byContainer = new Dictionary<string, (string Name, List<(string, int)> Entries)>(
                    StringComparer.OrdinalIgnoreCase);

                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    var containerKey = r.GetString(0);
                    if (!byContainer.TryGetValue(containerKey, out var bucket))
                        byContainer[containerKey] = bucket = (r.GetString(1), new List<(string, int)>());

                    bucket.Entries.Add((r.GetString(2), Math.Max(r.GetInt32(3), 1)));
                }

                foreach (var (key, (name, entries)) in byContainer)
                    found.Add(new ContainerReach(key, name, entries));
            }
            catch (Exception ex)
            {
                AppLogger.LogError($"PlacementLookup.ContainersReaching({listKey})", ex);
            }

            return found;
        }

        // How many of the item each container already holds.
        //
        // Summed per container rather than counted per row, because ContainerEntry is keyed
        // positionally: a container listing the same item twice is two rows and genuinely holds
        // both. Counting rows would report "1x" for a chest with three, which is the kind of wrong
        // number nobody checks.
        public static Dictionary<string, int> HeldPerContainer(IReadOnlyList<Placement> placements)
        {
            var held = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (var p in placements)
            {
                if (p.Kind != PlacementKind.Container) continue;

                held.TryGetValue(p.Key, out var running);
                held[p.Key] = running + Math.Max(p.Count, 1);
            }

            return held;
        }

        // The one line above the container list: what the game already has, counted.
        //
        // Containers are named AND marked in the list below; leveled lists are only counted. That
        // asymmetry is the whole design - 86% of the 4,312 leveled lists in the real load order hang
        // in no container at all, so there is no row a list placement could be marked on without
        // inventing one.
        //
        // hiddenContainers is what the current list cannot show: the standard view lists merchants
        // only, so a hit in an ordinary chest has no row to carry a badge. Saying "2 containers"
        // while nothing is marked would read as a bug in the marking.
        public static string DescribeExisting(int containers, int lists, int hiddenContainers)
        {
            if (containers == 0 && lists == 0)
                return "Already in your load order: nowhere yet - no container and no leveled list holds this item.";

            var summary = $"Already in your load order: {containers} container(s), {lists} leveled list(s). " +
                          "Marked below, and not something your patch adds.";

            if (hiddenContainers > 0)
                summary += $" {hiddenContainers} of them are outside the current list - switch to Expert view to see them.";

            return summary;
        }

        // One list with its entries, in list order. Names come from whichever table knows the
        // reference; an entry whose target was never scanned keeps its raw key rather than showing
        // an empty cell, because "no name" and "not in your load order" are worth telling apart.
        // Every list that lost something, for the overview window.
        //
        // WHY A WINDOW AND NOT JUST THE BLOCK IN THE LIST: the per-list block only tells you
        // anything if you already opened that list, and lists are reached through the item that
        // sits in them. On a real load order that means 155 affected lists nobody will ever open by
        // accident. Reported per list, most losses first, because that is the order in which they
        // are worth looking at.
        public static IReadOnlyList<LostListSummary> ReadListsWithLostEntries(string? dbPath = null)
        {
            var result = new List<LostListSummary>();

            try
            {
                using var c = TryOpen(dbPath);
                if (c == null || !HasTable(c, "LeveledListLostEntry")) return result;

                // Grouped in SQL rather than in memory: 340 lost rows is nothing, but this runs on
                // window open and there is no reason to carry them all across. The lost items
                // themselves are not named here - the row is a way in, and the list it opens shows
                // them with the names they would have had if they were still in it.
                using var cmd = c.CreateCommand();
                cmd.CommandText = @"
                    SELECT l.ListKey,
                           COALESCE(NULLIF(ll.EditorID, ''), l.ListKey) AS listName,
                           COUNT(*) AS lostCount
                    FROM LeveledListLostEntry l
                    LEFT JOIN LeveledList ll ON ll.Key = l.ListKey
                    GROUP BY l.ListKey
                    ORDER BY lostCount DESC, listName";

                using var r = cmd.ExecuteReader();
                while (r.Read())
                    result.Add(new LostListSummary(r.GetString(0), r.GetString(1), r.GetInt32(2)));
            }
            catch (Exception ex)
            {
                AppLogger.LogError("PlacementLookup.ReadListsWithLostEntries", ex);
            }

            return result;
        }

        public static LeveledListInfo? Read(string listKey, string? dbPath = null)
        {
            if (string.IsNullOrWhiteSpace(listKey)) return null;

            try
            {
                using var c = TryOpen(dbPath);
                if (c == null) return null;

                string editorId, flags, globalKey;
                int chanceNone;

                using (var cmd = c.CreateCommand())
                {
                    cmd.CommandText = "SELECT EditorID, ChanceNone, Flags, GlobalKey FROM LeveledList WHERE Key = @k";
                    cmd.Parameters.AddWithValue("@k", listKey);
                    using var r = cmd.ExecuteReader();
                    if (!r.Read()) return null;
                    editorId = r.GetString(0);
                    chanceNone = r.GetInt32(1);
                    flags = r.IsDBNull(2) ? "" : r.GetString(2);
                    globalKey = r.IsDBNull(3) ? "" : r.GetString(3);
                }

                // formid.db carries the reference tables - above all Materials, which is everything a
                // recipe can use as an ingredient (MISC plus INGR, AMMO, SLGM, ALCH). 4,413 of 28,475
                // entries point at one of those, and without this they were shown as a raw key.
                bool hasReferences = TryAttachFormIds(c, dbPath);

                var entries = new List<LeveledListEntryInfo>();
                using (var cmd = c.CreateCommand())
                {
                    // The LEFT JOINs decide whether an entry is a nested list or an item and supply a
                    // display name, without a round trip per row.
                    //
                    // Name before EditorID: Name is what the item is called in game ("Steel Sword"),
                    // EditorID is the modder's handle for it ("SteelSword01"). Name is usually there -
                    // 243 of 4,472 armors lack one - so EditorID is the fallback, not the first pick.
                    cmd.CommandText = $@"
                        SELECT e.Ordinal, e.Reference, e.Level, e.Count,
                               nested.EditorID,
                               a.Name, a.EditorID,
                               w.Name, w.EditorID
                               {(hasReferences ? ", m.Name" : ", NULL")}
                        FROM LeveledListEntry e
                        LEFT JOIN LeveledList nested ON nested.Key = e.Reference
                        LEFT JOIN Armor a           ON a.Key      = e.Reference
                        LEFT JOIN Weapons w         ON w.Key      = e.Reference
                        {(hasReferences ? "LEFT JOIN formids.Materials m ON m.Key = e.Reference" : "")}
                        WHERE e.ListKey = @k
                        ORDER BY e.Ordinal";
                    cmd.Parameters.AddWithValue("@k", listKey);
                    using var r = cmd.ExecuteReader();
                    while (r.Read())
                    {
                        var reference = r.GetString(1);
                        bool isList = !r.IsDBNull(4);

                        entries.Add(new LeveledListEntryInfo(
                            r.GetInt32(0), reference,
                            FirstNonEmpty(r, reference, 4, 5, 6, 7, 8, 9),
                            r.GetInt32(2), r.GetInt32(3), isList));
                    }
                }

                // What the overrides dropped. Same joins as the entries above, so a lost item reads
                // with the same name it would have had if it were still there. Ordered by name
                // rather than by any stored order: there is no order left to preserve - these rows
                // come from versions of the list that no longer exist.
                var lost = new List<LeveledListLostEntryInfo>();
                if (HasTable(c, "LeveledListLostEntry"))
                {
                    using var cmd = c.CreateCommand();
                    cmd.CommandText = $@"
                        SELECT l.Reference, l.Level, l.Count, l.LostFrom, l.Ambiguous,
                               nested.EditorID,
                               a.Name, a.EditorID,
                               w.Name, w.EditorID
                               {(hasReferences ? ", m.Name" : ", NULL")}
                        FROM LeveledListLostEntry l
                        LEFT JOIN LeveledList nested ON nested.Key = l.Reference
                        LEFT JOIN Armor a           ON a.Key      = l.Reference
                        LEFT JOIN Weapons w         ON w.Key      = l.Reference
                        {(hasReferences ? "LEFT JOIN formids.Materials m ON m.Key = l.Reference" : "")}
                        WHERE l.ListKey = @k";
                    cmd.Parameters.AddWithValue("@k", listKey);
                    using var r = cmd.ExecuteReader();
                    while (r.Read())
                    {
                        var reference = r.GetString(0);

                        // Columns 5..10 are the name candidates, in the order FirstNonEmpty should
                        // try them; 5 is the nested list's EditorID and therefore doubles as "this
                        // entry is a list".
                        lost.Add(new LeveledListLostEntryInfo(
                            reference,
                            FirstNonEmpty(r, reference, 5, 6, 7, 8, 9, 10),
                            r.GetInt32(1), r.GetInt32(2),
                            r.IsDBNull(3) ? "" : r.GetString(3),
                            IsList: !r.IsDBNull(5),
                            Ambiguous: !r.IsDBNull(4) && r.GetInt32(4) == 1));
                    }
                    lost.Sort((x, y) => string.Compare(x.Name, y.Name, StringComparison.CurrentCultureIgnoreCase));
                }

                return new LeveledListInfo(listKey, editorId, chanceNone, flags, globalKey, entries, lost);
            }
            catch (Exception ex)
            {
                AppLogger.LogError($"PlacementLookup.Read({listKey})", ex);
                return null;
            }
        }
    }
}
