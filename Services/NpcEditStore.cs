using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;
using SkyrimCraftingTool.Model;

namespace SkyrimCraftingTool.Services
{
    // Writes and clears the NPC shadow columns (Prio 8 / N-P2, docs/NPC-Plan.md).
    //
    // Deliberately NOT the item save pipeline. That one exists to debounce: an armor rating sits on a
    // slider and a keyword chip fires on every click, so writes have to be coalesced, flushed on
    // close, and cancelled per item. The NPC fields are text boxes committed on focus loss - one
    // edit, one write - so a debouncer would add a whole failure mode (a pending save lost on exit)
    // in exchange for nothing.
    //
    // The contract is the one the rest of the database uses: the base column stays as the scan read
    // it, IsEdited<X> holds the user's value, and IsEdited = 1 says the user's value counts. Reset
    // sets the shadow column back to NULL - never touching the base column, which is the only copy
    // of what the plugin actually says.
    public sealed class NpcEditStore
    {
        private readonly string _dbPath;

        public NpcEditStore(string dbPath) => _dbPath = dbPath;

        // Which columns may be written, spelled exactly as the schema spells them. A whitelist
        // rather than string concatenation of whatever the caller passes: these names go into SQL,
        // and "it is only ever called with constants" is a property of today's callers, not of the
        // method.
        //
        // The list covers every field with a shadow column, not just the ones N-P2 makes editable -
        // the later phases then need no change here.
        private static readonly HashSet<string> Editable = new(StringComparer.Ordinal)
        {
            "Name", "ShortName", "ClassKey", "RaceKey",
            "UsesPcLevelMult", "Level", "CalcMinLevel", "CalcMaxLevel",
            "Health", "Magicka", "Stamina",
            "Flags", "TemplateFlags",
            "VoiceKey", "DefaultOutfitKey", "SleepOutfitKey", "DeathItemKey", "SkinKey",
            "Weight", "Height",
            "Aggression", "Confidence", "Assistance", "Morality", "Mood",
            "Keywords",

            // N-P6: no SkyPatcher operation, so these two reach the game through an ESP override
            // rather than a rule. Editable all the same - the price is named in the report.
            "CombatStyleKey", "CrimeFactionKey",
        };

        public static IReadOnlyCollection<string> EditableColumns => Editable;

        private SqliteConnection Open()
        {
            var connection = new SqliteConnection($"Data Source={_dbPath}");
            connection.Open();
            return connection;
        }

        private bool Ready => !string.IsNullOrEmpty(_dbPath) && File.Exists(_dbPath);

        // One field on one NPC. Passing null clears that field's edit, which is what Reset does -
        // there is no other way to say "back to what the plugin says", because the base column is
        // never overwritten.
        public void SaveField(string npcKey, string column, object value)
        {
            if (!Ready || string.IsNullOrWhiteSpace(npcKey)) return;
            if (!Editable.Contains(column))
                throw new ArgumentException($"'{column}' is not an editable NPC column", nameof(column));

            using var connection = Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = $@"
                UPDATE Npc
                   SET IsEdited{column} = $v,
                       IsEdited = 1,
                       LastChanged = $now
                 WHERE Key = $key;";
            cmd.Parameters.AddWithValue("$v", value ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o"));
            cmd.Parameters.AddWithValue("$key", npcKey);
            cmd.ExecuteNonQuery();

            ClearEditedFlagIfNothingLeft(connection, npcKey);
        }

        // A skill is a child row, so its edit lives there - and SkillsEdited on the parent says so,
        // which is what keeps "is this NPC edited" to one read of one row in a tree of 6.580.
        public void SaveSkill(string npcKey, string skill, int? value)
        {
            if (!Ready || string.IsNullOrWhiteSpace(npcKey) || string.IsNullOrWhiteSpace(skill)) return;

            using var connection = Open();

            using (var cmd = connection.CreateCommand())
            {
                // A skill the scan never wrote a row for still has to be editable - INSERT the row
                // with a NULL base value rather than silently dropping the edit.
                cmd.CommandText = @"
                    INSERT INTO NpcSkills (NpcKey, Skill, Value, Offset, IsEditedValue)
                    VALUES ($key, $skill, NULL, 0, $v)
                    ON CONFLICT(NpcKey, Skill) DO UPDATE SET IsEditedValue = $v;";
                cmd.Parameters.AddWithValue("$key", npcKey);
                cmd.Parameters.AddWithValue("$skill", skill);
                cmd.Parameters.AddWithValue("$v", value.HasValue ? value.Value : (object)DBNull.Value);
                cmd.ExecuteNonQuery();
            }

            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = @"
                    UPDATE Npc
                       SET SkillsEdited = (SELECT COUNT(*) > 0 FROM NpcSkills
                                            WHERE NpcKey = $key AND IsEditedValue IS NOT NULL),
                           IsEdited = 1,
                           LastChanged = $now
                     WHERE Key = $key;";
                cmd.Parameters.AddWithValue("$key", npcKey);
                cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o"));
                cmd.ExecuteNonQuery();
            }

            ClearEditedFlagIfNothingLeft(connection, npcKey);
        }

        // The faction list, replaced wholesale rather than row by row: the editor holds the whole
        // list, and "these are the memberships now" is both simpler and impossible to get half-done.
        //
        // The first edit snapshots what the scan found into NpcFactions_Original - lazily, the same
        // way WornRestrictionKeywords does it, because taking a snapshot of all 6.580 NPCs up front
        // would be 14.832 rows nobody has asked for. Without the snapshot the rule builder could not
        // tell an added faction from one that was always there.
        public void SaveFactions(string npcKey, IEnumerable<NpcFactionRecord> factions)
        {
            if (!Ready || string.IsNullOrWhiteSpace(npcKey)) return;

            var list = (factions ?? Enumerable.Empty<NpcFactionRecord>())
                .Where(f => !string.IsNullOrWhiteSpace(f.FactionKey))
                .GroupBy(f => f.FactionKey, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.Last())
                .ToList();

            using var connection = Open();
            using var transaction = connection.BeginTransaction();

            SnapshotFactionsOnce(connection, transaction, npcKey);

            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = "DELETE FROM NpcFactions WHERE NpcKey = $key;";
                cmd.Parameters.AddWithValue("$key", npcKey);
                cmd.ExecuteNonQuery();
            }

            foreach (var faction in list)
            {
                using var cmd = connection.CreateCommand();
                cmd.Transaction = transaction;
                cmd.CommandText = "INSERT INTO NpcFactions (NpcKey, FactionKey, Rank) VALUES ($key, $f, $r);";
                cmd.Parameters.AddWithValue("$key", npcKey);
                cmd.Parameters.AddWithValue("$f", faction.FactionKey);
                cmd.Parameters.AddWithValue("$r", faction.Rank);
                cmd.ExecuteNonQuery();
            }

            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = @"
                    UPDATE Npc
                       SET FactionsEdited = 1, IsEdited = 1, LastChanged = $now
                     WHERE Key = $key;";
                cmd.Parameters.AddWithValue("$key", npcKey);
                cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o"));
                cmd.ExecuteNonQuery();
            }

            transaction.Commit();
        }

        // Once, and only if the NPC has never been edited: a second snapshot would record the user's
        // own earlier edit as if the plugin had shipped it, and the diff would then quietly lose
        // whatever they did first.
        private static void SnapshotFactionsOnce(SqliteConnection connection, SqliteTransaction transaction, string npcKey)
        {
            using (var check = connection.CreateCommand())
            {
                check.Transaction = transaction;
                check.CommandText = "SELECT COUNT(*) FROM Npc WHERE Key = $key AND FactionsEdited = 1;";
                check.Parameters.AddWithValue("$key", npcKey);
                if (Convert.ToInt64(check.ExecuteScalar()) > 0) return;
            }

            using var cmd = connection.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = @"
                DELETE FROM NpcFactions_Original WHERE NpcKey = $key;
                INSERT INTO NpcFactions_Original (NpcKey, FactionKey, Rank)
                SELECT NpcKey, FactionKey, Rank FROM NpcFactions WHERE NpcKey = $key;";
            cmd.Parameters.AddWithValue("$key", npcKey);
            cmd.ExecuteNonQuery();
        }

        // The three N-P3 lists. Same shape as SaveFactions: snapshot once, then replace wholesale.
        // One method with the table and the row shape passed in, because three copies of this would
        // be three places to forget the snapshot.
        public void SaveSpells(string npcKey, IEnumerable<NpcSpellRecord> spells) =>
            SaveList(npcKey, "NpcSpells", "SpellKey, Kind",
                (spells ?? Enumerable.Empty<NpcSpellRecord>())
                    .Where(s => !string.IsNullOrWhiteSpace(s.SpellKey))
                    .GroupBy(s => s.SpellKey, StringComparer.OrdinalIgnoreCase)
                    .Select(g => new object[] { g.Last().SpellKey, g.Last().Kind ?? "" })
                    .ToList());

        public void SavePerks(string npcKey, IEnumerable<NpcPerkRecord> perks) =>
            SaveList(npcKey, "NpcPerks", "PerkKey, Rank",
                (perks ?? Enumerable.Empty<NpcPerkRecord>())
                    .Where(p => !string.IsNullOrWhiteSpace(p.PerkKey))
                    .GroupBy(p => p.PerkKey, StringComparer.OrdinalIgnoreCase)
                    .Select(g => new object[] { g.Last().PerkKey, g.Last().Rank })
                    .ToList());

        public void SaveItems(string npcKey, IEnumerable<NpcItemRecord> items) =>
            SaveList(npcKey, "NpcItems", "ItemKey, Count",
                (items ?? Enumerable.Empty<NpcItemRecord>())
                    .Where(i => !string.IsNullOrWhiteSpace(i.ItemKey))
                    .GroupBy(i => i.ItemKey, StringComparer.OrdinalIgnoreCase)
                    .Select(g => new object[] { g.Last().ItemKey, g.Last().Count })
                    .ToList());

        // snapshotTable is not a parameter any more - the snapshot covers all three lists at once,
        // see SnapshotAllListsOnce.
        private void SaveList(string npcKey, string table, string columns, List<object[]> rows)
        {
            if (!Ready || string.IsNullOrWhiteSpace(npcKey)) return;

            using var connection = Open();
            using var transaction = connection.BeginTransaction();

            SnapshotAllListsOnce(connection, transaction, npcKey);

            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = $"DELETE FROM {table} WHERE NpcKey = $key;";
                cmd.Parameters.AddWithValue("$key", npcKey);
                cmd.ExecuteNonQuery();
            }

            var names = columns.Split(',').Select(c => c.Trim()).ToList();
            foreach (var row in rows)
            {
                using var cmd = connection.CreateCommand();
                cmd.Transaction = transaction;
                cmd.CommandText =
                    $"INSERT INTO {table} (NpcKey, {columns}) VALUES ($key, " +
                    string.Join(", ", names.Select((_, i) => $"$v{i}")) + ");";
                cmd.Parameters.AddWithValue("$key", npcKey);
                for (int i = 0; i < names.Count; i++)
                    cmd.Parameters.AddWithValue($"$v{i}", row[i] ?? DBNull.Value);
                cmd.ExecuteNonQuery();
            }

            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = @"
                    UPDATE Npc SET ListsEdited = 1, IsEdited = 1, LastChanged = $now WHERE Key = $key;";
                cmd.Parameters.AddWithValue("$key", npcKey);
                cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o"));
                cmd.ExecuteNonQuery();
            }

            transaction.Commit();
        }

        // ALL THREE lists are snapshotted together, on the first edit to any of them.
        //
        // WHY TOGETHER: "has this list been snapshotted yet" cannot be answered by looking at the
        // snapshot table, because an NPC whose list is empty in the plugins snapshots to nothing -
        // and no rows is indistinguishable from never taken. The next edit would then snapshot the
        // ALREADY EDITED list, and whatever was added first would look like the plugin's own.
        //
        // Taking all three at once makes the ListsEdited flag a truthful answer for all of them, at
        // the cost of copying two lists nobody asked about.
        private static void SnapshotAllListsOnce(SqliteConnection connection, SqliteTransaction transaction,
                                                 string npcKey)
        {
            using (var check = connection.CreateCommand())
            {
                check.Transaction = transaction;
                check.CommandText = "SELECT COUNT(*) FROM Npc WHERE Key = $key AND ListsEdited = 1;";
                check.Parameters.AddWithValue("$key", npcKey);
                if (Convert.ToInt64(check.ExecuteScalar()) > 0) return;
            }

            using var cmd = connection.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = @"
                DELETE FROM NpcSpells_Original WHERE NpcKey = $key;
                INSERT INTO NpcSpells_Original (NpcKey, SpellKey, Kind)
                SELECT NpcKey, SpellKey, Kind FROM NpcSpells WHERE NpcKey = $key;

                DELETE FROM NpcPerks_Original WHERE NpcKey = $key;
                INSERT INTO NpcPerks_Original (NpcKey, PerkKey, Rank)
                SELECT NpcKey, PerkKey, Rank FROM NpcPerks WHERE NpcKey = $key;

                DELETE FROM NpcItems_Original WHERE NpcKey = $key;
                INSERT INTO NpcItems_Original (NpcKey, ItemKey, Count)
                SELECT NpcKey, ItemKey, Count FROM NpcItems WHERE NpcKey = $key;";
            cmd.Parameters.AddWithValue("$key", npcKey);
            cmd.ExecuteNonQuery();
        }

        // Everything back to what the plugins say. Clears the shadow columns and the skill edits, not
        // the scanned values - a reset must never need a rescan to recover from.
        public void ResetNpc(string npcKey)
        {
            if (!Ready || string.IsNullOrWhiteSpace(npcKey)) return;

            using var connection = Open();
            using var transaction = connection.BeginTransaction();

            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = $@"
                    UPDATE Npc
                       SET {string.Join(", ", Editable.Select(c => $"IsEdited{c} = NULL"))},
                           SkillsEdited = 0,
                           FactionsEdited = 0,
                           ListsEdited = 0,
                           IsEdited = 0,
                           LastChanged = NULL
                     WHERE Key = $key;";
                cmd.Parameters.AddWithValue("$key", npcKey);
                cmd.ExecuteNonQuery();
            }

            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = "UPDATE NpcSkills SET IsEditedValue = NULL WHERE NpcKey = $key;";
                cmd.Parameters.AddWithValue("$key", npcKey);
                cmd.ExecuteNonQuery();
            }

            // Factions are restored FROM the snapshot rather than just unflagged: unlike a shadow
            // column, the live rows were overwritten, so the snapshot is the only copy of what the
            // plugins said. Then the snapshot goes, so the next edit takes a fresh one.
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = @"
                    DELETE FROM NpcFactions WHERE NpcKey = $key;
                    INSERT INTO NpcFactions (NpcKey, FactionKey, Rank)
                    SELECT NpcKey, FactionKey, Rank FROM NpcFactions_Original WHERE NpcKey = $key;
                    DELETE FROM NpcFactions_Original WHERE NpcKey = $key;

                    DELETE FROM NpcSpells WHERE NpcKey = $key;
                    INSERT INTO NpcSpells (NpcKey, SpellKey, Kind)
                    SELECT NpcKey, SpellKey, Kind FROM NpcSpells_Original WHERE NpcKey = $key;
                    DELETE FROM NpcSpells_Original WHERE NpcKey = $key;

                    DELETE FROM NpcPerks WHERE NpcKey = $key;
                    INSERT INTO NpcPerks (NpcKey, PerkKey, Rank)
                    SELECT NpcKey, PerkKey, Rank FROM NpcPerks_Original WHERE NpcKey = $key;
                    DELETE FROM NpcPerks_Original WHERE NpcKey = $key;

                    DELETE FROM NpcItems WHERE NpcKey = $key;
                    INSERT INTO NpcItems (NpcKey, ItemKey, Count)
                    SELECT NpcKey, ItemKey, Count FROM NpcItems_Original WHERE NpcKey = $key;
                    DELETE FROM NpcItems_Original WHERE NpcKey = $key;";
                cmd.Parameters.AddWithValue("$key", npcKey);
                cmd.ExecuteNonQuery();
            }

            transaction.Commit();
        }

        // Which NPCs carry an edit at all - what the tree's badge and the "only edited" filter read,
        // and what the patch generator will walk.
        public HashSet<string> EditedKeys()
        {
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!Ready) return keys;

            using var connection = Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT Key FROM Npc WHERE IsEdited = 1;";
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) keys.Add(reader.GetString(0));
            return keys;
        }

        // Undoing the last edit by hand has to leave the NPC clean, or the tree keeps a badge for a
        // record that matches its plugin exactly - and the patch generator would emit a rule with no
        // operations in it. Checked after every write rather than only on reset, because "typed 250,
        // then typed the original back" is the ordinary way people undo things.
        private static void ClearEditedFlagIfNothingLeft(SqliteConnection connection, string npcKey)
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = $@"
                UPDATE Npc
                   SET IsEdited = 0, LastChanged = NULL
                 WHERE Key = $key
                   AND SkillsEdited = 0
                   AND FactionsEdited = 0
                   AND ListsEdited = 0
                   AND {string.Join(" AND ", Editable.Select(c => $"IsEdited{c} IS NULL"))};";
            cmd.Parameters.AddWithValue("$key", npcKey);
            cmd.ExecuteNonQuery();
        }
    }
}
