using Microsoft.Data.Sqlite;
using SkyrimCraftingTool.Services;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace SkyrimCraftingTool.Model
{
    // TE-4: the NPCs tab's export unit (docs/TabExport-Plan.md).
    //
    // An NPC is always a scanned record - the tool creates none - so there is no identity question
    // for the NPC itself. What travels:
    //
    //   Fields               the shadow columns (IsEdited<X> for every NpcEditStore.EditableColumns)
    //   Rows["Skills"]       skill edits (IsEditedValue), when SkillsEdited
    //   Rows["Factions"]     the whole faction list, when FactionsEdited
    //   Rows["Spells"|"Perks"|"Items"]
    //                        the three N-P3 lists, all three together, when ListsEdited - they are
    //                        snapshotted together (NpcEditStore.SnapshotAllListsOnce), so they travel
    //                        together too
    //
    // A spell in an NPC's list may be one the user created; that row carries the spell's Uid as
    // "SpellKeyUid" (the row-level form of EditedItemDto.Refs), and FollowNpcRefs resolves it. The same
    // for an own leveled list in the item list ("ItemKeyUid") and as the death item (TE-6).
    //
    // The import mirrors NpcEditStore's writers step for step - snapshot once, replace wholesale,
    // set the flag - but inside the import's single transaction, which the store's own methods
    // (one connection each) cannot join.
    public partial class ItemDBHandler
    {
        internal const string NpcSkillRows = "Skills";
        internal const string NpcFactionRows = "Factions";
        internal const string NpcSpellRows = "Spells";
        internal const string NpcPerkRows = "Perks";
        internal const string NpcItemRows = "Items";
        internal const string NpcSpellUidColumn = "SpellKeyUid";

        // The three lists: rows name -> (table, its columns). One table, so export, apply and the
        // tests cannot disagree about a column.
        private static readonly (string Rows, string Table, string[] Columns)[] NpcLists =
        {
            (NpcSpellRows, "NpcSpells", new[] { "SpellKey", "Kind" }),
            (NpcPerkRows, "NpcPerks", new[] { "PerkKey", "Rank" }),
            (NpcItemRows, "NpcItems", new[] { "ItemKey", "Count" }),
        };

        private static string[] NpcShadowColumns =>
            NpcEditStore.EditableColumns.Select(c => "IsEdited" + c).ToArray();

        private static string Invariant(object value) =>
            Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";

        internal static List<EditedItemDto> GetEditedNpcs(SqliteConnection connection, ExportScope scope, string scopeValue)
        {
            // A database without the NPC tables (one from before them, or a test fixture) has no NPC
            // edits - not an error that should take the other tabs' units down with it.
            if (!UserRecords.TableExists(connection, "Npc")) return new List<EditedItemDto>();

            var shadow = NpcShadowColumns;
            var rows = new List<(EditedItemDto Dto, bool Skills, bool Factions, bool Lists)>();

            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = $@"SELECT Key, LastChanged, Name, SkillsEdited, FactionsEdited, ListsEdited,
                        {string.Join(", ", shadow)}
                    FROM Npc WHERE IsEdited = 1" + BuildScopeWhere(scope, scopeValue, cmd);

                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    var dto = new EditedItemDto
                    {
                        Table = "Npc",
                        Key = r.GetString(0),
                        LastChanged = SafeStr(r, 1),
                        DisplayName = SafeStr(r, 2),
                    };
                    for (int i = 0; i < shadow.Length; i++)
                        if (!r.IsDBNull(6 + i))
                            dto.Fields[shadow[i]] = Invariant(r.GetValue(6 + i));

                    rows.Add((dto, r.GetInt64(3) == 1, r.GetInt64(4) == 1, r.GetInt64(5) == 1));
                }
            }

            var result = new List<EditedItemDto>();
            foreach (var (dto, skills, factions, lists) in rows)
            {
                var childRows = new Dictionary<string, List<Dictionary<string, string>>>();

                if (skills)
                    childRows[NpcSkillRows] = ReadChildRows(connection,
                        "SELECT Skill, IsEditedValue AS Value FROM NpcSkills WHERE NpcKey = @k AND IsEditedValue IS NOT NULL", dto.Key);
                if (factions)
                    childRows[NpcFactionRows] = ReadChildRows(connection,
                        "SELECT FactionKey, Rank FROM NpcFactions WHERE NpcKey = @k", dto.Key);
                if (lists)
                {
                    foreach (var (name, table, columns) in NpcLists)
                        childRows[name] = ReadChildRows(connection,
                            $"SELECT {string.Join(", ", columns)} FROM {table} WHERE NpcKey = @k", dto.Key);

                }

                if (childRows.Count > 0) dto.Rows = childRows;
                AnnotateNpcRefs(connection, dto);
                result.Add(dto);
            }
            return result;
        }

        private static List<Dictionary<string, string>> ReadChildRows(SqliteConnection connection, string sql, string key)
        {
            var rows = new List<Dictionary<string, string>>();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = sql;
            cmd.Parameters.AddWithValue("@k", key);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var row = new Dictionary<string, string>();
                for (int i = 0; i < r.FieldCount; i++)
                    row[r.GetName(i)] = r.IsDBNull(i) ? "" : Invariant(r.GetValue(i));
                rows.Add(row);
            }
            return rows;
        }

        // The two NPC lists that can name a record the user created, and where that record lives.
        // A row carries the record's Uid in <KeyColumn>Uid. (Rows name, key column, table, wording, tab.)
        private static readonly (string Rows, string KeyColumn, string Table, string What, string Tab)[] NpcOwnRefs =
        {
            (NpcSpellRows, "SpellKey", "Spells", "spells is a spell", "Spells"),
            (NpcItemRows, "ItemKey", "LeveledList", "items is a leveled list", "Container/LeveledList"),
        };

        // The death item may be an own leveled list too (TE-6) - a field, so it goes through Refs.
        private static readonly ExportUnits.OwnRecordRef[] NpcFieldRefs =
        {
            new("IsEditedDeathItemKey", "LeveledList", "death item", "Container/LeveledList"),
        };

        private static void AnnotateNpcRefs(SqliteConnection connection, EditedItemDto dto)
        {
            ExportUnits.AnnotateRefs(connection, new[] { dto }, NpcFieldRefs);
            if (dto.Rows == null) return;

            foreach (var (rowsName, keyColumn, table, _, _) in NpcOwnRefs)
            {
                if (!dto.Rows.TryGetValue(rowsName, out var rows) || rows == null) continue;
                foreach (var row in rows)
                {
                    var uid = UserRecords.UidForReference(connection, table, row.GetValueOrDefault(keyColumn, ""));
                    if (uid.Length > 0) row[keyColumn + "Uid"] = uid;
                }
            }
        }

        // An NPC's spell or item that is one of the user's own records: this machine's copy, by Uid.
        // Not here means the file's Key may well be a different record of this machine's, so the row
        // (or the death item field) is held back and the summary says which tab to import first -
        // the same rule as an item's enchantment.
        internal static void FollowNpcRefs(SqliteConnection connection, IEnumerable<EditedItemDto> items, ImportKeyMap map, List<string> notes)
        {
            var list = items.ToList();
            ExportUnits.FollowRefs(connection, list, NpcFieldRefs, map, notes);

            foreach (var item in list)
            {
                if (item.Rows == null) continue;
                var who = string.IsNullOrEmpty(item.DisplayName) ? item.Key : item.DisplayName;

                foreach (var (rowsName, keyColumn, table, what, tab) in NpcOwnRefs)
                {
                    if (!item.Rows.TryGetValue(rowsName, out var rows) || rows == null) continue;

                    foreach (var row in rows.ToList())
                    {
                        if (!row.TryGetValue(keyColumn + "Uid", out var uid) || string.IsNullOrEmpty(uid)) continue;

                        var local = UserRecords.ResolveReference(connection, table, uid, map);
                        if (local != null)
                        {
                            row[keyColumn] = local;
                            continue;
                        }

                        rows.Remove(row);
                        notes.Add($"⚠ {who}: one of its {what} of your own that this setup does not have yet. " +
                                  $"Import the {tab} tab first, then this one again - until then it was left out of the list.");
                    }
                }
            }
        }

        internal static bool ApplyNpcItem(SqliteConnection connection, SqliteTransaction transaction, EditedItemDto item)
        {
            if (!RowExists(connection, transaction, "Npc", item.Key))
                return false;

            bool wrote = false;
            string stamp = string.IsNullOrEmpty(item.LastChanged) ? NowIso() : item.LastChanged;

            // ---- shadow columns: only names the store itself may write ----
            var allowed = new HashSet<string>(NpcShadowColumns, StringComparer.Ordinal);
            var fields = item.Fields.Where(kv => allowed.Contains(kv.Key)).ToList();
            if (fields.Count > 0)
            {
                using var cmd = connection.CreateCommand();
                cmd.Transaction = transaction;
                var sets = fields.Select((kv, i) => $"{kv.Key} = @f{i}").ToList();
                for (int i = 0; i < fields.Count; i++)
                    cmd.Parameters.AddWithValue($"@f{i}", fields[i].Value);
                cmd.CommandText = $"UPDATE Npc SET {string.Join(", ", sets)} WHERE Key = @key";
                cmd.Parameters.AddWithValue("@key", item.Key);
                cmd.ExecuteNonQuery();
                wrote = true;
            }

            var rows = item.Rows ?? new Dictionary<string, List<Dictionary<string, string>>>();

            // ---- skills: the file's set of skill edits replaces this machine's ----
            if (rows.TryGetValue(NpcSkillRows, out var skills) && skills != null)
            {
                Exec(connection, transaction, "UPDATE NpcSkills SET IsEditedValue = NULL WHERE NpcKey = @k", item.Key);
                foreach (var skill in skills)
                {
                    var name = skill.GetValueOrDefault("Skill", "");
                    if (string.IsNullOrWhiteSpace(name) || !int.TryParse(skill.GetValueOrDefault("Value", ""), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
                        continue;

                    using var cmd = connection.CreateCommand();
                    cmd.Transaction = transaction;
                    // Same upsert as NpcEditStore.SaveSkill: a skill the scan never wrote a row for is
                    // still editable.
                    cmd.CommandText = @"INSERT INTO NpcSkills (NpcKey, Skill, Value, Offset, IsEditedValue)
                                        VALUES (@k, @s, NULL, 0, @v)
                                        ON CONFLICT(NpcKey, Skill) DO UPDATE SET IsEditedValue = @v";
                    cmd.Parameters.AddWithValue("@k", item.Key);
                    cmd.Parameters.AddWithValue("@s", name);
                    cmd.Parameters.AddWithValue("@v", value);
                    cmd.ExecuteNonQuery();
                }
                Exec(connection, transaction,
                    "UPDATE Npc SET SkillsEdited = (SELECT COUNT(*) > 0 FROM NpcSkills WHERE NpcKey = @k AND IsEditedValue IS NOT NULL) WHERE Key = @k",
                    item.Key);
                wrote = true;
            }

            // ---- factions: snapshot once, replace, flag ----
            if (rows.TryGetValue(NpcFactionRows, out var factions) && factions != null)
            {
                NpcEditStore.SnapshotFactionsOnce(connection, transaction, item.Key);
                Exec(connection, transaction, "DELETE FROM NpcFactions WHERE NpcKey = @k", item.Key);
                foreach (var f in DistinctBy(factions, "FactionKey"))
                {
                    using var cmd = connection.CreateCommand();
                    cmd.Transaction = transaction;
                    cmd.CommandText = "INSERT INTO NpcFactions (NpcKey, FactionKey, Rank) VALUES (@k, @f, @r)";
                    cmd.Parameters.AddWithValue("@k", item.Key);
                    cmd.Parameters.AddWithValue("@f", f["FactionKey"]);
                    cmd.Parameters.AddWithValue("@r", IntOr(f.GetValueOrDefault("Rank", ""), 0));
                    cmd.ExecuteNonQuery();
                }
                Exec(connection, transaction, "UPDATE Npc SET FactionsEdited = 1 WHERE Key = @k", item.Key);
                wrote = true;
            }

            // ---- the three lists: one snapshot for all three, then each list the file has ----
            if (NpcLists.Any(l => rows.TryGetValue(l.Rows, out var list) && list != null))
            {
                NpcEditStore.SnapshotAllListsOnce(connection, transaction, item.Key);
                foreach (var (name, table, columns) in NpcLists)
                {
                    if (!rows.TryGetValue(name, out var list) || list == null) continue;

                    Exec(connection, transaction, $"DELETE FROM {table} WHERE NpcKey = @k", item.Key);
                    foreach (var row in DistinctBy(list, columns[0]))
                    {
                        using var cmd = connection.CreateCommand();
                        cmd.Transaction = transaction;
                        cmd.CommandText = $"INSERT INTO {table} (NpcKey, {columns[0]}, {columns[1]}) VALUES (@k, @a, @b)";
                        cmd.Parameters.AddWithValue("@k", item.Key);
                        cmd.Parameters.AddWithValue("@a", row[columns[0]]);
                        // Kind is text; Rank and Count are numbers, and a count of 0 would be an item
                        // that is not there.
                        object second = columns[1] switch
                        {
                            "Kind" => row.GetValueOrDefault("Kind", ""),
                            "Count" => IntOr(row.GetValueOrDefault("Count", ""), 1),
                            _ => IntOr(row.GetValueOrDefault(columns[1], ""), 0),
                        };
                        cmd.Parameters.AddWithValue("@b", second);
                        cmd.ExecuteNonQuery();
                    }
                }
                Exec(connection, transaction, "UPDATE Npc SET ListsEdited = 1 WHERE Key = @k", item.Key);
                wrote = true;
            }

            if (wrote)
            {
                using var cmd = connection.CreateCommand();
                cmd.Transaction = transaction;
                cmd.CommandText = "UPDATE Npc SET IsEdited = 1, LastChanged = @t WHERE Key = @k";
                cmd.Parameters.AddWithValue("@t", stamp);
                cmd.Parameters.AddWithValue("@k", item.Key);
                cmd.ExecuteNonQuery();
            }
            return wrote;
        }

        // A file may repeat a row; the tables' primary keys cannot hold that, and a constraint
        // violation would roll back the whole import. Last one wins, as in NpcEditStore.
        private static IEnumerable<Dictionary<string, string>> DistinctBy(IEnumerable<Dictionary<string, string>> rows, string keyColumn) =>
            rows.Where(r => !KeyFactory.IsUnsetKey(r.GetValueOrDefault(keyColumn, "")))
                .GroupBy(r => r[keyColumn], StringComparer.OrdinalIgnoreCase)
                .Select(g => g.Last());

        private static int IntOr(string raw, int fallback) =>
            int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;

        private static void Exec(SqliteConnection connection, SqliteTransaction transaction, string sql, string key)
        {
            using var cmd = connection.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = sql;
            cmd.Parameters.AddWithValue("@k", key);
            cmd.ExecuteNonQuery();
        }
    }
}
