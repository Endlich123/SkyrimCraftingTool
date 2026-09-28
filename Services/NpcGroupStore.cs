using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;
using SkyrimCraftingTool.Model;

namespace SkyrimCraftingTool.Services
{
    // Persistence for NPC groups (G2, docs/NPC-Gruppen-Plan.md section 4).
    //
    // Reads and writes the six NpcGroup* tables and nothing else. What it deliberately does NOT do is
    // store membership: that is computed by NpcGroupResolver on every read, because a saved member
    // list goes stale on the next rescan (section 8). The one member table here is the SNAPSHOT, and
    // it exists to report that drift rather than to define a group.
    //
    // Not the item save pipeline, for the same reason NpcEditStore is not: that one debounces slider
    // and chip edits. A group is saved when the user is done with it - one edit, one write.
    public sealed class NpcGroupStore
    {
        private readonly string _dbPath;

        public NpcGroupStore(string dbPath) => _dbPath = dbPath;

        private bool Ready => !string.IsNullOrEmpty(_dbPath) && File.Exists(_dbPath);

        private SqliteConnection Open()
        {
            var connection = new SqliteConnection($"Data Source={_dbPath}");
            connection.Open();
            return connection;
        }

        // An item.db written before these tables existed has none of them. That is not an error -
        // EnsureDatabaseSchema adds them on the next cache load, and until then "no groups" is the
        // honest answer. Same guard LoadNpcsFrom uses for the Npc table.
        private static bool TablesExist(SqliteConnection connection)
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name='NpcGroup';";
            return cmd.ExecuteScalar() != null;
        }

        // Every group with its children, in SortOrder. One SELECT per table rather than a five-way
        // join: a join multiplies each group row by its predicate, override, value and list counts and
        // hands back wide rows to be de-duplicated in memory, which is the same trade LoadNpcsFrom
        // settled the same way.
        //
        // Inactive groups are returned too. Whether a deactivated group is shown, and where, is the
        // interface's decision; a store that hides rows makes "why is it gone" unanswerable.
        public List<NpcGroup> LoadAll()
        {
            var groups = new List<NpcGroup>();
            if (!Ready) return groups;

            using var connection = Open();
            if (!TablesExist(connection)) return groups;

            var byId = new Dictionary<long, NpcGroup>();

            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = @"
                    SELECT Id, Name, Origin, Seed, SortOrder, Notes, Active
                      FROM NpcGroup
                     ORDER BY SortOrder, Id;";

                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var group = new NpcGroup
                    {
                        Id = reader.GetInt64(0),
                        Name = Text(reader, 1),
                        Origin = Text(reader, 2),
                        Seed = Text(reader, 3),
                        SortOrder = reader.IsDBNull(4) ? 0 : reader.GetInt32(4),
                        Notes = Text(reader, 5),
                        Active = reader.IsDBNull(6) || reader.GetInt32(6) != 0,
                    };

                    groups.Add(group);
                    byId[group.Id] = group;
                }
            }

            if (groups.Count == 0) return groups;

            Read(connection, "SELECT GroupId, Axis, Mode, Value, Origin FROM NpcGroupPredicate;", (id, reader) =>
            {
                if (!byId.TryGetValue(id, out var group)) return;
                group.Predicates.Add(new NpcGroupPredicate
                {
                    Axis = Text(reader, 1),
                    Mode = Text(reader, 2),
                    Value = Text(reader, 3),

                    // Empty on every row written before the column existed, which is the right answer:
                    // those lines were written by hand or by the seeder.
                    Origin = Text(reader, 4),
                });
            });

            Read(connection, "SELECT GroupId, NpcKey, Mode FROM NpcGroupMemberOverride;", (id, reader) =>
            {
                if (!byId.TryGetValue(id, out var group)) return;
                group.Overrides.Add(new NpcGroupMemberOverride
                {
                    NpcKey = Text(reader, 1),
                    Mode = Text(reader, 2),
                });
            });

            Read(connection, "SELECT GroupId, Field, Kind, Low, High FROM NpcGroupValue;", (id, reader) =>
            {
                if (!byId.TryGetValue(id, out var group)) return;
                group.Values.Add(new NpcGroupValue
                {
                    Field = Text(reader, 1),
                    Kind = Text(reader, 2),
                    Low = Text(reader, 3),
                    High = Text(reader, 4),
                });
            });

            Read(connection, "SELECT GroupId, Kind, TargetKey, Mode, Extra FROM NpcGroupList;", (id, reader) =>
            {
                if (!byId.TryGetValue(id, out var group)) return;
                group.Lists.Add(new NpcGroupList
                {
                    Kind = Text(reader, 1),
                    TargetKey = Text(reader, 2),
                    Mode = Text(reader, 3),
                    Extra = reader.IsDBNull(4) ? null : reader.GetString(4),
                });
            });

            return groups;
        }

        // Insert or update, children and all, in one transaction. Returns the Id, which for a new
        // group is the one SQLite handed out - NpcGroup.Id is an INTEGER PRIMARY KEY, so the number
        // comes from the database rather than from the caller.
        //
        // The children are DELETEd and re-INSERTed rather than upserted: they have no identity of
        // their own beyond their own columns, so "what this group has now" is the only question worth
        // answering, and a diff would be more code for the same result.
        //
        // WHAT THIS MEANS FOR A CALLER THAT RE-SEEDS (section 4, "a re-seed must not throw the hand
        // corrections away"): this writes exactly what the object holds. A seeder that builds a fresh
        // NpcGroup and saves it under an existing Id therefore DROPS that group's overrides. Use
        // ReplacePredicate below for that path - it is the whole reason it exists.
        public long Save(NpcGroup group)
        {
            if (!Ready || group == null) return 0;

            using var connection = Open();
            if (!TablesExist(connection)) return 0;

            using var transaction = connection.BeginTransaction();

            long id = group.Id;

            if (id <= 0)
            {
                using (var insert = connection.CreateCommand())
                {
                    insert.CommandText = @"
                        INSERT INTO NpcGroup (Name, Origin, Seed, SortOrder, Notes, Active)
                        VALUES ($name, $origin, $seed, $sort, $notes, $active);";
                    Bind(insert, group);
                    insert.ExecuteNonQuery();
                }

                // A second statement rather than an INSERT ... ; SELECT last_insert_rowid() in one
                // command: what ExecuteScalar returns for a multi-statement command is a detail of the
                // provider, and getting it wrong here would hand back 0 as the new group's Id and
                // write every child row against a group that does not exist. Inside the transaction,
                // so no other writer can get between the two.
                using (var rowid = connection.CreateCommand())
                {
                    rowid.CommandText = "SELECT last_insert_rowid();";
                    id = Convert.ToInt64(rowid.ExecuteScalar());
                    group.Id = id;
                }
            }
            else
            {
                using var update = connection.CreateCommand();
                update.CommandText = @"
                    UPDATE NpcGroup
                       SET Name = $name, Origin = $origin, Seed = $seed,
                           SortOrder = $sort, Notes = $notes, Active = $active
                     WHERE Id = $id;";
                Bind(update, group);
                update.Parameters.AddWithValue("$id", id);

                // A group whose row is gone - deleted in another window, or an Id carried over from
                // another database - is re-inserted under its own Id rather than silently updating
                // nothing. The alternative is children pointing at a parent that does not exist.
                if (update.ExecuteNonQuery() == 0)
                {
                    using var insert = connection.CreateCommand();
                    insert.CommandText = @"
                        INSERT INTO NpcGroup (Id, Name, Origin, Seed, SortOrder, Notes, Active)
                        VALUES ($id, $name, $origin, $seed, $sort, $notes, $active);";
                    Bind(insert, group);
                    insert.Parameters.AddWithValue("$id", id);
                    insert.ExecuteNonQuery();
                }
            }

            DeleteChildren(connection, id, includeSnapshot: false);
            WritePredicates(connection, id, group.Predicates);
            WriteOverrides(connection, id, group.Overrides);
            WriteValues(connection, id, group.Values);
            WriteLists(connection, id, group.Lists);

            transaction.Commit();
            return id;
        }

        // The re-seed path of section 7: rewrite what the auto rule owns - the predicate - and leave
        // the hand corrections, the values and the lists exactly where they are. That separation is
        // the entire reason NpcGroupMemberOverride is its own table.
        public void ReplacePredicate(long groupId, IEnumerable<NpcGroupPredicate>? predicates)
        {
            if (!Ready || groupId <= 0) return;

            using var connection = Open();
            if (!TablesExist(connection)) return;

            using var transaction = connection.BeginTransaction();

            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "DELETE FROM NpcGroupPredicate WHERE GroupId = $id;";
                cmd.Parameters.AddWithValue("$id", groupId);
                cmd.ExecuteNonQuery();
            }

            WritePredicates(connection, groupId, predicates);
            transaction.Commit();
        }

        public void Delete(long groupId)
        {
            if (!Ready || groupId <= 0) return;

            using var connection = Open();
            if (!TablesExist(connection)) return;

            using var transaction = connection.BeginTransaction();

            DeleteChildren(connection, groupId, includeSnapshot: true);

            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "DELETE FROM NpcGroup WHERE Id = $id;";
                cmd.Parameters.AddWithValue("$id", groupId);
                cmd.ExecuteNonQuery();
            }

            transaction.Commit();
        }

        public void SetActive(long groupId, bool active)
        {
            if (!Ready || groupId <= 0) return;

            using var connection = Open();
            if (!TablesExist(connection)) return;

            using var cmd = connection.CreateCommand();
            cmd.CommandText = "UPDATE NpcGroup SET Active = $a WHERE Id = $id;";
            cmd.Parameters.AddWithValue("$a", active ? 1 : 0);
            cmd.Parameters.AddWithValue("$id", groupId);
            cmd.ExecuteNonQuery();
        }

        // ---- the rescan snapshot ---------------------------------------------------------------

        // What the user last CONFIRMED this group contains. Written by the confirmation, never by a
        // resolution: a snapshot that updated itself on every read would agree with the new membership
        // every time and the diff of section 8 would have nothing to report.
        public void SaveSnapshot(long groupId, IEnumerable<string>? npcKeys)
        {
            if (!Ready || groupId <= 0) return;

            using var connection = Open();
            if (!TablesExist(connection)) return;

            using var transaction = connection.BeginTransaction();

            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "DELETE FROM NpcGroupMemberSnapshot WHERE GroupId = $id;";
                cmd.Parameters.AddWithValue("$id", groupId);
                cmd.ExecuteNonQuery();
            }

            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = @"
                    INSERT OR IGNORE INTO NpcGroupMemberSnapshot (GroupId, NpcKey)
                    VALUES ($id, $key);";
                cmd.Parameters.AddWithValue("$id", groupId);
                var key = cmd.Parameters.Add("$key", SqliteType.Text);

                foreach (var npcKey in npcKeys ?? Enumerable.Empty<string>())
                {
                    if (string.IsNullOrWhiteSpace(npcKey)) continue;
                    key.Value = npcKey;
                    cmd.ExecuteNonQuery();
                }
            }

            transaction.Commit();
        }

        // Empty means "never confirmed", which is not the same as "confirmed as empty" - and both read
        // the same from this method. The caller that needs to tell them apart is the diff, and it has
        // the group: a group with no snapshot rows is unconfirmed and marked as such in the patch
        // report (section 8).
        public List<string> LoadSnapshot(long groupId)
        {
            var keys = new List<string>();
            if (!Ready || groupId <= 0) return keys;

            using var connection = Open();
            if (!TablesExist(connection)) return keys;

            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT NpcKey FROM NpcGroupMemberSnapshot WHERE GroupId = $id;";
            cmd.Parameters.AddWithValue("$id", groupId);

            using var reader = cmd.ExecuteReader();
            while (reader.Read()) keys.Add(reader.GetString(0));
            return keys;
        }

        // Every group's snapshot in one read, for the rescan diff (G6). LoadSnapshot per group would be
        // one round trip each, and the diff needs all of them at once - after a scan, and again whenever
        // the group screen opens.
        //
        // A group with no rows is absent from the dictionary rather than present with an empty list: the
        // difference between "confirmed as empty" and "never confirmed" is the whole point of the
        // snapshot, and an empty list would make them look alike.
        public Dictionary<long, List<string>> LoadSnapshots()
        {
            var byGroup = new Dictionary<long, List<string>>();
            if (!Ready) return byGroup;

            using var connection = Open();
            if (!TablesExist(connection)) return byGroup;

            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT GroupId, NpcKey FROM NpcGroupMemberSnapshot;";

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                long id = reader.GetInt64(0);
                if (!byGroup.TryGetValue(id, out var keys))
                    byGroup[id] = keys = new List<string>();

                keys.Add(reader.GetString(1));
            }

            return byGroup;
        }

        // ---- writing the children --------------------------------------------------------------

        private static void DeleteChildren(SqliteConnection connection, long groupId, bool includeSnapshot)
        {
            var tables = new List<string>
            {
                "NpcGroupPredicate", "NpcGroupMemberOverride", "NpcGroupValue", "NpcGroupList",
            };
            if (includeSnapshot) tables.Add("NpcGroupMemberSnapshot");

            foreach (var table in tables)
            {
                using var cmd = connection.CreateCommand();
                cmd.CommandText = $"DELETE FROM {table} WHERE GroupId = $id;";
                cmd.Parameters.AddWithValue("$id", groupId);
                cmd.ExecuteNonQuery();
            }
        }

        // INSERT OR IGNORE on all four, because each table's primary key is its own content: two
        // identical clauses are one clause, and a duplicate in the object is a caller's slip that
        // should not become an exception in the middle of a transaction.
        private static void WritePredicates(SqliteConnection connection, long groupId, IEnumerable<NpcGroupPredicate>? rows)
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = @"
                INSERT OR IGNORE INTO NpcGroupPredicate (GroupId, Axis, Mode, Value, Origin)
                VALUES ($id, $axis, $mode, $value, $origin);";
            cmd.Parameters.AddWithValue("$id", groupId);
            var axis = cmd.Parameters.Add("$axis", SqliteType.Text);
            var mode = cmd.Parameters.Add("$mode", SqliteType.Text);
            var value = cmd.Parameters.Add("$value", SqliteType.Text);
            var origin = cmd.Parameters.Add("$origin", SqliteType.Text);

            foreach (var row in rows ?? Enumerable.Empty<NpcGroupPredicate>())
            {
                if (row == null || string.IsNullOrWhiteSpace(row.Axis) || string.IsNullOrWhiteSpace(row.Value))
                    continue;

                axis.Value = row.Axis.Trim();
                mode.Value = string.IsNullOrWhiteSpace(row.Mode) ? NpcGroupMode.Include : row.Mode.Trim();
                value.Value = row.Value.Trim();
                origin.Value = (row.Origin ?? "").Trim();
                cmd.ExecuteNonQuery();
            }
        }

        private static void WriteOverrides(SqliteConnection connection, long groupId, IEnumerable<NpcGroupMemberOverride>? rows)
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = @"
                INSERT OR IGNORE INTO NpcGroupMemberOverride (GroupId, NpcKey, Mode)
                VALUES ($id, $key, $mode);";
            cmd.Parameters.AddWithValue("$id", groupId);
            var key = cmd.Parameters.Add("$key", SqliteType.Text);
            var mode = cmd.Parameters.Add("$mode", SqliteType.Text);

            foreach (var row in rows ?? Enumerable.Empty<NpcGroupMemberOverride>())
            {
                if (row == null || string.IsNullOrWhiteSpace(row.NpcKey)) continue;

                key.Value = row.NpcKey.Trim();
                mode.Value = string.IsNullOrWhiteSpace(row.Mode) ? NpcGroupOverrideMode.Add : row.Mode.Trim();
                cmd.ExecuteNonQuery();
            }
        }

        private static void WriteValues(SqliteConnection connection, long groupId, IEnumerable<NpcGroupValue>? rows)
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = @"
                INSERT OR IGNORE INTO NpcGroupValue (GroupId, Field, Kind, Low, High)
                VALUES ($id, $field, $kind, $low, $high);";
            cmd.Parameters.AddWithValue("$id", groupId);
            var field = cmd.Parameters.Add("$field", SqliteType.Text);
            var kind = cmd.Parameters.Add("$kind", SqliteType.Text);
            var low = cmd.Parameters.Add("$low", SqliteType.Text);
            var high = cmd.Parameters.Add("$high", SqliteType.Text);

            foreach (var row in rows ?? Enumerable.Empty<NpcGroupValue>())
            {
                if (row == null || string.IsNullOrWhiteSpace(row.Field)) continue;

                field.Value = row.Field.Trim();
                kind.Value = string.IsNullOrWhiteSpace(row.Kind) ? NpcGroupValueKind.Direct : row.Kind.Trim();
                low.Value = (object?)row.Low ?? DBNull.Value;
                high.Value = (object?)row.High ?? DBNull.Value;
                cmd.ExecuteNonQuery();
            }
        }

        private static void WriteLists(SqliteConnection connection, long groupId, IEnumerable<NpcGroupList>? rows)
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = @"
                INSERT OR IGNORE INTO NpcGroupList (GroupId, Kind, TargetKey, Mode, Extra)
                VALUES ($id, $kind, $target, $mode, $extra);";
            cmd.Parameters.AddWithValue("$id", groupId);
            var kind = cmd.Parameters.Add("$kind", SqliteType.Text);
            var target = cmd.Parameters.Add("$target", SqliteType.Text);
            var mode = cmd.Parameters.Add("$mode", SqliteType.Text);
            var extra = cmd.Parameters.Add("$extra", SqliteType.Text);

            foreach (var row in rows ?? Enumerable.Empty<NpcGroupList>())
            {
                if (row == null || string.IsNullOrWhiteSpace(row.Kind) || string.IsNullOrWhiteSpace(row.TargetKey))
                    continue;

                kind.Value = row.Kind.Trim();
                target.Value = row.TargetKey.Trim();
                mode.Value = string.IsNullOrWhiteSpace(row.Mode) ? NpcGroupListMode.Add : row.Mode.Trim();
                extra.Value = (object?)row.Extra ?? DBNull.Value;
                cmd.ExecuteNonQuery();
            }
        }

        private static void Bind(SqliteCommand cmd, NpcGroup group)
        {
            cmd.Parameters.AddWithValue("$name", group.Name ?? "");
            cmd.Parameters.AddWithValue("$origin",
                string.IsNullOrWhiteSpace(group.Origin) ? NpcGroupOrigin.User : group.Origin.Trim());
            cmd.Parameters.AddWithValue("$seed",
                string.IsNullOrWhiteSpace(group.Seed) ? (object)DBNull.Value : group.Seed.Trim());
            cmd.Parameters.AddWithValue("$sort", group.SortOrder);
            cmd.Parameters.AddWithValue("$notes",
                string.IsNullOrWhiteSpace(group.Notes) ? (object)DBNull.Value : group.Notes);
            cmd.Parameters.AddWithValue("$active", group.Active ? 1 : 0);
        }

        private static void Read(SqliteConnection connection, string sql, Action<long, SqliteDataReader> onRow)
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = sql;
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) onRow(reader.GetInt64(0), reader);
        }

        private static string Text(SqliteDataReader reader, int ordinal)
            => reader.IsDBNull(ordinal) ? "" : reader.GetString(ordinal);
    }
}
