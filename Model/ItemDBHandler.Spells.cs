using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;
using SkyrimCraftingTool.Services;

namespace SkyrimCraftingTool.Model
{
    // The Spells tab's reads (docs/Spells-Plan.md, S-P2).
    //
    // Its own file rather than another thousand lines in ItemDBHandler.cs, which the code review
    // already called out as too large to navigate.
    public partial class ItemDBHandler
    {
        // Every spell with its effects attached.
        //
        // THE SHADOW COLUMNS ARE RESOLVED IN SQL, not afterwards: "the edited value if there is one,
        // otherwise the scanned one" is the rule the whole tool reads by, and expressing it anywhere
        // but the query invites a second place where it is expressed differently. Enchantments do
        // exactly this, field for field.
        public List<SpellRecord> GetAllSpells()
        {
            var spells = LoadSpells();
            var effects = LoadSpellEffects();

            // Indexed once rather than re-scanned per spell: 3.684 spells against 8.302 effects is
            // 30 million comparisons the naive way, for a screen that has to open promptly.
            var effectsBySpell = effects.ToLookup(e => e.SpellKey, StringComparer.OrdinalIgnoreCase);

            foreach (var spell in spells)
                foreach (var effect in effectsBySpell[spell.Key])
                    spell.Effects.Add(effect);

            return spells;
        }

        private List<SpellRecord> LoadSpells()
        {
            var list = new List<SpellRecord>();
            if (!File.Exists(ItemdbPath)) return list;

            using var connection = new SqliteConnection($"Data Source={ItemdbPath}");
            connection.Open();

            using var cmd = connection.CreateCommand();
            cmd.CommandText =
                @"SELECT
                    Key,
                    EditorID,
                    CASE WHEN IsEdited = 1 AND IsEditedName IS NOT NULL
                         THEN IsEditedName ELSE Name END AS Name,

                    -- Scan values with no shadow column: SkyPatcher has no operation for them, so
                    -- they are editable on tool-created records only (which write the base column).
                    SpellType,
                    TargetType,
                    CastDuration,
                    SpellRange,
                    Description,
                    Keywords,

                    CASE WHEN IsEdited = 1 AND IsEditedCastType IS NOT NULL
                         THEN IsEditedCastType ELSE CastType END AS CastType,
                    CASE WHEN IsEdited = 1 AND IsEditedBaseCost IS NOT NULL
                         THEN IsEditedBaseCost ELSE BaseCost END AS BaseCost,
                    CASE WHEN IsEdited = 1 AND IsEditedChargeTime IS NOT NULL
                         THEN IsEditedChargeTime ELSE ChargeTime END AS ChargeTime,
                    CASE WHEN IsEdited = 1 AND IsEditedHalfCostPerkKey IS NOT NULL
                         THEN IsEditedHalfCostPerkKey ELSE HalfCostPerkKey END AS HalfCostPerkKey,
                    CASE WHEN IsEdited = 1 AND IsEditedFlags IS NOT NULL
                         THEN IsEditedFlags ELSE Flags END AS Flags,

                    -- Edited for the tree and the only-edited filter means fields OR effects. Reset
                    -- fields clears IsEdited while the effects can still be the user's - reading
                    -- IsEdited alone dropped such a spell out of the filter with its edit still live
                    -- (found 2026-10-17).
                    CASE WHEN IsEdited = 1 OR EffectsEdited = 1 THEN 1 ELSE 0 END AS IsEdited,
                    Original,
                    DroppedEffectRows,
                    EquipType,

                    -- The shout whose word casts this spell, for the label. One answer even in the
                    -- rare case of a spell shared by two shouts: the first by key, so it is stable.
                    (SELECT COALESCE(NULLIF(sh.Name, ''), sh.EditorID)
                       FROM ShoutWords w JOIN Shouts sh ON sh.Key = w.ShoutKey
                      WHERE w.SpellKey = Spells.Key
                      ORDER BY w.ShoutKey, w.Ordinal LIMIT 1) AS ShoutName,
                    (SELECT w.Ordinal
                       FROM ShoutWords w
                      WHERE w.SpellKey = Spells.Key
                      ORDER BY w.ShoutKey, w.Ordinal LIMIT 1) AS ShoutWord,

                    -- Only for the half-cost perk mismatch note (SpellRecord.HalfCostPerkMismatch).
                    (SELECT p.EditorID FROM NpcPerkCatalogue p
                      WHERE p.Key = CASE WHEN IsEdited = 1 AND IsEditedHalfCostPerkKey IS NOT NULL
                                         THEN IsEditedHalfCostPerkKey ELSE HalfCostPerkKey END) AS PerkEditorId
                  FROM Spells
                  WHERE Active = 1;";

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                list.Add(new SpellRecord
                {
                    Key = reader.GetString(0),
                    EditorID = reader.IsDBNull(1) ? "" : reader.GetString(1),
                    Name = reader.IsDBNull(2) ? "" : reader.GetString(2),

                    SpellType = reader.IsDBNull(3) ? "" : reader.GetString(3),
                    TargetType = reader.IsDBNull(4) ? "" : reader.GetString(4),
                    CastDuration = reader.IsDBNull(5) ? 0f : (float)reader.GetDouble(5),
                    SpellRange = reader.IsDBNull(6) ? 0f : (float)reader.GetDouble(6),
                    Description = reader.IsDBNull(7) ? "" : reader.GetString(7),
                    Keywords = reader.IsDBNull(8) ? "" : reader.GetString(8),

                    CastType = reader.IsDBNull(9) ? "" : reader.GetString(9),
                    BaseCost = reader.IsDBNull(10) ? 0 : reader.GetInt32(10),
                    ChargeTime = reader.IsDBNull(11) ? 0f : (float)reader.GetDouble(11),
                    HalfCostPerkKey = reader.IsDBNull(12) ? "" : reader.GetString(12),
                    Flags = reader.IsDBNull(13) ? 0 : reader.GetInt32(13),

                    IsEdited = !reader.IsDBNull(14) && reader.GetInt32(14) != 0,
                    IsOriginal = reader.IsDBNull(15) || reader.GetInt32(15) != 0,
                    DroppedEffectRows = reader.IsDBNull(16) ? 0 : reader.GetInt32(16),
                    EquipType = reader.IsDBNull(17) ? "" : reader.GetString(17),
                    ShoutName = reader.IsDBNull(18) ? "" : reader.GetString(18),
                    ShoutWord = reader.IsDBNull(19) ? 0 : reader.GetInt32(19),
                    HalfCostPerkEditorId = reader.IsDBNull(20) ? "" : reader.GetString(20),
                });
            }

            return list;
        }

        private List<SpellEffectRecord> LoadSpellEffects()
        {
            var list = new List<SpellEffectRecord>();
            if (!File.Exists(ItemdbPath)) return list;

            using var connection = new SqliteConnection($"Data Source={ItemdbPath}");
            connection.Open();

            using var cmd = connection.CreateCommand();
            // LEFT JOIN, not JOIN: an effect whose MGEF is not in the scan still has to appear. A spell
            // shown short because one of its effects came from a plugin that has since left would
            // be a worse lie than one showing an effect with no school.
            cmd.CommandText =
                @"SELECT
                    e.SpellKey,
                    e.MagicEffectKey,
                    e.EditorID,
                    e.Name,
                    CASE WHEN e.IsEdited = 1 AND e.IsEditedMagnitude IS NOT NULL
                         THEN e.IsEditedMagnitude ELSE e.Magnitude END AS Magnitude,
                    CASE WHEN e.IsEdited = 1 AND e.IsEditedDuration IS NOT NULL
                         THEN e.IsEditedDuration ELSE e.Duration END AS Duration,
                    CASE WHEN e.IsEdited = 1 AND e.IsEditedArea IS NOT NULL
                         THEN e.IsEditedArea ELSE e.Area END AS Area,
                    m.MagicSkill,
                    m.MinimumSkillLevel,
                    m.BaseCost
                  FROM SpellEffects e
                  LEFT JOIN MagicEffects m ON m.Key = e.MagicEffectKey;";

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                list.Add(new SpellEffectRecord
                {
                    SpellKey = reader.GetString(0),
                    MagicEffectKey = reader.GetString(1),
                    EditorID = reader.IsDBNull(2) ? "" : reader.GetString(2),
                    Name = reader.IsDBNull(3) ? "" : reader.GetString(3),
                    Magnitude = reader.IsDBNull(4) ? 0f : (float)reader.GetDouble(4),
                    Duration = reader.IsDBNull(5) ? 0 : reader.GetInt32(5),
                    Area = reader.IsDBNull(6) ? 0 : reader.GetInt32(6),
                    School = reader.IsDBNull(7) ? "" : reader.GetString(7),
                    MinimumSkillLevel = reader.IsDBNull(8) ? 0 : reader.GetInt32(8),
                    EffectBaseCost = reader.IsDBNull(9) ? 0 : reader.GetDouble(9),
                });
            }

            return list;
        }

        // ===================================================================
        // the record's own fields (S-P5)
        // ===================================================================

        // Exactly the fields docs/Spell_Patcher.txt documents an operation for, and nothing else.
        //
        // A SHADOW COLUMN IS A PROMISE that the value can be carried into the game. There is no
        // targetType, no spell type, no range and no cast duration in that document, so those have
        // no shadow column and no writer here - they are editable on a tool-created record only,
        // where the generated ESP writes the record from scratch.
        internal static readonly string[] SpellShadowColumns =
            { "IsEditedName", "IsEditedBaseCost", "IsEditedCastType", "IsEditedChargeTime",
              "IsEditedHalfCostPerkKey", "IsEditedFlags", "IsEditedKeywords" };

        public static void UpdateSpellName(string key, string name)
            => UpdateField("Spells", "IsEditedName", key, name);

        public static void UpdateSpellBaseCost(string key, int baseCost)
            => UpdateField("Spells", "IsEditedBaseCost", key, baseCost);

        public static void UpdateSpellCastType(string key, string castType)
            => UpdateField("Spells", "IsEditedCastType", key, castType);

        public static void UpdateSpellChargeTime(string key, float chargeTime)
            => UpdateField("Spells", "IsEditedChargeTime", key, chargeTime);

        // The perk that halves the spell's cost. The one operation the spell patcher has and the
        // enchantment one does not.
        public static void UpdateSpellHalfCostPerk(string key, string perkKey)
            => UpdateField("Spells", "IsEditedHalfCostPerkKey", key, perkKey ?? "");

        // The RAW DWORD. SkyPatcher's flag names and Mutagen's do not match, and three bits that 415
        // spells in a real load order set have no name on either side - storing a name list would
        // throw those away. SpellFlags turns the number into names at the point it has to.
        public static void UpdateSpellFlags(string key, int flags)
            => UpdateField("Spells", "IsEditedFlags", key, flags);

        public static void UpdateSpellKeywords(string key, string keywordsCsv)
            => UpdateField("Spells", "IsEditedKeywords", key, keywordsCsv ?? "");

        // Puts every edited FIELD back. Deliberately not the effects: those live in their own table
        // with their own snapshot, and ResetSpellEffects is the operation for them. A single button
        // that did both would make "undo my cost change" also throw away an hour of effect work.
        public static void ResetSpellEdits(string key)
            => ResetEditShadows("Spells", SpellShadowColumns, key);

        // What the scan found, before any field edit. Used to show the user what Reset would go back
        // to, and by the patch generator as the left-hand side of its diff.
        public static SpellRecord GetOriginalSpell(string key)
        {
            if (string.IsNullOrEmpty(key) || !File.Exists(ItemdbPathStatic())) return null;

            using var connection = new SqliteConnection(ConnString);
            connection.Open();

            using var cmd = connection.CreateCommand();
            cmd.CommandText =
                @"SELECT Key, EditorID, Name, SpellType, CastType, TargetType, BaseCost, ChargeTime,
                         CastDuration, SpellRange, Description, HalfCostPerkKey, Flags, Keywords
                  FROM Spells WHERE Key = @key";
            cmd.Parameters.AddWithValue("@key", key);

            using var r = cmd.ExecuteReader();
            if (!r.Read()) return null;

            return new SpellRecord
            {
                Key = r.GetString(0),
                EditorID = r.IsDBNull(1) ? "" : r.GetString(1),
                Name = r.IsDBNull(2) ? "" : r.GetString(2),
                SpellType = r.IsDBNull(3) ? "" : r.GetString(3),
                CastType = r.IsDBNull(4) ? "" : r.GetString(4),
                TargetType = r.IsDBNull(5) ? "" : r.GetString(5),
                BaseCost = r.IsDBNull(6) ? 0 : r.GetInt32(6),
                ChargeTime = r.IsDBNull(7) ? 0f : (float)r.GetDouble(7),
                CastDuration = r.IsDBNull(8) ? 0f : (float)r.GetDouble(8),
                SpellRange = r.IsDBNull(9) ? 0f : (float)r.GetDouble(9),
                Description = r.IsDBNull(10) ? "" : r.GetString(10),
                HalfCostPerkKey = r.IsDBNull(11) ? "" : r.GetString(11),
                Flags = r.IsDBNull(12) ? 0 : r.GetInt32(12),
                Keywords = r.IsDBNull(13) ? "" : r.GetString(13),
            };
        }

        // Every perk in the load order, for the half-cost perk picker. From the catalogue the scan
        // writes for the NPC tab - every PERK record, not only the ones some NPC carries. Empty on a
        // database without the catalogue.
        public static List<PerkChoice> GetPerkChoices()
        {
            var list = new List<PerkChoice>();
            if (!File.Exists(ItemdbPathStatic())) return list;

            using var connection = new SqliteConnection(ConnString);
            connection.Open();
            if (!UserRecords.TableExists(connection, "NpcPerkCatalogue")) return list;

            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT Key, EditorID, Name FROM NpcPerkCatalogue WHERE Active = 1";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                string key = r.GetString(0);
                string editorId = r.IsDBNull(1) ? "" : r.GetString(1);
                string name = r.IsDBNull(2) ? "" : r.GetString(2);
                list.Add(new PerkChoice(key, editorId, name));
            }
            return list.OrderBy(p => p.Display, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static string ItemdbPathStatic()
            => Path.Combine(GlobalState.Tool.InputFolder, "Item", "item.db");

        // ===================================================================
        // spells of the user's own (S-P6)
        // ===================================================================

        // A brand-new spell, keyed under the tool's own pseudo-plugin exactly like a user-created
        // enchantment or recipe. It exists in no plugin - the generated ESP is what brings it into
        // the game - so the row carries Original = 0 and the scan leaves it alone.
        //
        // SpellType defaults to "Spell" rather than staying empty: it is the tree's grouping level,
        // and a record that appeared under "Unknown" until somebody noticed the dropdown would be a
        // worse first impression than one that guessed the overwhelmingly common case. Measured,
        // 2.389 of 3.684 are plain spells.
        public static SpellRecord CreateSpell(string editorId, string name)
        {
            string key = AllocateUserSpellKey();

            // An EditorID is what the tree shows and what xEdit will list, so it can never be blank.
            // The ID it is built from is the one thing about the record that is already unique.
            if (string.IsNullOrWhiteSpace(editorId))
                editorId = "SCT_Spell_" + key.Substring(key.IndexOf('|') + 1);

            using var connection = new SqliteConnection(ConnString);
            connection.Open();

            using var cmd = connection.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO Spells
                    (Key, EditorID, Name, SpellType, CastType, TargetType,
                     BaseCost, ChargeTime, CastDuration, SpellRange,
                     Description, HalfCostPerkKey, Flags, Keywords,
                     IsEdited, Active, EffectsEdited, KeywordsEdited, Original,
                     DroppedEffectRows, LastChanged)
                VALUES
                    (@key, @editorId, @name, 'Spell', '', '',
                     0, 0, 0, 0,
                     '', '', 0, '',
                     0, 1, 0, 0, 0,
                     0, @now);";
            cmd.Parameters.AddWithValue("@key", key);
            cmd.Parameters.AddWithValue("@editorId", editorId);
            cmd.Parameters.AddWithValue("@name", name ?? "");
            cmd.Parameters.AddWithValue("@now", NowIso());
            cmd.ExecuteNonQuery();

            return new SpellRecord
            {
                Key = key,
                EditorID = editorId,
                Name = name ?? "",
                SpellType = "Spell",
                IsOriginal = false,
            };
        }

        // Only ever a record of the user's own. A scanned spell belongs to its plugin; "deleting" it
        // here would mean nothing to the game and would lose the row the next rescan needs.
        public static bool DeleteSpell(string key)
        {
            if (string.IsNullOrWhiteSpace(key)) return false;

            try
            {
                using var connection = new SqliteConnection(ConnString);
                connection.Open();
                using var transaction = connection.BeginTransaction();

                using (var guard = new SqliteCommand(
                    "SELECT Original FROM Spells WHERE Key = @key", connection, transaction))
                {
                    guard.Parameters.AddWithValue("@key", key);
                    var original = guard.ExecuteScalar();
                    if (original == null || original == DBNull.Value || Convert.ToInt64(original) != 0)
                    {
                        AppLogger.LogWarning($"DeleteSpell refused for {key}: not a user-created record.");
                        return false;
                    }
                }

                // The snapshot table too: a key handed out again later would otherwise inherit the
                // dead record's pre-edit effects and Reset would restore someone else's work.
                foreach (var sql in new[]
                {
                    "DELETE FROM SpellEffects WHERE SpellKey = @key",
                    "DELETE FROM SpellEffects_Original WHERE SpellKey = @key",
                    "DELETE FROM Spells WHERE Key = @key",
                })
                {
                    using var cmd = new SqliteCommand(sql, connection, transaction);
                    cmd.Parameters.AddWithValue("@key", key);
                    cmd.ExecuteNonQuery();
                }

                transaction.Commit();
                return true;
            }
            catch (Exception ex)
            {
                AppLogger.LogError($"DeleteSpell failed (key={key})", ex);
                throw;
            }
        }

        // The shared counter for every own record (UserKeyAllocator). This used to count in the
        // Spells table alone and claimed that was harmless "because PatchFormIdMapStore maps a tool
        // key to an ESP FormID per record type". It does not - the map is keyed by the tool key
        // only - so own spell 001001 and own enchantment 001001 shared FormID 0x800 (2026-10-17).
        private static string AllocateUserSpellKey() => UserKeyAllocator.NextRecordKey();

        // The fields a tool-created record writes DIRECTLY, with no shadow column: they go into the
        // ESP from scratch, so there is nothing to shadow. The SQL guard is what keeps them from
        // ever touching a scanned record.
        public static void UpdateUserSpellField(string key, string column, object value)
        {
            if (string.IsNullOrEmpty(key)) return;

            // Whitelisted rather than passed through: the column name cannot be parameterised, so
            // the only safe source for it is this list.
            if (Array.IndexOf(UserOnlySpellColumns, column) < 0)
            {
                AppLogger.LogWarning($"UpdateUserSpellField refused unknown column '{column}'.");
                return;
            }

            try
            {
                using var conn = new SqliteConnection(ConnString);
                conn.Open();
                using var cmd = new SqliteCommand(
                    $"UPDATE Spells SET {column} = @val, IsEdited = 1, LastChanged = @now " +
                    "WHERE Key = @key AND Original = 0", conn);
                cmd.Parameters.AddWithValue("@key", key);
                cmd.Parameters.AddWithValue("@val", value ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@now", NowIso());

                if (cmd.ExecuteNonQuery() == 0)
                    AppLogger.LogWarning($"UpdateUserSpellField({column}) changed nothing for {key} - not a user record?");
            }
            catch (Exception ex)
            {
                AppLogger.LogError($"UpdateUserSpellField failed (column={column}, key={key})", ex);
                throw;
            }
        }

        // Editable on a user-created record only, because SkyPatcher has no operation for any of
        // them - see docs/Spell_Patcher.txt. EditorID is here for the same reason: a scanned
        // record's belongs to its plugin.
        internal static readonly string[] UserOnlySpellColumns =
            { "EditorID", "SpellType", "TargetType", "SpellRange", "CastDuration", "Description" };

        // ===================================================================
        // effects (S-P3)
        // ===================================================================

        // Replaces a spell's effects wholesale.
        //
        // WHOLESALE AND IN ONE TRANSACTION, both deliberately. The editor hands over the list it
        // has, so diffing here would be a second opinion about what the user did; and a delete that
        // committed without its inserts is how an enchantment once lost its effects entirely.
        public static void SaveSpellEffects(string spellKey, List<SpellEffectRecord> effects)
        {
            if (string.IsNullOrEmpty(spellKey)) return;

            try
            {
                using var conn = new SqliteConnection(ConnString);
                conn.Open();
                using var transaction = conn.BeginTransaction();

                // Before anything is written: the FIRST edit freezes the scanned rows, so Reset has
                // something pristine to go back to.
                SnapshotChildRowsIfFirstEdit(conn, transaction, ChildTables.SpellEffects, spellKey);

                using (var deleteCmd = new SqliteCommand(
                    "DELETE FROM SpellEffects WHERE SpellKey = @key", conn, transaction))
                {
                    deleteCmd.Parameters.AddWithValue("@key", spellKey);
                    deleteCmd.ExecuteNonQuery();
                }

                foreach (var effect in effects ?? new List<SpellEffectRecord>())
                {
                    using var insertCmd = new SqliteCommand(
                        @"INSERT INTO SpellEffects (SpellKey, MagicEffectKey, EditorID, Name, Magnitude, Duration, Area)
                          VALUES (@spell, @mgef, @editorID, @name, @magnitude, @duration, @area)", conn, transaction);
                    insertCmd.Parameters.AddWithValue("@spell", spellKey);
                    insertCmd.Parameters.AddWithValue("@mgef", effect.MagicEffectKey ?? "");
                    insertCmd.Parameters.AddWithValue("@editorID", effect.EditorID ?? "");
                    insertCmd.Parameters.AddWithValue("@name", effect.Name ?? "");
                    insertCmd.Parameters.AddWithValue("@magnitude", effect.Magnitude);
                    insertCmd.Parameters.AddWithValue("@duration", effect.Duration);
                    insertCmd.Parameters.AddWithValue("@area", effect.Area);
                    insertCmd.ExecuteNonQuery();
                }

                // Marks the effects as the user's, so a rescan refreshes the RECORD and leaves these
                // rows alone - the guard the scan reads as spellEffectsEditedKeys.
                using (var flagCmd = new SqliteCommand(
                    "UPDATE Spells SET EffectsEdited = 1, IsEdited = 1, LastChanged = @now WHERE Key = @key",
                    conn, transaction))
                {
                    flagCmd.Parameters.AddWithValue("@key", spellKey);
                    flagCmd.Parameters.AddWithValue("@now", NowIso());
                    flagCmd.ExecuteNonQuery();
                }

                transaction.Commit();
            }
            catch (Exception ex)
            {
                AppLogger.LogError($"SaveSpellEffects failed (spellKey={spellKey})", ex);
                throw;
            }
        }

        // The effects as the scan found them.
        //
        // When the spell has never been edited the snapshot table is EMPTY, and reading it would
        // report "this spell has no effects" rather than "it has not been changed". The live table
        // IS the pristine one in that case, so that is what gets read - the same reasoning as
        // GetOriginalCOBJConditions.
        public static List<SpellEffectRecord> GetOriginalSpellEffects(string spellKey)
        {
            var list = new List<SpellEffectRecord>();
            if (string.IsNullOrEmpty(spellKey)) return list;

            using var connection = new SqliteConnection(ConnString);
            connection.Open();

            bool effectsEdited;
            using (var flagCmd = connection.CreateCommand())
            {
                flagCmd.CommandText = "SELECT EffectsEdited FROM Spells WHERE Key = @key";
                flagCmd.Parameters.AddWithValue("@key", spellKey);
                var flag = flagCmd.ExecuteScalar();
                effectsEdited = flag != null && flag != DBNull.Value && Convert.ToInt64(flag) == 1;
            }

            string table = effectsEdited ? "SpellEffects_Original" : "SpellEffects";

            using var cmd = connection.CreateCommand();
            cmd.CommandText =
                $@"SELECT SpellKey, MagicEffectKey, EditorID, Name, Magnitude, Duration, Area
                   FROM {table} WHERE SpellKey = @key";
            cmd.Parameters.AddWithValue("@key", spellKey);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                list.Add(new SpellEffectRecord
                {
                    SpellKey = reader.GetString(0),
                    MagicEffectKey = reader.GetString(1),
                    EditorID = reader.IsDBNull(2) ? "" : reader.GetString(2),
                    Name = reader.IsDBNull(3) ? "" : reader.GetString(3),
                    Magnitude = reader.IsDBNull(4) ? 0f : (float)reader.GetDouble(4),
                    Duration = reader.IsDBNull(5) ? 0 : reader.GetInt32(5),
                    Area = reader.IsDBNull(6) ? 0 : reader.GetInt32(6),
                });
            }

            return list;
        }

        // Puts the scanned effects back.
        //
        // THE GUARD IS NOT OPTIONAL. A spell whose effects were never touched has an empty snapshot,
        // and restoring "from" it would delete the still-pristine live rows and put nothing in their
        // place - Reset would destroy exactly what it exists to protect. The enchantment and recipe
        // paths carry the identical guard for the identical reason.
        public static void ResetSpellEffects(string spellKey)
        {
            if (string.IsNullOrEmpty(spellKey)) return;

            try
            {
                using var conn = new SqliteConnection(ConnString);
                conn.Open();
                using var transaction = conn.BeginTransaction();

                using (var checkCmd = new SqliteCommand(
                    "SELECT EffectsEdited FROM Spells WHERE Key = @key", conn, transaction))
                {
                    checkCmd.Parameters.AddWithValue("@key", spellKey);
                    var flag = checkCmd.ExecuteScalar();
                    bool wasEdited = flag != null && flag != DBNull.Value && Convert.ToInt64(flag) == 1;
                    if (!wasEdited) return;
                }

                using (var deleteCmd = new SqliteCommand(
                    "DELETE FROM SpellEffects WHERE SpellKey = @key", conn, transaction))
                {
                    deleteCmd.Parameters.AddWithValue("@key", spellKey);
                    deleteCmd.ExecuteNonQuery();
                }

                using (var restoreCmd = new SqliteCommand(
                    @"INSERT INTO SpellEffects (SpellKey, MagicEffectKey, EditorID, Name, Magnitude, Duration, Area)
                      SELECT SpellKey, MagicEffectKey, EditorID, Name, Magnitude, Duration, Area
                      FROM SpellEffects_Original WHERE SpellKey = @key", conn, transaction))
                {
                    restoreCmd.Parameters.AddWithValue("@key", spellKey);
                    restoreCmd.ExecuteNonQuery();
                }

                // Consumed, so cleared - otherwise the next first-ever edit would snapshot on top of
                // it and Reset would restore two copies of every effect.
                using (var clearCmd = new SqliteCommand(
                    "DELETE FROM SpellEffects_Original WHERE SpellKey = @key", conn, transaction))
                {
                    clearCmd.Parameters.AddWithValue("@key", spellKey);
                    clearCmd.ExecuteNonQuery();
                }

                using (var flagCmd = new SqliteCommand(
                    // IsEdited follows what is left: SaveSpellEffects set it to 1, and without this a spell
                    // whose only edit was its effects stayed "edited" after the reset, with nothing in it.
                    $"UPDATE Spells SET EffectsEdited = 0, IsEdited = CASE WHEN {string.Join(" OR ", SpellShadowColumns.Select(c => c + " IS NOT NULL"))} THEN 1 ELSE 0 END, LastChanged = @now WHERE Key = @key",
                    conn, transaction))
                {
                    flagCmd.Parameters.AddWithValue("@key", spellKey);
                    flagCmd.Parameters.AddWithValue("@now", NowIso());
                    flagCmd.ExecuteNonQuery();
                }

                transaction.Commit();
            }
            catch (Exception ex)
            {
                AppLogger.LogError($"ResetSpellEffects failed (spellKey={spellKey})", ex);
                throw;
            }
        }
    }
}
