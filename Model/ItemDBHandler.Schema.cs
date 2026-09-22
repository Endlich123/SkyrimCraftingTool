using Microsoft.Data.Sqlite;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using SkyrimCraftingTool.Services;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace SkyrimCraftingTool.Model
{
    // Schema half of ItemDBHandler: CREATE TABLE IF NOT EXISTS, the ADD COLUMN migrations, the one-shot repair sweeps, and the prepared upsert/insert command factories.
    // Split out of ItemDBHandler.cs purely for navigability - no logic changed.
    public partial class ItemDBHandler
    {
        // CreateTables() only CREATE TABLE IF NOT EXISTS's; this method ALTER TABLEs in any column
        // a table is still missing, via ADD COLUMN with a DEFAULT. Idempotent, safe on every scan —
        // deliberately not a DROP+rebuild, since that would wipe every IsEdited*/Original value (the
        // only place manual user edits live) and destroy user-created COBJ recipes (Original=0,
        // never present in any scanned plugin) on every single rescan.
        // Internal seam for the schema tests: a column added to a table that already exists only
        // ever arrives through here, and forgetting it fails on a user.s database, not in a build.
        internal static void EnsureSchemaForTests(SqliteConnection connection) => EnsureSchema(connection);

        private static void EnsureSchema(SqliteConnection connection)
        {
            AddColumnIfMissing(connection, "Armor", "Active", "INTEGER NOT NULL DEFAULT 1");
            AddColumnIfMissing(connection, "Weapons", "Active", "INTEGER NOT NULL DEFAULT 1");
            AddColumnIfMissing(connection, "COBJ", "Active", "INTEGER NOT NULL DEFAULT 1");
            AddColumnIfMissing(connection, "COBJ", "ConditionsEdited", "INTEGER NOT NULL DEFAULT 0");
            AddColumnIfMissing(connection, "Armor", "ArmorType", "TEXT");
            AddColumnIfMissing(connection, "Armor", "IsEditedArmorType", "TEXT");
            AddColumnIfMissing(connection, "COBJ_Conditions", "CompareOperator", "TEXT");
            AddColumnIfMissing(connection, "COBJ_Conditions", "Flags", "TEXT");
            AddColumnIfMissing(connection, "COBJ_Conditions_Original", "CompareOperator", "TEXT");
            AddColumnIfMissing(connection, "COBJ_Conditions_Original", "Flags", "TEXT");
            AddColumnIfMissing(connection, "Enchantments", "Active", "INTEGER NOT NULL DEFAULT 1");
            AddColumnIfMissing(connection, "Enchantments", "EffectsEdited", "INTEGER NOT NULL DEFAULT 0");
            AddColumnIfMissing(connection, "Enchantments", "KeywordsEdited", "INTEGER NOT NULL DEFAULT 0");
            // BaseEnchantment (ENCH inherited from) — read-only scan value, drives the "derived" tree
            // tag + base-only filter. No shadow column: not user-editable.
            AddColumnIfMissing(connection, "Enchantments", "BaseEnchantmentKey", "TEXT");
            // Original = 0 means "the user made this one" — it exists in no plugin, and the generated
            // ESP is what brings it into the game. Same flag and the same meaning as COBJ's, including
            // the consequence: the scan must never mark such a row inactive, because no plugin will
            // ever produce it again (see MarkInactiveExcept's extraWhere in the scan).
            AddColumnIfMissing(connection, "Enchantments", "Original", "INTEGER NOT NULL DEFAULT 1");

            // The remaining ENIT fields. They stay NULL on an existing database until the next scan
            // fills them - the scan is the only writer of the base columns, and there is no value to
            // invent for a record nobody has re-read yet.
            AddColumnIfMissing(connection, "Enchantments", "EnchantType", "TEXT");
            AddColumnIfMissing(connection, "Enchantments", "Flags", "INTEGER");
            AddColumnIfMissing(connection, "Enchantments", "ChargeTime", "REAL");
            AddColumnIfMissing(connection, "Enchantments", "EnchantmentAmount", "INTEGER");
            AddColumnIfMissing(connection, "Enchantments", "IsEditedFlags", "INTEGER");
            AddColumnIfMissing(connection, "Enchantments", "IsEditedChargeTime", "REAL");
            AddColumnIfMissing(connection, "Enchantments", "IsEditedEnchantmentAmount", "INTEGER");
            AddColumnIfMissing(connection, "Container", "Active", "INTEGER NOT NULL DEFAULT 1");
            AddColumnIfMissing(connection, "MagicEffects", "Active", "INTEGER NOT NULL DEFAULT 1");

            // LastChanged/LastPatched are only meaningful on the 4 tables with live IsEdited* tracking.
            // LastChanged is set by every user-edit write path (UpdateField, SaveCOBJConditions,
            // SaveEnchantmentEffects, SaveWornRestrictionKeywords, CreateNewCOBJRecordForItem) and is
            // deliberately excluded from the UPSERT column lists below so a rescan never touches it.
            // LastPatched is a schema placeholder for a future patch-export feature — not written yet.
            foreach (var table in new[] { "Armor", "Weapons", "COBJ", "Enchantments" })
            {
                AddColumnIfMissing(connection, table, "LastChanged", "TEXT");
                AddColumnIfMissing(connection, table, "LastPatched", "TEXT");
            }

            // ContainerString used to be written directly to the base column (UpdateArmor/
            // WeaponContainerString), bypassing the shadow-column protection every other editable
            // field has — it never set IsEdited/LastChanged, so a container assignment alone never
            // marked the item as "edited" and Import/Export silently never saw it. Brought in line
            // with the rest of Armor/Weapons here.
            AddColumnIfMissing(connection, "Armor", "IsEditedContainerString", "TEXT");
            AddColumnIfMissing(connection, "Weapons", "IsEditedContainerString", "TEXT");

            // WHICH enchantment an item wears (Prio 7). Scanned into the base column, edited through
            // the shadow one like every other editable field - ContainerString is the cautionary tale
            // here: it wrote the base column directly for a while, so a container assignment never
            // marked the item as edited and Import/Export never saw it.
            foreach (var table in new[] { "Armor", "Weapons" })
            {
                AddColumnIfMissing(connection, table, "ObjectEffectKey", "TEXT");
                AddColumnIfMissing(connection, table, "IsEditedObjectEffectKey", "TEXT");
            }

            RepairBlankWornRestrictionEdits(connection);

            // After the repairs above: those run plain UPDATE/DELETE on the existing tables, and
            // this rebuilds some of them. Rebuilding first would just make them do the same work on
            // a freshly copied table.
            NormalizeKeyCaseCollation(connection);
        }

        // A pre-fix bug: an FLST-less enchantment got WornRestrictionListKey = "Null|000000" (the
        // string form of FormKey.Null) instead of "". ~1100 enchantments then shared that value, so
        // toggling / resetting one's worn-restriction keywords ran
        // "UPDATE Enchantments SET KeywordsEdited=1/0, LastChanged=now WHERE WornRestrictionListKey =
        // 'Null|000000'" - mass-touching them all. Clean it: normalize the bad key to "", drop the
        // false KeywordsEdited flag + orphan keyword rows, and clear LastChanged on rows with no
        // actual edit. Idempotent. (A rescan also fixes the base column via the upsert; this covers
        // a plain launch without one.)
        private static void RepairBlankWornRestrictionEdits(SqliteConnection connection)
        {
            // Nothing to repair on a brand-new database: this runs (via EnsureSchema) BEFORE
            // CreateTables, so none of these tables exist yet. Without the early-out the whole batch
            // throws on the first missing table and every fresh install writes an alarming-looking
            // ERROR into the log - noise in exactly the place where a new user's bug report is read.
            if (!TableExists(connection, "Enchantments") ||
                !TableExists(connection, "WornRestrictionKeywords"))
            {
                return;
            }

            // Both halves of "is any field edit active on this row", built from the SHARED array and
            // never written out by hand. A hand-written subset is exactly how this went wrong: the
            // rules below listed five of the eight shadow columns, so a row whose only edit was
            // Flags, ChargeTime or EnchantmentAmount looked untouched to this repair - which then
            // cleared its IsEdited flag and its timestamp on EVERY app start. The value stayed in
            // its shadow column but became invisible everywhere it counts: no badge in the tree,
            // nothing for the export to find, and an imported value quietly disowned the next time
            // the app opened. That is the "flags gehen nicht" report, and it was the sixth copy of
            // this column list. See docs/ImportExport-Analyse.md (B6).
            var allShadowsNull = string.Join(" AND ", EnchantmentShadowColumns.Select(c => $"{c} IS NULL"));
            var anyShadowSet = string.Join(" OR ", EnchantmentShadowColumns.Select(c => $"{c} IS NOT NULL"));

            try
            {
                using var cmd = connection.CreateCommand();
                cmd.CommandText = $@"
                    -- This repair runs (via EnsureSchema) BEFORE CreateTables, so the E3 state table
                    -- may not exist yet on an old DB - the migration below needs it.
                    CREATE TABLE IF NOT EXISTS WornRestrictionListState (
                        ListKey TEXT PRIMARY KEY COLLATE NOCASE,
                        IsEdited INTEGER NOT NULL DEFAULT 0,
                        LastChanged TEXT
                    );

                    -- Undo the short-lived 'synthetic per-enchantment FLST' experiment: a row whose
                    -- WornRestrictionListKey shadow points at its own Key. Drop its keyword rows and
                    -- edit markers entirely.
                    DELETE FROM WornRestrictionKeywords
                    WHERE ListKey IN (SELECT Key FROM Enchantments WHERE IsEditedWornRestrictionListKey = Key);
                    UPDATE Enchantments SET IsEditedWornRestrictionListKey = NULL, KeywordsEdited = 0
                    WHERE IsEditedWornRestrictionListKey = Key;

                    UPDATE Enchantments SET WornRestrictionListKey = ''
                    WHERE WornRestrictionListKey LIKE 'Null|%'
                       OR WornRestrictionListKey = Key;

                    -- E3 one-shot migration: the deprecated per-enchant KeywordsEdited flag becomes a
                    -- single per-list WornRestrictionListState row. Runs after the bad keys above are
                    -- normalised to '' so they can't be migrated.
                    INSERT OR IGNORE INTO WornRestrictionListState (ListKey, IsEdited, LastChanged)
                    SELECT WornRestrictionListKey, 1, MAX(LastChanged)
                    FROM Enchantments
                    WHERE KeywordsEdited = 1
                      AND WornRestrictionListKey IS NOT NULL AND WornRestrictionListKey <> ''
                      AND WornRestrictionListKey NOT LIKE 'Null|%'
                    GROUP BY WornRestrictionListKey;

                    UPDATE Enchantments SET KeywordsEdited = 0 WHERE KeywordsEdited = 1;

                    -- IsEdited = 1 means 'a field shadow is active'; with every shadow NULL it's a
                    -- stale flag from a payload-less import.
                    UPDATE Enchantments SET IsEdited = 0
                    WHERE IsEdited = 1
                      AND {allShadowsNull};

                    -- Mirror image: IsEdited = 0 means no shadow may be active, so any leftover
                    -- CastType/TargetType shadow is dead data. Older ResetEnchantmentEdits didn't
                    -- clear those two (they have no UI edit path, but import can write them), and a
                    -- later Name edit would flip IsEdited back to 1 and revive them.
                    UPDATE Enchantments SET IsEditedCastType = NULL, IsEditedTargetType = NULL
                    WHERE IsEdited = 0
                      AND (IsEditedCastType IS NOT NULL OR IsEditedTargetType IS NOT NULL);

                    DELETE FROM WornRestrictionKeywords
                    WHERE ListKey IS NULL OR ListKey = '' OR ListKey LIKE 'Null|%';

                    UPDATE Enchantments SET LastChanged = NULL
                    WHERE LastChanged IS NOT NULL
                      AND IsEdited = 0 AND EffectsEdited = 0 AND KeywordsEdited = 0
                      AND {allShadowsNull};

                    -- The mirror of the first rule, and the repair for every database the old
                    -- five-column version already damaged: a row that HAS an active shadow must be
                    -- flagged edited. Runs last, after the CastType/TargetType orphans above have
                    -- been cleared, so it only revives rows with a shadow that really is an edit.
                    --
                    -- LastChanged is put back only where it was lost. It is not the moment of the
                    -- original edit - that timestamp is gone - but a blank one is worse than an
                    -- approximate one: the export writes it out empty and the importer rejects the
                    -- file as corrupt (ToSkipInvalid).
                    UPDATE Enchantments SET IsEdited = 1, LastChanged = COALESCE(LastChanged, @repairedAt)
                    WHERE IsEdited = 0 AND ({anyShadowSet});";
                cmd.Parameters.AddWithValue("@repairedAt", NowIso());
                cmd.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                AppLogger.LogError("RepairBlankWornRestrictionEdits failed (non-fatal)", ex);
            }
        }

        // --- One-shot: case-insensitive keys -------------------------------------------------
        //
        // Skyrim ignores case in plugin filenames, so the same plugin legitimately appears as
        // "ccBGSSSE001-Fish.esm" in one mod's master list and "ccbgssse001-fish.esm" in another's.
        // Keys are "<plugin>|<formid>" and SQLite's default TEXT collation is BINARY, so one record
        // ended up as two rows: a duplicate entry in the tree, a double count in the scan report, and
        // a reference that could be flagged dead purely because it was spelled differently.
        //
        // Fixed at the database rather than by rewriting keys: COLLATE NOCASE leaves the stored
        // spelling untouched - so existing presets, exports and patch file names keep working - while
        // the two variants become one row, and a scan's upsert lands on the existing record instead
        // of inserting a second one.
        //
        // Covers every key column that takes part in a PRIMARY KEY, plus the columns joining child
        // rows to their parent. Tables are rebuilt because SQLite cannot ALTER a collation.
        private static readonly (string Table, string[] KeyColumns)[] CaseInsensitiveKeyColumns =
        {
            ("Armor",                            new[] { "Key" }),
            ("Weapons",                          new[] { "Key" }),
            ("COBJ",                             new[] { "Key" }),
            ("Enchantments",                     new[] { "Key" }),
            ("MagicEffects",                     new[] { "Key" }),
            ("Container",                        new[] { "ContainerKey" }),
            ("ContainerLVLI",                    new[] { "ContainerKey", "LVLiKey" }),
            ("EnchantmentEffects",               new[] { "EnchantmentKey", "MagicEffectKey" }),
            ("EnchantmentEffects_Original",      new[] { "EnchantmentKey", "MagicEffectKey" }),
            ("WornRestrictionKeywords",          new[] { "ListKey", "KeywordKey" }),
            ("WornRestrictionKeywords_Original", new[] { "ListKey", "KeywordKey" }),
            ("WornRestrictionListState",         new[] { "ListKey" }),
            ("COBJ_Conditions",                  new[] { "COBJKey" }),
            ("COBJ_Conditions_Original",         new[] { "COBJKey" }),
            // Maps a tool key to the FormID handed out for it in a generated ESP. Two spellings here
            // would mean two FormIDs for one recipe on the next patch run.
            ("PatchFormIdMap",                   new[] { "ToolKey" }),
            ("LeveledList",                      new[] { "Key" }),
            ("LeveledListEntry",                 new[] { "ListKey", "Reference" }),
            ("ContainerEntry",                   new[] { "ContainerKey", "Reference" }),
        };

        // internal rather than private so the migration can be run against a real SQLite file in
        // tests - it rewrites tables holding user edits, which is not something to verify by reading.
        internal static void NormalizeKeyCaseCollation(SqliteConnection connection)
        {
            foreach (var (table, keyColumns) in CaseInsensitiveKeyColumns)
            {
                try
                {
                    if (!TableExists(connection, table)) continue;

                    var ddl = GetTableDdl(connection, table);
                    if (string.IsNullOrWhiteSpace(ddl)) continue;

                    // Already migrated - the case on every launch after the first.
                    if (keyColumns.All(c => Services.SchemaCollation.HasNoCase(ddl, c))) continue;

                    var rebuilt = Services.SchemaCollation.AddNoCase(ddl, keyColumns);
                    if (rebuilt == ddl) continue;   // nothing matched; leave the table alone

                    RebuildTableWithCollation(connection, table, keyColumns, rebuilt);
                    AppLogger.LogWarning($"Key collation: {table} rebuilt with case-insensitive keys.");
                }
                catch (Exception ex)
                {
                    // One table failing must not stop the app from starting: that table keeps its old
                    // collation, everything else is migrated, and the next launch tries again.
                    AppLogger.LogError($"Key collation migration failed for {table} (left unchanged)", ex);
                }
            }
        }

        private static string GetTableDdl(SqliteConnection connection, string table)
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT sql FROM sqlite_master WHERE type='table' AND name=@t;";
            cmd.Parameters.AddWithValue("@t", table);
            return cmd.ExecuteScalar() as string ?? "";
        }

        private static List<string> GetColumns(SqliteConnection connection, string table)
        {
            var columns = new List<string>();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = $"PRAGMA table_info({table});";
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) columns.Add(reader.GetString(1));
            return columns;
        }

        // The columns that actually form the PRIMARY KEY, in key order, read from the table itself.
        //
        // Deliberately NOT the list of columns being given the collation: those overlap but are not
        // the same thing. COBJ_Conditions.COBJKey is a key column worth collating, but it is a link
        // to the parent recipe, not a unique one - a recipe has many conditions under the same
        // COBJKey. Merging "duplicates" by it deleted 1,894 of 4,345 condition rows in a trial run
        // against a real database. Only a real PRIMARY KEY says which rows are the same row.
        private static List<string> GetPrimaryKeyColumns(SqliteConnection connection, string table)
        {
            var keyColumns = new List<(int Position, string Name)>();

            using var cmd = connection.CreateCommand();
            cmd.CommandText = $"PRAGMA table_info({table});";
            using (var reader = cmd.ExecuteReader())
            {
                while (reader.Read())
                {
                    int pkPosition = reader.GetInt32(5);   // 0 = not part of the primary key
                    if (pkPosition > 0) keyColumns.Add((pkPosition, reader.GetString(1)));
                }
            }

            keyColumns.Sort((a, b) => a.Position.CompareTo(b.Position));
            return keyColumns.ConvertAll(k => k.Name);
        }

        private static void RebuildTableWithCollation(
            SqliteConnection connection, string table, string[] keyColumns, string newDdl)
        {
            var columns = GetColumns(connection, table);
            if (columns.Count == 0) return;

            var columnList = string.Join(", ", columns.Select(c => $"\"{c}\""));
            var temp = table + "__nocase";

            // Foreign keys have to be off for the swap, and the pragma is a no-op inside a
            // transaction - so it goes here, not below. COBJ_Conditions declares a foreign key onto
            // COBJ(Key); with enforcement on, dropping COBJ fails with "FOREIGN KEY constraint
            // failed" and COBJ stays unmigrated while every other table changes. Worth stating
            // plainly because the project never turns foreign keys on itself: Microsoft.Data.Sqlite
            // does it per connection.
            bool foreignKeysWereOn = ReadPragmaBool(connection, "foreign_keys");
            SetPragma(connection, "foreign_keys", false);

            try
            {
                using var tx = connection.BeginTransaction();

                DropDuplicateKeyRows(connection, tx, table, keyColumns, columns);

                using (var cmd = connection.CreateCommand())
                {
                    cmd.Transaction = tx;

                    // Fill the new table before dropping the old one, all inside the transaction: if
                    // any step throws, the rollback leaves the original table exactly as it was.
                    // Nothing is deleted until the copy holds the data.
                    cmd.CommandText = Services.SchemaCollation.RenameTable(newDdl, table, temp);
                    cmd.ExecuteNonQuery();

                    cmd.CommandText = $"INSERT INTO {temp} ({columnList}) SELECT {columnList} FROM {table};";
                    cmd.ExecuteNonQuery();

                    cmd.CommandText = $"DROP TABLE {table};";
                    cmd.ExecuteNonQuery();

                    cmd.CommandText = $"ALTER TABLE {temp} RENAME TO {table};";
                    cmd.ExecuteNonQuery();
                }

                tx.Commit();
            }
            finally
            {
                if (foreignKeysWereOn) SetPragma(connection, "foreign_keys", true);
            }
        }

        private static bool ReadPragmaBool(SqliteConnection connection, string pragma)
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = $"PRAGMA {pragma};";
            return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L) != 0;
        }

        private static void SetPragma(SqliteConnection connection, string pragma, bool on)
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = $"PRAGMA {pragma} = {(on ? "on" : "off")};";
            cmd.ExecuteNonQuery();
        }

        // Rows that differ only by case have to go before the rebuild, or the new case-insensitive
        // PRIMARY KEY rejects the copy.
        //
        // Which row survives is not arbitrary: one carrying user edits wins. The scan data in the
        // other is identical anyway - same record, same plugin, different spelling - and comes back
        // on the next scan, while a dropped edit is gone for good. Among equals the most recently
        // changed wins, then the lowest rowid, so the outcome is at least deterministic.
        private static void DropDuplicateKeyRows(
            SqliteConnection connection, SqliteTransaction tx,
            string table, string[] keyColumns, List<string> columns)
        {
            // Grouped by the table's REAL primary key, not by the columns being collated. Those two
            // differ exactly where it matters: COBJ_Conditions.COBJKey is worth collating but is a
            // link to the parent recipe, and a recipe has many conditions sharing it. Grouping by it
            // treated them as duplicates.
            var primaryKey = GetPrimaryKeyColumns(connection, table);
            if (primaryKey.Count == 0) return;   // nothing defines "the same row" here

            // LOWER() only on the columns actually being made case-insensitive; a non-key part of the
            // primary key keeps its own comparison.
            var collated = new HashSet<string>(keyColumns, StringComparer.OrdinalIgnoreCase);
            var groupBy = string.Join(", ", primaryKey.Select(
                c => collated.Contains(c) ? $"LOWER(\"{c}\")" : $"\"{c}\""));

            var rank = new List<string>();
            if (columns.Contains("IsEdited")) rank.Add("IsEdited DESC");
            if (columns.Contains("LastChanged")) rank.Add("LastChanged DESC");
            rank.Add("rowid ASC");

            using var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = $@"
                DELETE FROM {table}
                WHERE rowid NOT IN (
                    SELECT rowid FROM (
                        SELECT rowid,
                               ROW_NUMBER() OVER (PARTITION BY {groupBy}
                                                  ORDER BY {string.Join(", ", rank)}) AS rn
                        FROM {table})
                    WHERE rn = 1);";

            int removed = cmd.ExecuteNonQuery();
            if (removed > 0)
                AppLogger.LogWarning(
                    $"Key collation: {table} had {removed} row(s) that were one record under a " +
                    "differently-spelled plugin name; kept the edited copy.");
        }

        private static bool TableExists(SqliteConnection connection, string table)
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=@table;";
            cmd.Parameters.AddWithValue("@table", table);
            return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
        }

        private static void AddColumnIfMissing(SqliteConnection connection, string table, string column, string columnDefSql)
        {
            // sqlite_master only has an entry for a table once it exists — on a brand-new DB file,
            // CreateTables() (called right after this) creates it with the column already present, so
            // there's nothing to migrate and this is a safe no-op.
            using (var existsCmd = connection.CreateCommand())
            {
                existsCmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=@table;";
                existsCmd.Parameters.AddWithValue("@table", table);
                if (Convert.ToInt64(existsCmd.ExecuteScalar()) == 0)
                    return;
            }

            using (var pragmaCmd = connection.CreateCommand())
            {
                pragmaCmd.CommandText = $"PRAGMA table_info({table});";
                using var reader = pragmaCmd.ExecuteReader();
                while (reader.Read())
                {
                    // column 1 = column name
                    if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                        return;
                }
            }

            using var alterCmd = connection.CreateCommand();
            alterCmd.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {columnDefSql};";
            alterCmd.ExecuteNonQuery();
        }

        // internal static, not private: the DDL below is the only description of these tables that
        // exists, and a typo in it fails at runtime on a real database. The schema tests run it
        // against a throwaway file and checks the tables and columns really land.
        // Static because the body only ever touches the connection it is handed.
        internal static void CreateTables(SqliteConnection connection)
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText =
            @"
                CREATE TABLE IF NOT EXISTS Armor (
                    Key TEXT PRIMARY KEY COLLATE NOCASE,
                    EditorID TEXT NOT NULL,
                    Name TEXT,
                    Weight REAL,
                    Value INTEGER,
                    ArmorRating REAL,
                    BodySlotMask INTEGER,
                    ArmorType TEXT,
                    Keywords TEXT,
                    ContainerString TEXT,
                    ObjectEffectKey TEXT,

                    IsEditedName Text,
                    IsEditedWeight REAL,
                    IsEditedValue INTEGER,
                    IsEditedArmorRating REAL,
                    IsEditedBodySlotMask INTEGER,
                    IsEditedArmorType TEXT,
                    IsEditedKeywords TEXT,
                    IsEditedContainerString TEXT,
                    IsEditedObjectEffectKey TEXT,

                    IsEdited INTEGER DEFAULT 0,
                    Active INTEGER NOT NULL DEFAULT 1,
                    LastChanged TEXT,
                    LastPatched TEXT
                );

                CREATE TABLE IF NOT EXISTS Weapons (
                    Key TEXT PRIMARY KEY COLLATE NOCASE,
                    EditorID TEXT NOT NULL,
                    Name TEXT,
                    Weight REAL,
                    Value INTEGER,
                    Damage INTEGER,
                    Speed REAL,
                    Reach REAL,
                    Stagger REAL,
                    Keywords TEXT,
                    ContainerString TEXT,
                    ObjectEffectKey TEXT,

                    IsEditedName Text,
                    IsEditedWeight REAL,
                    IsEditedValue INTEGER,
                    IsEditedDamage INTEGER,
                    IsEditedSpeed REAL,
                    IsEditedReach REAL,
                    IsEditedStagger REAL,
                    IsEditedKeywords TEXT,
                    IsEditedContainerString TEXT,
                    IsEditedObjectEffectKey TEXT,

                    IsEdited INTEGER DEFAULT 0,
                    Active INTEGER NOT NULL DEFAULT 1,
                    LastChanged TEXT,
                    LastPatched TEXT
                );

                CREATE TABLE IF NOT EXISTS COBJ (
                    Key TEXT PRIMARY KEY COLLATE NOCASE,
                    Original INTEGER NOT NULL DEFAULT 1,
                    Name TEXT NOT NULL,
                    CreatedItem TEXT NOT NULL,
                    WorkbenchKeyword TEXT,
                    Ingredients TEXT,

                    IsEditedName TEXT,
                    IsEditedCreatedItem TEXT,
                    IsEditedWorkbenchKeyword TEXT,
                    IsEditedIngredients TEXT,

                    IsEdited INTEGER DEFAULT 0,
                    Active INTEGER NOT NULL DEFAULT 1,
                    ConditionsEdited INTEGER NOT NULL DEFAULT 0,
                    LastChanged TEXT,
                    LastPatched TEXT
                );

                CREATE TABLE IF NOT EXISTS COBJ_Conditions (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    COBJKey TEXT NOT NULL COLLATE NOCASE,
                    ConditionType TEXT NOT NULL,

                    Target TEXT,
                    Value TEXT,
                    Extra TEXT,
                    RunOn TEXT,

                    -- Comparison operator (EqualTo, GreaterThanOrEqualTo, ...) and Condition.Flags
                    -- (OR, SwapSubjectAndTarget, comma-separated). Both used to be guessed at ESP
                    -- build time; the OR flag in particular has no safe default, since rebuilding an
                    -- OR-chained pair as AND turns either-perk into both-perks and the recipe
                    -- disappears from the crafting menu.
                    CompareOperator TEXT,
                    Flags TEXT,

                    IsEditedTarget TEXT,
                    IsEditedValue TEXT,
                    IsEditedExtra TEXT,
                    IsEditedRunOn TEXT,

                    IsEdited INTEGER DEFAULT 0,

                    FOREIGN KEY (COBJKey) REFERENCES COBJ(Key)
                );

                -- Lazily-populated, permanently-frozen snapshot of a COBJ's conditions as they
                -- looked right before the first user edit (see SaveCOBJConditions) - COBJ_Conditions
                -- itself is destructively DELETE+INSERTed on every save, so there is nothing else to
                -- revert to once the user has edited a condition. No Id/IsEdited* columns: this table
                -- is only ever bulk-replaced per COBJKey, never updated in place.
                CREATE TABLE IF NOT EXISTS COBJ_Conditions_Original (
                    COBJKey TEXT NOT NULL COLLATE NOCASE,
                    ConditionType TEXT NOT NULL,
                    Target TEXT,
                    Value TEXT,
                    Extra TEXT,
                    RunOn TEXT,
                    CompareOperator TEXT,
                    Flags TEXT
                );


                CREATE TABLE IF NOT EXISTS Enchantments (
                    Key TEXT PRIMARY KEY COLLATE NOCASE,
                    EditorID TEXT NOT NULL,
                    Name TEXT,
                    CastType TEXT,
                    TargetType TEXT,
                    EnchantmentCost REAL,
                    WornRestrictionListKey TEXT,
                    BaseEnchantmentKey TEXT,

                    -- The rest of ENIT. Flags is the raw dword, NOT the enum's ToString: Mutagen's
                    -- ObjectEffect.Flag only names two of the bits, and SkyPatcher knows a third
                    -- (fooditem). Storing the number keeps a bit nobody has named yet.
                    EnchantType TEXT,
                    Flags INTEGER,
                    ChargeTime REAL,
                    EnchantmentAmount INTEGER,

                    IsEditedName TEXT,
                    IsEditedCastType TEXT,
                    IsEditedTargetType TEXT,
                    IsEditedEnchantmentCost REAL,
                    IsEditedWornRestrictionListKey TEXT,

                    -- Shadow columns only for what SkyPatcher can actually patch on a scanned
                    -- record: setFlags/removeFlags, chargeTime, enchantmentAmount. EnchantType has
                    -- none - there is no operation for it, so it is editable on user-created records
                    -- only, where the base column is written directly (as EditorID is).
                    IsEditedFlags INTEGER,
                    IsEditedChargeTime REAL,
                    IsEditedEnchantmentAmount INTEGER,

                    IsEdited INTEGER DEFAULT 0,
                    Active INTEGER NOT NULL DEFAULT 1,
                    EffectsEdited INTEGER NOT NULL DEFAULT 0,
                    KeywordsEdited INTEGER NOT NULL DEFAULT 0,
                    -- 0 = created in this tool, exists in no plugin. See the migration above.
                    Original INTEGER NOT NULL DEFAULT 1,
                    LastChanged TEXT,
                    LastPatched TEXT
                );

                CREATE TABLE IF NOT EXISTS EnchantmentEffects (
                    EnchantmentKey TEXT NOT NULL COLLATE NOCASE,
                    MagicEffectKey TEXT NOT NULL COLLATE NOCASE,
                    EditorID TEXT,
                    Name TEXT,
                    Magnitude REAL,
                    Duration INTEGER,
                    Area INTEGER,

                    IsEditedMagnitude REAL,
                    IsEditedDuration INTEGER,
                    IsEditedArea INTEGER,

                    IsEdited INTEGER DEFAULT 0,

                    PRIMARY KEY (EnchantmentKey, MagicEffectKey)
                );

                -- Same lazy-snapshot pattern as COBJ_Conditions_Original - see that table's comment.
                CREATE TABLE IF NOT EXISTS EnchantmentEffects_Original (
                    EnchantmentKey TEXT NOT NULL COLLATE NOCASE,
                    MagicEffectKey TEXT NOT NULL COLLATE NOCASE,
                    EditorID TEXT,
                    Name TEXT,
                    Magnitude REAL,
                    Duration INTEGER,
                    Area INTEGER
                );

                CREATE TABLE IF NOT EXISTS WornRestrictionKeywords (
                    ListKey TEXT NOT NULL COLLATE NOCASE,
                    KeywordKey TEXT NOT NULL COLLATE NOCASE,

                    IsEditedKeywordKey TEXT,

                    IsEdited INTEGER DEFAULT 0,

                    PRIMARY KEY (ListKey, KeywordKey)
                );

                -- Same lazy-snapshot pattern as COBJ_Conditions_Original - see that table's comment.
                CREATE TABLE IF NOT EXISTS WornRestrictionKeywords_Original (
                    ListKey TEXT NOT NULL COLLATE NOCASE,
                    KeywordKey TEXT NOT NULL
                );

                -- E3: FLST content-edit state lives per-list here, NOT smeared across every
                -- Enchantments row that references the list (the old Enchantments.KeywordsEdited,
                -- flagged by ListKey, marked N enchantments + exported N DTOs for one list edit).
                -- IsEdited=1 also tells a rescan to leave this list's member rows alone.
                CREATE TABLE IF NOT EXISTS WornRestrictionListState (
                    ListKey TEXT PRIMARY KEY COLLATE NOCASE,
                    IsEdited INTEGER NOT NULL DEFAULT 0,
                    LastChanged TEXT
                );

                CREATE TABLE IF NOT EXISTS Container (
                    ContainerKey TEXT PRIMARY KEY COLLATE NOCASE,
                    Name TEXT NOT NULL,
                    Active INTEGER NOT NULL DEFAULT 1
                );

                CREATE TABLE IF NOT EXISTS ContainerLVLI (
                    ContainerKey TEXT NOT NULL COLLATE NOCASE,
                    LVLiKey TEXT NOT NULL COLLATE NOCASE,
                    LVLiName TEXT,
                    PRIMARY KEY (ContainerKey, LVLiKey)
                );

                -- Everything a container holds, not just the leveled lists.
                --
                -- ContainerLVLI above is the Container tab's working set: which leveled lists hang in
                -- a container, deduplicated, with the list name cached. This is the complete record -
                -- of 13,158 container entries in the real load order, only 3,630 are leveled lists;
                -- the other ~9,500 point straight at an item. Without them, the question of where an
                -- item already appears can answer for lists but not for containers, which is half an
                -- answer and worse than none.
                --
                -- Positional key for the same reason as LeveledListEntry: a container may list the
                -- same thing twice, and that is data, not a duplicate.
                CREATE TABLE IF NOT EXISTS ContainerEntry (
                    ContainerKey TEXT NOT NULL COLLATE NOCASE,
                    Ordinal INTEGER NOT NULL,
                    Reference TEXT NOT NULL COLLATE NOCASE,
                    Count INTEGER NOT NULL DEFAULT 1,
                    PRIMARY KEY (ContainerKey, Ordinal)
                );

                -- The leveled lists themselves, with their own properties. formid.db already has an
                -- LVLi table, but that is a name lookup (Key -> EditorID) with no contents; this is
                -- the scanned record. Kept here rather than there because formid.db is dropped and
                -- rebuilt on every scan, and because everything that reads list CONTENTS reads
                -- item.db anyway.
                --
                -- ChanceNone/Flags/Global are properties of the WHOLE list, not of any one entry -
                -- which is exactly why editing them is treated separately from adding an item
                -- (see docs/TODO.md, Prio 6): they change behaviour for every mod feeding that list.
                CREATE TABLE IF NOT EXISTS LeveledList (
                    Key TEXT PRIMARY KEY COLLATE NOCASE,
                    EditorID TEXT NOT NULL,
                    ChanceNone INTEGER NOT NULL DEFAULT 0,
                    Flags TEXT,
                    GlobalKey TEXT COLLATE NOCASE,
                    Active INTEGER NOT NULL DEFAULT 1
                );

                -- One row per entry, in list order.
                --
                -- Ordinal is the primary key, and it has to be: an entry has no identity of its own.
                -- Measured against the real load order, 3,982 entries share (list, reference, level)
                -- with another entry in the same list - the same item listed twice at the same level
                -- is legitimate and changes the odds. Keying on the reference would silently merge
                -- those and quietly change what the list does.
                --
                -- Reference may point at another leveled list rather than an item: 10,148 of 28,419
                -- entries do, up to 9 levels deep. Resolving that is the reader's job, not the
                -- schema's - the row just stores where it points.
                CREATE TABLE IF NOT EXISTS LeveledListEntry (
                    ListKey TEXT NOT NULL COLLATE NOCASE,
                    Ordinal INTEGER NOT NULL,
                    Reference TEXT NOT NULL COLLATE NOCASE,
                    Level INTEGER NOT NULL DEFAULT 1,
                    Count INTEGER NOT NULL DEFAULT 1,
                    PRIMARY KEY (ListKey, Ordinal)
                );

                -- What the USER changed about a list, kept strictly apart from what was scanned.
                --
                -- Same split as everywhere else in this schema: LeveledList above holds the load
                -- order as it is, this holds the edit on top of it. Nothing here is ever written by
                -- a scan, so a rescan can replace the scanned values without touching a single
                -- decision the user made, and a reset is a DELETE rather than a guess at what the
                -- original was.
                --
                -- NULL means not-edited per column, which is why neither has a default: a list
                -- whose chance was changed but whose flags were left alone must emit one operation,
                -- not two. A row with both columns NULL is deleted rather than kept.
                --
                -- These properties belong to the WHOLE list, unlike a placement: changing them
                -- changes the odds for every mod feeding that list. That is the user's decision to
                -- make (2026-09-15), and the calculator in the list window is what makes it an
                -- informed one - it shows the new number while the value is being typed.
                CREATE TABLE IF NOT EXISTS LeveledListEdit (
                    ListKey TEXT PRIMARY KEY COLLATE NOCASE,
                    ChanceNone INTEGER,
                    CalcFlagMode TEXT,
                    LastChanged TEXT
                );

                -- Global variables (GLOB), for one purpose: a leveled list can take its chance-none
                -- from one instead of from a fixed number. 223 lists in the real load order do,
                -- across 125 globals.
                --
                -- Value is scanned as well, because a list with a global has no fixed chance and the
                -- calculator would otherwise have nothing to compute with. It is a starting value -
                -- quests and scripts move it during play - so the window says where it came from.
                -- NULL means the scan could not read the payload shape; 0 is a real chance and must
                -- not be invented.
                CREATE TABLE IF NOT EXISTS Globals (
                    Key TEXT PRIMARY KEY COLLATE NOCASE,
                    EditorID TEXT,
                    Value REAL,
                    Active INTEGER NOT NULL DEFAULT 1
                );

                CREATE TABLE IF NOT EXISTS MagicEffects (
                    Key TEXT PRIMARY KEY COLLATE NOCASE,
                    EditorID TEXT,
                    Name TEXT NOT NULL,
                    HasMagnitude INTEGER,
                    HasDuration INTEGER,
                    HasArea INTEGER,
                    CastType TEXT,
                    TargetType TEXT,
                    Active INTEGER NOT NULL DEFAULT 1
                );
            ";
            cmd.ExecuteNonQuery();

        }

        // Single-row UPSERT for the 6 "parent" tables — same semantics as PrepareUpsertBatch, used
        // for the tail of rows that doesn't fill a full batch. Driven by the same ColumnNames/
        // ParamNames arrays as the batch versions, so there's exactly one place that knows each
        // table's real columns (an "INSERT OR REPLACE" here would delete+reinsert the whole row,
        // wiping IsEdited*/Original on every conflict).
        private static SqliteCommand PrepareUpsert(SqliteConnection connection, string table, string[] columnNames, string[] paramNames)
        {
            var columns = string.Join(", ", columnNames) + ", Active";
            var values = string.Join(", ", paramNames) + ", 1";
            var updateSet = string.Join(", ", columnNames.Skip(1).Select(c => $"{c} = excluded.{c}")) + ", Active = 1";

            var cmd = connection.CreateCommand();
            cmd.CommandText =
                $"INSERT INTO {table} ({columns}) VALUES ({values}) " +
                $"ON CONFLICT({columnNames[0]}) DO UPDATE SET {updateSet}";

            foreach (var p in paramNames)
                cmd.Parameters.Add(new SqliteParameter(p, DBNull.Value));

            return cmd;
        }

        // Single-row plain INSERT for the 4 child tables — see PrepareInsertBatch for why "plain"
        // (no OR REPLACE) is correct here: callers always DELETE the relevant parent keys' rows first.
        private static SqliteCommand PrepareInsert(SqliteConnection connection, string table, string[] columnNames, string[] paramNames)
        {
            var columns = string.Join(", ", columnNames);
            var values = string.Join(", ", paramNames);

            var cmd = connection.CreateCommand();
            cmd.CommandText = $"INSERT INTO {table} ({columns}) VALUES ({values})";

            foreach (var p in paramNames)
                cmd.Parameters.Add(new SqliteParameter(p, DBNull.Value));

            return cmd;
        }
    }
}
