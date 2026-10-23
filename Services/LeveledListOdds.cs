using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;
using SkyrimCraftingTool.Model;

namespace SkyrimCraftingTool.Services
{
    // How likely is this item out of that leveled list.
    //
    // The number nobody has today. A placement in a list five levels deep looks the same in the UI
    // as one in the list a merchant actually rolls, and the difference is two orders of magnitude -
    // measured on the real load order, an item in LItemBlacksmithArmor75 comes out at 4.3% per roll,
    // the same item one level deeper at a fraction of that.
    //
    // WHY THIS IS A SNAPSHOT AND NOT A SET OF QUERIES. The window recalculates on every keystroke of
    // the chance box and every tick of the level slider. Reading the database per recalculation
    // would mean dozens of queries per second for an answer that cannot change while the window is
    // open - so the whole reachable subtree is read once into OddsModel, and the math never touches
    // SQLite again.
    //
    // WHAT IT DELIBERATELY DOES NOT MODEL: how often the game rolls a list at all (merchant restock
    // is ~48h of game time and depends on the cell, quests and mods), and COED per-entry conditions
    // and owners. Both would turn a number that is exactly right per roll into one that is vaguely
    // wrong per playthrough.

    // One entry as the math sees it. IsList is resolved against the loaded model, not against the
    // database: a reference to a list whose plugin is gone is not a list any more, it is just some
    // other entry occupying a slot - which is exactly how it behaves for the odds.
    public sealed record OddsEntry(string Reference, int Level, int Count, bool IsList);

    // GlobalChance is what a chance-none GLOBAL currently holds, when the list has one.
    //
    // It is not a detail: a list with a global has NO fixed chance - the game reads the global and
    // ignores the stored number, which is parked at 100 in 209 of the 223 cases. Computing with that
    // 100 would report "this list never gives anything" for every gated DLC list in the load order.
    public sealed record OddsList(
        string Key, string EditorId, int ChanceNone, string Flags, IReadOnlyList<OddsEntry> Entries,
        double? GlobalChance = null);

    // The user's pending edits, applied on top of the scanned values without writing anything. This
    // is what makes the preview live: the window hands over what the boxes currently say, and the
    // same math runs against "before" and "after".
    public sealed record OddsOverride(int? ChanceNone, CalcFlagMode Flags = CalcFlagMode.Unchanged);

    // A placement the patch WOULD add. The whole point of the calculator is answering "if I patch it
    // like this, how often will I see it" - and at that moment the item is not in the list yet, so
    // there is nothing to measure.
    //
    // Passed as a COLLECTION, and that is not a convenience: every item the user has lined up for
    // this list belongs in the calculation, not only the one whose window is open. An item joining a
    // list of twelve is one in thirteen; with nine more waiting in other items' settings it is one
    // in twenty-two. Counting only the open item would flatter every number this window shows.
    public sealed record PendingPlacement(string ListKey, string ItemKey, int Level, int Count);

    // Probability that at least one copy appears, and how many copies to expect. Both, because they
    // answer different questions and a count above 1 makes them diverge: "do I see it at all" is the
    // shopping question, "how many" is the balance question.
    public sealed record OddsResult(double Chance, double ExpectedCount)
    {
        public static readonly OddsResult Nothing = new(0, 0);
    }

    public sealed class OddsModel
    {
        private readonly Dictionary<string, OddsList> _lists;

        public OddsModel(IEnumerable<OddsList> lists)
            => _lists = lists.ToDictionary(l => l.Key, StringComparer.OrdinalIgnoreCase);

        public int Count => _lists.Count;

        public OddsList? Get(string? key)
            => key != null && _lists.TryGetValue(key, out var l) ? l : null;

        public bool Has(string? key) => key != null && _lists.ContainsKey(key);

        private static string ItemDbPath =>
            Path.Combine(GlobalState.Tool?.InputFolder ?? "", "Item", "item.db");

        // Everything reachable downward from one list, in two queries.
        //
        // Downward only: the odds of a roll of THIS list depend on what is inside it, never on who
        // calls it. Who calls it is a separate question (which containers reach this list) and a
        // separate query - answering both here would load a large part of the 4,312 lists for a
        // window that asks about one.
        public static OddsModel Load(string rootListKey, string? dbPath = null)
        {
            var lists = new List<OddsList>();
            if (string.IsNullOrWhiteSpace(rootListKey)) return new OddsModel(lists);

            var path = string.IsNullOrWhiteSpace(dbPath) ? ItemDbPath : dbPath;
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return new OddsModel(lists);

            // UNION, not UNION ALL: a leveled list may legitimately be reached twice, and lists can
            // even form a cycle. UNION is what stops the recursion in both cases.
            const string Reachable = @"
                WITH RECURSIVE reachable(Key) AS (
                    SELECT Key FROM LeveledList WHERE Key = @root AND Active = 1
                    UNION
                    SELECT nested.Key
                    FROM LeveledListEntry e
                    JOIN reachable ON reachable.Key = e.ListKey
                    JOIN LeveledList nested ON nested.Key = e.Reference AND nested.Active = 1
                )";

            try
            {
                using var c = new SqliteConnection($"Data Source={path};Mode=ReadOnly");
                c.Open();

                var meta = new Dictionary<string, (string EditorId, int ChanceNone, string Flags, double? GlobalChance)>(
                    StringComparer.OrdinalIgnoreCase);

                // Globals is younger than this query. A database from before it - every installation
                // that has not rescanned since - has no such table, and joining it would throw,
                // empty the model and silently report 0% for everything. Checked rather than
                // assumed, and the join is simply left out when it is not there.
                bool hasGlobals = TableExists(c, "Globals");

                using (var cmd = c.CreateCommand())
                {
                    // The LEFT JOIN is what makes a gated list computable: its stored chance is a
                    // parked 100 and the real one lives in the global.
                    cmd.CommandText = Reachable + (hasGlobals
                        ? @"
                        SELECT l.Key, l.EditorID, l.ChanceNone, COALESCE(l.Flags, ''), g.Value
                        FROM LeveledList l
                        JOIN reachable r ON r.Key = l.Key
                        LEFT JOIN Globals g ON g.Key = l.GlobalKey AND g.Active = 1"
                        : @"
                        SELECT l.Key, l.EditorID, l.ChanceNone, COALESCE(l.Flags, ''), NULL
                        FROM LeveledList l
                        JOIN reachable r ON r.Key = l.Key");
                    cmd.Parameters.AddWithValue("@root", rootListKey);
                    using var r = cmd.ExecuteReader();
                    while (r.Read())
                        meta[r.GetString(0)] = (r.GetString(1), r.GetInt32(2), r.GetString(3),
                                                r.IsDBNull(4) ? null : r.GetDouble(4));
                }

                var entries = meta.Keys.ToDictionary(k => k, _ => new List<OddsEntry>(),
                    StringComparer.OrdinalIgnoreCase);

                using (var cmd = c.CreateCommand())
                {
                    cmd.CommandText = Reachable + @"
                        SELECT e.ListKey, e.Reference, e.Level, e.Count
                        FROM LeveledListEntry e JOIN reachable r ON r.Key = e.ListKey
                        ORDER BY e.ListKey, e.Ordinal";
                    cmd.Parameters.AddWithValue("@root", rootListKey);
                    using var r = cmd.ExecuteReader();
                    while (r.Read())
                    {
                        var owner = r.GetString(0);
                        if (!entries.TryGetValue(owner, out var bucket)) continue;

                        var reference = r.GetString(1);
                        bucket.Add(new OddsEntry(reference, r.GetInt32(2), r.GetInt32(3),
                            meta.ContainsKey(reference)));
                    }
                }

                foreach (var (key, (editorId, chanceNone, flags, globalChance)) in meta)
                    lists.Add(new OddsList(key, editorId, chanceNone, flags, entries[key], globalChance));
            }
            catch (Exception ex)
            {
                // A calculator that cannot read must show nothing, never take the window with it.
                AppLogger.LogError($"OddsModel.Load({rootListKey})", ex);
            }

            return new OddsModel(lists);
        }

        private static bool TableExists(SqliteConnection c, string table)
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=@t";
            cmd.Parameters.AddWithValue("@t", table);
            return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L) > 0;
        }
    }

    public static class LeveledListOdds
    {
        // One request of `count` items from a list, at a player level, with the user's pending edits
        // applied.
        //
        // "Request of count items" rather than "one roll" is the honest unit, because the caller
        // decides how many it asks for and the CalculateForEachItemInCount flag turns that number
        // into repeated rolls - which is the difference between "3 copies of one draw" and "three
        // independent chances".
        public static OddsResult Chance(
            OddsModel model,
            string listKey,
            string itemKey,
            int playerLevel,
            int requestedCount = 1,
            IReadOnlyDictionary<string, OddsOverride>? overrides = null,
            IReadOnlyCollection<PendingPlacement>? pending = null)
        {
            if (model == null || string.IsNullOrWhiteSpace(listKey) || string.IsNullOrWhiteSpace(itemKey))
                return OddsResult.Nothing;

            return new Run(model, itemKey, playerLevel, overrides, pending)
                .Request(listKey, Math.Max(requestedCount, 1));
        }

        // One calculation. Everything except the list and the requested count is fixed for its whole
        // duration, which is what makes the memo below correct - and necessary: the widest tree in
        // the real load order reaches 1,202 lists, and without memoisation one pass through it costs
        // 23 ms. The window recalculates while a slider is being dragged, so that is the difference
        // between a live number and a stuttering one.
        private sealed class Run
        {
            private readonly OddsModel _model;
            private readonly string _itemKey;
            private readonly int _playerLevel;
            private readonly IReadOnlyDictionary<string, OddsOverride>? _overrides;
            private readonly IReadOnlyCollection<PendingPlacement>? _pending;

            private readonly Dictionary<(string Key, int Count), OddsResult> _memo = new();
            private readonly HashSet<string> _path = new(StringComparer.OrdinalIgnoreCase);

            public Run(OddsModel model, string itemKey, int playerLevel,
                       IReadOnlyDictionary<string, OddsOverride>? overrides, IReadOnlyCollection<PendingPlacement>? pending)
            {
                _model = model;
                _itemKey = itemKey;
                _playerLevel = playerLevel;
                _overrides = overrides;
                _pending = pending;
            }

            // Set while a result is being computed whose subtree hit the cycle guard. Such a result
            // is only valid for the path it was reached on, so it must not be remembered - the same
            // list reached from somewhere else would inherit a zero it never had.
            private bool _cycleBelow;

            public OddsResult Request(string listKey, int requestedCount)
            {
                var list = _model.Get(listKey);
                if (list == null) return OddsResult.Nothing;

                var memoKey = (listKey.ToLowerInvariant(), requestedCount);
                if (_memo.TryGetValue(memoKey, out var cached)) return cached;

                // A list that is its own ancestor would recurse forever. Skyrim tolerates such a
                // record existing; the odds through the second visit are treated as zero rather than
                // guessed at.
                if (!_path.Add(listKey))
                {
                    _cycleBelow = true;
                    return OddsResult.Nothing;
                }

                bool cycleBefore = _cycleBelow;
                _cycleBelow = false;

                OddsResult result;
                try
                {
                    var single = SingleRoll(list);

                    if (requestedCount <= 1)
                    {
                        result = single;
                    }
                    else
                    {
                        var (_, eachItem, _) = FlagsFor(list, _overrides);

                        // With "for each item in count" the list is rolled `count` times
                        // independently, so the chances compound. Without it there is one roll whose
                        // result is multiplied - the same single chance, just more copies of it.
                        result = eachItem
                            ? new OddsResult(1 - Math.Pow(1 - single.Chance, requestedCount),
                                             single.ExpectedCount * requestedCount)
                            : new OddsResult(single.Chance, single.ExpectedCount * requestedCount);
                    }
                }
                finally
                {
                    _path.Remove(listKey);
                }

                if (!_cycleBelow) _memo[memoKey] = result;
                _cycleBelow |= cycleBefore;

                return result;
            }

            private OddsResult SingleRoll(OddsList list) => Roll(_model, list, _itemKey, _playerLevel,
                _overrides, _pending, (key, count) => Request(key, count));
        }

        private static OddsResult Roll(
            OddsModel model, OddsList list, string itemKey, int playerLevel,
            IReadOnlyDictionary<string, OddsOverride>? overrides, IReadOnlyCollection<PendingPlacement>? pending,
            Func<string, int, OddsResult> nested)
        {
            var entries = EntriesOf(list, pending);

            // Entries above the player's level do not exist for this roll.
            var eligible = entries.Where(e => e.Level <= playerLevel).ToList();
            if (eligible.Count == 0) return OddsResult.Nothing;

            var (allLevels, _, useAll) = FlagsFor(list, overrides);

            // Without "from all levels", only the highest level band that the player has reached is
            // in play - everything below it drops out entirely. That single bit is often a bigger
            // lever on an item's odds than chanceNone is.
            if (!allLevels)
            {
                int band = eligible.Max(e => e.Level);
                eligible = eligible.Where(e => e.Level == band).ToList();
            }

            double something = 1.0 - ChanceNoneOf(list, overrides) / 100.0;
            if (something <= 0) return OddsResult.Nothing;

            double missAll = 1.0, expected = 0.0, drawn = 0.0;

            foreach (var entry in eligible)
            {
                var hit = Outcome(entry, itemKey, nested);

                if (useAll)
                {
                    // Every entry is handed out, so the entry chances are independent rather than
                    // shares of one draw.
                    missAll *= 1 - hit.Chance;
                    expected += hit.ExpectedCount;
                }
                else
                {
                    // One entry is drawn, uniformly. Duplicates are not merged anywhere on the way
                    // here for exactly this reason: a list naming the same item twice really does
                    // give it two slots out of N.
                    drawn += hit.Chance / eligible.Count;
                    expected += hit.ExpectedCount / eligible.Count;
                }
            }

            double chance = useAll ? 1 - missAll : drawn;

            return new OddsResult(something * chance, something * expected);
        }

        // What one entry contributes if it is the one that comes up: certainty for the item itself,
        // a nested request for a list, nothing for anything else (an entry that is neither is still
        // a slot in the draw, which is why it is counted and not skipped).
        private static OddsResult Outcome(OddsEntry entry, string itemKey, Func<string, int, OddsResult> nested)
        {
            if (string.Equals(entry.Reference, itemKey, StringComparison.OrdinalIgnoreCase))
                return new OddsResult(1, Math.Max(entry.Count, 1));

            if (!entry.IsList) return OddsResult.Nothing;

            return nested(entry.Reference, Math.Max(entry.Count, 1));
        }

        // The list's entries plus the placement the patch would add.
        //
        // addOnceToLLs adds an item only if the list does not already contain it
        // (docs/Leveled_List_Patcher.txt), so a pending placement into a list that already has the
        // item changes nothing - and the calculator has to agree with the patcher, or it would
        // promise an improvement the patch will not deliver.
        private static IReadOnlyList<OddsEntry> EntriesOf(OddsList list, IReadOnlyCollection<PendingPlacement>? pending)
        {
            if (pending == null || pending.Count == 0) return list.Entries;

            var forThisList = pending
                .Where(p => p != null && p.Level > 0
                            && string.Equals(p.ListKey, list.Key, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (forThisList.Count == 0) return list.Entries;

            var withPending = new List<OddsEntry>(list.Entries);

            foreach (var p in forThisList)
            {
                // addOnce, per item: one already in the list is skipped, the others still count.
                if (withPending.Any(e => string.Equals(e.Reference, p.ItemKey, StringComparison.OrdinalIgnoreCase)))
                    continue;

                withPending.Add(new OddsEntry(p.ItemKey, p.Level, Math.Max(p.Count, 1), IsList: false));
            }

            return withPending;
        }

        // What share of rolls produces nothing, in the order the game decides it:
        //   1. the user's pending edit, if there is one
        //   2. the list's chance-none GLOBAL, if it has one - the stored number is then dead weight
        //      (parked at 100 in 209 of the 223 cases, which would read as "never gives anything")
        //   3. the stored number
        private static int ChanceNoneOf(OddsList list, IReadOnlyDictionary<string, OddsOverride>? overrides)
        {
            if (overrides != null && overrides.TryGetValue(list.Key, out var o) && o.ChanceNone.HasValue)
                return Math.Clamp(o.ChanceNone.Value, 0, 100);

            if (list.GlobalChance.HasValue)
                return (int)Math.Round(Math.Clamp(list.GlobalChance.Value, 0, 100));

            return Math.Clamp(list.ChanceNone, 0, 100);
        }

        private static (bool AllLevels, bool EachItem, bool UseAll) FlagsFor(
            OddsList list, IReadOnlyDictionary<string, OddsOverride>? overrides)
        {
            var mode = overrides != null && overrides.TryGetValue(list.Key, out var o)
                ? o.Flags
                : CalcFlagMode.Unchanged;

            return LeveledListFlags.Resolve(mode, list.Flags);
        }

        // "You will see it about every N rolls" - the form the number is actually useful in. A
        // percentage below one per cent says little; "roughly every 160th restock" says it all.
        public static double RollsFor(double chancePerRoll, double confidence = 0.5)
        {
            if (chancePerRoll <= 0) return double.PositiveInfinity;
            if (chancePerRoll >= 1) return 1;

            return Math.Log(1 - confidence) / Math.Log(1 - chancePerRoll);
        }
    }
}
