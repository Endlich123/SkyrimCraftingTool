using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
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
                                THEN IsEditedWornRestrictionListKey ELSE WornRestrictionListKey END
                    FROM Enchantments
                    WHERE (IsEdited = 1 OR EffectsEdited = 1) AND Active = 1";

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
                        },
                        new EnchantmentRecord
                        {
                            Key = key,
                            EditorID = editorId,
                            Name = Str(r, 3),
                            EnchantmentCost = (float)Dbl(r, 5),
                            WornRestrictionListKey = Str(r, 8),
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

        private static string Str(SqliteDataReader r, int i) => r.IsDBNull(i) ? "" : r.GetString(i);

        // Robust to SQLite type-affinity surprises (a value stored as TEXT vs REAL/INT).
        private static double Dbl(SqliteDataReader r, int i)
            => r.IsDBNull(i) ? 0d : Convert.ToDouble(r.GetValue(i), CultureInfo.InvariantCulture);

        private static long Lng(SqliteDataReader r, int i)
            => r.IsDBNull(i) ? 0L : Convert.ToInt64(r.GetValue(i), CultureInfo.InvariantCulture);
    }
}
