using Microsoft.Data.Sqlite;
using SkyrimCraftingTool.Services;
using System;
using System.Collections.Generic;
using System.Linq;

namespace SkyrimCraftingTool.Model
{
    // TE-6: the Container/LeveledList tab's export units (docs/TabExport-Plan.md §4, §6.1, §6.4).
    //
    //   Placement            an item's placements (IsEditedContainerString) - stored on the item
    //                        (Armor, Weapons, WorldItem), exported here (§6.1)
    //   LeveledListEdit      a list's chance-none / calc-flag edit
    //   LeveledListRemoved   objects taken out of a list        } one DTO per owner,
    //   LeveledListRestored  lost entries put back into a list  } the file's set replaces
    //   ContainerRemoved     objects taken out of a container   } this machine's
    //   UserLeveledList      an own list and where it hangs (identity by Uid, §6.4)
    //
    // OWN LISTS ARE REFERENCED FROM EVERYWHERE in this tab: a placement can name one, an own list can
    // hang in another, an edit can be about one. Each such reference carries the list's Uid, and the
    // import finds the list by it - in this same import first, where it is usually created
    // (ImportKeyMap.RememberUid), then in the database.
    internal static class ContainerUnitHelpers
    {
        internal const string OwnListTable = "LeveledList";

        // The plugin/item scope over an owner column, the same reading BuildScopeWhere gives "Key".
        internal static string ScopeWhere(SqliteCommand cmd, string column, ExportScope scope, string? scopeValue, bool hasWhere)
        {
            string glue = hasWhere ? " AND " : " WHERE ";
            switch (scope)
            {
                case ExportScope.Item:
                    cmd.Parameters.AddWithValue("@scope", scopeValue ?? "");
                    return $"{glue}{column} = @scope";
                case ExportScope.Plugin:
                    cmd.Parameters.AddWithValue("@scope", (scopeValue ?? "") + "|%");
                    return $"{glue}{column} LIKE @scope";
                default:
                    return "";
            }
        }

        internal static bool OwnerExists(SqliteConnection connection, string table, string keyColumn, string key)
        {
            if (!UserRecords.TableExists(connection, table)) return false;
            using var cmd = connection.CreateCommand();
            cmd.CommandText = $"SELECT 1 FROM {table} WHERE {keyColumn} = @k";
            cmd.Parameters.AddWithValue("@k", key);
            return cmd.ExecuteScalar() != null;
        }

        // The DTO's own Key is an own list: carry its Uid, so the import finds the list again.
        internal static void AnnotateOwnKey(SqliteConnection connection, EditedItemDto dto)
        {
            var uid = UserRecords.UidForReference(connection, OwnListTable, dto.Key);
            if (uid.Length > 0) (dto.Refs ??= new Dictionary<string, string>())["Key"] = uid;
        }

        // ... and at import, point the DTO at this machine's copy. Not here: the DTO is made to point
        // nowhere, so the probe reports it as "not present locally" rather than writing the decision
        // onto whatever list of the user's happens to have the file's key.
        internal const string Unresolved = "?unresolved";

        internal static void FollowOwnKey(SqliteConnection connection, EditedItemDto dto, ImportKeyMap map, List<string> notes, string what)
        {
            if (dto.Refs == null || !dto.Refs.TryGetValue("Key", out var uid)) return;
            var local = UserRecords.ResolveReference(connection, OwnListTable, uid, map);
            if (local != null) { dto.Key = local; return; }

            notes.Add($"⚠ {what} for your own list '{dto.DisplayName}' was skipped: that list is neither in this export nor in this setup.");
            dto.Key = Unresolved;
        }

        internal static void Exec(SqliteConnection connection, SqliteTransaction transaction, string sql, params (string Name, object? Value)[] args)
        {
            using var cmd = connection.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = sql;
            foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }

        internal static string ListName(SqliteConnection connection, string listKey)
        {
            if (!UserRecords.TableExists(connection, OwnListTable)) return listKey;
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT EditorID FROM LeveledList WHERE Key = @k";
            cmd.Parameters.AddWithValue("@k", listKey);
            return cmd.ExecuteScalar() as string ?? listKey;
        }
    }

    // ===================================================================
    // Placement
    // ===================================================================

    internal sealed class PlacementUnit : ExportUnit
    {
        // Where a placement can live. Also the whitelist for the Source field a file names - a table
        // name goes into SQL.
        private static readonly string[] SourceTables = { "Armor", "Weapons", "WorldItem" };

        public override string Table => "Placement";

        internal override List<EditedItemDto> Collect(SqliteConnection connection, ExportScope scope, string? scopeValue)
        {
            var result = new List<EditedItemDto>();
            foreach (var source in SourceTables.Where(t => UserRecords.TableExists(connection, t)))
            {
                using var cmd = connection.CreateCommand();
                cmd.CommandText = $"SELECT Key, Name, LastChanged, IsEditedContainerString FROM {source} WHERE IsEdited = 1 AND IsEditedContainerString IS NOT NULL"
                                  + ContainerUnitHelpers.ScopeWhere(cmd, "Key", scope, scopeValue, hasWhere: true);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    result.Add(new EditedItemDto
                    {
                        Table = Table,
                        Key = r.GetString(0),
                        DisplayName = r.IsDBNull(1) ? r.GetString(0) : r.GetString(1),
                        LastChanged = r.IsDBNull(2) ? "" : r.GetString(2),
                        Fields = { ["Source"] = source, ["ContainerString"] = r.GetString(3) },
                    });
                }
            }

            // An own list inside a placement: its Uid, keyed by the list's key here.
            foreach (var dto in result)
                foreach (var entry in ContainerStringParser.Parse(dto.Fields["ContainerString"]))
                    foreach (var listKey in entry.Placements.Keys)
                    {
                        var uid = UserRecords.UidForReference(connection, ContainerUnitHelpers.OwnListTable, listKey);
                        if (uid.Length > 0) (dto.Refs ??= new Dictionary<string, string>())[listKey] = uid;
                    }

            return result;
        }

        // Rewrites the own lists in a placement to their keys here, and drops one this setup does
        // not have - with a note. The string is rebuilt only when something changed, so a placement
        // without own lists arrives byte for byte as it left.
        internal override void FollowKeys(SqliteConnection connection, IReadOnlyList<EditedItemDto> items, ImportKeyMap map, List<string> notes)
        {
            foreach (var dto in items)
            {
                if (dto.Refs == null || dto.Refs.Count == 0) continue;

                bool changed = false;
                var rebuilt = new List<ContainerStringEntry>();
                foreach (var entry in ContainerStringParser.Parse(dto.Fields.GetValueOrDefault("ContainerString", "")))
                {
                    var placements = new List<(string Key, int Level, int Count)>();
                    foreach (var (listKey, placement) in entry.Placements)
                    {
                        if (!dto.Refs.TryGetValue(listKey, out var uid))
                        {
                            placements.Add((listKey, placement.Level, placement.Count));
                            continue;
                        }

                        var local = UserRecords.ResolveReference(connection, ContainerUnitHelpers.OwnListTable, uid, map);
                        if (local == null)
                        {
                            changed = true;
                            notes.Add($"⚠ {dto.DisplayName}: one of its placements is in your own list '{listKey}', which is neither in this export nor in this setup - that placement was left out.");
                            continue;
                        }

                        if (!string.Equals(local, listKey, StringComparison.OrdinalIgnoreCase)) changed = true;
                        placements.Add((local, placement.Level, placement.Count));
                    }
                    rebuilt.Add(new ContainerStringEntry(entry.ContainerKey, placements));
                }

                if (changed) dto.Fields["ContainerString"] = ContainerStringBuilder.Build(rebuilt);
            }
        }

        private static string? Source(EditedItemDto dto)
        {
            var source = dto.Fields.GetValueOrDefault("Source", "");
            return SourceTables.FirstOrDefault(t => string.Equals(t, source, StringComparison.OrdinalIgnoreCase));
        }

        internal override ImportTarget Probe(SqliteConnection connection, EditedItemDto item)
        {
            var source = Source(item);
            return source == null ? ImportTarget.Missing : ExportUnits.ProbeKeyedRow(connection, source, item.Key);
        }

        internal override bool Apply(SqliteConnection connection, SqliteTransaction transaction, EditedItemDto item)
        {
            var source = Source(item);
            if (source == null || !item.Fields.TryGetValue("ContainerString", out var value)) return false;

            using var cmd = connection.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = $"UPDATE {source} SET IsEditedContainerString = @v, IsEdited = 1, LastChanged = @t WHERE Key = @k";
            cmd.Parameters.AddWithValue("@v", value);
            cmd.Parameters.AddWithValue("@t", string.IsNullOrEmpty(item.LastChanged) ? ItemDBHandler.NowIso() : item.LastChanged);
            cmd.Parameters.AddWithValue("@k", item.Key);
            return cmd.ExecuteNonQuery() > 0;
        }
    }

    // ===================================================================
    // A list's own properties
    // ===================================================================

    internal sealed class LeveledListEditUnit : ExportUnit
    {
        public override string Table => "LeveledListEdit";

        internal override List<EditedItemDto> Collect(SqliteConnection connection, ExportScope scope, string? scopeValue)
        {
            var result = new List<EditedItemDto>();
            if (!UserRecords.TableExists(connection, "LeveledListEdit")) return result;

            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "SELECT ListKey, ChanceNone, CalcFlagMode, LastChanged FROM LeveledListEdit"
                                  + ContainerUnitHelpers.ScopeWhere(cmd, "ListKey", scope, scopeValue, hasWhere: false);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    var dto = new EditedItemDto { Table = Table, Key = r.GetString(0), LastChanged = r.IsDBNull(3) ? "" : r.GetString(3) };
                    if (!r.IsDBNull(1)) dto.Fields["ChanceNone"] = Convert.ToString(r.GetValue(1), System.Globalization.CultureInfo.InvariantCulture) ?? "";
                    if (!r.IsDBNull(2)) dto.Fields["CalcFlagMode"] = r.GetString(2);
                    result.Add(dto);
                }
            }

            foreach (var dto in result)
            {
                dto.DisplayName = ContainerUnitHelpers.ListName(connection, dto.Key);
                ContainerUnitHelpers.AnnotateOwnKey(connection, dto);
            }
            return result;
        }

        internal override void FollowKeys(SqliteConnection connection, IReadOnlyList<EditedItemDto> items, ImportKeyMap map, List<string> notes)
        {
            foreach (var dto in items) ContainerUnitHelpers.FollowOwnKey(connection, dto, map, notes, "The chance/flag edit");
        }

        // The list has to be in this load order (or be an own list this import creates - then the
        // probe still says Missing, because nothing is applied yet; see below).
        internal override ImportTarget Probe(SqliteConnection connection, EditedItemDto item)
        {
            bool listHere = ContainerUnitHelpers.OwnerExists(connection, "LeveledList", "Key", item.Key);
            if (!listHere && !UserRecords.IsUserKey(item.Key)) return ImportTarget.Missing;
            if (item.Key == ContainerUnitHelpers.Unresolved) return ImportTarget.Missing;

            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT LastChanged FROM LeveledListEdit WHERE ListKey = @k";
            cmd.Parameters.AddWithValue("@k", item.Key);
            var local = cmd.ExecuteScalar();
            return local == null ? ImportTarget.Untouched : ImportTarget.FromLastChanged(local as string);
        }

        internal override bool Apply(SqliteConnection connection, SqliteTransaction transaction, EditedItemDto item)
        {
            int? chance = int.TryParse(item.Fields.GetValueOrDefault("ChanceNone", ""), out var c) ? Math.Clamp(c, 0, 100) : null;
            string mode = item.Fields.GetValueOrDefault("CalcFlagMode", "");

            // An edit that changes nothing is no row - LeveledListEditStore.Save's rule.
            if (chance == null && (mode.Length == 0 || mode == nameof(CalcFlagMode.Unchanged))) return false;

            // No mode in the file means none was stored - NULL, as LeveledListEditStore writes it for a
            // chance-only edit, not "Unchanged" (which reads the same but is not the same row; found in
            // the user's import test, 2026-10-17).
            ContainerUnitHelpers.Exec(connection, transaction, @"
                INSERT INTO LeveledListEdit (ListKey, ChanceNone, CalcFlagMode, LastChanged) VALUES (@k, @c, @m, @t)
                ON CONFLICT(ListKey) DO UPDATE SET ChanceNone = @c, CalcFlagMode = @m, LastChanged = @t",
                ("@k", item.Key), ("@c", chance),
                ("@m", mode.Length == 0 ? null : mode),
                ("@t", string.IsNullOrEmpty(item.LastChanged) ? ItemDBHandler.NowIso() : item.LastChanged));
            LeveledListEditStore.InvalidateCache();
            return true;
        }
    }

    // ===================================================================
    // Per-owner sets: removed / restored entries
    // ===================================================================

    // One DTO per owner (a list or a container) with that owner's whole set in Rows["Entries"]; the
    // import replaces the set. Same shape as the recipe decisions (RecipeDecisionUnits), same reason:
    // the decision IS the row, there is no flag, and "use the file version" means the file's set.
    internal sealed class OwnerSetUnit : ExportUnit
    {
        internal const string RowsName = "Entries";

        private readonly string _table;
        private readonly string _dbTable;
        private readonly string _ownerColumn;
        private readonly string[] _columns;       // first one is the entry's identity
        private readonly string _ownerTable;
        private readonly string _ownerKeyColumn;
        private readonly string _what;

        public OwnerSetUnit(string table, string dbTable, string ownerColumn, string[] columns,
                            string ownerTable, string ownerKeyColumn, string what)
        {
            _table = table; _dbTable = dbTable; _ownerColumn = ownerColumn; _columns = columns;
            _ownerTable = ownerTable; _ownerKeyColumn = ownerKeyColumn; _what = what;
        }

        public override string Table => _table;

        private bool OwnerIsList => _ownerTable == "LeveledList";

        internal override List<EditedItemDto> Collect(SqliteConnection connection, ExportScope scope, string? scopeValue)
        {
            var byOwner = new Dictionary<string, EditedItemDto>(StringComparer.OrdinalIgnoreCase);
            if (!UserRecords.TableExists(connection, _dbTable)) return new List<EditedItemDto>();

            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = $"SELECT {_ownerColumn}, LastChanged, {string.Join(", ", _columns)} FROM {_dbTable}"
                                  + ContainerUnitHelpers.ScopeWhere(cmd, _ownerColumn, scope, scopeValue, hasWhere: false)
                                  + $" ORDER BY {_ownerColumn}";
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    string owner = r.GetString(0);
                    string changed = r.IsDBNull(1) ? "" : r.GetString(1);
                    if (!byOwner.TryGetValue(owner, out var dto))
                    {
                        dto = new EditedItemDto
                        {
                            Table = _table, Key = owner, LastChanged = "",
                            Rows = new Dictionary<string, List<Dictionary<string, string>>> { [RowsName] = new() },
                        };
                        byOwner[owner] = dto;
                    }
                    // ISO-8601 sorts as it compares; the newest row dates the set.
                    if (string.CompareOrdinal(changed, dto.LastChanged) > 0) dto.LastChanged = changed;

                    var row = new Dictionary<string, string>();
                    for (int i = 0; i < _columns.Length; i++)
                        row[_columns[i]] = r.IsDBNull(2 + i) ? "" : Convert.ToString(r.GetValue(2 + i), System.Globalization.CultureInfo.InvariantCulture) ?? "";
                    dto.Rows![RowsName].Add(row);
                }
            }

            foreach (var dto in byOwner.Values)
            {
                dto.DisplayName = OwnerIsList ? ContainerUnitHelpers.ListName(connection, dto.Key) : ContainerName(connection, dto.Key);
                if (OwnerIsList) ContainerUnitHelpers.AnnotateOwnKey(connection, dto);
            }
            return byOwner.Values.ToList();
        }

        private static string ContainerName(SqliteConnection connection, string key)
        {
            if (!UserRecords.TableExists(connection, "Container")) return key;
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT Name FROM Container WHERE ContainerKey = @k";
            cmd.Parameters.AddWithValue("@k", key);
            return cmd.ExecuteScalar() as string ?? key;
        }

        internal override void FollowKeys(SqliteConnection connection, IReadOnlyList<EditedItemDto> items, ImportKeyMap map, List<string> notes)
        {
            if (!OwnerIsList) return;
            foreach (var dto in items) ContainerUnitHelpers.FollowOwnKey(connection, dto, map, notes, _what);
        }

        internal override ImportTarget Probe(SqliteConnection connection, EditedItemDto item)
        {
            if (item.Key == ContainerUnitHelpers.Unresolved) return ImportTarget.Missing;
            bool ownerHere = ContainerUnitHelpers.OwnerExists(connection, _ownerTable, _ownerKeyColumn, item.Key)
                             || (OwnerIsList && UserRecords.IsUserKey(item.Key));
            if (!ownerHere) return ImportTarget.Missing;
            if (!UserRecords.TableExists(connection, _dbTable)) return ImportTarget.Untouched;

            using var cmd = connection.CreateCommand();
            cmd.CommandText = $"SELECT MAX(LastChanged) FROM {_dbTable} WHERE {_ownerColumn} = @k";
            cmd.Parameters.AddWithValue("@k", item.Key);
            return ImportTarget.FromLastChanged(cmd.ExecuteScalar() as string);
        }

        internal override bool Apply(SqliteConnection connection, SqliteTransaction transaction, EditedItemDto item)
        {
            if (item.Rows == null || !item.Rows.TryGetValue(RowsName, out var rows) || rows == null) return false;

            string stamp = string.IsNullOrEmpty(item.LastChanged) ? ItemDBHandler.NowIso() : item.LastChanged;
            ContainerUnitHelpers.Exec(connection, transaction, $"DELETE FROM {_dbTable} WHERE {_ownerColumn} = @k", ("@k", item.Key));

            // Last one wins on a repeated entry - the table's key could not hold two.
            var distinct = rows.Where(r => !KeyFactory.IsUnsetKey(r.GetValueOrDefault(_columns[0], "")))
                .GroupBy(r => r[_columns[0]], StringComparer.OrdinalIgnoreCase).Select(g => g.Last());

            foreach (var row in distinct)
            {
                using var cmd = connection.CreateCommand();
                cmd.Transaction = transaction;
                cmd.CommandText = $"INSERT INTO {_dbTable} ({_ownerColumn}, {string.Join(", ", _columns)}, LastChanged) " +
                                  $"VALUES (@owner, {string.Join(", ", _columns.Select((_, i) => $"@c{i}"))}, @t)";
                cmd.Parameters.AddWithValue("@owner", item.Key);
                for (int i = 0; i < _columns.Length; i++)
                {
                    var raw = row.GetValueOrDefault(_columns[i], "");
                    // Level and Count are numbers; a missing one is 1, as everywhere a placement is read.
                    object value = i == 0 ? raw : (int.TryParse(raw, out var n) && n > 0 ? n : 1);
                    cmd.Parameters.AddWithValue($"@c{i}", value);
                }
                cmd.Parameters.AddWithValue("@t", stamp);
                cmd.ExecuteNonQuery();
            }
            return true;
        }
    }

    // ===================================================================
    // Own leveled lists
    // ===================================================================

    // The list's existence, its few properties, and where it hangs (UserLeveledListTarget). Not its
    // contents: those arrive the way they arrive in any list - placements, rules - and travel with
    // them. Identity by Uid; content compared like an NPC group, because LeveledList has no timestamp.
    internal sealed class UserLeveledListUnit : ExportUnit
    {
        internal const string TargetRows = "Targets";
        internal const string TargetUidColumn = "TargetKeyUid";
        private static readonly string[] Props = { "EditorID", "ChanceNone", "Flags", "GlobalKey" };

        public override string Table => "UserLeveledList";

        internal override List<EditedItemDto> Collect(SqliteConnection connection, ExportScope scope, string? scopeValue)
        {
            var result = new List<EditedItemDto>();
            if (!UserRecords.TableExists(connection, "LeveledList")) return result;

            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = $"SELECT Key, {string.Join(", ", Props)} FROM LeveledList WHERE Original = 0"
                                  + ContainerUnitHelpers.ScopeWhere(cmd, "Key", scope, scopeValue, hasWhere: true);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    var dto = new EditedItemDto { Table = Table, Key = r.GetString(0), Original = 0, LastChanged = ItemDBHandler.NowIso() };
                    for (int i = 0; i < Props.Length; i++)
                        dto.Fields[Props[i]] = r.IsDBNull(1 + i) ? "" : Convert.ToString(r.GetValue(1 + i), System.Globalization.CultureInfo.InvariantCulture) ?? "";
                    dto.DisplayName = dto.Fields["EditorID"];
                    result.Add(dto);
                }
            }

            foreach (var dto in result)
            {
                dto.Uid = UserRecords.EnsureUid(connection, "LeveledList", dto.Key);
                dto.Rows = new Dictionary<string, List<Dictionary<string, string>>> { [TargetRows] = ReadTargets(connection, dto.Key) };
                foreach (var target in dto.Rows[TargetRows])
                {
                    var uid = UserRecords.UidForReference(connection, "LeveledList", target["TargetKey"]);
                    if (uid.Length > 0) target[TargetUidColumn] = uid;
                }
            }
            return result;
        }

        private static List<Dictionary<string, string>> ReadTargets(SqliteConnection connection, string listKey)
        {
            var rows = new List<Dictionary<string, string>>();
            if (!UserRecords.TableExists(connection, "UserLeveledListTarget")) return rows;

            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT TargetKey, TargetKind, Level, Count FROM UserLeveledListTarget WHERE ListKey = @k";
            cmd.Parameters.AddWithValue("@k", listKey);
            using var r = cmd.ExecuteReader();
            while (r.Read())
                rows.Add(new Dictionary<string, string>
                {
                    ["TargetKey"] = r.GetString(0),
                    ["TargetKind"] = r.GetString(1),
                    ["Level"] = Convert.ToString(r.GetValue(2), System.Globalization.CultureInfo.InvariantCulture) ?? "1",
                    ["Count"] = Convert.ToString(r.GetValue(3), System.Globalization.CultureInfo.InvariantCulture) ?? "1",
                });
            return rows;
        }

        // Own lists live in their own key range with their own counter (UserLeveledListStore).
        internal override void ResolveKeys(SqliteConnection connection, IReadOnlyList<EditedItemDto> items, ImportKeyMap map)
            => UserRecords.ResolveKeys(connection, "LeveledList", items, map, new[] { "LeveledList" },
                () => UserKeyAllocator.NextListKey(connection));

        // A target that is itself an own list: its key here. Not here at all - the target is left
        // out, with a note; hanging the list into whatever has the file's key would be a guess.
        internal override void FollowKeys(SqliteConnection connection, IReadOnlyList<EditedItemDto> items, ImportKeyMap map, List<string> notes)
        {
            foreach (var dto in items)
            {
                if (dto.Rows == null || !dto.Rows.TryGetValue(TargetRows, out var targets) || targets == null) continue;
                foreach (var target in targets.ToList())
                {
                    if (!target.TryGetValue(TargetUidColumn, out var uid) || string.IsNullOrEmpty(uid)) continue;
                    var local = UserRecords.ResolveReference(connection, "LeveledList", uid, map);
                    if (local != null) { target["TargetKey"] = local; continue; }

                    targets.Remove(target);
                    notes.Add($"⚠ Your list '{dto.DisplayName}' hung in another list of yours that is neither in this export nor in this setup - that link was left out.");
                }
            }
        }

        internal override ImportTarget Probe(SqliteConnection connection, EditedItemDto item)
        {
            if (!UserRecords.TableExists(connection, "LeveledList")) return ImportTarget.Missing;
            if (!ContainerUnitHelpers.OwnerExists(connection, "LeveledList", "Key", item.Key)) return ImportTarget.Create;

            var local = Collect(connection, ExportScope.Item, item.Key).FirstOrDefault();
            return local != null && Canonical(local) == Canonical(item) ? ImportTarget.Identical : ImportTarget.Differs;
        }

        private static string Canonical(EditedItemDto dto) =>
            string.Join("|", Props.Select(p => dto.Fields.GetValueOrDefault(p, "").Trim().ToUpperInvariant()))
            + "|" + string.Join(";", (dto.Rows?.GetValueOrDefault(TargetRows) ?? new List<Dictionary<string, string>>())
                .Select(t => $"{t.GetValueOrDefault("TargetKey", "").ToUpperInvariant()},{t.GetValueOrDefault("TargetKind", "")},{t.GetValueOrDefault("Level", "")},{t.GetValueOrDefault("Count", "")}")
                .OrderBy(s => s, StringComparer.Ordinal));

        internal override bool Apply(SqliteConnection connection, SqliteTransaction transaction, EditedItemDto item)
        {
            string editorId = item.Fields.GetValueOrDefault("EditorID", "").Trim();
            if (editorId.Length == 0) editorId = UserLeveledListStore.EditorIdPrefix + item.Key.Substring(item.Key.IndexOf('|') + 1);
            int chance = int.TryParse(item.Fields.GetValueOrDefault("ChanceNone", ""), out var c) ? Math.Clamp(c, 0, 100) : 0;
            string flags = item.Fields.GetValueOrDefault("Flags", "");
            string global = item.Fields.GetValueOrDefault("GlobalKey", "");
            bool hasUid = UserRecords.HasColumn(connection, "LeveledList", "Uid");

            ContainerUnitHelpers.Exec(connection, transaction, $@"
                INSERT INTO LeveledList (Key, EditorID, ChanceNone, Flags, GlobalKey, Active, Original{(hasUid ? ", Uid" : "")})
                VALUES (@k, @e, @c, @f, @g, 1, 0{(hasUid ? ", @u" : "")})
                ON CONFLICT(Key) DO UPDATE SET EditorID = @e, ChanceNone = @c, Flags = @f, GlobalKey = @g, Active = 1{(hasUid ? ", Uid = @u" : "")}
                WHERE Original = 0",
                ("@k", item.Key), ("@e", editorId), ("@c", chance), ("@f", flags),
                ("@g", global.Length == 0 ? null : global), ("@u", item.Uid));

            if (item.Rows != null && item.Rows.TryGetValue(TargetRows, out var targets) && targets != null
                && UserRecords.TableExists(connection, "UserLeveledListTarget"))
            {
                ContainerUnitHelpers.Exec(connection, transaction, "DELETE FROM UserLeveledListTarget WHERE ListKey = @k", ("@k", item.Key));
                foreach (var t in targets.Where(t => !KeyFactory.IsUnsetKey(t.GetValueOrDefault("TargetKey", "")))
                                         .GroupBy(t => t["TargetKey"], StringComparer.OrdinalIgnoreCase).Select(g => g.Last()))
                {
                    var kind = Enum.TryParse<UserListTargetKind>(t.GetValueOrDefault("TargetKind", ""), out var k) ? k : UserListTargetKind.LeveledList;
                    ContainerUnitHelpers.Exec(connection, transaction,
                        "INSERT OR IGNORE INTO UserLeveledListTarget (ListKey, TargetKey, TargetKind, Level, Count) VALUES (@l, @t, @kind, @lv, @n)",
                        ("@l", item.Key), ("@t", t["TargetKey"]), ("@kind", kind.ToString()),
                        ("@lv", int.TryParse(t.GetValueOrDefault("Level", ""), out var lv) && lv > 0 ? lv : 1),
                        ("@n", int.TryParse(t.GetValueOrDefault("Count", ""), out var n) && n > 0 ? n : 1));
                }
            }
            return true;
        }
    }
}
