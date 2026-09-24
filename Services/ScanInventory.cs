using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;
using SkyrimCraftingTool.Model;

namespace SkyrimCraftingTool.Services
{
    // What a scan actually found, counted per record type.
    //
    // The rescan report used to say one thing - how many item keys appeared and disappeared - which
    // made it look like nothing had happened whenever a plugin changed enchantments, recipes or
    // containers but no armor or weapon keys. Everything below is already scanned and stored; it was
    // simply never counted.
    //
    // These are ACTIVE RECORDS ACROSS THE WHOLE LOAD ORDER, not rows per plugin. An override does
    // not add a line: the key is built from the record's master FormKey, so a mod overriding
    // Skyrim.esm's steel sword writes onto that same key, and the plugins are written in load order
    // so the winner is what remains. A mod that only overrides therefore moves no number here - by
    // design, because listing overrides separately would inflate the list into something other than
    // "what the load order actually contains".
    //
    // The two groups are not cosmetic, they behave differently and the report says so:
    //   * item.db keeps rows that vanished from the load order, flagged Active = 0, so your edits to
    //     them survive. Counting therefore has to ask for Active = 1, or the numbers only ever grow.
    //   * formid.db is dropped and rebuilt on every scan (see FormIDDBHandler.CreateTables), so its
    //     numbers are simply "what the current load order resolves to" - there is no history in it.
    // Changed answers the question the counts cannot: a plugin update that rebalances armor ratings
    // adds and removes nothing, so every number above it stays put and the report reads as though
    // the scan did nothing. It counts records that were already there and whose SCANNED values are
    // different afterwards - not edits of yours, which live in separate columns and are untouched by
    // a scan. -1 where it could not be worked out (no snapshot, missing table), and rendered as
    // nothing rather than as "0 changed", for the same reason Count uses n/a.
    public sealed record ScanCategory(string Label, int Count, int Changed = -1)
    {
        // Counting failed for this one (missing table, older DB). Reported as "n/a" rather than as a
        // zero, because a zero here reads as "this scan found nothing" and would send people hunting
        // for a scan bug that isn't there.
        public bool IsKnown => Count >= 0;

        public bool HasChanged => Changed > 0;
    }

    // Every scanned record's values as they were before a scan, so the one after it can say which
    // ones actually moved. Held in memory rather than in a side table: it lives for the length of
    // one scan, and a table would have to survive the rebuild that scan performs.
    public sealed class ScanValueSnapshot
    {
        internal Dictionary<string, Dictionary<string, int>> ByLabel { get; } =
            new(StringComparer.Ordinal);
    }

    public sealed class ScanInventory
    {
        public IReadOnlyList<ScanCategory> Records { get; init; } = Array.Empty<ScanCategory>();
        public IReadOnlyList<ScanCategory> References { get; init; } = Array.Empty<ScanCategory>();

        public int RecordTotal => Records.Where(c => c.IsKnown).Sum(c => c.Count);
        public int ReferenceTotal => References.Where(c => c.IsKnown).Sum(c => c.Count);
        public int Total => RecordTotal + ReferenceTotal;

        public bool IsEmpty => Records.Count == 0 && References.Count == 0;

        public int? CountOf(string label)
        {
            var hit = Records.Concat(References).FirstOrDefault(c => c.Label == label);
            return hit is { IsKnown: true } ? hit.Count : null;
        }
    }

    public static class ScanInventoryReader
    {
        // Active = 1 matters here; see the class comment above. KeyColumn is what makes a row one
        // record - see Count() for why it is not simply COUNT(*).
        private static readonly (string Table, string Label, string KeyColumn, bool HasActive)[] ItemTables =
        {
            ("Armor",         "Armor",         "Key",          true),
            ("Weapons",       "Weapons",       "Key",          true),
            ("COBJ",          "Recipes",       "Key",          true),
            ("Enchantments",  "Enchantments",  "Key",          true),
            ("MagicEffects",  "Magic effects", "Key",          true),
            ("Container",     "Containers",    "ContainerKey", true),
        };

        private static readonly (string Table, string Label, string KeyColumn, bool HasActive)[] FormIdTables =
        {
            ("Keywords",  "Keywords",      "Key", false),
            ("Materials", "Materials",     "Key", false),
            ("Perks",     "Perks",         "Key", false),
            ("Quests",    "Quests",        "Key", false),
            ("LVLi",      "Leveled lists", "Key", false),
            ("FormLists", "Form lists",    "Key", false),
        };

        // What counts as a value for each record type: exactly the columns the scan itself writes,
        // taken from the scan's own lists rather than restated here. Anything else in those tables
        // is either the user's (the IsEdited* shadows, ContainerString) or bookkeeping (Active,
        // LastChanged) - a scan must never move those, and counting them as changes would report
        // the user's own work back at them as if a mod had done it.
        private static readonly Dictionary<string, string[]> ValueColumns = new(StringComparer.Ordinal)
        {
            ["Armor"] = ItemDBHandler.ArmorColumnNames,
            ["Weapons"] = ItemDBHandler.WeaponColumnNames,
            ["Recipes"] = ItemDBHandler.CobjColumnNames,
            ["Enchantments"] = ItemDBHandler.EnchantmentColumnNames,
            ["Magic effects"] = ItemDBHandler.MagicEffectColumnNames,
            ["Containers"] = ItemDBHandler.ContainerColumnNames,
        };

        public static ScanInventory Read(ScanValueSnapshot? before = null)
        {
            var input = GlobalState.Tool?.InputFolder;
            if (string.IsNullOrWhiteSpace(input))
                return new ScanInventory();

            return new ScanInventory
            {
                Records = CountAll(Path.Combine(input, "Item", "item.db"), ItemTables, before),
                // formid.db is dropped and rebuilt on every scan, so every row in it is new by
                // construction and "changed" has nothing to mean there.
                References = CountAll(Path.Combine(input, "FormID", "formid.db"), FormIdTables),
            };
        }

        // Taken before the scan runs. Only the item tables: see the note on References above.
        public static ScanValueSnapshot SnapshotValues()
        {
            var snapshot = new ScanValueSnapshot();

            var input = GlobalState.Tool?.InputFolder;
            if (string.IsNullOrWhiteSpace(input)) return snapshot;

            var dbPath = Path.Combine(input, "Item", "item.db");
            if (!File.Exists(dbPath)) return snapshot;

            try
            {
                using var connection = new SqliteConnection($"Data Source={dbPath}");
                connection.Open();

                foreach (var table in ItemTables)
                {
                    var signatures = ReadSignatures(connection, table.Table, table.Label, table.KeyColumn, table.HasActive);
                    if (signatures != null) snapshot.ByLabel[table.Label] = signatures;
                }
            }
            catch (Exception ex)
            {
                // Never allowed to hold up a scan - the report degrades to counts only.
                AppLogger.LogError("Scan value snapshot failed", ex);
            }

            return snapshot;
        }

        // Key -> hash of that record's scanned values. A hash rather than the values themselves: the
        // point is only "is this different", and 17,000 records' worth of concatenated strings is
        // memory spent to answer a yes/no.
        private static Dictionary<string, int>? ReadSignatures(
            SqliteConnection connection, string table, string label, string keyColumn, bool hasActive)
        {
            if (!ValueColumns.TryGetValue(label, out var columns)) return null;

            try
            {
                var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

                using var cmd = connection.CreateCommand();
                cmd.CommandText =
                    $"SELECT {keyColumn}, {string.Join(", ", columns)} FROM {table}" +
                    (hasActive ? " WHERE Active = 1" : "");

                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    if (r.IsDBNull(0)) continue;

                    var hash = new HashCode();
                    for (int i = 1; i <= columns.Length; i++)
                        hash.Add(r.IsDBNull(i) ? "" : Convert.ToString(r.GetValue(i), CultureInfo.InvariantCulture) ?? "");

                    result[r.GetString(0)] = hash.ToHashCode();
                }

                return result;
            }
            catch (Exception ex)
            {
                AppLogger.LogError($"Scan value snapshot: {table}", ex);
                return null;
            }
        }

        // Records present both before and after whose values differ. Records that appeared or
        // vanished are NOT counted here - those already move the count beside it, and counting them
        // twice would make a new mod look like a mod that rewrote everything.
        private static int CountChanged(
            SqliteConnection connection, string table, string label, string keyColumn, bool hasActive,
            ScanValueSnapshot? before)
        {
            if (before == null || !before.ByLabel.TryGetValue(label, out var old)) return -1;

            var now = ReadSignatures(connection, table, label, keyColumn, hasActive);
            if (now == null) return -1;

            int changed = 0;
            foreach (var pair in now)
                if (old.TryGetValue(pair.Key, out var was) && was != pair.Value)
                    changed++;

            return changed;
        }

        private static IReadOnlyList<ScanCategory> CountAll(
            string dbPath, (string Table, string Label, string KeyColumn, bool HasActive)[] tables,
            ScanValueSnapshot? before = null)
        {
            if (!File.Exists(dbPath))
                return Array.Empty<ScanCategory>();

            try
            {
                using var connection = new SqliteConnection($"Data Source={dbPath}");
                connection.Open();

                // One table failing must not cost the whole inventory - a report is a nice-to-have,
                // and an older DB missing one table should still show every other number.
                return tables.Select(t => new ScanCategory(
                                 t.Label,
                                 Count(connection, t.Table, t.KeyColumn, t.HasActive),
                                 CountChanged(connection, t.Table, t.Label, t.KeyColumn, t.HasActive, before)))
                             .ToList();
            }
            catch (Exception ex)
            {
                AppLogger.LogError($"Scan inventory: {Path.GetFileName(dbPath)} could not be read", ex);
                return Array.Empty<ScanCategory>();
            }
        }

        // DISTINCT LOWER(key), not COUNT(*). One record can occupy two rows, because the same plugin
        // name reaches KeyFactory in two spellings and the key carries it: "ccBGSSSE001-Fish.esm|000E4C"
        // and "ccbgssse001-fish.esm|000E4C" are one armor stored twice (see the key-case issue -
        // Creation Club plugins are where it shows). COUNT(*) would report that armor twice, and a
        // report whose job is to show what the load order actually contains must not inflate itself
        // with its own bookkeeping artefacts.
        //
        // This makes the NUMBER right; it does not remove the duplicate rows. Those are a separate,
        // still-open problem, and the tree shows both entries.
        private static int Count(SqliteConnection connection, string table, string keyColumn, bool hasActive)
        {
            try
            {
                using var cmd = connection.CreateCommand();
                // Table and column names are from the fixed lists above, never from input.
                cmd.CommandText = hasActive
                    ? $"SELECT COUNT(DISTINCT LOWER({keyColumn})) FROM {table} WHERE Active = 1"
                    : $"SELECT COUNT(DISTINCT LOWER({keyColumn})) FROM {table}";

                return Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
            }
            catch (Exception ex)
            {
                AppLogger.LogWarning($"Scan inventory: counting {table} failed ({ex.Message})");
                return -1;
            }
        }
    }
}
