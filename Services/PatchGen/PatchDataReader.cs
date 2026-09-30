using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
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

        // NPCs, edited ones only.
        //
        // NOT backed up with the rest of the NPC code in 2026-09-18 - this method was the one
        // piece of the rollback that lived in a file nobody copied, and it had to be written again
        // from its two call sites. LoadNpcsFrom takes a PATH, not a connection string, so the one
        // this reader was built with is unpicked here rather than adding a second way to say where
        // the database is.
        public List<NpcRecord> ReadEditedNpcs()
        {
            var builder = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(_connString);
            return ItemDBHandler.LoadNpcsFrom(builder.DataSource, editedOnly: true);
        }

        // Every NPC, for the group rules: membership is computed from the predicate, so the builder needs
        // the whole set and not only the edited rows. Same unpicking of the connection string as above.
        public List<NpcRecord> ReadAllNpcs()
        {
            var builder = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(_connString);
            return ItemDBHandler.LoadNpcsFrom(builder.DataSource);
        }

        // The levelled character lists, so a group spanning NPCs drawn from one gets a real level range
        // rather than none (G8).
        public Dictionary<string, List<string>> ReadLeveledNpcs()
        {
            var builder = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(_connString);
            return ItemDBHandler.LoadLeveledNpcsFrom(builder.DataSource);
        }

        // The groups themselves. The store guards a database without the tables, so a patch run against
        // an item.db from before G2 gets an empty list rather than an exception.
        public List<NpcGroup> ReadNpcGroups()
        {
            var builder = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(_connString);
            return new NpcGroupStore(builder.DataSource).LoadAll();
        }

        // What the user last confirmed each group contains (section 8). The report holds this against the
        // membership the rules were built from, so it can say "54 -> 71, 17 new from XYZ.esp" rather than
        // only whether a group was checked at all.
        public Dictionary<long, List<string>> ReadGroupSnapshots()
        {
            var builder = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(_connString);
            return new NpcGroupStore(builder.DataSource).LoadSnapshots();
        }

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
                       ContainerString, IsEditedContainerString,
                       ObjectEffectKey, IsEditedObjectEffectKey,
                       EnchantAmount, IsEditedEnchantAmount
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
                    ObjectEffectKey = Str(r, 18),
                    EnchantAmount = (int)Lng(r, 20),
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
                    // Empty is a REAL value here, not "unset": it means the user took the
                    // enchantment off. NULL is the one that means "not edited".
                    ObjectEffectKey = r.IsDBNull(19) ? original.ObjectEffectKey : r.GetString(19),
                    // 0 is a REAL value here too - an item taken back down to no charge.
                    EnchantAmount = r.IsDBNull(21) ? original.EnchantAmount : (int)Lng(r, 21),
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
                       ContainerString, IsEditedContainerString,
                       ObjectEffectKey, IsEditedObjectEffectKey,
                       EnchantAmount, IsEditedEnchantAmount
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
                    ObjectEffectKey = Str(r, 20),
                    EnchantAmount = (int)Lng(r, 22),
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
                    ObjectEffectKey = r.IsDBNull(21) ? original.ObjectEffectKey : r.GetString(21),
                    // See the armor block above.
                    EnchantAmount = r.IsDBNull(23) ? original.EnchantAmount : (int)Lng(r, 23),
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
