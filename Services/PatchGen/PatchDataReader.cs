using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Microsoft.Data.Sqlite;
using SkyrimCraftingTool.Model;

namespace SkyrimCraftingTool.Services.PatchGen
{
    public sealed record ArmorPatchPair(ArmorRecord Original, ArmorRecord Edited);
    public sealed record WeaponPatchPair(WeaponRecord Original, WeaponRecord Edited);

    // Reads item.db for edited ARMO/WEAP rows, returning the pristine scanned record alongside the
    // effective (shadow-applied) record so the rule builder can diff them. GetEditedItems only
    // carries the deltas, which isn't enough for keyword / biped-slot diffing.
    public sealed class PatchDataReader
    {
        private readonly string _connString;

        public PatchDataReader(string? connString = null)
        {
            _connString = connString
                ?? $"Data Source={Path.Combine(GlobalState.Tool.InputFolder, "Item", "item.db")}";
        }

        public IReadOnlyList<ArmorPatchPair> ReadEditedArmor()
        {
            var pairs = new List<ArmorPatchPair>();
            using var conn = new SqliteConnection(_connString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT Key, EditorID,
                       Name, IsEditedName,
                       ArmorRating, IsEditedArmorRating,
                       Value, IsEditedValue,
                       Weight, IsEditedWeight,
                       Keywords, IsEditedKeywords,
                       BodySlotMask, IsEditedBodySlotMask,
                       ArmorType, IsEditedArmorType,
                       ContainerString, IsEditedContainerString
                FROM Armor
                -- IsEdited, NOT ""LastChanged IS NOT NULL"": ResetArmorEdits clears the flag + every
                -- shadow but deliberately leaves LastChanged set (it feeds the import conflict
                -- check), so the old filter kept re-reading reset items. It also guarantees the
                -- shadow-vs-base pick below matches LoadArmor's ""CASE WHEN IsEdited = 1 AND …"" —
                -- a row with IsEdited = 0 and a leftover shadow would otherwise be patched with a
                -- value the UI itself doesn't show. Same rule as ItemDBHandler.GetEditedItems.
                WHERE IsEdited = 1 AND Active = 1";

            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                string key = r.GetString(0);
                string editorId = Str(r, 1);

                var original = new ArmorRecord
                {
                    Key = key,
                    EditorID = editorId,
                    Name = Str(r, 2),
                    ArmorRating = (float)Dbl(r, 4),
                    Value = (int)Lng(r, 6),
                    Weight = (float)Dbl(r, 8),
                    Keywords = Csv(Str(r, 10)),
                    BodySlotMask = (uint)Lng(r, 12),
                    ArmorType = Str(r, 14),
                    ContainerString = Str(r, 16),
                };

                var edited = new ArmorRecord
                {
                    Key = key,
                    EditorID = editorId,
                    Name = r.IsDBNull(3) ? original.Name : r.GetString(3),
                    ArmorRating = r.IsDBNull(5) ? original.ArmorRating : (float)Dbl(r, 5),
                    Value = r.IsDBNull(7) ? original.Value : (int)Lng(r, 7),
                    Weight = r.IsDBNull(9) ? original.Weight : (float)Dbl(r, 9),
                    Keywords = r.IsDBNull(11) ? original.Keywords : Csv(r.GetString(11)),
                    BodySlotMask = r.IsDBNull(13) ? original.BodySlotMask : (uint)Lng(r, 13),
                    ArmorType = r.IsDBNull(15) ? original.ArmorType : r.GetString(15),
                    // The scan never writes the base column (ArmorParamNames has no ContainerString),
                    // so in practice Original is always empty and this shadow IS the whole placement
                    // set - see LeveledListRuleBuilder on why that makes the feature additive-only.
                    ContainerString = r.IsDBNull(17) ? original.ContainerString : r.GetString(17),
                };

                pairs.Add(new ArmorPatchPair(original, edited));
            }
            return pairs;
        }

        public IReadOnlyList<WeaponPatchPair> ReadEditedWeapons()
        {
            var pairs = new List<WeaponPatchPair>();
            using var conn = new SqliteConnection(_connString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT Key, EditorID,
                       Name, IsEditedName,
                       Damage, IsEditedDamage,
                       Speed, IsEditedSpeed,
                       Reach, IsEditedReach,
                       Stagger, IsEditedStagger,
                       Value, IsEditedValue,
                       Weight, IsEditedWeight,
                       Keywords, IsEditedKeywords,
                       ContainerString, IsEditedContainerString
                FROM Weapons
                -- see ReadEditedArmor for why this is IsEdited and not LastChanged
                WHERE IsEdited = 1 AND Active = 1";

            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                string key = r.GetString(0);
                string editorId = Str(r, 1);

                var original = new WeaponRecord
                {
                    Key = key,
                    EditorID = editorId,
                    Name = Str(r, 2),
                    Damage = (int)Lng(r, 4),
                    Speed = (float)Dbl(r, 6),
                    Reach = (float)Dbl(r, 8),
                    Stagger = (float)Dbl(r, 10),
                    Value = (int)Lng(r, 12),
                    Weight = (float)Dbl(r, 14),
                    Keywords = Csv(Str(r, 16)),
                    ContainerString = Str(r, 18),
                };

                var edited = new WeaponRecord
                {
                    Key = key,
                    EditorID = editorId,
                    Name = r.IsDBNull(3) ? original.Name : r.GetString(3),
                    Damage = r.IsDBNull(5) ? original.Damage : (int)Lng(r, 5),
                    Speed = r.IsDBNull(7) ? original.Speed : (float)Dbl(r, 7),
                    Reach = r.IsDBNull(9) ? original.Reach : (float)Dbl(r, 9),
                    Stagger = r.IsDBNull(11) ? original.Stagger : (float)Dbl(r, 11),
                    Value = r.IsDBNull(13) ? original.Value : (int)Lng(r, 13),
                    Weight = r.IsDBNull(15) ? original.Weight : (float)Dbl(r, 15),
                    Keywords = r.IsDBNull(17) ? original.Keywords : Csv(r.GetString(17)),
                    // See ReadEditedArmor.
                    ContainerString = r.IsDBNull(19) ? original.ContainerString : r.GetString(19),
                };

                pairs.Add(new WeaponPatchPair(original, edited));
            }
            return pairs;
        }

        // LVLiKey -> display name, from the container scan's child table. Only used for the "; ..."
        // comment above each leveled-list rule, so a missing entry costs nothing but readability.
        // The same list can hang in several containers; the names agree, so last one wins.
        public IReadOnlyDictionary<string, string> ReadLeveledListNames()
        {
            var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            // Purely cosmetic - it fills the "; leveled list <name>" comment above each rule - so it
            // must never be able to take the export down. It can genuinely fail: ContainerLVLI is a
            // younger table than the rest, and a database from before it existed (or one whose WAL
            // has not been checkpointed into the file being read) answers with "no such table".
            // Every other reader here queries tables that have always been there; this one degrades
            // to keys-only comments instead.
            try
            {
                using var conn = new SqliteConnection(_connString);
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT LVLiKey, LVLiName FROM ContainerLVLI";

                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    var key = Str(r, 0);
                    if (key.Length == 0) continue;
                    names[key] = Str(r, 1);
                }
            }
            catch (Exception ex)
            {
                AppLogger.LogError("PatchDataReader.ReadLeveledListNames failed - rules keep their key-only comments", ex);
            }

            return names;
        }

        // ContainerKey -> display name, for the "; ... via <container>" half of a rule's comment.
        // Cosmetic like ReadLeveledListNames, and fails the same way rather than taking the export
        // down with it.
        public IReadOnlyDictionary<string, string> ReadContainerNames()
        {
            var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using var conn = new SqliteConnection(_connString);
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT ContainerKey, Name FROM Container";

                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    var key = Str(r, 0);
                    if (key.Length == 0) continue;
                    names[key] = Str(r, 1);
                }
            }
            catch (Exception ex)
            {
                AppLogger.LogError("PatchDataReader.ReadContainerNames failed - comments keep their keys", ex);
            }

            return names;
        }

        private static string Str(SqliteDataReader r, int i) => r.IsDBNull(i) ? "" : r.GetString(i);

        // Robust to SQLite type-affinity surprises (a shadow value stored as TEXT vs REAL/INT).
        private static double Dbl(SqliteDataReader r, int i)
            => r.IsDBNull(i) ? 0d : Convert.ToDouble(r.GetValue(i), CultureInfo.InvariantCulture);

        private static long Lng(SqliteDataReader r, int i)
            => r.IsDBNull(i) ? 0L : Convert.ToInt64(r.GetValue(i), CultureInfo.InvariantCulture);

        private static List<string> Csv(string s)
            => string.IsNullOrWhiteSpace(s) ? new List<string>() : new List<string>(s.Split(','));
    }
}
