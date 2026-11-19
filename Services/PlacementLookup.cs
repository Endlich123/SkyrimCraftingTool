using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Data.Sqlite;
using SkyrimCraftingTool.Model;

namespace SkyrimCraftingTool.Services
{
    public enum PlacementKind { LeveledList, Container }

    // One place a thing was found. Level/Count are only meaningful for a leveled list.
    public sealed record Placement(
        PlacementKind Kind, string Key, string Name, int Level, int Count);

    public sealed record LeveledListEntryInfo(
        int Ordinal, string Reference, string Name, int Level, int Count, bool IsList,
        bool IsPlanned = false, bool IsRestored = false);

    // An item that used to be in the list and is not in the winning version any more. Name is
    // resolved the same way an entry's is.
    //
    // THREE PLUGINS, DELIBERATELY. AddedBy put it in, LostFrom is the last version that still had
    // it, DroppedBy is the version right after that. For a long time only LostFrom was carried, and
    // a tester read it as the culprit - which is the one thing it is not.
    public sealed record LeveledListLostEntryInfo(
        string Reference, string Name, int Level, int Count, string LostFrom, bool IsList,
        bool Ambiguous = false, string AddedBy = "", string DroppedBy = "");

    public sealed record OwnerSummary(string Key, string Name, int EntryCount, bool IsOwn = false);

    public sealed record OwnedEntry(
        string Reference, string Name, int Occurrences, int TotalCount, bool IsList, bool IsRemoved,
        IReadOnlyList<OwnedOccurrence>? Breakdown = null);

    // Who will put a row there. Scanned is the load order as it is; the other two are the patch.
    public enum OwnedOrigin { Scanned, Planned, Readded }

    // One row of a container or a leveled list. Level is 0 for a container - a chest has no levels,
    // and ContainerEntry has no column for one.
    public sealed record OwnedOccurrence(int Level, int Count, OwnedOrigin Origin = OwnedOrigin.Scanned);

    // One line of the overview: a list that lost entries, how many, and who dropped them.
    //
    // DroppedBy is a SUMMARY across the list's lost entries, so it has two shapes: the one plugin
    // responsible, or - when several were - how many. Without it the overview could only say that
    // something went missing, which is the complaint that produced this field ("it tends to raise
    // more questions than it answers").
    public sealed record LostListSummary(
        string ListKey, string EditorId, int LostCount,
        string DroppedBy = "", int DroppedByCount = 0);

    // What one leveled list has lost, for a History that collects several of them - see
    // PlacementLookup.ReadLostEntries. ListName is the list's EditorID, because in a container's
    // History the row has to say which of the chest's lists it came from.
    public sealed record LostEntryGroup(
        string ListKey, string ListName, IReadOnlyList<LeveledListLostEntryInfo> Entries);

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

        // formid.db is not attached any more (2026-11-07). It was, for the names of its Materials table -
        // a copy of WorldItem's misc/ingredient/food/ammo/soul-gem rows, which the queries below join
        // directly. With it went the pooled-ATTACH workaround (a disposed connection came back still
        // attached, possibly to another file) that this spot used to need.

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

        public static IReadOnlyList<PlannedPlacement> PlannedFor(string listKey, string? dbPath = null)
        {
            var planned = new List<PlannedPlacement>();

            foreach (var (itemKey, name, containerString) in ReadPlacementStrings(listKey, dbPath, nameof(PlannedFor)))
            {
                var best = LowestPlacementIn(containerString, listKey);
                if (best != null)
                    planned.Add(new PlannedPlacement(itemKey, name, best.Value.Level, best.Value.Count));
            }

            return planned;
        }

        // The same question for a CONTAINER: which items will the patch put into this chest itself.
        //
        // IT IS A DIFFERENT TEST, which is why PlannedFor could not answer it and quietly returned
        // nothing. A placement is stored as "{ContainerKey: {LVLiKey,Level; ...}}", and the sliders
        // decide what was meant: any slider above 0 places the item into THOSE leveled lists, every
        // slider at 0 places it into the container itself. PlannedFor looks for a raised slider on
        // the list it was asked about; a direct container placement has none, so it matched nothing.
        //
        // The test below is the one LeveledListRuleBuilder.ParsePlacements makes before it writes
        // filterByContainers + addOnceToContainers. It has to stay the same test: a view that
        // disagrees with the rule builder either shows a row the patch never writes or hides one it
        // does.
        //
        // Count is 1 because that is what the rule carries (LeveledListRuleBuilder.EntryCount) - the
        // Container tab models a level per list, never an amount.
        public static IReadOnlyList<PlannedPlacement> PlannedForContainer(string containerKey, string? dbPath = null)
        {
            var planned = new List<PlannedPlacement>();

            foreach (var (itemKey, name, containerString) in ReadPlacementStrings(containerKey, dbPath, nameof(PlannedForContainer)))
            {
                if (IsDirectPlacementInto(containerString, containerKey))
                    planned.Add(new PlannedPlacement(itemKey, name, Level: 0, Count: 1));
            }

            return planned;
        }

        private static IEnumerable<(string ItemKey, string Name, string ContainerString)> ReadPlacementStrings(
            string key, string? dbPath, string what)
        {
            var rows = new List<(string, string, string)>();
            if (string.IsNullOrWhiteSpace(key)) return rows;

            try
            {
                using var c = TryOpen(dbPath);
                if (c == null) return rows;

                foreach (var table in new[] { "Armor", "Weapons", "WorldItem" })
                {
                    // An older database may not have WorldItem at all; the other two still answer.
                    if (!HasTable(c, table)) continue;

                    using var cmd = c.CreateCommand();
                    cmd.CommandText = $@"
                        SELECT Key, COALESCE(NULLIF(Name, ''), EditorID), IsEditedContainerString
                        FROM {table}
                        WHERE IsEdited = 1 AND Active = 1
                          AND IsEditedContainerString IS NOT NULL
                          AND IsEditedContainerString LIKE @like
                        ORDER BY 2";
                    cmd.Parameters.AddWithValue("@like", "%" + key + "%");

                    using var r = cmd.ExecuteReader();
                    while (r.Read())
                    {
                        var itemKey = r.GetString(0);
                        rows.Add((
                            itemKey,
                            r.IsDBNull(1) ? itemKey : r.GetString(1),
                            r.IsDBNull(2) ? "" : r.GetString(2)));
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.LogError($"PlacementLookup.{what}({key})", ex);
            }

            return rows;
        }

        // "Goes straight into that container": the container is named and not one of its sliders was
        // raised. See PlannedForContainer for why this mirrors the rule builder exactly.
        private static bool IsDirectPlacementInto(string containerString, string containerKey)
        {
            List<ParsedContainerEntry> parsed;
            try
            {
                parsed = ContainerStringParser.Parse(containerString);
            }
            catch (Exception)
            {
                // A malformed string costs this one item's row, never the view.
                return false;
            }

            foreach (var container in parsed)
            {
                if (!string.Equals(container.ContainerKey, containerKey, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!container.Placements.Any(p => p.Value.Level > 0))
                    return true;
            }

            return false;
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
        // --- The Container / LeveledList tab (2026-09-30) ---
        //
        // Two trees and two content views over data the scan has always collected: ContainerEntry
        // holds every entry of every container, LeveledListEntry every entry of every list. Until
        // now both were read only from the far side - "where does this item already appear" - so
        // nobody could open a container and look inside it.

        // The tree of containers. Only those that hold something: an empty container is a row nobody
        // can do anything with, and the real load order has plenty of them.
        public static IReadOnlyList<OwnerSummary> AllContainers(string? dbPath = null)
            => ReadOwners(dbPath,
                @"SELECT k.ContainerKey, COALESCE(NULLIF(k.Name, ''), k.ContainerKey), COUNT(e.Reference)
                  FROM Container k
                  JOIN ContainerEntry e ON e.ContainerKey = k.ContainerKey
                  WHERE k.Active = 1
                  GROUP BY k.ContainerKey
                  ORDER BY 2",
                "AllContainers");

        public static IReadOnlyList<OwnerSummary> AllLeveledLists(string? dbPath = null)
            => ReadOwners(dbPath, HasOwnListColumn(dbPath) ? LeveledListsSql : LeveledListsWithoutOwnSql, "AllLeveledLists");

        // --- The name of any list, for a placement into a NESTED list ---
        //
        // A container's rows come from ContainerLVLI, which only knows the lists hanging in it
        // directly. A placement into a list further down (stored under the container it was reached
        // through) has no such row, so ContainerEntryVM builds one - and needs the name for it, and
        // the answer to "does this list still exist at all". A list that left the load order, or an
        // own list that was deleted, resolves to null, and its placement is dropped exactly as a
        // vanished direct list always was (see UserLeveledListStore.Delete).
        //
        // One query for every list, cached: the item editor rebuilds container rows on every item
        // change, and a query per row would be the per-row cost LeveledListEditStore.EditedKeys was
        // cached to avoid. Dropped on a rescan and on every own-list write.
        private static Dictionary<string, string>? _listNames;
        private static readonly object ListNamesLock = new();

        // Test seam: answers instead of the database while set. AsyncLocal, so one test class setting
        // it cannot change what a test running in parallel sees.
        private static readonly System.Threading.AsyncLocal<Func<string, string?>?> _listNameOverride = new();
        internal static Func<string, string?>? ListNameOverride
        {
            get => _listNameOverride.Value;
            set => _listNameOverride.Value = value;
        }

        public static string? LeveledListName(string listKey)
        {
            if (string.IsNullOrWhiteSpace(listKey)) return null;
            if (ListNameOverride is { } answer) return answer(listKey);

            lock (ListNamesLock)
            {
                _listNames ??= AllLeveledLists()
                    .GroupBy(l => l.Key, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.First().Name, StringComparer.OrdinalIgnoreCase);

                return _listNames.TryGetValue(listKey, out var name) ? name : null;
            }
        }

        public static void InvalidateListNames()
        {
            lock (ListNamesLock) _listNames = null;
        }

        private const string ToolOutputKeyFilter =
            "(l.Key LIKE 'SkyrimCraftingTool.esp|%' OR l.Key LIKE 'SkyrimCraftingTool - %.esp|%')";

        private const string LeveledListsSql =
            @"SELECT l.Key, COALESCE(NULLIF(l.EditorID, ''), l.Key), COUNT(e.Reference),
                     CASE WHEN l.Original = 0 THEN 1 ELSE 0 END
              FROM LeveledList l
              LEFT JOIN LeveledListEntry e ON e.ListKey = l.Key
              WHERE l.Active = 1
                AND NOT (l.Original = 1 AND " + ToolOutputKeyFilter + @")
              GROUP BY l.Key
              HAVING COUNT(e.Reference) > 0 OR MAX(l.Original) = 0
              ORDER BY 2";

        private const string LeveledListsWithoutOwnSql =
            @"SELECT l.Key, COALESCE(NULLIF(l.EditorID, ''), l.Key), COUNT(e.Reference)
              FROM LeveledList l
              JOIN LeveledListEntry e ON e.ListKey = l.Key
              WHERE l.Active = 1
                AND NOT " + ToolOutputKeyFilter + @"
              GROUP BY l.Key
              ORDER BY 2";

        private static bool HasOwnListColumn(string? dbPath)
        {
            try
            {
                using var c = TryOpen(dbPath);
                if (c == null) return false;

                using var cmd = c.CreateCommand();
                cmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('LeveledList') WHERE name = 'Original'";
                return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static IReadOnlyList<OwnerSummary> ReadOwners(string? dbPath, string sql, string what)
        {
            var result = new List<OwnerSummary>();
            try
            {
                using var c = TryOpen(dbPath);
                if (c == null) return result;

                using var cmd = c.CreateCommand();
                cmd.CommandText = sql;
                using var r = cmd.ExecuteReader();

                bool hasOwnFlag = r.FieldCount > 3;

                while (r.Read())
                    result.Add(new OwnerSummary(
                        r.GetString(0), r.GetString(1), r.GetInt32(2),
                        IsOwn: hasOwnFlag && r.GetInt32(3) == 1));
            }
            catch (Exception ex)
            {
                AppLogger.LogError($"PlacementLookup.{what}", ex);
            }
            return result;
        }

        // What is inside one container, folded per object - see OwnedEntry for why folded.
        public static IReadOnlyList<OwnedEntry> ReadContainerContents(string containerKey, string? dbPath = null)
            => ReadContents(RemovalScope.Container, containerKey, dbPath,
                "FROM ContainerEntry e", "e.ContainerKey", hasLevel: false);

        // The same for a leveled list.
        public static IReadOnlyList<OwnedEntry> ReadListContents(string listKey, string? dbPath = null)
            => ReadContents(RemovalScope.LeveledList, listKey, dbPath,
                "FROM LeveledListEntry e", "e.ListKey", hasLevel: true);

        private static IReadOnlyList<OwnedEntry> ReadContents(
            RemovalScope scope, string ownerKey, string? dbPath,
            string fromClause, string ownerColumn, bool hasLevel)
        {
            var result = new List<OwnedEntry>();
            if (string.IsNullOrWhiteSpace(ownerKey)) return result;

            try
            {
                using var c = TryOpen(dbPath);
                if (c == null) return result;

                bool hasWorldItems = HasTable(c, "WorldItem");

                // The decisions are read once and matched in memory: one small set per owner, and a
                // join against another table would have to cope with it not existing on an old
                // database. RemovalStore already answers that question safely.
                var removed = RemovalStore.RemovedKeys(scope, ownerKey, dbPath);

                using var cmd = c.CreateCommand();
                // ONE ROW PER ENTRY, folded here rather than by SQL. It used to be a GROUP BY with
                // COUNT/SUM, which answered "how many and how much" and threw away the only thing a
                // leveled list's rows differ in: the LEVEL. A list that hands out a sword at level 4
                // and again at level 30 is not "a sword x2", and that is what the window next door
                // has always shown. The aggregate is still carried - see OwnedEntry.
                //
                // Name resolution is the same ladder as everywhere else - nested list, armor,
                // weapon, then the world items (misc, ingredients, food, ammo, soul gems, books,
                // scrolls, keys). The candidate columns stay at 3..8 so the positional FirstNonEmpty
                // call below keeps working; Count and Level took 1 and 2, where the two aggregates
                // used to be. Column 8 is an empty slot: it was formid.db's Materials, a copy of
                // WorldItem's rows that went on 2026-11-07 (and the ATTACH with it).
                cmd.CommandText = $@"
                    SELECT e.Reference, e.Count, {(hasLevel ? "e.Level" : "0")},
                           nested.EditorID,
                           a.Name, a.EditorID,
                           w.Name, w.EditorID,
                           NULL
                           {(hasWorldItems ? ", wi.Name, wi.EditorID" : ", NULL, NULL")}
                    {fromClause}
                    LEFT JOIN LeveledList nested ON nested.Key = e.Reference
                    LEFT JOIN Armor a           ON a.Key      = e.Reference
                    LEFT JOIN Weapons w         ON w.Key      = e.Reference
                    {(hasWorldItems ? "LEFT JOIN WorldItem wi ON wi.Key = e.Reference" : "")}
                    WHERE {ownerColumn} = @k
                    ORDER BY e.Ordinal";
                cmd.Parameters.AddWithValue("@k", ownerKey);

                // Insertion-ordered, so the rows of one object stay in the order the record lists
                // them - a list read top to bottom is how anyone compares it against xEdit.
                var byReference = new Dictionary<string, (string Name, bool IsList, List<OwnedOccurrence> Rows)>(
                    StringComparer.OrdinalIgnoreCase);
                var order = new List<string>();

                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    var reference = r.GetString(0);
                    int count = r.IsDBNull(1) ? 1 : r.GetInt32(1);
                    int level = r.IsDBNull(2) ? 0 : r.GetInt32(2);

                    if (!byReference.TryGetValue(reference, out var folded))
                    {
                        // 9 and 10 are the world item's name and EditorID; 8 is the empty slot.
                        folded = (FirstNonEmpty(r, reference, 3, 4, 5, 6, 7, 9, 10), !r.IsDBNull(3), new List<OwnedOccurrence>());
                        byReference[reference] = folded;
                        order.Add(reference);
                    }

                    folded.Rows.Add(new OwnedOccurrence(level, count));
                }

                foreach (var reference in order)
                {
                    var folded = byReference[reference];

                    result.Add(new OwnedEntry(
                        reference,
                        folded.Name,
                        folded.Rows.Count,
                        folded.Rows.Sum(o => o.Count),
                        IsList: folded.IsList,
                        IsRemoved: removed.Contains(reference),
                        Breakdown: folded.Rows));
                }

                // Name, not "most occurrences first": someone looking for a duplicate wants the two
                // copies next to each other, and someone looking for a known item wants it findable.
                result.Sort((x, y) => string.Compare(x.Name, y.Name, StringComparison.CurrentCultureIgnoreCase));
            }
            catch (Exception ex)
            {
                AppLogger.LogError($"PlacementLookup.ReadContents({scope}, {ownerKey})", ex);
            }

            return result;
        }

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
                // MIN(DroppedBy) with COUNT(DISTINCT DroppedBy) rather than a second query: where
                // one plugin dropped everything - the common case, since a list is usually pruned in
                // one go - the count is 1 and MIN is that plugin. Where several did, the count says
                // so and the name is not shown at all, because naming one of three would be wrong.
                cmd.CommandText = @"
                    SELECT l.ListKey,
                           COALESCE(NULLIF(ll.EditorID, ''), l.ListKey) AS listName,
                           COUNT(*) AS lostCount,
                           MIN(NULLIF(l.DroppedBy, '')) AS droppedBy,
                           COUNT(DISTINCT NULLIF(l.DroppedBy, '')) AS droppedByCount
                    FROM LeveledListLostEntry l
                    LEFT JOIN LeveledList ll ON ll.Key = l.ListKey
                    GROUP BY l.ListKey
                    ORDER BY lostCount DESC, listName";

                using var r = cmd.ExecuteReader();
                while (r.Read())
                    result.Add(new LostListSummary(
                        r.GetString(0), r.GetString(1), r.GetInt32(2),
                        r.IsDBNull(3) ? "" : r.GetString(3),
                        r.IsDBNull(4) ? 0 : r.GetInt32(4)));
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

                // Misc, ingredients, food, ammo, soul gems (and books, scrolls, keys) are WorldItem
                // rows. 4,413 of 28,475 entries point at one of those, and without this join they
                // were shown as a raw key. Until 2026-11-07 formid.db was attached for its Materials
                // copy of the same rows.
                bool hasWorldItems = HasTable(c, "WorldItem");

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
                               w.Name, w.EditorID,
                               NULL
                               {(hasWorldItems ? ", wi.Name, wi.EditorID" : ", NULL, NULL")}
                        FROM LeveledListEntry e
                        LEFT JOIN LeveledList nested ON nested.Key = e.Reference
                        LEFT JOIN Armor a           ON a.Key      = e.Reference
                        LEFT JOIN Weapons w         ON w.Key      = e.Reference
                        {(hasWorldItems ? "LEFT JOIN WorldItem wi ON wi.Key = e.Reference" : "")}
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
                            // 10 and 11 are the world item; 9 is the empty slot.
                            FirstNonEmpty(r, reference, 4, 5, 6, 7, 8, 10, 11),
                            r.GetInt32(2), r.GetInt32(3), isList));
                    }
                }

                // What the overrides dropped. Same joins as the entries above, so a lost item reads
                // with the same name it would have had if it were still there. Ordered by name
                // rather than by any stored order: there is no order left to preserve - these rows
                // come from versions of the list that no longer exist.
                var lost = ReadLostRows(c, new[] { listKey })
                    .Select(row => row.Info)
                    .ToList();

                lost.Sort((x, y) => string.Compare(x.Name, y.Name, StringComparison.CurrentCultureIgnoreCase));

                return new LeveledListInfo(listKey, editorId, chanceNone, flags, globalKey, entries, lost);
            }
            catch (Exception ex)
            {
                AppLogger.LogError($"PlacementLookup.Read({listKey})", ex);
                return null;
            }
        }

        // What several lists have lost, in one query.
        //
        // FOR THE HISTORY OF A CONTAINER. A chest has no override history of its own - the scan
        // keeps only the winning version of a CONT record (see ItemDBHandler.Scan's
        // latestContainerByKey, which has no version chain, and the LeveledListLostEntry schema
        // comment for what the lists get instead). What a chest DOES have is the lists hanging in
        // it, and what those lost is a real part of what the chest used to hand out. So the History
        // there is the losses of the lists it holds, one hop down - never deeper, for the same
        // reason the contents never expand nested lists: a nesting depth of up to 9 turns one chest
        // into thousands of rows, and the interesting answer is almost always the next hop.
        //
        // Not Read() in a loop: that pulls every list's full contents to get at the lost rows, and a
        // chest can hold dozens of lists.
        public static IReadOnlyList<LostEntryGroup> ReadLostEntries(
            IReadOnlyCollection<string> listKeys, string? dbPath = null)
        {
            var result = new List<LostEntryGroup>();
            if (listKeys == null || listKeys.Count == 0) return result;

            try
            {
                using var c = TryOpen(dbPath);
                if (c == null) return result;

                var rows = ReadLostRows(c, listKeys);

                foreach (var group in rows.GroupBy(r => r.ListKey, StringComparer.OrdinalIgnoreCase))
                {
                    var entries = group
                        .Select(r => r.Info)
                        .OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase)
                        .ToList();

                    result.Add(new LostEntryGroup(
                        group.Key,
                        group.Select(r => r.ListName).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n)) ?? group.Key,
                        entries));
                }

                // Most losses first, like the lost-lists overview: the list that dropped fifteen
                // things is the one worth reading about.
                result.Sort((x, y) => y.Entries.Count.CompareTo(x.Entries.Count));
            }
            catch (Exception ex)
            {
                AppLogger.LogError("PlacementLookup.ReadLostEntries", ex);
            }

            return result;
        }

        // The one place the lost-entry SELECT lives, for one list or for fifty. Read() had it inline
        // until the container History needed the same rows for several lists at once, and a second
        // copy of a query whose name-candidate columns are POSITIONAL (see below) would have been
        // the kind of duplicate that breaks quietly the next time a column is added.
        private static List<(string ListKey, string ListName, LeveledListLostEntryInfo Info)> ReadLostRows(
            SqliteConnection c, IReadOnlyCollection<string> listKeys)
        {
            var rows = new List<(string, string, LeveledListLostEntryInfo)>();
            if (!HasTable(c, "LeveledListLostEntry")) return rows;

            // Books, scrolls, misc, keys and the rest live here. Guarded like every other optional
            // table: an item.db from before this one existed must still answer, and a join against a
            // missing table takes the whole read down - quietly, since the caller logs and carries on.
            bool hasWorldItems = HasTable(c, "WorldItem");

            var names = listKeys.Select((_, i) => $"@k{i}").ToList();

            using var cmd = c.CreateCommand();
            // AddedBy/DroppedBy go LAST so the name-candidate indices 5..10 below stay put - they
            // are positional and were getting re-numbered every time this grew. The owning list's
            // own name goes after those, for the same reason.
            cmd.CommandText = $@"
                SELECT l.Reference, l.Level, l.Count, l.LostFrom, l.Ambiguous,
                       nested.EditorID,
                       a.Name, a.EditorID,
                       w.Name, w.EditorID,
                       NULL,
                       l.AddedBy, l.DroppedBy,
                       l.ListKey, owner.EditorID
                       {(hasWorldItems ? ", wi.Name, wi.EditorID" : ", NULL, NULL")}
                FROM LeveledListLostEntry l
                LEFT JOIN LeveledList nested ON nested.Key = l.Reference
                LEFT JOIN LeveledList owner  ON owner.Key  = l.ListKey
                LEFT JOIN Armor a            ON a.Key      = l.Reference
                LEFT JOIN Weapons w          ON w.Key      = l.Reference
                {(hasWorldItems ? "LEFT JOIN WorldItem wi ON wi.Key = l.Reference" : "")}
                WHERE l.ListKey IN ({string.Join(",", names)})";

            int index = 0;
            foreach (var key in listKeys)
                cmd.Parameters.AddWithValue(names[index++], key);

            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var reference = r.GetString(0);

                // Columns 5..9 plus the world item at 15/16 are the name candidates, in the order
                // FirstNonEmpty should try them; 5 is the nested list's EditorID and therefore
                // doubles as "this entry is a list". 10 is the empty slot (formid.db's Materials).
                var info = new LeveledListLostEntryInfo(
                    reference,
                    FirstNonEmpty(r, reference, 5, 6, 7, 8, 9, 15, 16),
                    r.GetInt32(1), r.GetInt32(2),
                    r.IsDBNull(3) ? "" : r.GetString(3),
                    IsList: !r.IsDBNull(5),
                    Ambiguous: !r.IsDBNull(4) && r.GetInt32(4) == 1,
                    // Empty on a database scanned before these existed. The display treats that as
                    // "not known" rather than inventing a plugin name.
                    AddedBy: r.IsDBNull(11) ? "" : r.GetString(11),
                    DroppedBy: r.IsDBNull(12) ? "" : r.GetString(12));

                rows.Add((
                    r.GetString(13),
                    r.IsDBNull(14) ? "" : r.GetString(14),
                    info));
            }

            return rows;
        }
    }
}
