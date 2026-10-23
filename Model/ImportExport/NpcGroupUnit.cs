using Microsoft.Data.Sqlite;
using SkyrimCraftingTool.Services;
using System;
using System.Collections.Generic;
using System.Linq;

namespace SkyrimCraftingTool.Model
{
    // TE-5: the NPC groups tab's export unit (docs/TabExport-Plan.md §6.2).
    //
    // WHAT TRAVELS IS THE DEFINITION, never the members: membership is computed from the predicate on
    // every read (NpcGroupResolver), so a member list would be stale on the other machine before it
    // arrived. The member SNAPSHOT is not exported either - it records what the user confirmed HERE,
    // and an imported group shows as unconfirmed until it is confirmed THERE.
    //
    // IDENTITY. The local Id means nothing elsewhere. An auto group is its Seed - seeding is additive
    // and never creates a second group for a known seed (NpcGroupVM.Seed), so the same seed on both
    // sides IS the same group. A user group is its Name. The DTO Key spells that out:
    // "auto|EncBandit03" or "user|My bandits", which also files them under Exports\NpcGroups\auto\
    // and \user\.
    //
    // NO TIMESTAMP. NpcGroup has no LastChanged, so the conflict check compares CONTENT: identical is
    // skipped, different is a conflict the user decides (ImportTargetState.Identical / Differs).
    internal sealed class NpcGroupUnit : ExportUnit
    {
        internal const string PredicateRows = "Predicates";
        internal const string OverrideRows = "Overrides";
        internal const string ValueRows = "Values";
        internal const string ListRows = "Lists";
        internal const string TargetUidColumn = "TargetKeyUid";

        public override string Table => "NpcGroup";

        private static string Identity(string origin, string seed, string name) =>
            string.Equals(origin, NpcGroupOrigin.Auto, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(seed)
                ? $"{NpcGroupOrigin.Auto}|{seed.Trim()}"
                : $"{NpcGroupOrigin.User}|{(name ?? "").Trim()}";

        // Which own-record table a list row of this kind can point into, if any.
        private static string? OwnTableFor(string? kind) => kind switch
        {
            "spell" => "Spells",
            "item" => "LeveledList",
            _ => null,
        };

        private static List<NpcGroup> LoadGroups(SqliteConnection connection) =>
            UserRecords.TableExists(connection, "NpcGroup")
                ? new NpcGroupStore(connection.DataSource).LoadAll()
                : new List<NpcGroup>();

        // ---- export ----

        internal override List<EditedItemDto> Collect(SqliteConnection connection, ExportScope scope, string? scopeValue)
        {
            // A group belongs to no plugin and no item, so only the whole-tab export carries groups.
            if (scope != ExportScope.All) return new List<EditedItemDto>();

            var result = new List<EditedItemDto>();
            foreach (var group in LoadGroups(connection))
            {
                var dto = ToDto(group);
                dto.LastChanged = ItemDBHandler.NowIso();

                // A spell or item the group adds or removes may be one the user created: an own spell,
                // or an own leveled list (TE-6).
                foreach (var row in dto.Rows![ListRows])
                {
                    var table = OwnTableFor(row["Kind"]);
                    if (table == null) continue;
                    var uid = UserRecords.UidForReference(connection, table, row["TargetKey"]);
                    if (uid.Length > 0) row[TargetUidColumn] = uid;
                }
                result.Add(dto);
            }
            return result;
        }

        private static EditedItemDto ToDto(NpcGroup group) => new()
        {
            Table = "NpcGroup",
            Key = Identity(group.Origin, group.Seed, group.Name),
            DisplayName = group.Name,
            Fields =
            {
                ["Name"] = group.Name ?? "",
                ["Origin"] = string.IsNullOrWhiteSpace(group.Origin) ? NpcGroupOrigin.User : group.Origin,
                ["Seed"] = group.Seed ?? "",
                ["Notes"] = group.Notes ?? "",
                ["Active"] = group.Active ? "1" : "0",
            },
            Rows = new Dictionary<string, List<Dictionary<string, string>>>
            {
                [PredicateRows] = group.Predicates.Select(p => Row(("Axis", p.Axis), ("Mode", p.Mode), ("Value", p.Value), ("Origin", p.Origin))).ToList(),
                [OverrideRows] = group.Overrides.Select(o => Row(("NpcKey", o.NpcKey), ("Mode", o.Mode))).ToList(),
                [ValueRows] = group.Values.Select(v => Row(("Field", v.Field), ("Kind", v.Kind), ("Low", v.Low), ("High", v.High))).ToList(),
                [ListRows] = group.Lists.Select(l => Row(("Kind", l.Kind), ("TargetKey", l.TargetKey), ("Mode", l.Mode), ("Extra", l.Extra ?? ""))).ToList(),
            },
        };

        private static Dictionary<string, string> Row(params (string Name, string? Value)[] cells) =>
            cells.ToDictionary(c => c.Name, c => c.Value ?? "");

        // ---- import ----

        // A group's spell that is one of the user's own: this machine's copy, by Uid, or the row is
        // left out with a note - the same rule as an NPC's spell list (TE-4).
        internal override void FollowKeys(SqliteConnection connection, IReadOnlyList<EditedItemDto> items, ImportKeyMap map, List<string> notes)
        {
            foreach (var item in items)
            {
                if (item.Rows == null || !item.Rows.TryGetValue(ListRows, out var lists) || lists == null) continue;

                foreach (var row in lists.ToList())
                {
                    if (!row.TryGetValue(TargetUidColumn, out var uid) || string.IsNullOrEmpty(uid)) continue;

                    var table = OwnTableFor(row.GetValueOrDefault("Kind", "")) ?? "Spells";
                    var local = UserRecords.ResolveReference(connection, table, uid, map);

                    if (local != null)
                    {
                        row["TargetKey"] = local;
                        continue;
                    }

                    lists.Remove(row);
                    var (what, tab) = table == "Spells" ? ("spells is a spell", "Spells") : ("items is a leveled list", "Container/LeveledList");
                    notes.Add($"⚠ Group '{item.DisplayName}': one of its {what} of your own that this setup does not have yet. " +
                              $"Import the {tab} tab first, then this one again - until then it was left out of the group.");
                }
            }
        }

        internal override ImportTarget Probe(SqliteConnection connection, EditedItemDto item)
        {
            if (!UserRecords.TableExists(connection, "NpcGroup")) return ImportTarget.Missing;

            var local = LoadGroups(connection).FirstOrDefault(g =>
                string.Equals(Identity(g.Origin, g.Seed, g.Name), item.Key, StringComparison.OrdinalIgnoreCase));

            // A group needs nothing scanned to exist: one this machine lacks is created.
            if (local == null) return ImportTarget.Create;

            return Canonical(ToDto(local)) == Canonical(item) ? ImportTarget.Identical : ImportTarget.Differs;
        }

        // The content, in an order that does not depend on how either side happened to store it.
        // Origin and Seed are part of the identity and the Key already says them; the Uid columns are
        // bookkeeping that is resolved away before this runs.
        private static string Canonical(EditedItemDto dto)
        {
            static string Rows(EditedItemDto d, string name) =>
                string.Join(";", (d.Rows?.GetValueOrDefault(name) ?? new List<Dictionary<string, string>>())
                    .Select(r => string.Join(",", r.Where(kv => kv.Key != TargetUidColumn)
                        .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                        .Select(kv => $"{kv.Key}={kv.Value.Trim().ToUpperInvariant()}")))
                    .OrderBy(s => s, StringComparer.Ordinal));

            string F(string name) => dto.Fields.GetValueOrDefault(name, "").Trim();

            return string.Join("|", F("Name"), F("Notes"), F("Active"),
                Rows(dto, PredicateRows), Rows(dto, OverrideRows), Rows(dto, ValueRows), Rows(dto, ListRows));
        }

        internal override bool Apply(SqliteConnection connection, SqliteTransaction transaction, EditedItemDto item)
        {
            if (!UserRecords.TableExists(connection, "NpcGroup")) return false;

            string name = item.Fields.GetValueOrDefault("Name", "").Trim();
            string origin = string.Equals(item.Fields.GetValueOrDefault("Origin", ""), NpcGroupOrigin.Auto, StringComparison.OrdinalIgnoreCase)
                ? NpcGroupOrigin.Auto : NpcGroupOrigin.User;
            string seed = item.Fields.GetValueOrDefault("Seed", "").Trim();
            if (name.Length == 0) return false;

            object Nullable(string s) => string.IsNullOrWhiteSpace(s) ? DBNull.Value : s;
            string notes = item.Fields.GetValueOrDefault("Notes", "");
            int active = item.Fields.GetValueOrDefault("Active", "1") == "0" ? 0 : 1;

            long? id = null;
            using (var find = connection.CreateCommand())
            {
                find.Transaction = transaction;
                find.CommandText = origin == NpcGroupOrigin.Auto && seed.Length > 0
                    ? "SELECT Id FROM NpcGroup WHERE Origin = 'auto' AND Seed = @s COLLATE NOCASE ORDER BY Id LIMIT 1"
                    : "SELECT Id FROM NpcGroup WHERE Origin <> 'auto' AND Name = @n COLLATE NOCASE ORDER BY Id LIMIT 1";
                find.Parameters.AddWithValue("@s", seed);
                find.Parameters.AddWithValue("@n", name);
                if (find.ExecuteScalar() is long found) id = found;
            }

            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.Parameters.AddWithValue("@name", name);
                cmd.Parameters.AddWithValue("@notes", Nullable(notes));
                cmd.Parameters.AddWithValue("@active", active);

                if (id == null)
                {
                    // At the end of this machine's list - the file's position means nothing here.
                    cmd.CommandText = @"INSERT INTO NpcGroup (Name, Origin, Seed, SortOrder, Notes, Active)
                                        VALUES (@name, @origin, @seed, (SELECT COALESCE(MAX(SortOrder), -1) + 1 FROM NpcGroup), @notes, @active)";
                    cmd.Parameters.AddWithValue("@origin", origin);
                    cmd.Parameters.AddWithValue("@seed", Nullable(seed));
                    cmd.ExecuteNonQuery();

                    using var rowid = connection.CreateCommand();
                    rowid.Transaction = transaction;
                    rowid.CommandText = "SELECT last_insert_rowid()";
                    id = Convert.ToInt64(rowid.ExecuteScalar());
                }
                else
                {
                    // Position stays where the user put it on this machine.
                    cmd.CommandText = "UPDATE NpcGroup SET Name = @name, Notes = @notes, Active = @active WHERE Id = @id";
                    cmd.Parameters.AddWithValue("@id", id.Value);
                    cmd.ExecuteNonQuery();
                }
            }

            // The snapshot stays: it is what the user confirmed here, and if the imported definition
            // reaches other NPCs the drift report is exactly what should say so.
            var rows = item.Rows ?? new Dictionary<string, List<Dictionary<string, string>>>();
            List<Dictionary<string, string>> Of(string n) => rows.GetValueOrDefault(n) ?? new List<Dictionary<string, string>>();

            NpcGroupStore.DeleteChildren(connection, id.Value, includeSnapshot: false);
            NpcGroupStore.WritePredicates(connection, id.Value, Of(PredicateRows).Select(r => new NpcGroupPredicate
            {
                Axis = r.GetValueOrDefault("Axis", ""), Mode = r.GetValueOrDefault("Mode", ""),
                Value = r.GetValueOrDefault("Value", ""), Origin = r.GetValueOrDefault("Origin", ""),
            }));
            NpcGroupStore.WriteOverrides(connection, id.Value, Of(OverrideRows).Select(r => new NpcGroupMemberOverride
            {
                NpcKey = r.GetValueOrDefault("NpcKey", ""), Mode = r.GetValueOrDefault("Mode", ""),
            }));
            NpcGroupStore.WriteValues(connection, id.Value, Of(ValueRows).Select(r => new NpcGroupValue
            {
                Field = r.GetValueOrDefault("Field", ""), Kind = r.GetValueOrDefault("Kind", ""),
                Low = r.GetValueOrDefault("Low", ""), High = r.GetValueOrDefault("High", ""),
            }));
            NpcGroupStore.WriteLists(connection, id.Value, Of(ListRows).Select(r => new NpcGroupList
            {
                Kind = r.GetValueOrDefault("Kind", ""), TargetKey = r.GetValueOrDefault("TargetKey", ""),
                Mode = r.GetValueOrDefault("Mode", ""),
                Extra = string.IsNullOrEmpty(r.GetValueOrDefault("Extra", "")) ? null : r["Extra"],
            }));
            return true;
        }
    }
}
