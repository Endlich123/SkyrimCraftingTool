using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;
using SkyrimCraftingTool.Model;

namespace SkyrimCraftingTool.Services.PatchGen
{
    public sealed record EnchantmentPatchPair(EnchantmentRecord Original, EnchantmentRecord Edited);

    // Reads item.db for edited ENCH rows, returning the pristine scanned record alongside the
    // effective (shadow-applied) record so EnchantmentRuleBuilder can diff them. Same shape as
    // PatchDataReader. See docs/EnchantmentPatch-Plan.md (E-P1/E-P2).
    public sealed class EnchantmentPatchReader
    {
        private readonly string _connString;

        public EnchantmentPatchReader(string? connString = null)
        {
            _connString = connString
                ?? $"Data Source={Path.Combine(GlobalState.Tool.InputFolder, "Item", "item.db")}";
        }

        public IReadOnlyList<EnchantmentPatchPair> ReadEditedEnchantments()
        {
            using var conn = new SqliteConnection(_connString);
            conn.Open();

            var pairs = new List<EnchantmentPatchPair>();
            var effectsEditedKeys = new HashSet<string>(StringComparer.Ordinal);

            using (var cmd = conn.CreateCommand())
            {
                // (IsEdited OR EffectsEdited), not "LastChanged IS NOT NULL": the reset paths clear
                // the flags but keep LastChanged (it feeds the import conflict check). EffectsEdited
                // matters on its own - an enchantment whose ONLY change is its effect values has
                // IsEdited = 0. Same predicate as ItemDBHandler.GetEditedEnchantments.
                //
                // The CASE gating on IsEdited mirrors LoadEnchantments exactly, so the patch can
                // never write a field value the editor itself doesn't show.
                cmd.CommandText = @"
                    SELECT Key, EditorID,
                           Name,
                           CASE WHEN IsEdited = 1 AND IsEditedName IS NOT NULL
                                THEN IsEditedName ELSE Name END,
                           EnchantmentCost,
                           CASE WHEN IsEdited = 1 AND IsEditedEnchantmentCost IS NOT NULL
                                THEN IsEditedEnchantmentCost ELSE EnchantmentCost END,
                           EffectsEdited,
                           WornRestrictionListKey,
                           CASE WHEN IsEdited = 1 AND IsEditedWornRestrictionListKey IS NOT NULL
                                THEN IsEditedWornRestrictionListKey ELSE WornRestrictionListKey END,
                           -- The three ENIT fields SkyPatcher can patch on a scanned record.
                           Flags,
                           CASE WHEN IsEdited = 1 AND IsEditedFlags IS NOT NULL
                                THEN IsEditedFlags ELSE Flags END,
                           ChargeTime,
                           CASE WHEN IsEdited = 1 AND IsEditedChargeTime IS NOT NULL
                                THEN IsEditedChargeTime ELSE ChargeTime END,
                           EnchantmentAmount,
                           CASE WHEN IsEdited = 1 AND IsEditedEnchantmentAmount IS NOT NULL
                                THEN IsEditedEnchantmentAmount ELSE EnchantmentAmount END
                    FROM Enchantments
                    -- Original = 1 only. A user-created enchantment has no scanned record to patch,
                    -- and its tool key is not the FormID it will end up with either - it goes the
                    -- ESP route instead (ReadNewEnchantments below). A rule naming its tool key
                    -- would target a record that exists nowhere.
                    WHERE (IsEdited = 1 OR EffectsEdited = 1) AND Active = 1 AND Original = 1";

                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    string key = r.GetString(0);
                    string editorId = Str(r, 1);

                    pairs.Add(new EnchantmentPatchPair(
                        new EnchantmentRecord
                        {
                            Key = key,
                            EditorID = editorId,
                            Name = Str(r, 2),
                            EnchantmentCost = (float)Dbl(r, 4),
                            WornRestrictionListKey = Str(r, 7),
                            Flags = (int)Lng(r, 9),
                            ChargeTime = (float)Dbl(r, 11),
                            EnchantmentAmount = (int)Lng(r, 13),
                        },
                        new EnchantmentRecord
                        {
                            Key = key,
                            EditorID = editorId,
                            Name = Str(r, 3),
                            EnchantmentCost = (float)Dbl(r, 5),
                            WornRestrictionListKey = Str(r, 8),
                            Flags = (int)Lng(r, 10),
                            ChargeTime = (float)Dbl(r, 12),
                            EnchantmentAmount = (int)Lng(r, 14),
                        }));

                    if (!r.IsDBNull(6) && Convert.ToInt64(r.GetValue(6)) == 1)
                        effectsEditedKeys.Add(key);
                }
            }

            if (pairs.Count == 0) return pairs;

            // Two bulk reads instead of a query per enchantment (N+1). EnchantmentEffects holds the
            // CURRENT values - effect edits are a destructive replace of the base columns, not a
            // shadow-column write (see SaveEnchantmentEffects). EnchantmentEffects_Original is the
            // lazy pre-edit snapshot, and only exists once EffectsEdited flipped to 1; before that
            // the live table IS the scanned state.
            var live = ReadEffects(conn, "EnchantmentEffects");
            var snapshot = ReadEffects(conn, "EnchantmentEffects_Original");

            foreach (var pair in pairs)
            {
                var key = pair.Edited.Key;
                var editedEffects = live.TryGetValue(key, out var l) ? l : new List<EnchantmentEffectRecord>();
                var originalEffects = effectsEditedKeys.Contains(key) && snapshot.TryGetValue(key, out var s)
                    ? s
                    : editedEffects;

                foreach (var e in originalEffects) pair.Original.Effects.Add(e);
                foreach (var e in editedEffects) pair.Edited.Effects.Add(e);
            }

            return pairs;
        }

        private static Dictionary<string, List<EnchantmentEffectRecord>> ReadEffects(
            SqliteConnection conn, string table)
        {
            var byEnchantment = new Dictionary<string, List<EnchantmentEffectRecord>>(StringComparer.Ordinal);
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                $"SELECT EnchantmentKey, MagicEffectKey, EditorID, Name, Magnitude, Duration, Area FROM {table}";

            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var enchKey = Str(r, 0);
                if (!byEnchantment.TryGetValue(enchKey, out var list))
                    byEnchantment[enchKey] = list = new List<EnchantmentEffectRecord>();

                list.Add(new EnchantmentEffectRecord
                {
                    EnchantmentKey = enchKey,
                    MagicEffectKey = Str(r, 1),
                    EditorID = Str(r, 2),
                    Name = Str(r, 3),
                    Magnitude = (float)Dbl(r, 4),
                    Duration = (int)Lng(r, 5),
                    Area = (int)Lng(r, 6),
                });
            }
            return byEnchantment;
        }

        // The enchantments the user created in the tool (Original = 0). They have no scanned
        // original to diff against - they exist nowhere until the generated ESP writes them - so
        // they take the ESP route rather than producing SkyPatcher rules.
        //
        // The effective values are read the same way the editor shows them, shadow columns and all:
        // a user record is edited through the very same paths as a scanned one.
        public IReadOnlyList<CobjEspBuilder.NewEnchantmentEspEntry> ReadNewEnchantments()
        {
            using var conn = new SqliteConnection(_connString);
            conn.Open();

            var effects = ReadEffects(conn, "EnchantmentEffects");
            var list = new List<CobjEspBuilder.NewEnchantmentEspEntry>();

            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT Key, EditorID,
                       CASE WHEN IsEdited = 1 AND IsEditedName IS NOT NULL
                            THEN IsEditedName ELSE Name END,
                       CASE WHEN IsEdited = 1 AND IsEditedCastType IS NOT NULL
                            THEN IsEditedCastType ELSE CastType END,
                       CASE WHEN IsEdited = 1 AND IsEditedTargetType IS NOT NULL
                            THEN IsEditedTargetType ELSE TargetType END,
                       CASE WHEN IsEdited = 1 AND IsEditedEnchantmentCost IS NOT NULL
                            THEN IsEditedEnchantmentCost ELSE EnchantmentCost END,
                       CASE WHEN IsEdited = 1 AND IsEditedWornRestrictionListKey IS NOT NULL
                            THEN IsEditedWornRestrictionListKey ELSE WornRestrictionListKey END,
                       -- EnchantType has no shadow column: user-created records write the base one.
                       EnchantType,
                       CASE WHEN IsEdited = 1 AND IsEditedFlags IS NOT NULL
                            THEN IsEditedFlags ELSE Flags END,
                       CASE WHEN IsEdited = 1 AND IsEditedChargeTime IS NOT NULL
                            THEN IsEditedChargeTime ELSE ChargeTime END,
                       CASE WHEN IsEdited = 1 AND IsEditedEnchantmentAmount IS NOT NULL
                            THEN IsEditedEnchantmentAmount ELSE EnchantmentAmount END
                FROM Enchantments
                WHERE Original = 0 AND Active = 1;";

            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var key = Str(r, 0);
                var own = effects.TryGetValue(key, out var rows)
                    ? rows.Select(e => new CobjEspBuilder.NewEnchantmentEffect(
                                           e.MagicEffectKey, e.Magnitude, e.Duration, e.Area)).ToList()
                    : new List<CobjEspBuilder.NewEnchantmentEffect>();

                list.Add(new CobjEspBuilder.NewEnchantmentEspEntry(
                    ToolKey: key,
                    EditorId: Str(r, 1),
                    Name: Str(r, 2),
                    CastType: Str(r, 3),
                    TargetType: Str(r, 4),
                    Cost: (float)Dbl(r, 5),
                    EnchantType: Str(r, 7),
                    Flags: (int)Lng(r, 8),
                    ChargeTime: (float)Dbl(r, 9),
                    Amount: (int)Lng(r, 10),
                    WornRestrictionListKey: Str(r, 6),
                    Effects: own));
            }

            return list;
        }

        private static string Str(SqliteDataReader r, int i) => r.IsDBNull(i) ? "" : r.GetString(i);

        // Robust to SQLite type-affinity surprises (a value stored as TEXT vs REAL/INT).
        private static double Dbl(SqliteDataReader r, int i)
            => r.IsDBNull(i) ? 0d : Convert.ToDouble(r.GetValue(i), CultureInfo.InvariantCulture);

        private static long Lng(SqliteDataReader r, int i)
            => r.IsDBNull(i) ? 0L : Convert.ToInt64(r.GetValue(i), CultureInfo.InvariantCulture);
    }
}
