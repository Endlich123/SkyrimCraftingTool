using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;
using SkyrimCraftingTool.Model;

namespace SkyrimCraftingTool.Services.PatchGen
{
    public sealed record SpellPatchPair(SpellRecord Original, SpellRecord Edited);

    // Reads item.db for edited SPEL rows, returning the pristine scanned record alongside the
    // effective one so SpellRuleBuilder can diff them (docs/Spells-Plan.md, S-P4).
    //
    // Same shape as EnchantmentPatchReader, and that is the point - the two records differ in their
    // fields, not in how an edit is stored.
    public sealed class SpellPatchReader
    {
        private readonly string _connString;

        public SpellPatchReader(string? connString = null)
        {
            _connString = connString
                ?? $"Data Source={Path.Combine(GlobalState.Tool.InputFolder, "Item", "item.db")}";
        }

        public IReadOnlyList<SpellPatchPair> ReadEditedSpells()
        {
            if (!File.Exists(DbPath())) return Array.Empty<SpellPatchPair>();

            using var conn = new SqliteConnection(_connString);
            conn.Open();

            var pairs = new List<SpellPatchPair>();
            var effectsEditedKeys = new HashSet<string>(StringComparer.Ordinal);

            using (var cmd = conn.CreateCommand())
            {
                // (IsEdited OR EffectsEdited), not "LastChanged IS NOT NULL": the reset paths clear
                // the flags but keep LastChanged, which feeds the import conflict check.
                // EffectsEdited matters on its own - a spell whose only change is an effect value
                // may still have IsEdited back at 0 after a partial reset.
                //
                // Original = 1 only. A spell the user created has no scanned record to patch, and
                // its tool key is not the FormID it will end up with either - that goes the ESP
                // route (S-P6). A rule naming its tool key would target a record existing nowhere.
                //
                // The CASE gating on IsEdited mirrors LoadSpells exactly, so the patch can never
                // write a value the editor itself does not show.
                cmd.CommandText = @"
                    SELECT Key, EditorID, EffectsEdited,
                           Name,
                           CASE WHEN IsEdited = 1 AND IsEditedName IS NOT NULL
                                THEN IsEditedName ELSE Name END,
                           BaseCost,
                           CASE WHEN IsEdited = 1 AND IsEditedBaseCost IS NOT NULL
                                THEN IsEditedBaseCost ELSE BaseCost END,
                           CastType,
                           CASE WHEN IsEdited = 1 AND IsEditedCastType IS NOT NULL
                                THEN IsEditedCastType ELSE CastType END,
                           ChargeTime,
                           CASE WHEN IsEdited = 1 AND IsEditedChargeTime IS NOT NULL
                                THEN IsEditedChargeTime ELSE ChargeTime END,
                           HalfCostPerkKey,
                           CASE WHEN IsEdited = 1 AND IsEditedHalfCostPerkKey IS NOT NULL
                                THEN IsEditedHalfCostPerkKey ELSE HalfCostPerkKey END,
                           Flags,
                           CASE WHEN IsEdited = 1 AND IsEditedFlags IS NOT NULL
                                THEN IsEditedFlags ELSE Flags END,
                           Keywords,
                           CASE WHEN IsEdited = 1 AND IsEditedKeywords IS NOT NULL
                                THEN IsEditedKeywords ELSE Keywords END
                    FROM Spells
                    WHERE (IsEdited = 1 OR EffectsEdited = 1) AND Active = 1 AND Original = 1";

                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    string key = r.GetString(0);
                    string editorId = Str(r, 1);

                    pairs.Add(new SpellPatchPair(
                        new SpellRecord
                        {
                            Key = key,
                            EditorID = editorId,
                            Name = Str(r, 3),
                            BaseCost = (int)Lng(r, 5),
                            CastType = Str(r, 7),
                            ChargeTime = (float)Dbl(r, 9),
                            HalfCostPerkKey = Str(r, 11),
                            Flags = (int)Lng(r, 13),
                            Keywords = Str(r, 15),
                        },
                        new SpellRecord
                        {
                            Key = key,
                            EditorID = editorId,
                            Name = Str(r, 4),
                            BaseCost = (int)Lng(r, 6),
                            CastType = Str(r, 8),
                            ChargeTime = (float)Dbl(r, 10),
                            HalfCostPerkKey = Str(r, 12),
                            Flags = (int)Lng(r, 14),
                            Keywords = Str(r, 16),
                        }));

                    if (!r.IsDBNull(2) && Convert.ToInt64(r.GetValue(2)) == 1)
                        effectsEditedKeys.Add(key);
                }
            }

            if (pairs.Count == 0) return pairs;

            // Two bulk reads instead of a query per spell. SpellEffects holds the CURRENT values -
            // an effect edit is a destructive replace of the base columns, not a shadow-column
            // write - and SpellEffects_Original is the lazy pre-edit snapshot, which only exists
            // once EffectsEdited flipped to 1. Before that the live table IS the scanned state,
            // which is why the snapshot is consulted only for the keys that carry the flag.
            var live = ReadEffects(conn, "SpellEffects");
            var snapshot = ReadEffects(conn, "SpellEffects_Original");

            foreach (var pair in pairs)
            {
                string key = pair.Edited.Key;

                var editedEffects = live.TryGetValue(key, out var l) ? l : new List<SpellEffectRecord>();
                var originalEffects = effectsEditedKeys.Contains(key) && snapshot.TryGetValue(key, out var s)
                    ? s
                    : editedEffects;

                foreach (var e in originalEffects) pair.Original.Effects.Add(e);
                foreach (var e in editedEffects) pair.Edited.Effects.Add(e);
            }

            return pairs;
        }

        // The spells the user created in the tool (Original = 0).
        //
        // They have no scanned original to diff against - they exist nowhere until the generated ESP
        // writes them - so they take the ESP route rather than producing SkyPatcher rules. And they
        // carry MORE than a rule could: SpellType, TargetType, Range and CastDuration have no
        // operation at all, which is exactly why a record built from scratch can have them.
        //
        // The effective values are read as the editor shows them, shadow columns and all: a user
        // record is edited through the very same paths as a scanned one.
        public IReadOnlyList<CobjEspBuilder.NewSpellEspEntry> ReadNewSpells()
        {
            if (!File.Exists(DbPath())) return Array.Empty<CobjEspBuilder.NewSpellEspEntry>();

            using var conn = new SqliteConnection(_connString);
            conn.Open();

            var effects = ReadEffects(conn, "SpellEffects");
            var list = new List<CobjEspBuilder.NewSpellEspEntry>();

            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT Key, EditorID,
                       CASE WHEN IsEdited = 1 AND IsEditedName IS NOT NULL
                            THEN IsEditedName ELSE Name END,
                       Description,
                       SpellType,
                       CASE WHEN IsEdited = 1 AND IsEditedCastType IS NOT NULL
                            THEN IsEditedCastType ELSE CastType END,
                       TargetType,
                       CASE WHEN IsEdited = 1 AND IsEditedBaseCost IS NOT NULL
                            THEN IsEditedBaseCost ELSE BaseCost END,
                       CASE WHEN IsEdited = 1 AND IsEditedChargeTime IS NOT NULL
                            THEN IsEditedChargeTime ELSE ChargeTime END,
                       CastDuration,
                       SpellRange,
                       CASE WHEN IsEdited = 1 AND IsEditedHalfCostPerkKey IS NOT NULL
                            THEN IsEditedHalfCostPerkKey ELSE HalfCostPerkKey END,
                       CASE WHEN IsEdited = 1 AND IsEditedFlags IS NOT NULL
                            THEN IsEditedFlags ELSE Flags END
                FROM Spells
                WHERE Original = 0 AND Active = 1";

            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                string key = Str(r, 0);

                list.Add(new CobjEspBuilder.NewSpellEspEntry(
                    ToolKey: key,
                    EditorId: Str(r, 1),
                    Name: Str(r, 2),
                    Description: Str(r, 3),
                    SpellType: Str(r, 4),
                    CastType: Str(r, 5),
                    TargetType: Str(r, 6),
                    BaseCost: (int)Lng(r, 7),
                    ChargeTime: (float)Dbl(r, 8),
                    CastDuration: (float)Dbl(r, 9),
                    Range: (float)Dbl(r, 10),
                    HalfCostPerkKey: Str(r, 11),
                    Flags: (int)Lng(r, 12),
                    Effects: (effects.TryGetValue(key, out var rows) ? rows : new List<SpellEffectRecord>())
                        .Select(e => new CobjEspBuilder.NewSpellEffect(
                            e.MagicEffectKey, e.Magnitude, e.Duration, e.Area))
                        .ToList()));
            }

            return list;
        }

        private string DbPath()
        {
            // The connection string is the only thing this class is given, so the existence check
            // has to come out of it rather than from GlobalState - a test points both at a temp file.
            const string prefix = "Data Source=";
            int at = _connString.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
            if (at < 0) return _connString;

            string rest = _connString[(at + prefix.Length)..];
            int semicolon = rest.IndexOf(';');
            return semicolon >= 0 ? rest[..semicolon] : rest;
        }

        private static Dictionary<string, List<SpellEffectRecord>> ReadEffects(
            SqliteConnection conn, string table)
        {
            var bySpell = new Dictionary<string, List<SpellEffectRecord>>(StringComparer.Ordinal);

            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                $"SELECT SpellKey, MagicEffectKey, EditorID, Name, Magnitude, Duration, Area FROM {table}";

            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                string spellKey = Str(r, 0);
                if (!bySpell.TryGetValue(spellKey, out var list))
                    bySpell[spellKey] = list = new List<SpellEffectRecord>();

                list.Add(new SpellEffectRecord
                {
                    SpellKey = spellKey,
                    MagicEffectKey = Str(r, 1),
                    EditorID = Str(r, 2),
                    Name = Str(r, 3),
                    Magnitude = (float)Dbl(r, 4),
                    Duration = (int)Lng(r, 5),
                    Area = (int)Lng(r, 6),
                });
            }

            return bySpell;
        }

        private static string Str(SqliteDataReader r, int i) => r.IsDBNull(i) ? "" : r.GetString(i);
        private static double Dbl(SqliteDataReader r, int i) => r.IsDBNull(i) ? 0 : Convert.ToDouble(r.GetValue(i));
        private static long Lng(SqliteDataReader r, int i) => r.IsDBNull(i) ? 0 : Convert.ToInt64(r.GetValue(i));
    }
}
