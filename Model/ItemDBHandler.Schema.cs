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
        private static void EnsureSchema(SqliteConnection connection)
        {
            AddColumnIfMissing(connection, "Armor", "Active", "INTEGER NOT NULL DEFAULT 1");
            AddColumnIfMissing(connection, "Weapons", "Active", "INTEGER NOT NULL DEFAULT 1");
            AddColumnIfMissing(connection, "COBJ", "Active", "INTEGER NOT NULL DEFAULT 1");
            AddColumnIfMissing(connection, "COBJ", "ConditionsEdited", "INTEGER NOT NULL DEFAULT 0");
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

            RepairBlankWornRestrictionEdits(connection);
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
            try
            {
                using var cmd = connection.CreateCommand();
                cmd.CommandText = @"
                    -- This repair runs (via EnsureSchema) BEFORE CreateTables, so the E3 state table
                    -- may not exist yet on an old DB - the migration below needs it.
                    CREATE TABLE IF NOT EXISTS WornRestrictionListState (
                        ListKey TEXT PRIMARY KEY,
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
                      AND IsEditedName IS NULL AND IsEditedCastType IS NULL AND IsEditedTargetType IS NULL
                      AND IsEditedEnchantmentCost IS NULL AND IsEditedWornRestrictionListKey IS NULL;

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
                      AND IsEditedName IS NULL AND IsEditedCastType IS NULL AND IsEditedTargetType IS NULL
                      AND IsEditedEnchantmentCost IS NULL AND IsEditedWornRestrictionListKey IS NULL;";
                cmd.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                AppLogger.LogError("RepairBlankWornRestrictionEdits failed (non-fatal)", ex);
            }
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

        private void CreateTables(SqliteConnection connection)
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText =
            @"
                CREATE TABLE IF NOT EXISTS Armor (
                    Key TEXT PRIMARY KEY,
                    EditorID TEXT NOT NULL,
                    Name TEXT,
                    Weight REAL,
                    Value INTEGER,
                    ArmorRating REAL,
                    BodySlotMask INTEGER,
                    Keywords TEXT,
                    ContainerString TEXT,

                    IsEditedName Text,
                    IsEditedWeight REAL,
                    IsEditedValue INTEGER,
                    IsEditedArmorRating REAL,
                    IsEditedBodySlotMask INTEGER,
                    IsEditedKeywords TEXT,
                    IsEditedContainerString TEXT,

                    IsEdited INTEGER DEFAULT 0,
                    Active INTEGER NOT NULL DEFAULT 1,
                    LastChanged TEXT,
                    LastPatched TEXT
                );

                CREATE TABLE IF NOT EXISTS Weapons (
                    Key TEXT PRIMARY KEY,
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

                    IsEditedName Text,
                    IsEditedWeight REAL,
                    IsEditedValue INTEGER,
                    IsEditedDamage INTEGER,
                    IsEditedSpeed REAL,
                    IsEditedReach REAL,
                    IsEditedStagger REAL,
                    IsEditedKeywords TEXT,
                    IsEditedContainerString TEXT,

                    IsEdited INTEGER DEFAULT 0,
                    Active INTEGER NOT NULL DEFAULT 1,
                    LastChanged TEXT,
                    LastPatched TEXT
                );

                CREATE TABLE IF NOT EXISTS COBJ (
                    Key TEXT PRIMARY KEY,
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
                    COBJKey TEXT NOT NULL,
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
                    COBJKey TEXT NOT NULL,
                    ConditionType TEXT NOT NULL,
                    Target TEXT,
                    Value TEXT,
                    Extra TEXT,
                    RunOn TEXT,
                    CompareOperator TEXT,
                    Flags TEXT
                );


                CREATE TABLE IF NOT EXISTS Enchantments (
                    Key TEXT PRIMARY KEY,
                    EditorID TEXT NOT NULL,
                    Name TEXT,
                    CastType TEXT,
                    TargetType TEXT,
                    EnchantmentCost REAL,
                    WornRestrictionListKey TEXT,
                    BaseEnchantmentKey TEXT,

                    IsEditedName TEXT,
                    IsEditedCastType TEXT,
                    IsEditedTargetType TEXT,
                    IsEditedEnchantmentCost REAL,
                    IsEditedWornRestrictionListKey TEXT,

                    IsEdited INTEGER DEFAULT 0,
                    Active INTEGER NOT NULL DEFAULT 1,
                    EffectsEdited INTEGER NOT NULL DEFAULT 0,
                    KeywordsEdited INTEGER NOT NULL DEFAULT 0,
                    LastChanged TEXT,
                    LastPatched TEXT
                );

                CREATE TABLE IF NOT EXISTS EnchantmentEffects (
                    EnchantmentKey TEXT NOT NULL,
                    MagicEffectKey TEXT NOT NULL,
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
                    EnchantmentKey TEXT NOT NULL,
                    MagicEffectKey TEXT NOT NULL,
                    EditorID TEXT,
                    Name TEXT,
                    Magnitude REAL,
                    Duration INTEGER,
                    Area INTEGER
                );

                CREATE TABLE IF NOT EXISTS WornRestrictionKeywords (
                    ListKey TEXT NOT NULL,
                    KeywordKey TEXT NOT NULL,

                    IsEditedKeywordKey TEXT,

                    IsEdited INTEGER DEFAULT 0,

                    PRIMARY KEY (ListKey, KeywordKey)
                );

                -- Same lazy-snapshot pattern as COBJ_Conditions_Original - see that table's comment.
                CREATE TABLE IF NOT EXISTS WornRestrictionKeywords_Original (
                    ListKey TEXT NOT NULL,
                    KeywordKey TEXT NOT NULL
                );

                -- E3: FLST content-edit state lives per-list here, NOT smeared across every
                -- Enchantments row that references the list (the old Enchantments.KeywordsEdited,
                -- flagged by ListKey, marked N enchantments + exported N DTOs for one list edit).
                -- IsEdited=1 also tells a rescan to leave this list's member rows alone.
                CREATE TABLE IF NOT EXISTS WornRestrictionListState (
                    ListKey TEXT PRIMARY KEY,
                    IsEdited INTEGER NOT NULL DEFAULT 0,
                    LastChanged TEXT
                );

                CREATE TABLE IF NOT EXISTS Container (
                    ContainerKey TEXT PRIMARY KEY,
                    Name TEXT NOT NULL,
                    Active INTEGER NOT NULL DEFAULT 1
                );

                CREATE TABLE IF NOT EXISTS ContainerLVLI (
                    ContainerKey TEXT NOT NULL,
                    LVLiKey TEXT NOT NULL,
                    LVLiName TEXT,
                    PRIMARY KEY (ContainerKey, LVLiKey)
                );

                CREATE TABLE IF NOT EXISTS MagicEffects (
                    Key TEXT PRIMARY KEY,
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
