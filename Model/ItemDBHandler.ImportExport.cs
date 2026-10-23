using Microsoft.Data.Sqlite;
using SkyrimCraftingTool.Services;
using System.Collections.Generic;
using System.Linq;

namespace SkyrimCraftingTool.Model
{
    // Import/Export half of ItemDBHandler: which rows count as edited, what an export file
    // carries, and how an imported file is previewed against the local DB and applied.
    // Split out of ItemDBHandler.cs (~4100 lines) purely for navigability - no logic changed.
    public partial class ItemDBHandler
    {
        // ===================================================
        // Import/Export
        // ===================================================

        private static string SafeStr(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? "" : reader.GetString(ordinal);

        private static string BuildScopeWhere(ExportScope scope, string scopeValue, SqliteCommand cmd)
        {
            switch (scope)
            {
                case ExportScope.Item:
                    cmd.Parameters.AddWithValue("@scopeKey", scopeValue);
                    return " AND Key = @scopeKey";
                case ExportScope.Plugin:
                    cmd.Parameters.AddWithValue("@scopePrefix", scopeValue + "|%");
                    return " AND Key LIKE @scopePrefix";
                default:
                    return "";
            }
        }

        // Whole-database facade over every unit (ExportUnits.All). The tabs call the overloads that
        // take their own unit list - see TabExportModule.
        public List<EditedItemDto> GetEditedItems(ExportScope scope, string scopeValue = null)
            => GetEditedItems(ExportUnits.All, scope, scopeValue);

        public List<EditedItemDto> GetEditedItems(IEnumerable<ExportUnit> units, ExportScope scope, string scopeValue = null)
        {
            var items = new List<EditedItemDto>();
            using var connection = new ItemDbConnection(ConnString);
            connection.Open();

            foreach (var unit in units)
                items.AddRange(unit.Collect(connection, scope, scopeValue));

            return items;
        }

        // Same arrays the reset + import-whitelist paths use — one source of truth per table for
        // "which IsEdited* shadow columns exist" (they used to be spelled out three times).
        internal static List<EditedItemDto> GetEditedArmorOrWeapons(SqliteConnection connection, string table, string[] editedColumns, ExportScope scope, string scopeValue)
        {
            var result = new List<EditedItemDto>();
            using var cmd = connection.CreateCommand();
            var columns = "Key, LastChanged, Name, " + string.Join(", ", editedColumns);
            // IsEdited=1, NOT "LastChanged IS NOT NULL": ResetArmor/WeaponEdits clear IsEdited + every
            // shadow column but leave LastChanged set (it means "ever touched, incl. resets"), so a
            // reset item would otherwise still export / count as edited after a restart. Same fix as
            // GetEditedEnchantments. Every real edit path (UpdateField, ApplyFieldUpdate) sets IsEdited=1.
            cmd.CommandText = $"SELECT {columns} FROM {table} WHERE IsEdited = 1" + BuildScopeWhere(scope, scopeValue, cmd);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                // SafeStr, not GetString: an import can write LastChanged = NULL, which would
                // otherwise blow up the NEXT export with an InvalidCastException.
                var dto = new EditedItemDto { Table = table, Key = reader.GetString(0), LastChanged = SafeStr(reader, 1), DisplayName = SafeStr(reader, 2) };
                for (int i = 0; i < editedColumns.Length; i++)
                {
                    int ordinal = 3 + i;
                    if (!reader.IsDBNull(ordinal))
                        dto.Fields[editedColumns[i]] = reader.GetValue(ordinal).ToString();
                }
                result.Add(dto);
            }
            return result;
        }

        internal static List<EditedItemDto> GetEditedCOBJ(SqliteConnection connection, ExportScope scope, string scopeValue)
        {
            // The last column list in this file that was still written out by hand, and the last
            // place a new editable COBJ field could be forgotten. Both halves come from
            // CobjShadowColumns now: the shadow columns, and the base columns they shadow
            // ("IsEditedName" -> "Name"), which a user-created row exports in place of the diff.
            // ImportExportRoundTripTests walks the same array, so a fifth field that never reaches
            // this SELECT fails the build instead of quietly vanishing on export.
            string[] shadowCols = CobjShadowColumns;
            string[] baseCols = shadowCols.Select(c => c.Substring("IsEdited".Length)).ToArray();

            // Key, LastChanged, Original, ConditionsEdited, Name (for display), then the two blocks.
            const int displayNameOrdinal = 4;
            int baseOffset = displayNameOrdinal + 1;
            int shadowOffset = baseOffset + baseCols.Length;

            var rows = new List<(string Key, bool ConditionsEdited, EditedItemDto Dto)>();
            using (var cmd = connection.CreateCommand())
            {
                // Same LastChanged-vs-flag rationale as GetEditedArmorOrWeapons. ResetCOBJEdits /
                // ResetCOBJConditions clear IsEdited / ConditionsEdited but leave LastChanged set.
                // Original=0 (user-created) is always an edit — it's new content that must export
                // even if some path ever cleared its IsEdited (a reset of a user recipe deletes the
                // row, so it can't be a stale pristine Original=0 here).
                cmd.CommandText = $@"SELECT Key, LastChanged, Original, ConditionsEdited, Name,
                        {string.Join(", ", baseCols)},
                        {string.Join(", ", shadowCols)}
                    FROM COBJ WHERE (IsEdited = 1 OR ConditionsEdited = 1 OR Original = 0)" + BuildScopeWhere(scope, scopeValue, cmd);

                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    string key = reader.GetString(0);
                    int original = reader.GetInt32(2);
                    bool conditionsEdited = reader.GetInt32(3) == 1;

                    // SafeStr, not GetString - see GetEditedArmorOrWeapons.
                    var dto = new EditedItemDto { Table = "COBJ", Key = key, LastChanged = SafeStr(reader, 1), Original = original };

                    for (int i = 0; i < shadowCols.Length; i++)
                    {
                        int shadowOrdinal = shadowOffset + i;

                        if (original == 0)
                        {
                            // User-created: no scan to fall back to, so export the full effective row
                            // (shadow value if set, else the base value) rather than just the shadow diff.
                            dto.Fields[baseCols[i]] = reader.IsDBNull(shadowOrdinal)
                                ? SafeStr(reader, baseOffset + i)
                                : reader.GetString(shadowOrdinal);
                        }
                        else if (!reader.IsDBNull(shadowOrdinal))
                        {
                            dto.Fields[shadowCols[i]] = reader.GetString(shadowOrdinal);
                        }
                    }

                    // A created row shows the name it will be given, a scanned one the name it has.
                    dto.DisplayName = original == 0 && dto.Fields.TryGetValue("Name", out var effectiveName)
                        ? effectiveName
                        : SafeStr(reader, displayNameOrdinal);

                    rows.Add((key, conditionsEdited, dto));
                }
            }

            var result = new List<EditedItemDto>();
            foreach (var row in rows)
            {
                if (row.ConditionsEdited)
                    row.Dto.ConditionRows = GetCOBJConditionRowsForExport(connection, row.Key);
                if (row.Dto.Original == 0)
                    row.Dto.Uid = EnsureCobjUid(connection, row.Key);
                result.Add(row.Dto);
            }
            return result;
        }

        // ---------------------------------------------------
        // Identity of a user-created recipe (TE-2) - see UserRecords
        // ---------------------------------------------------

        internal static bool HasColumn(SqliteConnection connection, string table, string column)
            => UserRecords.HasColumn(connection, table, column);

        private static string EnsureCobjUid(SqliteConnection connection, string key)
            => UserRecords.EnsureUid(connection, "COBJ", key);

        internal static void ResolveUserCobjKeys(SqliteConnection connection, IEnumerable<EditedItemDto> items, ImportKeyMap map)
            => UserRecords.ResolveRecordKeys(connection, "COBJ", items, map);

        private static List<Dictionary<string, string>> GetCOBJConditionRowsForExport(SqliteConnection connection, string cobjKey)
        {
            var rows = new List<Dictionary<string, string>>();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT ConditionType, Target, Value, Extra, RunOn, CompareOperator, Flags FROM COBJ_Conditions WHERE COBJKey = @key";
            cmd.Parameters.AddWithValue("@key", cobjKey);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                rows.Add(new Dictionary<string, string>
                {
                    ["ConditionType"] = SafeStr(reader, 0),
                    ["Target"] = SafeStr(reader, 1),
                    ["Value"] = SafeStr(reader, 2),
                    ["Extra"] = SafeStr(reader, 3),
                    ["RunOn"] = SafeStr(reader, 4),
                    ["CompareOperator"] = SafeStr(reader, 5),
                    ["Flags"] = SafeStr(reader, 6),
                });
            }
            return rows;
        }

        internal static List<EditedItemDto> GetEditedEnchantments(SqliteConnection connection, ExportScope scope, string scopeValue)
        {
            var rows = new List<(string Key, bool EffectsEdited, bool Own, EditedItemDto Dto)>();

            // From the shared list, not a second copy of it - see EnchantmentShadowColumns.
            string[] editedCols = EnchantmentShadowColumns;

            using (var cmd = connection.CreateCommand())
            {
                // Flags, not LastChanged: the reset paths clear the flags but leave LastChanged
                // non-null, so LastChanged means "ever touched incl. resets" — wrong for "what to
                // export". Matches the tree's edited badge. E3: KeywordsEdited dropped — worn-
                // restriction-list content is its own export unit (GetEditedWornRestrictionLists).
                //
                // Original = 0 (TE-3): a user-created enchantment is new content and always exports,
                // edited flag or not - created and never touched again is still something the
                // other machine does not have. Same rule as GetEditedCOBJ.
                cmd.CommandText = $@"SELECT Key, LastChanged, EffectsEdited,
                        Name, Original,
                        {string.Join(", ", editedCols)}
                    FROM Enchantments
                    WHERE (IsEdited = 1 OR EffectsEdited = 1 OR Original = 0)" + BuildScopeWhere(scope, scopeValue, cmd);

                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    string key = reader.GetString(0);
                    bool effectsEdited = reader.GetInt32(2) == 1;
                    bool own = !reader.IsDBNull(4) && reader.GetInt64(4) == 0;

                    var dto = new EditedItemDto { Table = "Enchantments", Key = key, LastChanged = SafeStr(reader, 1), DisplayName = SafeStr(reader, 3) };
                    for (int i = 0; i < editedCols.Length; i++)
                    {
                        int ordinal = 5 + i;
                        if (!reader.IsDBNull(ordinal))
                            dto.Fields[editedCols[i]] = reader.GetValue(ordinal).ToString();
                    }

                    rows.Add((key, effectsEdited, own, dto));
                }
            }

            var result = new List<EditedItemDto>();
            foreach (var row in rows)
            {
                if (row.Own)
                {
                    // The whole row, base and shadow alike, and its effects whatever the flag says:
                    // there is no scanned record on the other side to fall back to. See UserRecords.
                    row.Dto.Original = 0;
                    row.Dto.Fields = UserRecords.ReadRow(connection, "Enchantments", row.Key);
                    row.Dto.Uid = UserRecords.EnsureUid(connection, "Enchantments", row.Key);
                    row.Dto.EffectRows = GetEnchantmentEffectRowsForExport(connection, row.Key);
                }
                else if (row.EffectsEdited)
                {
                    row.Dto.EffectRows = GetEnchantmentEffectRowsForExport(connection, row.Key);
                }
                result.Add(row.Dto);
            }
            return result;
        }

        // E3: worn-restriction-list content edits export as their own unit, keyed by ListKey. One
        // DTO per edited FLST (WornRestrictionListState.IsEdited=1) — not one per enchantment that
        // points at it.
        internal static List<EditedItemDto> GetEditedWornRestrictionLists(SqliteConnection connection, ExportScope scope, string scopeValue)
        {
            // Scope by plugin/item filters Armor/Weapon/COBJ/Enchantment keys; an FLST list key
            // doesn't fit that model, so edited lists are only included for a full ("All") export.
            if (scope != ExportScope.All)
                return new List<EditedItemDto>();

            var listKeys = new List<(string Key, string LastChanged)>();
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "SELECT ListKey, LastChanged FROM WornRestrictionListState WHERE IsEdited = 1";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                    listKeys.Add((reader.GetString(0), SafeStr(reader, 1)));
            }

            var result = new List<EditedItemDto>();
            foreach (var (key, lastChanged) in listKeys)
            {
                result.Add(new EditedItemDto
                {
                    Table = "WornRestrictionList",
                    Key = key,
                    LastChanged = lastChanged,
                    DisplayName = key,
                    WornRestrictionKeywords = GetWornRestrictionKeywordsForExport(connection, key),
                });
            }
            return result;
        }

        private static List<Dictionary<string, string>> GetEnchantmentEffectRowsForExport(SqliteConnection connection, string enchantmentKey)
        {
            var rows = new List<Dictionary<string, string>>();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT MagicEffectKey, EditorID, Name, Magnitude, Duration, Area FROM EnchantmentEffects WHERE EnchantmentKey = @key";
            cmd.Parameters.AddWithValue("@key", enchantmentKey);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                rows.Add(new Dictionary<string, string>
                {
                    ["MagicEffectKey"] = SafeStr(reader, 0),
                    ["EditorID"] = SafeStr(reader, 1),
                    ["Name"] = SafeStr(reader, 2),
                    ["Magnitude"] = reader.IsDBNull(3) ? "0" : reader.GetValue(3).ToString(),
                    ["Duration"] = reader.IsDBNull(4) ? "0" : reader.GetValue(4).ToString(),
                    ["Area"] = reader.IsDBNull(5) ? "0" : reader.GetValue(5).ToString(),
                });
            }
            return rows;
        }

        private static List<string> GetWornRestrictionKeywordsForExport(SqliteConnection connection, string listKey)
        {
            var list = new List<string>();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT KeywordKey FROM WornRestrictionKeywords WHERE ListKey = @key";
            cmd.Parameters.AddWithValue("@key", listKey);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                list.Add(reader.GetString(0));
            return list;
        }

        // Real DateTime comparison (not string comparison) so minor formatting differences between
        // export runs can't cause a false conflict/mismatch — see plan risk 1.
        //
        // TryCompareIso, not a throwing Parse: `a` is a timestamp straight out of an imported file
        // (untrusted / hand-editable). A blank or malformed value used to throw out of
        // PreviewImport and abort the ENTIRE "Import All" run instead of skipping the one bad file.
        private static bool TryCompareIso(string a, string b, out int comparison)
        {
            comparison = 0;
            if (!TryParseIso(a, out var da) || !TryParseIso(b, out var db))
                return false;
            comparison = da.CompareTo(db);
            return true;
        }

        private static bool TryParseIso(string value, out DateTime parsed) =>
            DateTime.TryParse(value, null, System.Globalization.DateTimeStyles.RoundtripKind, out parsed);

        // Unit lookup by DTO.Table. The table name comes straight from an imported file - an untrusted
        // or corrupted one - and a unit interpolates its own table into SQL. Only a Table that maps to
        // one of the caller's units ever reaches a query; everything else is dropped here, which is
        // also what keeps a tab's import from touching another tab's records.
        private static Dictionary<string, ExportUnit> UnitsByTable(IEnumerable<ExportUnit> units)
        {
            var map = new Dictionary<string, ExportUnit>(StringComparer.Ordinal);
            foreach (var unit in units)
                map[unit.Table] = unit;
            return map;
        }

        // Whole-database facade over every unit (ExportUnits.All).
        public ImportPlan PreviewImport(List<EditedItemDto> fileItems)
            => PreviewImport(ExportUnits.All, fileItems);

        public ImportPlan PreviewImport(IEnumerable<ExportUnit> units, List<EditedItemDto> fileItems)
        {
            var byTable = UnitsByTable(units);
            var plan = new ImportPlan();
            using var connection = new ItemDbConnection(ConnString);
            connection.Open();

            // Keys first, all units, then references - see ExportUnit.ResolveKeys / FollowKeys.
            // Before the probe on purpose: a user-created recipe whose Key belongs to someone
            // else's recipe here must be probed (and later applied) under its new Key.
            var perUnit = fileItems
                .Where(i => i != null && byTable.ContainsKey(i.Table ?? ""))
                .GroupBy(i => byTable[i.Table])
                .ToList();
            var keyMap = new ImportKeyMap();
            foreach (var group in perUnit)
                group.Key.ResolveKeys(connection, group.ToList(), keyMap);
            foreach (var group in perUnit)
                group.Key.FollowKeys(connection, group.ToList(), keyMap, plan.Notes);

            foreach (var item in fileItems)
            {
                if (item == null || !byTable.TryGetValue(item.Table ?? "", out var unit))
                    continue;

                var target = unit.Probe(connection, item);
                switch (target.State)
                {
                    case ImportTargetState.Missing:
                        // Nothing to build a real row from - usually the owning plugin isn't
                        // installed (see plan risk).
                        plan.ToSkipMissing.Add(item);
                        continue;

                    case ImportTargetState.Create:
                    case ImportTargetState.Untouched:
                        // Nothing local to conflict with.
                        plan.ToApply.Add(item);
                        continue;

                    // No timestamp on either side - the unit compared content (TE-5).
                    case ImportTargetState.Identical:
                        plan.ToSkipEqual.Add(item);
                        continue;
                    case ImportTargetState.Differs:
                        plan.Conflicts.Add(new ImportConflict { FileItem = item, LocalLastChanged = "(differs, no date)" });
                        continue;
                }

                if (!TryCompareIso(item.LastChanged, target.LocalLastChanged, out int cmp))
                {
                    // Corrupt/blank timestamp in the file — skip just this item instead of throwing
                    // out of the whole import run.
                    plan.ToSkipInvalid.Add(item);
                    continue;
                }

                if (cmp == 0)
                    plan.ToSkipEqual.Add(item);
                else if (cmp > 0)
                    plan.ToApply.Add(item);
                else
                    plan.Conflicts.Add(new ImportConflict { FileItem = item, LocalLastChanged = target.LocalLastChanged });
            }

            return plan;
        }

        // Whole-database facade over every unit (ExportUnits.All).
        public ImportResult ApplyImport(ImportPlan plan, HashSet<string> conflictKeysToUseFileVersion)
            => ApplyImport(ExportUnits.All, plan, conflictKeysToUseFileVersion);

        public ImportResult ApplyImport(IEnumerable<ExportUnit> units, ImportPlan plan, HashSet<string> conflictKeysToUseFileVersion)
        {
            var byTable = UnitsByTable(units);
            var result = new ImportResult
            {
                SkippedEqual = plan.ToSkipEqual.Count,
                SkippedMissing = plan.ToSkipMissing,
                SkippedInvalid = plan.ToSkipInvalid.Count,
                Notes = plan.Notes
            };

            var toWrite = new List<EditedItemDto>(plan.ToApply);
            foreach (var conflict in plan.Conflicts)
            {
                if (conflictKeysToUseFileVersion.Contains(conflict.FileItem.Table + "|" + conflict.FileItem.Key))
                {
                    toWrite.Add(conflict.FileItem);
                    result.ConflictsUsedFile++;
                }
                else
                {
                    result.ConflictsKeptLocal++;
                }
            }

            using (var connection = new ItemDbConnection(ConnString))
            {
                connection.Open();
                using var transaction = connection.BeginTransaction();

                foreach (var item in toWrite)
                {
                    // Only count what actually wrote something — a missing target row, a
                    // payload-less DTO or an unknown Table used to still bump "Updated: N".
                    if (byTable.TryGetValue(item.Table ?? "", out var unit)
                        && unit.Apply(connection, transaction, item))
                        result.Applied++;
                }

                transaction.Commit();
            }

            // The in-memory caches (armor/weapon/COBJ/enchantment lists this handler serves to the
            // rest of the app) are now stale — force the next read to reload from the DB, same as
            // PutIntoDataBank already does after a rescan.
            InvalidateCache();

            return result;
        }

        // E3: rewrite one FLST's member rows from the imported payload and mark the list edited.
        // Snapshots the pristine members into _Original first if this is the list's first local edit
        // (same lazy-snapshot rule as SaveWornRestrictionKeywords), so a later Reset still works.
        internal static bool ApplyWornRestrictionListItem(SqliteConnection connection, SqliteTransaction transaction, EditedItemDto item)
        {
            if (KeyFactory.IsUnsetKey(item.Key) || item.WornRestrictionKeywords == null)
                return false;

            SnapshotChildRowsIfFirstEdit(connection, transaction, ChildTables.WornRestrictionKeywords, item.Key);

            using (var deleteCmd = connection.CreateCommand())
            {
                deleteCmd.Transaction = transaction;
                deleteCmd.CommandText = "DELETE FROM WornRestrictionKeywords WHERE ListKey = @key";
                deleteCmd.Parameters.AddWithValue("@key", item.Key);
                deleteCmd.ExecuteNonQuery();
            }
            foreach (var kw in item.WornRestrictionKeywords)
            {
                using var insertCmd = connection.CreateCommand();
                insertCmd.Transaction = transaction;
                // OR IGNORE: the payload comes from a file and may legitimately repeat a member,
                // but the table has PRIMARY KEY(ListKey, KeywordKey) - a plain INSERT would throw
                // and roll back the WHOLE import transaction, taking every other item with it.
                // "First one wins" matches how the scan path dedupes its own rows.
                insertCmd.CommandText = "INSERT OR IGNORE INTO WornRestrictionKeywords (ListKey, KeywordKey) VALUES (@key, @kw)";
                insertCmd.Parameters.AddWithValue("@key", item.Key);
                insertCmd.Parameters.AddWithValue("@kw", kw);
                insertCmd.ExecuteNonQuery();
            }

            using (var stateCmd = connection.CreateCommand())
            {
                stateCmd.Transaction = transaction;
                stateCmd.CommandText =
                    @"INSERT INTO WornRestrictionListState (ListKey, IsEdited, LastChanged) VALUES (@key, 1, @now)
                      ON CONFLICT(ListKey) DO UPDATE SET IsEdited = 1, LastChanged = @now";
                stateCmd.Parameters.AddWithValue("@key", item.Key);
                stateCmd.Parameters.AddWithValue("@now", string.IsNullOrEmpty(item.LastChanged) ? NowIso() : item.LastChanged);
                stateCmd.ExecuteNonQuery();
            }

            return true;
        }

        private static bool RowExists(SqliteConnection connection, SqliteTransaction transaction, string table, string key)
        {
            using var cmd = connection.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = $"SELECT COUNT(*) FROM {table} WHERE Key = @key";
            cmd.Parameters.AddWithValue("@key", key);
            return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
        }

        // Field-name whitelist per table. item.Fields keys come straight out of an imported JSON file
        // — an untrusted or corrupted one — and would otherwise be interpolated directly into the
        // UPDATE's SET clause below (SQLite parameters can only bind values, not column names). Any
        // key not on this list is silently dropped instead of reaching the SQL text.
        //
        // Built from the same *ShadowColumns arrays the reset + export paths use, so a new shadow
        // column can't be added to one place and forgotten in the other two. Lazily initialised on
        // purpose: static field initialisers run in textual declaration order, which is unspecified
        // once this class is split across partial-class files — a plain initialiser could otherwise
        // read the arrays before they exist.
        private static Dictionary<string, HashSet<string>> _allowedImportFields;
        private static Dictionary<string, HashSet<string>> AllowedImportFields =>
            _allowedImportFields ??= new()
            {
                ["Armor"] = new HashSet<string>(ArmorShadowColumns),
                ["Weapons"] = new HashSet<string>(WeaponShadowColumns),
                ["WorldItem"] = new HashSet<string>(WorldItemShadowColumns),
                ["COBJ"] = new HashSet<string>(CobjShadowColumns),
                ["Enchantments"] = new HashSet<string>(EnchantmentShadowColumns),
                ["Spells"] = new HashSet<string>(SpellShadowColumns),
                // E3: FLST content import unit — no scalar fields, its payload is
                // WornRestrictionKeywords. Which tables are importable at all is decided by the
                // unit list (UnitsByTable), not by this dictionary.
                ["WornRestrictionList"] = new HashSet<string>(),
            };

        // Returns true if an UPDATE actually ran.
        private static bool ApplyFieldUpdate(SqliteConnection connection, SqliteTransaction transaction, string table, string key, string lastChanged, Dictionary<string, string> fields)
        {
            var allowed = AllowedImportFields[table];

            using var cmd = connection.CreateCommand();
            cmd.Transaction = transaction;
            var setClauses = new List<string>();
            int i = 0;
            foreach (var kv in fields)
            {
                if (!allowed.Contains(kv.Key))
                    continue;
                var p = $"@f{i++}";
                setClauses.Add($"{kv.Key} = {p}");
                cmd.Parameters.AddWithValue(p, kv.Value);
            }

            // No applicable field in the payload -> nothing to import. Without this, a payload-less
            // DTO (e.g. from an old export of a since-reset row) still ran
            // "UPDATE ... SET IsEdited = 1, LastChanged = now", spuriously marking the row edited.
            if (setClauses.Count == 0)
                return false;

            setClauses.Insert(0, "IsEdited = 1");
            setClauses.Add("LastChanged = @now");
            cmd.CommandText = $"UPDATE {table} SET {string.Join(", ", setClauses)} WHERE Key = @key";
            cmd.Parameters.AddWithValue("@now", lastChanged);
            cmd.Parameters.AddWithValue("@key", key);
            return cmd.ExecuteNonQuery() > 0;
        }

        internal static bool ApplySimpleFieldItem(SqliteConnection connection, SqliteTransaction transaction, string table, EditedItemDto item)
        {
            // Preview already routed missing keys to ToSkipMissing — this is just a defensive guard.
            if (!RowExists(connection, transaction, table, item.Key))
                return false;

            return ApplyFieldUpdate(connection, transaction, table, item.Key, item.LastChanged, item.Fields);
        }

        internal static bool ApplyCobjItem(SqliteConnection connection, SqliteTransaction transaction, EditedItemDto item)
        {
            bool exists = RowExists(connection, transaction, "COBJ", item.Key);
            bool wrote = false;

            // The output count was exported for a user-created row from the start (it is one of
            // CobjShadowColumns' base columns) but never written back - every imported own recipe
            // came out producing one. Absent in older files -> 1, the column's default.
            object createdCount = int.TryParse(item.Fields.GetValueOrDefault("CreatedObjectCount", ""), out var n) && n > 0 ? n : 1;
            bool hasUid = !string.IsNullOrEmpty(item.Uid) && HasColumn(connection, "COBJ", "Uid");

            if (item.Original == 0 && !exists)
            {
                using var insertCmd = connection.CreateCommand();
                insertCmd.Transaction = transaction;
                insertCmd.CommandText = $@"INSERT INTO COBJ (Key, Original, Name, CreatedItem, CreatedObjectCount, WorkbenchKeyword, Ingredients, IsEdited, LastChanged{(hasUid ? ", Uid" : "")})
                                           VALUES (@key, 0, @name, @created, @count, @wbk, @ingr, 1, @now{(hasUid ? ", @uid" : "")})";
                insertCmd.Parameters.AddWithValue("@key", item.Key);
                insertCmd.Parameters.AddWithValue("@name", item.Fields.GetValueOrDefault("Name", ""));
                insertCmd.Parameters.AddWithValue("@created", item.Fields.GetValueOrDefault("CreatedItem", ""));
                insertCmd.Parameters.AddWithValue("@count", createdCount);
                insertCmd.Parameters.AddWithValue("@wbk", item.Fields.GetValueOrDefault("WorkbenchKeyword", ""));
                insertCmd.Parameters.AddWithValue("@ingr", item.Fields.GetValueOrDefault("Ingredients", ""));
                insertCmd.Parameters.AddWithValue("@now", item.LastChanged);
                if (hasUid) insertCmd.Parameters.AddWithValue("@uid", item.Uid);
                insertCmd.ExecuteNonQuery();
                wrote = true;
            }
            else if (!exists)
            {
                return false; // Original==1 but missing locally — Preview routes this to ToSkipMissing.
            }
            else if (item.Original == 0)
            {
                // Existing user-created recipe: overwrite base columns directly — a rescan never
                // touches Original=0 rows, so there is no shadow/base split to preserve here.
                using var updateCmd = connection.CreateCommand();
                updateCmd.Transaction = transaction;
                // Count only when the file has one: an export older than the column must not reset a
                // local recipe that produces five back to one.
                bool hasCount = item.Fields.ContainsKey("CreatedObjectCount");
                updateCmd.CommandText = $@"UPDATE COBJ SET Name = @name, CreatedItem = @created{(hasCount ? ", CreatedObjectCount = @count" : "")}, WorkbenchKeyword = @wbk,
                                           Ingredients = @ingr, IsEdited = 1, LastChanged = @now{(hasUid ? ", Uid = @uid" : "")} WHERE Key = @key";
                updateCmd.Parameters.AddWithValue("@name", item.Fields.GetValueOrDefault("Name", ""));
                updateCmd.Parameters.AddWithValue("@created", item.Fields.GetValueOrDefault("CreatedItem", ""));
                updateCmd.Parameters.AddWithValue("@count", createdCount);
                if (hasUid) updateCmd.Parameters.AddWithValue("@uid", item.Uid);
                updateCmd.Parameters.AddWithValue("@wbk", item.Fields.GetValueOrDefault("WorkbenchKeyword", ""));
                updateCmd.Parameters.AddWithValue("@ingr", item.Fields.GetValueOrDefault("Ingredients", ""));
                updateCmd.Parameters.AddWithValue("@now", item.LastChanged);
                updateCmd.Parameters.AddWithValue("@key", item.Key);
                wrote = updateCmd.ExecuteNonQuery() > 0;
            }
            else
            {
                wrote = ApplyFieldUpdate(connection, transaction, "COBJ", item.Key, item.LastChanged, item.Fields);
            }

            if (item.ConditionRows != null)
            {
                // An import IS an edit of these child rows, so it owes the same snapshot every other
                // writer takes. Without it Reset later sees ConditionsEdited = 1, passes its guard,
                // deletes the live rows and restores from an empty COBJ_Conditions_Original -
                // leaving the recipe with no conditions at all. This was the last of the three
                // child-row writers still missing it; see docs/ImportExport-Analyse.md, B2.
                SnapshotChildRowsIfFirstEdit(connection, transaction, ChildTables.CobjConditions, item.Key);

                using (var deleteCmd = connection.CreateCommand())
                {
                    deleteCmd.Transaction = transaction;
                    deleteCmd.CommandText = "DELETE FROM COBJ_Conditions WHERE COBJKey = @key";
                    deleteCmd.Parameters.AddWithValue("@key", item.Key);
                    deleteCmd.ExecuteNonQuery();
                }
                foreach (var cond in item.ConditionRows)
                {
                    using var insertCmd = connection.CreateCommand();
                    insertCmd.Transaction = transaction;
                    insertCmd.CommandText = @"INSERT INTO COBJ_Conditions (COBJKey, ConditionType, Target, Value, Extra, RunOn, CompareOperator, Flags)
                                               VALUES (@key, @type, @target, @value, @extra, @runOn, @op, @flags)";
                    insertCmd.Parameters.AddWithValue("@key", item.Key);
                    insertCmd.Parameters.AddWithValue("@type", cond.GetValueOrDefault("ConditionType", ""));
                    insertCmd.Parameters.AddWithValue("@target", cond.GetValueOrDefault("Target", ""));
                    insertCmd.Parameters.AddWithValue("@value", cond.GetValueOrDefault("Value", ""));
                    insertCmd.Parameters.AddWithValue("@extra", cond.GetValueOrDefault("Extra", ""));
                    insertCmd.Parameters.AddWithValue("@runOn", cond.GetValueOrDefault("RunOn", ""));
                    // Absent in export files written before these columns existed -> "" -> the ESP
                    // builder falls back to its per-type guess, i.e. the old behaviour.
                    insertCmd.Parameters.AddWithValue("@op", cond.GetValueOrDefault("CompareOperator", ""));
                    insertCmd.Parameters.AddWithValue("@flags", cond.GetValueOrDefault("Flags", ""));
                    insertCmd.ExecuteNonQuery();
                }
                using (var flagCmd = connection.CreateCommand())
                {
                    flagCmd.Transaction = transaction;
                    // LastChanged too - see ApplySpellItem: a record whose only edit is its child rows
                    // must not arrive without a timestamp.
                    flagCmd.CommandText = "UPDATE COBJ SET ConditionsEdited = 1, LastChanged = @t WHERE Key = @key";
                    flagCmd.Parameters.AddWithValue("@key", item.Key);
                    flagCmd.Parameters.AddWithValue("@t", string.IsNullOrEmpty(item.LastChanged) ? NowIso() : item.LastChanged);
                    flagCmd.ExecuteNonQuery();
                }
                wrote = true;
            }

            return wrote;
        }

        internal static bool ApplyEnchantmentItem(SqliteConnection connection, SqliteTransaction transaction, EditedItemDto item)
        {
            bool wrote;
            if (item.Original == 0)
            {
                // TE-3: a user-created enchantment is recreated from its whole row (UserRecords) -
                // created here if this machine does not have it, under the Key ResolveKeys settled.
                wrote = UserRecords.WriteRow(connection, transaction, "Enchantments", item, item.LastChanged);
                if (!wrote) return false;
            }
            else
            {
                // Preview already routed missing keys to ToSkipMissing — this is just a defensive guard.
                if (!RowExists(connection, transaction, "Enchantments", item.Key))
                    return false;

                wrote = ApplyFieldUpdate(connection, transaction, "Enchantments", item.Key, item.LastChanged, item.Fields);
            }

            if (item.EffectRows != null)
            {
                // DATA LOSS IF THIS IS MISSING, and it was.
                //
                // An import is an effect edit like any other: it deletes the live rows and writes
                // its own, then sets EffectsEdited = 1. But it used to skip the snapshot that
                // SaveEnchantmentEffects takes on the first edit - so Reset later saw
                // EffectsEdited = 1, passed its guard, deleted the live rows and restored from an
                // EMPTY EnchantmentEffects_Original. The enchantment came back with no effects at
                // all, and nothing short of a rescan brought them back.
                //
                SnapshotChildRowsIfFirstEdit(connection, transaction, ChildTables.EnchantmentEffects, item.Key);

                using (var deleteCmd = connection.CreateCommand())
                {
                    deleteCmd.Transaction = transaction;
                    deleteCmd.CommandText = "DELETE FROM EnchantmentEffects WHERE EnchantmentKey = @key";
                    deleteCmd.Parameters.AddWithValue("@key", item.Key);
                    deleteCmd.ExecuteNonQuery();
                }
                foreach (var eff in item.EffectRows)
                {
                    using var insertCmd = connection.CreateCommand();
                    insertCmd.Transaction = transaction;
                    // OR IGNORE for the same reason as ApplyWornRestrictionListItem: PRIMARY KEY
                    // (EnchantmentKey, MagicEffectKey) can't hold a repeated base effect, and a
                    // constraint violation here would roll back the entire import transaction.
                    insertCmd.CommandText = @"INSERT OR IGNORE INTO EnchantmentEffects (EnchantmentKey, MagicEffectKey, EditorID, Name, Magnitude, Duration, Area)
                                               VALUES (@key, @mgef, @editorId, @name, @magnitude, @duration, @area)";
                    insertCmd.Parameters.AddWithValue("@key", item.Key);
                    insertCmd.Parameters.AddWithValue("@mgef", eff.GetValueOrDefault("MagicEffectKey", ""));
                    insertCmd.Parameters.AddWithValue("@editorId", eff.GetValueOrDefault("EditorID", ""));
                    insertCmd.Parameters.AddWithValue("@name", eff.GetValueOrDefault("Name", ""));
                    insertCmd.Parameters.AddWithValue("@magnitude", eff.GetValueOrDefault("Magnitude", "0"));
                    insertCmd.Parameters.AddWithValue("@duration", eff.GetValueOrDefault("Duration", "0"));
                    insertCmd.Parameters.AddWithValue("@area", eff.GetValueOrDefault("Area", "0"));
                    insertCmd.ExecuteNonQuery();
                }
                using (var flagCmd = connection.CreateCommand())
                {
                    flagCmd.Transaction = transaction;
                    // LastChanged too - see ApplySpellItem: a record whose only edit is its child rows
                    // must not arrive without a timestamp.
                    flagCmd.CommandText = "UPDATE Enchantments SET EffectsEdited = 1, LastChanged = @t WHERE Key = @key";
                    flagCmd.Parameters.AddWithValue("@key", item.Key);
                    flagCmd.Parameters.AddWithValue("@t", string.IsNullOrEmpty(item.LastChanged) ? NowIso() : item.LastChanged);
                    flagCmd.ExecuteNonQuery();
                }
                wrote = true;
            }

            // E3: worn-restriction-list content is no longer carried on the Enchantments DTO — it
            // imports as its own Table="WornRestrictionList" unit (ApplyWornRestrictionListItem). A
            // stale payload on an old Enchantments export is ignored here on purpose.
            return wrote;
        }
    }
}
