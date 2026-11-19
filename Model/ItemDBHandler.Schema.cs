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
        internal static void EnsureSchemaForTests(SqliteConnection connection) => EnsureSchema(connection);

        private static void EnsureSchema(SqliteConnection connection)
        {
            AddColumnIfMissing(connection, "Armor", "Active", "INTEGER NOT NULL DEFAULT 1");
            AddColumnIfMissing(connection, "Weapons", "Active", "INTEGER NOT NULL DEFAULT 1");
            AddColumnIfMissing(connection, "COBJ", "Active", "INTEGER NOT NULL DEFAULT 1");
            AddColumnIfMissing(connection, "COBJ", "ConditionsEdited", "INTEGER NOT NULL DEFAULT 0");

            // How many objects a recipe produces (NAM1). DEFAULT 1 is not a placeholder: it is what
            // every row in an existing database effectively already meant, since nothing read or
            // wrote the value before. The real numbers arrive with the next scan.
            AddColumnIfMissing(connection, "COBJ", "CreatedObjectCount", "INTEGER NOT NULL DEFAULT 1");
            AddColumnIfMissing(connection, "COBJ", "IsEditedCreatedObjectCount", "TEXT");
            AddColumnIfMissing(connection, "COBJ", "Uid", "TEXT");
            AddColumnIfMissing(connection, "Enchantments", "Uid", "TEXT");
            AddColumnIfMissing(connection, "Spells", "Uid", "TEXT");
            AddColumnIfMissing(connection, "Armor", "ArmorType", "TEXT");
            AddColumnIfMissing(connection, "Armor", "IsEditedArmorType", "TEXT");
            AddColumnIfMissing(connection, "COBJ_Conditions", "CompareOperator", "TEXT");
            AddColumnIfMissing(connection, "COBJ_Conditions", "Flags", "TEXT");
            AddColumnIfMissing(connection, "COBJ_Conditions_Original", "CompareOperator", "TEXT");
            AddColumnIfMissing(connection, "COBJ_Conditions_Original", "Flags", "TEXT");
            // Empty on rows scanned before it existed; CobjEspBuilder treats such a Reference row
            // as stale and keeps the original conditions (docs/Conditions-Plan.md, C2).
            AddColumnIfMissing(connection, "COBJ_Conditions", "RunOnRef", "TEXT");
            AddColumnIfMissing(connection, "COBJ_Conditions_Original", "RunOnRef", "TEXT");
            AddColumnIfMissing(connection, "Enchantments", "Active", "INTEGER NOT NULL DEFAULT 1");
            AddColumnIfMissing(connection, "Enchantments", "EffectsEdited", "INTEGER NOT NULL DEFAULT 0");
            AddColumnIfMissing(connection, "Enchantments", "KeywordsEdited", "INTEGER NOT NULL DEFAULT 0");
            // BaseEnchantment (ENCH inherited from) — read-only scan value, drives the "derived" tree
            // tag + base-only filter. No shadow column: not user-editable.
            AddColumnIfMissing(connection, "Enchantments", "BaseEnchantmentKey", "TEXT");
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
            // Added after the table shipped. Stays 0 on an existing database until the next scan,
            // which is honest: nothing knows yet whether those rows had to make the choice.
            AddColumnIfMissing(connection, "LeveledListLostEntry", "Ambiguous", "INTEGER NOT NULL DEFAULT 0");

            // Who put the entry there and who took it away (Anwenderrückmeldung Avrie, 2026-09-30:
            // "without knowing which plugin removed the item from the list, it tends to raise more
            // questions than it answers").
            //
            // LostFrom was never the remover - it names the last plugin that still HAD the entry.
            // Both of these are derived in the same pass that already walks every version of a list
            // in load order; see the lost-entry block in ItemDBHandler.Scan.cs.
            AddColumnIfMissing(connection, "LeveledListLostEntry", "AddedBy", "TEXT");
            AddColumnIfMissing(connection, "LeveledListLostEntry", "DroppedBy", "TEXT");
            AddColumnIfMissing(connection, "LeveledList", "Original", "INTEGER NOT NULL DEFAULT 1");
            AddColumnIfMissing(connection, "LeveledList", "Uid", "TEXT");
            AddColumnIfMissing(connection, "Container", "Active", "INTEGER NOT NULL DEFAULT 1");
            AddColumnIfMissing(connection, "MagicEffects", "Active", "INTEGER NOT NULL DEFAULT 1");

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

                AddColumnIfMissing(connection, table, "EnchantAmount", "INTEGER");
                AddColumnIfMissing(connection, table, "IsEditedEnchantAmount", "INTEGER");
            }

            // The armor an enchanted variant was made from (TNAM, "Template Armor"), 2026-10-25 - the
            // armor counterpart of an enchantment's base. Scanned only, never edited, so no shadow
            // column. NULL on an existing database until the next scan fills it.
            AddColumnIfMissing(connection, "Armor", "TemplateKey", "TEXT");
            AddColumnIfMissing(connection, "Weapons", "TemplateKey", "TEXT");   // CNAM, the same for weapons (2026-10-26)

            // Critical damage (CRDT), 2026-10-23. NULL on an existing database until the next scan
            // fills it - reads as 0, and an unedited 0 writes nothing to the patch.
            AddColumnIfMissing(connection, "Weapons", "CritDamage", "INTEGER");
            AddColumnIfMissing(connection, "Weapons", "IsEditedCritDamage", "INTEGER");
            AddColumnIfMissing(connection, "Weapons", "CritPercentMult", "REAL");
            AddColumnIfMissing(connection, "Weapons", "IsEditedCritPercentMult", "REAL");

            // Npc shipped before the three N-P3 lists existed, so an item.db from earlier in this
            // development has the table but not the column. CREATE TABLE IF NOT EXISTS does nothing
            // for an existing table - this is the only thing that adds a column to one.
            AddColumnIfMissing(connection, "Npc", "ListsEdited", "INTEGER NOT NULL DEFAULT 0");

            // N-P6 made these two editable, so an item.db from before it has the table without the
            // columns.
            AddColumnIfMissing(connection, "Npc", "IsEditedCombatStyleKey", "TEXT");
            AddColumnIfMissing(connection, "Npc", "IsEditedCrimeFactionKey", "TEXT");

            // The EditorID of the defining plugin, which SkyPatcher's EditorID filters match against
            // (2026-10-31). Empty until the next scan fills it.
            AddColumnIfMissing(connection, "Npc", "OriginalEditorID", "TEXT");

            // The AutoCalc inputs, added after Classes and Races had shipped as name catalogues.
            // Stay NULL on an existing database until the next scan - the scan is the only writer.
            AddColumnIfMissing(connection, "Classes", "HealthWeight", "INTEGER");
            AddColumnIfMissing(connection, "Classes", "MagickaWeight", "INTEGER");
            AddColumnIfMissing(connection, "Classes", "StaminaWeight", "INTEGER");
            AddColumnIfMissing(connection, "Races", "StartingHealth", "REAL");
            AddColumnIfMissing(connection, "Races", "StartingMagicka", "REAL");
            AddColumnIfMissing(connection, "Races", "StartingStamina", "REAL");

            // The race's keywords (ActorTypeNPC ...) and flags (Child ...), scanned with the race. NULL until
            // the next scan.
            AddColumnIfMissing(connection, "Races", "Keywords", "TEXT");
            AddColumnIfMissing(connection, "Races", "Flags", "INTEGER");

            // Who wrote a filter line. Empty for every line that existed before this column did, which
            // is the right answer: they were all written by hand or by the seeder, and the auto-check
            // must not offer to remove something it did not add.
            AddColumnIfMissing(connection, "NpcGroupPredicate", "Origin", "TEXT NOT NULL DEFAULT ''");

            // Added after MeshGeom shipped. A database whose cache predates it reads every shape as "not
            // moved by a parent", which is the right default: those rows were written by a parser that
            // did not compose the node tree, so none of them WAS moved by one.
            AddColumnIfMissing(connection, "MeshGeom", "MovedByParent", "INTEGER NOT NULL DEFAULT 0");
            AddColumnIfMissing(connection, "MeshGeom", "DiffuseTexture", "TEXT");
            AddColumnIfMissing(connection, "MeshGeom", "NormalTexture", "TEXT");
            AddColumnIfMissing(connection, "MeshGeom", "EnvironmentMask", "TEXT");

            // The tangent frame, for per-pixel normal mapping. NULL on a row written before it existed.
            AddColumnIfMissing(connection, "MeshGeom", "Tangents", "BLOB");
            AddColumnIfMissing(connection, "MeshGeom", "Bitangents", "BLOB");

            // The magic school, skill tier and base cost, for the Spells tab (docs/Spells-Plan.md).
            // NULL until the next scan, which is the right answer: the scan is the only writer, and a
            // NULL here reads as "not scanned yet" rather than as an effect belonging to no school.
            // BaseCost is what picks a spell's COSTLIEST effect - the one the game files it under.
            AddColumnIfMissing(connection, "MagicEffects", "MagicSkill", "TEXT");
            AddColumnIfMissing(connection, "MagicEffects", "MinimumSkillLevel", "INTEGER");
            AddColumnIfMissing(connection, "MagicEffects", "BaseCost", "REAL");

            // The in-game description (DNAM), for the picker tooltip (2026-11-08). Filled by the next
            // scan; NULL until then.
            AddColumnIfMissing(connection, "MagicEffects", "Description", "TEXT");

            // The Spells tab (docs/Spells-Plan.md). Spells shipped long before it, as a bare name
            // catalogue for the NPC spell lists - so every existing database has the table with four
            // columns and needs the rest added rather than created.
            //
            // They stay NULL until the next scan, which is the right answer: the scan is the only
            // writer of the base columns, and a NULL here reads as "not scanned yet" rather than as
            // a spell with no cast type.
            foreach (var (column, type) in new[]
                     {
                         ("SpellType", "TEXT"), ("CastType", "TEXT"), ("TargetType", "TEXT"),
                         ("EquipType", "TEXT"),
                         ("BaseCost", "INTEGER"), ("ChargeTime", "REAL"), ("CastDuration", "REAL"),
                         ("SpellRange", "REAL"), ("Description", "TEXT"), ("HalfCostPerkKey", "TEXT"),
                         ("Flags", "INTEGER"), ("Keywords", "TEXT"),
                         ("DroppedEffectRows", "INTEGER NOT NULL DEFAULT 0"),

                         ("IsEditedName", "TEXT"), ("IsEditedBaseCost", "INTEGER"),
                         ("IsEditedCastType", "TEXT"), ("IsEditedChargeTime", "REAL"),
                         ("IsEditedHalfCostPerkKey", "TEXT"), ("IsEditedFlags", "INTEGER"),
                         ("IsEditedKeywords", "TEXT"),

                         ("IsEdited", "INTEGER DEFAULT 0"),
                         ("EffectsEdited", "INTEGER NOT NULL DEFAULT 0"),
                         ("KeywordsEdited", "INTEGER NOT NULL DEFAULT 0"),

                         // 1, not 0: every row an existing database holds came from a plugin. The
                         // default has to say "scanned", or the first patch run would try to write
                         // all of them into the generated ESP.
                         ("Original", "INTEGER NOT NULL DEFAULT 1"),
                         ("LastChanged", "TEXT"), ("LastPatched", "TEXT"),
                     })
            {
                AddColumnIfMissing(connection, "Spells", column, type);
            }

            DiscardGeometryFromOlderParser(connection);

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
            ("Spells",                           new[] { "Key" }),
            ("SpellEffects",                     new[] { "SpellKey", "MagicEffectKey" }),
            ("SpellEffects_Original",            new[] { "SpellKey", "MagicEffectKey" }),
            ("WornRestrictionKeywords",         new[] { "ListKey", "KeywordKey" }),
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
                    EnchantAmount INTEGER,
                    TemplateKey TEXT,

                    IsEditedName Text,
                    IsEditedWeight REAL,
                    IsEditedValue INTEGER,
                    IsEditedArmorRating REAL,
                    IsEditedBodySlotMask INTEGER,
                    IsEditedArmorType TEXT,
                    IsEditedKeywords TEXT,
                    IsEditedContainerString TEXT,
                    IsEditedObjectEffectKey TEXT,
                    IsEditedEnchantAmount INTEGER,

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
                    EnchantAmount INTEGER,
                    CritDamage INTEGER,
                    CritPercentMult REAL,
                    TemplateKey TEXT,

                    IsEditedName Text,
                    IsEditedWeight REAL,
                    IsEditedValue INTEGER,
                    IsEditedDamage INTEGER,
                    IsEditedCritDamage INTEGER,
                    IsEditedCritPercentMult REAL,
                    IsEditedSpeed REAL,
                    IsEditedReach REAL,
                    IsEditedStagger REAL,
                    IsEditedKeywords TEXT,
                    IsEditedContainerString TEXT,
                    IsEditedObjectEffectKey TEXT,
                    IsEditedEnchantAmount INTEGER,

                    IsEdited INTEGER DEFAULT 0,
                    Active INTEGER NOT NULL DEFAULT 1,
                    LastChanged TEXT,
                    LastPatched TEXT
                );

                CREATE TABLE IF NOT EXISTS WorldItem (
                    Key TEXT PRIMARY KEY COLLATE NOCASE,
                    EditorID TEXT NOT NULL,
                    Name TEXT,
                    Kind TEXT NOT NULL,
                    ContainerString TEXT,

                    IsEditedContainerString TEXT,

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
                    CreatedObjectCount INTEGER NOT NULL DEFAULT 1,
                    WorkbenchKeyword TEXT,
                    Ingredients TEXT,

                    IsEditedName TEXT,
                    IsEditedCreatedItem TEXT,
                    IsEditedCreatedObjectCount TEXT,
                    IsEditedWorkbenchKeyword TEXT,
                    IsEditedIngredients TEXT,

                    IsEdited INTEGER DEFAULT 0,
                    Active INTEGER NOT NULL DEFAULT 1,
                    ConditionsEdited INTEGER NOT NULL DEFAULT 0,
                    LastChanged TEXT,
                    LastPatched TEXT,
                    Uid TEXT
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

                    -- Plugin|FormID of the reference a RunOn = Reference condition runs on
                    -- (PlayerRef for 96% of them). Empty for every other RunOn.
                    RunOnRef TEXT,

                    IsEditedTarget TEXT,
                    IsEditedValue TEXT,
                    IsEditedExtra TEXT,
                    IsEditedRunOn TEXT,

                    IsEdited INTEGER DEFAULT 0,

                    FOREIGN KEY (COBJKey) REFERENCES COBJ(Key)
                );

                CREATE TABLE IF NOT EXISTS COBJ_Conditions_Original (
                    COBJKey TEXT NOT NULL COLLATE NOCASE,
                    ConditionType TEXT NOT NULL,
                    Target TEXT,
                    Value TEXT,
                    Extra TEXT,
                    RunOn TEXT,
                    CompareOperator TEXT,
                    Flags TEXT,
                    RunOnRef TEXT
                );

                -- Every read, save and reset of ONE recipe's conditions goes by COBJKey. Without these
                -- each was a scan of all ~27,000 condition rows: measured 2026-10-22, 40 of the 74 s
                -- of a load-order-wide preset patch (GetOriginalCOBJConditions 25 s, SaveCOBJConditions
                -- 14.5 s). The column collation (NOCASE) carries over, so the = lookups use them.
                CREATE INDEX IF NOT EXISTS idx_COBJ_Conditions_COBJKey ON COBJ_Conditions(COBJKey);
                CREATE INDEX IF NOT EXISTS idx_COBJ_Conditions_Original_COBJKey ON COBJ_Conditions_Original(COBJKey);


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
                    LastPatched TEXT,
                    Uid TEXT
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
                    Active INTEGER NOT NULL DEFAULT 1,
                    -- 0 = created in the tool, keyed under SkyrimCraftingTool.esp; see EnsureSchema.
                    Original INTEGER NOT NULL DEFAULT 1,
                    -- Identity of an own list across machines (TE-6); NULL until first exported.
                    Uid TEXT
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

                CREATE TABLE IF NOT EXISTS LeveledListLostEntry (
                    ListKey TEXT NOT NULL COLLATE NOCASE,
                    Reference TEXT NOT NULL COLLATE NOCASE,
                    Level INTEGER NOT NULL DEFAULT 1,
                    Count INTEGER NOT NULL DEFAULT 1,
                    LostFrom TEXT,
                    Ambiguous INTEGER NOT NULL DEFAULT 0,
                    AddedBy TEXT,
                    DroppedBy TEXT,
                    PRIMARY KEY (ListKey, Reference)
                );

                -- Separate from LeveledListLostEntry because they are different kinds of fact. That
                -- one is scanned and the scan rewrites it wholesale on every run; this is a decision
                -- and must outlive any number of rescans. Same split as LeveledList vs
                -- LeveledListEdit right below.
                --
                -- NOT expressed as an item placement, although a placement says exactly the same
                -- thing. A placement lives in the ITEM's ContainerString, and only Armor and Weapons
                -- have one. Measured on the real load order: of 340 lost entries, 12 are armor and
                -- 78 are weapons - the other 250 are nested lists, books and scrolls, record types
                -- the scan does not carry at all. Built on placements this feature would have
                -- silently worked for a quarter of the cases. SkyPatcher needs nothing but the
                -- FormID, so the restoration is kept here, on the list, where every type fits.
                --
                -- Level and Count come from where the entry was last seen, so putting one back
                -- restores it as it was rather than at some default.
                CREATE TABLE IF NOT EXISTS LeveledListRestoredEntry (
                    ListKey TEXT NOT NULL COLLATE NOCASE,
                    Reference TEXT NOT NULL COLLATE NOCASE,
                    Level INTEGER NOT NULL DEFAULT 1,
                    Count INTEGER NOT NULL DEFAULT 1,
                    LastChanged TEXT,
                    PRIMARY KEY (ListKey, Reference)
                );

                -- The opposite decision: take this object OUT of the list.
                --
                -- A SIBLING OF THE TABLE ABOVE, NOT A COLUMN ON IT. Restore and remove are opposite
                -- answers about the same (list, reference), and one table could not say which was
                -- meant. Restore also carries Level and Count because putting something back needs
                -- them; removing does not.
                --
                -- NO LEVEL OR COUNT, AND THAT IS NOT AN OMISSION. SkyPatcher's removeFromLLs takes an
                -- OBJECT and removes every occurrence of it - the documentation's own example reads
                -- removes ALL Potion of Extreme Healing from the filtered LL. The optional
                -- level/count operators narrow which occurrences are hit, they do not pick one of
                -- several identical entries. So a decision here is
                -- always about an object, never about a row - see LeveledListEntry's ordinal key for
                -- why those are different things, and why the UI has to group identical entries
                -- rather than offer them as separate ticks.
                --
                -- THIS IS WHERE THE ADDITIVE GUARANTEE ENDS. Everything else this tool writes only
                -- ever adds, which is what makes the patch safe to regenerate and safe to run beside
                -- anything else (see LeveledListRestoreStore's own comment). A row in here removes
                -- content another mod put there. Kept in its own table, its own screen and its own
                -- section of the patch report for exactly that reason.
                CREATE TABLE IF NOT EXISTS LeveledListRemovedEntry (
                    ListKey TEXT NOT NULL COLLATE NOCASE,
                    Reference TEXT NOT NULL COLLATE NOCASE,
                    LastChanged TEXT,
                    PRIMARY KEY (ListKey, Reference)
                );

                CREATE TABLE IF NOT EXISTS ContainerRemovedEntry (
                    ContainerKey TEXT NOT NULL COLLATE NOCASE,
                    Reference TEXT NOT NULL COLLATE NOCASE,
                    LastChanged TEXT,
                    PRIMARY KEY (ContainerKey, Reference)
                );

                -- Take this recipe out of the game. Third scope of the same removal store.
                --
                -- ItemKey is the ARMO/WEAP the recipe belongs to, Reference the COBJ. Only a MOD's
                -- recipe is ever listed here: one the tool created is simply deleted, row and all,
                -- because nothing in any plugin would bring it back. A row here becomes an ESP
                -- override carrying the Deleted record flag.
                CREATE TABLE IF NOT EXISTS RecipeRemovedEntry (
                    ItemKey TEXT NOT NULL COLLATE NOCASE,
                    Reference TEXT NOT NULL COLLATE NOCASE,
                    LastChanged TEXT,
                    PRIMARY KEY (ItemKey, Reference)
                );

                -- Which recipe of a kind the item's editor shows and edits.
                --
                -- The order recipes come out of the database in is stable in practice, so this is
                -- NOT about scan drift: it is about the make-this-the-main-one button surviving a
                -- restart. Absent means the first one, which is what every item without a decision
                -- gets.
                --
                -- Keyed by the ITEM, not by the recipe's created object: a breakdown recipe creates
                -- a material, so its CreatedItem says nothing about which item it belongs to.
                CREATE TABLE IF NOT EXISTS MainRecipe (
                    ItemKey TEXT NOT NULL COLLATE NOCASE,
                    Kind TEXT NOT NULL COLLATE NOCASE,
                    CobjKey TEXT NOT NULL COLLATE NOCASE,
                    LastChanged TEXT,
                    PRIMARY KEY (ItemKey, Kind)
                );

                CREATE TABLE IF NOT EXISTS LeveledListEdit (
                    ListKey TEXT PRIMARY KEY COLLATE NOCASE,
                    ChanceNone INTEGER,
                    CalcFlagMode TEXT,
                    LastChanged TEXT
                );

                CREATE TABLE IF NOT EXISTS UserLeveledListTarget (
                    ListKey TEXT NOT NULL COLLATE NOCASE,
                    TargetKey TEXT NOT NULL COLLATE NOCASE,
                    TargetKind TEXT NOT NULL,
                    Level INTEGER NOT NULL DEFAULT 1,
                    Count INTEGER NOT NULL DEFAULT 1,
                    PRIMARY KEY (ListKey, TargetKey)
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


                CREATE TABLE IF NOT EXISTS Npc (
                    Key TEXT PRIMARY KEY COLLATE NOCASE,
                    EditorID TEXT NOT NULL,
                    Name TEXT,
                    ShortName TEXT,

                    -- The EditorID of the plugin that defines the NPC; an override may have renamed
                    -- it, and SkyPatcher's EditorID filters still see this one.
                    OriginalEditorID TEXT,

                    -- Tree levels. Every NPC has exactly one class (measured: 0 without), which is
                    -- what makes Plugin -> Class -> NPC a tree with no duplicates and no catch-all
                    -- node - see NPC-Plan.md section 8.
                    ClassKey TEXT COLLATE NOCASE,
                    RaceKey TEXT COLLATE NOCASE,

                    -- Level. Exactly one of Level / LevelMult is meaningful and UsesPcLevelMult says
                    -- which: the record stores either a fixed level or a multiplier of the player's.
                    -- CalcMin/CalcMax only bound the multiplier case.
                    UsesPcLevelMult INTEGER NOT NULL DEFAULT 0,
                    Level INTEGER,
                    LevelMult REAL,
                    CalcMinLevel INTEGER,
                    CalcMaxLevel INTEGER,

                    -- Two different things, both scanned, because it is not yet established which of
                    -- them SkyPatcher's ""changeStats=health=200"" actually writes (NPC-Plan.md
                    -- section 7, offene Frage 4). Guessing one and dropping the other would make
                    -- that question unanswerable from the tool's own data.
                    Health INTEGER,
                    Magicka INTEGER,
                    Stamina INTEGER,
                    HealthOffset INTEGER,
                    MagickaOffset INTEGER,
                    StaminaOffset INTEGER,

                    -- Raw ACBS flag bits and the template flags. Edited through setFlags/removeFlags
                    -- and setTemplateFlags/removeTemplateFlags, so the diff is bit against bit.
                    Flags INTEGER NOT NULL DEFAULT 0,
                    TemplateFlags INTEGER NOT NULL DEFAULT 0,

                    -- THE TEMPLATE TRAP (NPC-Plan.md section 4): 61,4 % of NPCs inherit their stats
                    -- from the record this points at, and for those a stat edit writes a field the
                    -- game never reads. Scanned so the editor can say so and the generator can warn.
                    -- Not editable - SkyPatcher can change the template FLAGS but not the link.
                    TemplateKey TEXT COLLATE NOCASE,

                    VoiceKey TEXT COLLATE NOCASE,
                    DefaultOutfitKey TEXT COLLATE NOCASE,
                    SleepOutfitKey TEXT COLLATE NOCASE,
                    DeathItemKey TEXT COLLATE NOCASE,
                    SkinKey TEXT COLLATE NOCASE,
                    CombatStyleKey TEXT COLLATE NOCASE,
                    CrimeFactionKey TEXT COLLATE NOCASE,

                    Weight REAL,
                    Height REAL,

                    -- AI data, stored as the token the matching operation takes (""aggressive"",
                    -- ""brave"", ...) rather than as a number, so nothing has to translate later.
                    --
                    -- Morality comes from the record field the layout calls Responsibility - its
                    -- values are AnyCrime/ViolenceAgainstEnemies/PropertyCrimeOnly/NoCrime, which is
                    -- exactly what setMorality takes. There is no second, numeric responsibility
                    -- field; an earlier draft of this table had one and it could never have been
                    -- filled.
                    Aggression TEXT,
                    Confidence TEXT,
                    Assistance TEXT,
                    Morality TEXT,
                    Mood TEXT,

                    -- The one AI value with no operation of its own.
                    EnergyLevel INTEGER,

                    Keywords TEXT,

                    IsEditedName TEXT,
                    IsEditedShortName TEXT,
                    IsEditedClassKey TEXT,
                    IsEditedRaceKey TEXT,
                    IsEditedUsesPcLevelMult INTEGER,
                    IsEditedLevel INTEGER,
                    IsEditedCalcMinLevel INTEGER,
                    IsEditedCalcMaxLevel INTEGER,
                    IsEditedHealth INTEGER,
                    IsEditedMagicka INTEGER,
                    IsEditedStamina INTEGER,
                    IsEditedFlags INTEGER,
                    IsEditedTemplateFlags INTEGER,
                    IsEditedVoiceKey TEXT,
                    IsEditedDefaultOutfitKey TEXT,
                    IsEditedSleepOutfitKey TEXT,
                    IsEditedDeathItemKey TEXT,
                    IsEditedSkinKey TEXT,
                    IsEditedWeight REAL,
                    IsEditedHeight REAL,
                    IsEditedAggression TEXT,
                    IsEditedConfidence TEXT,
                    IsEditedAssistance TEXT,
                    IsEditedMorality TEXT,
                    IsEditedMood TEXT,
                    IsEditedKeywords TEXT,

                    -- N-P6. These two have NO SkyPatcher operation: reaching them needs an ESP
                    -- override of the NPC record, which is why N-P1 deliberately left them without a
                    -- shadow column. That phase has arrived, and the columns with it - but the price
                    -- has not changed, so the generator names every override it writes in the report
                    -- (NPC-Plan.md section 1 and 3.2).
                    IsEditedCombatStyleKey TEXT,
                    IsEditedCrimeFactionKey TEXT,

                    -- Set when a child row (skills, factions) was edited, so ""is this NPC edited""
                    -- stays one read of one row - the same reason COBJ has ConditionsEdited.
                    SkillsEdited INTEGER NOT NULL DEFAULT 0,
                    FactionsEdited INTEGER NOT NULL DEFAULT 0,

                    -- Spells, perks and inventory (N-P3). One flag for the three: they are edited
                    -- on one screen and a column each would always be set together.
                    ListsEdited INTEGER NOT NULL DEFAULT 0,

                    IsEdited INTEGER DEFAULT 0,
                    Active INTEGER NOT NULL DEFAULT 1,
                    LastChanged TEXT,
                    LastPatched TEXT
                );

                -- The 18 skills, one row each rather than 18 columns: that is the shape
                -- ""changeSkills=twohanded=45,onehanded=50"" is built from, and a row per skill keeps
                -- the diff to ""which rows differ"" instead of 18 column comparisons.
                --
                -- Skill is stored as SkyPatcher's own spelling (""twohanded"", ""heavyarmor""), so the
                -- rule builder never has to translate.
                CREATE TABLE IF NOT EXISTS NpcSkills (
                    NpcKey TEXT NOT NULL COLLATE NOCASE,
                    Skill TEXT NOT NULL COLLATE NOCASE,
                    Value INTEGER,

                    -- Read-only twin of Value: the record carries both a base value and an offset,
                    -- and changeSkills only addresses one of them. Scanned so the difference stays
                    -- visible rather than being silently averaged away.
                    Offset INTEGER,

                    IsEditedValue INTEGER,

                    PRIMARY KEY (NpcKey, Skill)
                );

                -- Faction membership. A membership is faction PLUS rank
                -- (factionsToAdd=Skyrim.esm|0001CBED=0), so rank is a column and not an afterthought
                -- - measured on the real load order, 857 of 14.832 memberships carry a rank other
                -- than 0, which is few but not none.
                --
                -- No NPC lists the same faction twice, so (NpcKey, FactionKey) is a real key.
                CREATE TABLE IF NOT EXISTS NpcFactions (
                    NpcKey TEXT NOT NULL COLLATE NOCASE,
                    FactionKey TEXT NOT NULL COLLATE NOCASE,
                    Rank INTEGER NOT NULL DEFAULT 0,
                    PRIMARY KEY (NpcKey, FactionKey)
                );

                -- Same lazy-snapshot pattern as WornRestrictionKeywords_Original: written once when
                -- an NPC's faction list is first edited, so the rule builder can diff added against
                -- removed without a rescan.
                CREATE TABLE IF NOT EXISTS NpcFactions_Original (
                    NpcKey TEXT NOT NULL COLLATE NOCASE,
                    FactionKey TEXT NOT NULL COLLATE NOCASE,
                    Rank INTEGER NOT NULL DEFAULT 0
                );

                -- ===================================================================
                -- Reference catalogues for the NPC editor
                -- ===================================================================
                --
                -- Name lookups for the records an NPC points at. They live here rather than in
                -- formid.db for the same reason LeveledList does: formid.db is dropped and rebuilt
                -- on every scan, and everything that reads NPC data reads item.db anyway.
                --
                -- Sizes in the real load order: 1.458 factions, 180 classes, 335 races, 687 outfits,
                -- 186 voice types, 270 combat styles. Small enough to load whole.
                CREATE TABLE IF NOT EXISTS Factions (
                    Key TEXT PRIMARY KEY COLLATE NOCASE,
                    EditorID TEXT,
                    Name TEXT,
                    Active INTEGER NOT NULL DEFAULT 1
                );

                -- Classes carry the WEIGHTS that decide an auto-calculated NPC's attributes.
                --
                -- Measured in game and the reason this exists: 4.440 of 6.580 NPCs (67,5 %) have
                -- AutoCalcStats set, and for those the engine ignores the stored Health/Magicka/
                -- Stamina entirely and recomputes them from race, class and level. A record said
                -- 35 Health where the game showed 300. Reading the record is therefore not enough
                -- to show a true number - the inputs of that calculation have to be read too.
                --
                -- StatWeights distribute the per-level gain between the three attributes;
                -- SkillWeights decide how fast each skill climbs. Both come from CLAS.
                CREATE TABLE IF NOT EXISTS Classes (
                    Key TEXT PRIMARY KEY COLLATE NOCASE,
                    EditorID TEXT,
                    Name TEXT,
                    HealthWeight INTEGER,
                    MagickaWeight INTEGER,
                    StaminaWeight INTEGER,
                    Active INTEGER NOT NULL DEFAULT 1
                );

                -- One row per class and skill, for the skill half of the same calculation. Separate
                -- table rather than 18 columns: the record stores a sparse dictionary, and a class
                -- naming three skills should not carry fifteen NULLs.
                CREATE TABLE IF NOT EXISTS ClassSkillWeights (
                    ClassKey TEXT NOT NULL COLLATE NOCASE,
                    Skill TEXT NOT NULL,
                    Weight INTEGER NOT NULL DEFAULT 0,
                    PRIMARY KEY (ClassKey, Skill)
                );

                -- Races carry the BASE values the calculation starts from, before any level.
                CREATE TABLE IF NOT EXISTS Races (
                    Key TEXT PRIMARY KEY COLLATE NOCASE,
                    EditorID TEXT,
                    Name TEXT,
                    StartingHealth REAL,
                    StartingMagicka REAL,
                    StartingStamina REAL,
                    Keywords TEXT,
                    Flags INTEGER,
                    Active INTEGER NOT NULL DEFAULT 1
                );

                -- A race grants up to seven skill bonuses (Nord +10 two-handed, and so on). Stored
                -- like the class weights above and for the same reason: seven slots in the record,
                -- most of them empty on most races.
                CREATE TABLE IF NOT EXISTS RaceSkillBoosts (
                    RaceKey TEXT NOT NULL COLLATE NOCASE,
                    Skill TEXT NOT NULL,
                    Boost INTEGER NOT NULL DEFAULT 0,
                    PRIMARY KEY (RaceKey, Skill)
                );

                CREATE TABLE IF NOT EXISTS Outfits (
                    Key TEXT PRIMARY KEY COLLATE NOCASE,
                    EditorID TEXT,
                    Active INTEGER NOT NULL DEFAULT 1
                );

                CREATE TABLE IF NOT EXISTS VoiceTypes (
                    Key TEXT PRIMARY KEY COLLATE NOCASE,
                    EditorID TEXT,
                    Active INTEGER NOT NULL DEFAULT 1
                );

                CREATE TABLE IF NOT EXISTS CombatStyles (
                    Key TEXT PRIMARY KEY COLLATE NOCASE,
                    EditorID TEXT,
                    Active INTEGER NOT NULL DEFAULT 1
                );


                -- ===================================================================
                -- The remaining NPC list fields (Prio 8 / N-P3, docs/NPC-Plan.md)
                -- ===================================================================
                --
                -- Each of the three follows the faction pattern: the live rows are what the editor
                -- holds, an _Original snapshot is taken on the first edit, and the diff between them
                -- is what the rule builder turns into add/remove operations.

                -- Spells, shouts and leveled spells. The RECORD keeps all three in one list
                -- (ActorEffect); SkyPatcher addresses them with three different operations
                -- (spellsToAdd, shoutsToAdd, levSpellsToAdd). Kind is decided at scan time by looking
                -- the key up in the three catalogues below - the alternative would be deciding it in
                -- the rule builder, which has no way to know.
                CREATE TABLE IF NOT EXISTS NpcSpells (
                    NpcKey TEXT NOT NULL COLLATE NOCASE,
                    SpellKey TEXT NOT NULL COLLATE NOCASE,

                    -- ""spell"", ""shout"" or ""levspell"". Empty when the key resolved to none of the
                    -- three, which is a dead reference and must not be guessed at.
                    Kind TEXT NOT NULL DEFAULT '',

                    PRIMARY KEY (NpcKey, SpellKey)
                );

                CREATE TABLE IF NOT EXISTS NpcSpells_Original (
                    NpcKey TEXT NOT NULL COLLATE NOCASE,
                    SpellKey TEXT NOT NULL COLLATE NOCASE,
                    Kind TEXT NOT NULL DEFAULT ''
                );

                -- Perks. The record carries a rank per perk, but perksToAdd takes only the perk -
                -- so the rank is scanned and shown and never patched.
                --
                -- There is no perksToRemove. A perk the plugins gave an NPC therefore cannot be
                -- taken away by any patch this tool writes, and the editor does not offer it: an
                -- edit the patch cannot carry is worse than no edit (NPC-Plan.md section 3.2).
                CREATE TABLE IF NOT EXISTS NpcPerks (
                    NpcKey TEXT NOT NULL COLLATE NOCASE,
                    PerkKey TEXT NOT NULL COLLATE NOCASE,
                    Rank INTEGER NOT NULL DEFAULT 0,
                    PRIMARY KEY (NpcKey, PerkKey)
                );

                CREATE TABLE IF NOT EXISTS NpcPerks_Original (
                    NpcKey TEXT NOT NULL COLLATE NOCASE,
                    PerkKey TEXT NOT NULL COLLATE NOCASE,
                    Rank INTEGER NOT NULL DEFAULT 0
                );

                -- Inventory. objectsToAdd carries a count, objectsToRemove does not.
                --
                -- Keyed by (NpcKey, ItemKey) rather than positionally, unlike ContainerEntry: a
                -- record CAN list the same item twice, but the two operations address an item by its
                -- key, so two rows for one key could not be patched apart anyway. The counts are
                -- summed at scan time instead, which is what the game ends up giving the NPC.
                CREATE TABLE IF NOT EXISTS NpcItems (
                    NpcKey TEXT NOT NULL COLLATE NOCASE,
                    ItemKey TEXT NOT NULL COLLATE NOCASE,
                    Count INTEGER NOT NULL DEFAULT 1,
                    PRIMARY KEY (NpcKey, ItemKey)
                );

                CREATE TABLE IF NOT EXISTS NpcItems_Original (
                    NpcKey TEXT NOT NULL COLLATE NOCASE,
                    ItemKey TEXT NOT NULL COLLATE NOCASE,
                    Count INTEGER NOT NULL DEFAULT 1
                );

                -- The catalogues these three point at. Spells, shouts and leveled spells are also
                -- what the scan classifies ActorEffect entries against, so they are not optional.
                --
                -- SPELLS IS TWO THINGS IN ONE TABLE, deliberately (docs/Spells-Plan.md). It began as
                -- a name catalogue for the NPC spell lists and is also the record the Spells tab
                -- edits. One row per SPEL rather than a second table on the same keys: duplicate rows
                -- for one record are what produced disagreeing copies in the enchantment table, and
                -- the NPC side reads named columns, so it never notices.
                CREATE TABLE IF NOT EXISTS Spells (
                    Key TEXT PRIMARY KEY COLLATE NOCASE,
                    EditorID TEXT,
                    Name TEXT,

                    -- SPIT. SpellType, TargetType and EquipType have no SkyPatcher operation, so
                    -- they are shown, never patched. SpellType is also what the tree groups by.
                    SpellType TEXT,
                    CastType TEXT,
                    TargetType TEXT,
                    EquipType TEXT,
                    BaseCost INTEGER,
                    ChargeTime REAL,
                    CastDuration REAL,

                    -- NOT ""Range"": that is a reserved word in SQLite's window-function syntax.
                    SpellRange REAL,

                    Description TEXT,
                    HalfCostPerkKey TEXT,

                    -- The raw dword, NOT a name list. SkyPatcher's flag NAMES do not match
                    -- Mutagen's, and three bits real spells set have no name in either - see
                    -- Services/SpellFlags.cs.
                    Flags INTEGER,

                    -- Comma-joined keys, as Armor/Weapons store theirs.
                    Keywords TEXT,

                    -- How many of this spell's effect entries SpellEffects could not hold:
                    -- PRIMARY KEY(SpellKey, MagicEffectKey) keeps one row per pair, and a SPEL may
                    -- name one effect twice. Counted at scan time because the duplicates are gone
                    -- afterwards. mgefsToChange cannot address the second instance either.
                    DroppedEffectRows INTEGER NOT NULL DEFAULT 0,

                    -- Shadow columns only for what SkyPatcher can patch on a SCANNED record.
                    IsEditedName TEXT,
                    IsEditedBaseCost INTEGER,
                    IsEditedCastType TEXT,
                    IsEditedChargeTime REAL,
                    IsEditedHalfCostPerkKey TEXT,
                    IsEditedFlags INTEGER,
                    IsEditedKeywords TEXT,

                    IsEdited INTEGER DEFAULT 0,
                    Active INTEGER NOT NULL DEFAULT 1,
                    EffectsEdited INTEGER NOT NULL DEFAULT 0,
                    KeywordsEdited INTEGER NOT NULL DEFAULT 0,

                    -- 0 = created in this tool, exists in no plugin.
                    Original INTEGER NOT NULL DEFAULT 1,
                    LastChanged TEXT,
                    LastPatched TEXT,
                    Uid TEXT
                );

                -- One row per effect entry of a spell. Same shape as EnchantmentEffects: Mutagen
                -- gives Spell and ObjectEffect the SAME effect type.
                CREATE TABLE IF NOT EXISTS SpellEffects (
                    SpellKey TEXT NOT NULL COLLATE NOCASE,
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

                    PRIMARY KEY (SpellKey, MagicEffectKey)
                );

                -- The pre-edit snapshot, declared once in ChildTables.SpellEffects.
                CREATE TABLE IF NOT EXISTS SpellEffects_Original (
                    SpellKey TEXT NOT NULL COLLATE NOCASE,
                    MagicEffectKey TEXT NOT NULL COLLATE NOCASE,
                    EditorID TEXT,
                    Name TEXT,
                    Magnitude REAL,
                    Duration INTEGER,
                    Area INTEGER
                );

                CREATE TABLE IF NOT EXISTS Shouts (
                    Key TEXT PRIMARY KEY COLLATE NOCASE,
                    EditorID TEXT,
                    Name TEXT,
                    Active INTEGER NOT NULL DEFAULT 1
                );

                -- Which spell each word of a shout casts. Read-only, for the Spells tab's labels:
                -- the game shows ""Unrelenting Force"", the record behind word 2 is a Voice-type
                -- SPEL with an EditorID nobody recognises. The shout's own data (recovery time,
                -- word order) has no SkyPatcher operation and is not edited anywhere.
                CREATE TABLE IF NOT EXISTS ShoutWords (
                    ShoutKey TEXT NOT NULL COLLATE NOCASE,
                    Ordinal INTEGER NOT NULL,
                    SpellKey TEXT NOT NULL COLLATE NOCASE,
                    PRIMARY KEY (ShoutKey, Ordinal)
                );

                CREATE TABLE IF NOT EXISTS LeveledSpells (
                    Key TEXT PRIMARY KEY COLLATE NOCASE,
                    EditorID TEXT,
                    Active INTEGER NOT NULL DEFAULT 1
                );

                DROP TABLE IF EXISTS NpcPerkCatalogue;

                CREATE INDEX IF NOT EXISTS idx_Npc_ClassKey ON Npc(ClassKey);
                CREATE INDEX IF NOT EXISTS idx_NpcFactions_FactionKey ON NpcFactions(FactionKey);

                -- ===================================================================
                -- Levelled CHARACTER lists (LVLN) — G8, docs/NPC-Gruppen-Plan.md section 2
                -- ===================================================================
                --
                -- Not the same table as LeveledList above: that one holds LVLI, the item lists the
                -- container tab is built on. This one holds LVLN, and it exists for one reason -
                -- 1.650 NPCs take their stats from a levelled character list rather than from a
                -- record, and without knowing what is IN that list the tool can only say ""no single
                -- value"" where it could say ""35 - 497, 12 candidates"".
                --
                -- Deliberately smaller than its LVLI sibling: no ChanceNone, no Flags, no Global and
                -- no lost-entry history. Those exist because the container tab EDITS leveled lists;
                -- this one is read to answer a question, and a column nothing reads is a column that
                -- goes stale without anyone noticing.
                CREATE TABLE IF NOT EXISTS LeveledNpc (
                    Key TEXT PRIMARY KEY COLLATE NOCASE,
                    EditorID TEXT NOT NULL,
                    Active INTEGER NOT NULL DEFAULT 1
                );

                -- One entry per row, positionally keyed for the same reason LeveledListEntry is: an
                -- entry has no identity of its own, and the same NPC may sit in one list twice at two
                -- levels.
                --
                -- Reference may point at another LVLN rather than at an NPC - the lists nest, the same
                -- way the item lists do. Resolving that is the reader's job (NpcStatResolver).
                CREATE TABLE IF NOT EXISTS LeveledNpcEntry (
                    ListKey TEXT NOT NULL COLLATE NOCASE,
                    Ordinal INTEGER NOT NULL,
                    Reference TEXT NOT NULL COLLATE NOCASE,
                    Level INTEGER NOT NULL DEFAULT 1,
                    Count INTEGER NOT NULL DEFAULT 1,
                    PRIMARY KEY (ListKey, Ordinal)
                );

                CREATE INDEX IF NOT EXISTS idx_LeveledNpcEntry_ListKey ON LeveledNpcEntry(ListKey);

                -- ===================================================================
                -- NPC groups (G2, docs/NPC-Gruppen-Plan.md section 4)
                -- ===================================================================
                --
                -- The work unit of the NPC patcher is a NAMED FILTER SET, not the single NPC: a group
                -- is edited once and reaches its members at runtime. A single NPC is the degenerate
                -- case - no predicate, one add-override - so there is one editor and one rule builder
                -- rather than two (section 4.1).
                --
                -- WHAT IS NOT IN HERE IS THE POINT: there is no member table. Membership is computed
                -- from the predicate on every read (NpcGroupResolver). A stored member list would go
                -- stale on the next rescan, and a saved change would then reach NPCs nobody ever
                -- looked at - a new mod, and ""Bandit Stufe 3"" quietly grows from 54 members to 71
                -- (section 8). The only member rows kept are the snapshot at the bottom, which exists
                -- to REPORT that growth rather than to define the group.
                CREATE TABLE IF NOT EXISTS NpcGroup (
                    Id          INTEGER PRIMARY KEY,
                    Name        TEXT NOT NULL,

                    Origin      TEXT NOT NULL,

                    Seed        TEXT,

                    SortOrder   INTEGER NOT NULL DEFAULT 0,
                    Notes       TEXT,
                    Active      INTEGER NOT NULL DEFAULT 1
                );

                -- The definition. Clauses of the same axis and mode combine per axis - AND where an
                -- NPC can hold several values (factions, EditorID substrings, flags), OR where it
                -- holds exactly one (class, race, mod, gender), because ANDing two classes could only
                -- ever yield an empty group. 'includeOr' is SkyPatcher's own second reading, the one
                -- behind filterByFactionsOr; NpcGroupResolver.MultiValued is where that lives.
                CREATE TABLE IF NOT EXISTS NpcGroupPredicate (
                    GroupId     INTEGER NOT NULL,
                    Axis        TEXT NOT NULL,     -- class|race|faction|editorid|mod|flag|gender
                    Mode        TEXT NOT NULL,     -- include|includeOr|exclude
                    Value       TEXT NOT NULL COLLATE NOCASE,
                    -- '' = written by hand, 'autoexclude' = written by the overlap auto-check, which
                    -- has to recognise its own lines to take them back or replace them.
                    Origin      TEXT NOT NULL DEFAULT '',
                    PRIMARY KEY (GroupId, Axis, Mode, Value)
                );

                -- Hand corrections, kept apart from the predicate so a re-seed cannot throw them away.
                --
                -- The key is (GroupId, NpcKey) WITHOUT Mode on purpose: one NPC cannot be both added
                -- and removed in the same group, so the contradiction is impossible rather than
                -- resolved by whichever row happens to be read second.
                CREATE TABLE IF NOT EXISTS NpcGroupMemberOverride (
                    GroupId     INTEGER NOT NULL,
                    NpcKey      TEXT NOT NULL COLLATE NOCASE,
                    Mode        TEXT NOT NULL,     -- add|remove
                    PRIMARY KEY (GroupId, NpcKey)
                );

                -- What the group changes about a value. Low and High are TEXT because the four kinds
                -- carry four different things: a number, the two ends of a span, a multiplier like
                -- 1.5, or the N of calcHealth=N (section 5.2).
                CREATE TABLE IF NOT EXISTS NpcGroupValue (
                    GroupId     INTEGER NOT NULL,
                    Field       TEXT NOT NULL,     -- health|magicka|stamina|level|...
                    Kind        TEXT NOT NULL,     -- direct|span|mult|calc
                    Low         TEXT,
                    High        TEXT,
                    PRIMARY KEY (GroupId, Field)
                );

                -- Perks, spells, items, factions and keywords the group adds or removes. Extra is the
                -- rank of a faction or the count of an item, and NULL where the operation takes
                -- neither - perksToAdd carries no rank, objectsToRemove no count.
                CREATE TABLE IF NOT EXISTS NpcGroupList (
                    GroupId     INTEGER NOT NULL,
                    Kind        TEXT NOT NULL,     -- perk|spell|item|faction|keyword
                    TargetKey   TEXT NOT NULL COLLATE NOCASE,
                    Mode        TEXT NOT NULL,     -- add|remove
                    Extra       TEXT,
                    PRIMARY KEY (GroupId, Kind, TargetKey, Mode)
                );

                CREATE TABLE IF NOT EXISTS NpcGroupMemberSnapshot (
                    GroupId     INTEGER NOT NULL,
                    NpcKey      TEXT NOT NULL COLLATE NOCASE,
                    PRIMARY KEY (GroupId, NpcKey)
                );

                -- Every child table is read per group, which is how the editor loads one and how the
                -- rule builder walks all of them.
                CREATE INDEX IF NOT EXISTS idx_NpcGroupPredicate_GroupId ON NpcGroupPredicate(GroupId);
                CREATE INDEX IF NOT EXISTS idx_NpcGroupMemberOverride_GroupId ON NpcGroupMemberOverride(GroupId);
                CREATE INDEX IF NOT EXISTS idx_NpcGroupValue_GroupId ON NpcGroupValue(GroupId);
                CREATE INDEX IF NOT EXISTS idx_NpcGroupList_GroupId ON NpcGroupList(GroupId);
                CREATE INDEX IF NOT EXISTS idx_NpcGroupMemberSnapshot_GroupId ON NpcGroupMemberSnapshot(GroupId);

                CREATE TABLE IF NOT EXISTS MagicEffects (
                    Key TEXT PRIMARY KEY COLLATE NOCASE,
                    EditorID TEXT,
                    Name TEXT NOT NULL,
                    HasMagnitude INTEGER,
                    HasDuration INTEGER,
                    HasArea INTEGER,
                    CastType TEXT,
                    TargetType TEXT,

                    -- The magic school, skill tier and base cost (docs/Spells-Plan.md). They live
                    -- here because a spell has none of them: the game files a spell under the
                    -- school of its costliest effect. ""None"" is a real value (script effects,
                    -- abilities, diseases), not missing data.
                    MagicSkill TEXT,
                    MinimumSkillLevel INTEGER,
                    BaseCost REAL,

                    -- The in-game description (DNAM), with the game's <mag>/<dur>/<area> placeholders.
                    Description TEXT,

                    Active INTEGER NOT NULL DEFAULT 1
                );

                -- ===================================================================
                -- Mesh index (render stage 1) — docs/Render-Plan.md
                -- ===================================================================
                --
                -- WHAT THIS IS AND IS NOT: an index of which model file each record names, and where
                -- that file actually lies. It holds no geometry. Parsing a NIF is stage 2, and the
                -- split is deliberate - the index is verifiable on its own (""4.460 paths, 32 resolve
                -- to nothing""), months before anything can draw a triangle.
                --
                -- ARMO HAS NO MODEL PATH. That is the whole reason two tables stand between an armor
                -- and its mesh: ARMO points at a list of ARMA (its Armature), and only ARMA carries
                -- WorldModel/FirstPersonModel. Measured on vanilla: 2.762 ARMO hold 4.298 armature
                -- links into 766 ARMA, and 702 of those armors name more than one. Weapons are the
                -- easy case - WEAP carries its own MODL - and they go through the same MeshRef table
                -- rather than a second one, because ""which file does this record name"" is one
                -- question whoever asks it.
                CREATE TABLE IF NOT EXISTS ArmorAddon (
                    Key TEXT PRIMARY KEY COLLATE NOCASE,
                    EditorID TEXT,
                    Active INTEGER NOT NULL DEFAULT 1
                );

                -- ARMO -> ARMA, positionally keyed. Ordinal rather than (ArmorKey, AddonKey): the
                -- list is ordered and an armor may legitimately name the same addon twice, so a
                -- composite key on the pair would drop the second one with a UNIQUE failure.
                CREATE TABLE IF NOT EXISTS ArmorArmature (
                    ArmorKey TEXT NOT NULL COLLATE NOCASE,
                    Ordinal INTEGER NOT NULL,
                    AddonKey TEXT NOT NULL COLLATE NOCASE,
                    PRIMARY KEY (ArmorKey, Ordinal)
                );

                -- Reached from the addon side too: ""which armors use this mesh"" is how a geometry
                -- cache decides what is worth keeping.
                CREATE INDEX IF NOT EXISTS idx_ArmorArmature_AddonKey ON ArmorArmature(AddonKey);

                -- One row per DISTINCT model path. This dedupe is the point of the table, not a
                -- nicety: measured over 124 plugins, 25.055 record->path references collapse onto
                -- 4.460 distinct paths, so parsing per reference would do the same work 5,6 times.
                --
                -- PathNorm is lowercase, backslashes, 'meshes\' prefixed exactly once - see
                -- Services/MeshPath, which is the ONLY place that spelling is produced. COLLATE
                -- NOCASE on top of the lowercasing is not redundant: it is what keeps a row written
                -- by an older build from becoming a second row now.
                --
                -- ROWS ARE NEVER DELETED, and there is no Active column. MeshId is a stable handle
                -- that stage 2's geometry cache will hang off, so a path dropping out of the load
                -- order must not renumber anything - and the cached geometry for a mesh is still
                -- correct when the mod comes back. SourceKind/SourceRef are the part a rescan
                -- refreshes; they are what goes stale, not the identity.
                CREATE TABLE IF NOT EXISTS Mesh (
                    MeshId INTEGER PRIMARY KEY,
                    PathNorm TEXT NOT NULL UNIQUE COLLATE NOCASE,

                    -- Services.MeshSourceKind: 0 unresolved, 1 loose file, 2 archive.
                    SourceKind INTEGER NOT NULL DEFAULT 0,

                    -- The mod folder name for a loose file, the .bsa file name for an archive.
                    SourceRef TEXT,

                    -- Stage 2's own bookkeeping, written by nothing yet: 0 unparsed, 1 parsed, 2
                    -- parse failed. Here now so the index does not need a migration the day the
                    -- parser lands.
                    GeomState INTEGER NOT NULL DEFAULT 0
                );

                -- Record -> mesh, one row per model SLOT. The slot is what separates the four paths
                -- an ARMA can carry (male/female x world/first-person) and the two a weapon can
                -- (MODL, scope), so the same record can name several meshes without the rows
                -- colliding.
                CREATE TABLE IF NOT EXISTS MeshRef (
                    RecordKey TEXT NOT NULL COLLATE NOCASE,
                    Slot TEXT NOT NULL,
                    MeshId INTEGER NOT NULL,
                    PRIMARY KEY (RecordKey, Slot)
                );

                -- ""Who references this mesh"" - needed to report a dead path against the records that
                -- name it, rather than as a bare file name nobody recognises.
                CREATE INDEX IF NOT EXISTS idx_MeshRef_MeshId ON MeshRef(MeshId);

                -- Parsed geometry, one row per SHAPE. A NIF is not one mesh: measured over the load
                -- order, 4.427 files hold 11.393 shapes, so a per-file row would have to concatenate
                -- them and lose the names.
                --
                -- WHY THIS IS FILLED LAZILY AND NOT BY THE SCAN: the whole load order comes to roughly
                -- 17,7 million vertices and 27 million triangles - about 890 MB of BLOB if every mesh
                -- were cached. Parsing on first view instead costs 0,83 ms per mesh and stores only
                -- what someone actually looked at, while keeping the same ""only once"" property,
                -- because Mesh.MeshId is stable across scans.
                --
                -- The arrays are raw little-endian float32/int32, written and read by
                -- Services/MeshGeometryStore and by nothing else. Normals and UVs are nullable
                -- because a real mesh may carry neither: 720 shapes have no normals and 200 no UVs.
                CREATE TABLE IF NOT EXISTS MeshGeom (
                    MeshId    INTEGER NOT NULL,
                    ShapeIdx  INTEGER NOT NULL,
                    Name      TEXT,
                    VertCount INTEGER NOT NULL,
                    TriCount  INTEGER NOT NULL,

                    -- 3 float32 per vertex.
                    Positions BLOB NOT NULL,

                    -- 3 float32 per vertex, or NULL.
                    Normals   BLOB,

                    -- 2 float32 per vertex, or NULL.
                    Uvs       BLOB,

                    -- 3 int32 per triangle. int32 rather than the uint16 a NIF stores, because a
                    -- consolidated skin partition can exceed 65.535 vertices in one shape.
                    Indices   BLOB NOT NULL,

                    -- Was this shape placed by a PARENT node rather than by its own transform?
                    --
                    -- Stored rather than recomputed because it is a diagnostic, and a diagnostic that
                    -- only survives until the first cache hit is worse than none: the preview would warn
                    -- once and then go quiet forever about the same mesh. Measured: 390 of 11.393 shapes
                    -- are in this state, 66 of them moved by more than a unit and one by 90,8.
                    MovedByParent INTEGER NOT NULL DEFAULT 0,

                    -- The normal map, kept for its ALPHA channel - that is where Skyrim stores the
                    -- specular mask. Present for 97,3 % of shapes, against 57,0 % for a separate _m.dds.
                    NormalTexture TEXT,

                    -- Slot 5, the _m.dds. A second answer to ""where should this shine"": the normal
                    -- map's alpha is the direct specular mask, this one says where the material
                    -- reflects its surroundings. Neither alone covers both metal and latex.
                    EnvironmentMask TEXT,

                    -- The diffuse texture this shape names, normalised. Stored rather than re-derived
                    -- because it comes out of the same parse and nothing else knows it: a shape has no
                    -- other route back to its NIF once the geometry is cached.
                    DiffuseTexture TEXT,

                    -- The tangent frame, 3 float32 per vertex each, or NULL when the shape carries
                    -- none - which 27 % of them do not, their vertex descriptor having the TANGENTS
                    -- bit clear. Needed to apply a normal map: it is the frame the map's directions
                    -- are expressed in.
                    Tangents   BLOB,
                    Bitangents BLOB,

                    PRIMARY KEY (MeshId, ShapeIdx)
                );

                CREATE TABLE IF NOT EXISTS MeshParser (
                    OnlyRow INTEGER PRIMARY KEY CHECK (OnlyRow = 0),
                    Version INTEGER NOT NULL
                );
            ";
            cmd.ExecuteNonQuery();

        }

        // Bump this whenever a change to NifReader alters the numbers it produces for a mesh it already
        // read. Cached geometry from an older build is then thrown away and re-parsed on next use.
        //
        // 2: SSE normals and the tangent frame are decoded as UNSIGNED bytes spanning -1..1. Version 1
        //    read them as signed over 127, which flipped the sign of every component above 127 while
        //    leaving the vector about the right length - wrong shading on every SSE mesh in the load
        //    order, and invisible to a length check.
        private const int MeshParserVersion = 2;

        private static void DiscardGeometryFromOlderParser(SqliteConnection connection)
        {
            // This runs in the migration pass, which is handed databases in every state the tool has
            // ever written - including ones that predate MeshGeom entirely. The sibling calls here are
            // all AddColumnIfMissing, which shrugs at a missing table; this has to do the same rather
            // than throw and take the whole startup with it.
            using (var ensure = connection.CreateCommand())
            {
                ensure.CommandText =
                    "CREATE TABLE IF NOT EXISTS MeshParser (" +
                    "  OnlyRow INTEGER PRIMARY KEY CHECK (OnlyRow = 0)," +
                    "  Version INTEGER NOT NULL);";
                ensure.ExecuteNonQuery();
            }

            using (var exists = connection.CreateCommand())
            {
                exists.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'MeshGeom';";
                if (exists.ExecuteScalar() is null) return;
            }

            int stored;
            using (var read = connection.CreateCommand())
            {
                read.CommandText = "SELECT Version FROM MeshParser WHERE OnlyRow = 0;";
                var value = read.ExecuteScalar();

                // No row at all means either a brand-new database - where MeshGeom is empty and there is
                // nothing to discard - or one written before this counter existed, which is version 1.
                if (value is null or DBNull)
                {
                    using var count = connection.CreateCommand();
                    count.CommandText = "SELECT EXISTS(SELECT 1 FROM MeshGeom);";
                    stored = Convert.ToInt32(count.ExecuteScalar()) == 0 ? MeshParserVersion : 1;
                }
                else
                {
                    stored = Convert.ToInt32(value);
                }
            }

            if (stored < MeshParserVersion)
            {
                using var clear = connection.CreateCommand();
                clear.CommandText = "DELETE FROM MeshGeom; UPDATE Mesh SET GeomState = 0;";
                clear.ExecuteNonQuery();
            }

            using var write = connection.CreateCommand();
            write.CommandText =
                "INSERT INTO MeshParser (OnlyRow, Version) VALUES (0, @v) " +
                "ON CONFLICT(OnlyRow) DO UPDATE SET Version = excluded.Version;";
            write.Parameters.AddWithValue("@v", MeshParserVersion);
            write.ExecuteNonQuery();
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
