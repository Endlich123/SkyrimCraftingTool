using System;
using System.Linq;
using Microsoft.Data.Sqlite;
using SkyrimCraftingTool.Model;

namespace SkyrimCraftingTool.Services.TemplateChildren
{
    // K5 (docs/Plan_Aktuell/KinderPatcher-Plan.md): clears the edits template children carry in the
    // fields they now inherit - made before the Kinder-Patcher, against the wrong base, and ignored by
    // the patch since. Slots, type and keywords on every child; the numbers on an enchanted child that
    // follows its template's numbers (no KeepValues / LeaveUntouched extras). Name, enchantment, charge
    // and placements stay. A row left without any edit loses its edited flag, so Reset all and the
    // "only edited" filter agree with the patch again.
    public static class ChildEditCleanup
    {
        public sealed record Result(int Armor, int Weapons)
        {
            public int Total => Armor + Weapons;
        }

        private static readonly string[] ArmorAlways = { "IsEditedKeywords", "IsEditedBodySlotMask", "IsEditedArmorType" };
        private static readonly string[] ArmorNumbers = { "IsEditedArmorRating", "IsEditedWeight", "IsEditedValue" };
        private static readonly string[] WeaponAlways = { "IsEditedKeywords" };
        private static readonly string[] WeaponNumbers =
            { "IsEditedDamage", "IsEditedCritDamage", "IsEditedWeight", "IsEditedValue", "IsEditedSpeed", "IsEditedReach", "IsEditedStagger", "IsEditedCritPercentMult" };

        // apply = false only counts: the rows that would change.
        public static Result Run(string dbPath, bool apply)
        {
            new ChildExtrasStore(dbPath).LoadAll();   // creates the ChildInheritance table when missing

            using var connection = new SqliteConnection($"Data Source={dbPath}");
            connection.Open();
            using var tx = connection.BeginTransaction();
            int armor = Table(connection, tx, "Armor", ArmorAlways, ArmorNumbers, ItemDBHandler.ArmorShadowColumns, apply);
            int weapons = Table(connection, tx, "Weapons", WeaponAlways, WeaponNumbers, ItemDBHandler.WeaponShadowColumns, apply);
            if (apply) tx.Commit();
            return new Result(armor, weapons);
        }

        private static int Table(SqliteConnection connection, SqliteTransaction tx, string table,
            string[] always, string[] numbers, string[] allShadows, bool apply)
        {
            // A child: active, edited, its template scanned and active. A chain's middle link counts
            // too - the patch follows the chain's root either way.
            string children = $@"
                SELECT c.Key FROM {table} c
                 WHERE c.Active = 1 AND c.IsEdited = 1
                   AND c.TemplateKey IS NOT NULL AND c.TemplateKey <> '' AND c.TemplateKey <> c.Key COLLATE NOCASE
                   AND EXISTS (SELECT 1 FROM {table} t WHERE t.Key = c.TemplateKey COLLATE NOCASE AND t.Active = 1)";
            string followsNumbers = $@"
                COALESCE(CASE WHEN IsEdited = 1 THEN IsEditedObjectEffectKey END, ObjectEffectKey, '') <> ''
                AND NOT EXISTS (SELECT 1 FROM ChildInheritance x WHERE x.Key = {table}.Key COLLATE NOCASE
                                AND (x.KeepValues = 1 OR x.LeaveUntouched = 1))";
            string anyAlways = string.Join(" OR ", always.Select(c => $"{c} IS NOT NULL"));
            string anyNumbers = string.Join(" OR ", numbers.Select(c => $"{c} IS NOT NULL"));

            SqliteCommand Command(string sql)
            {
                var cmd = connection.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = sql;
                cmd.Parameters.AddWithValue("@now", ItemDBHandler.NowIso());
                return cmd;
            }

            int count;
            using (var cmd = Command($@"SELECT COUNT(*) FROM {table}
                                         WHERE Key IN ({children})
                                           AND (({anyAlways}) OR (({anyNumbers}) AND {followsNumbers}))"))
                count = Convert.ToInt32(cmd.ExecuteScalar());
            if (!apply || count == 0) return count;

            using (var cmd = Command($@"UPDATE {table} SET {string.Join(", ", numbers.Select(c => $"{c} = NULL"))}, LastChanged = @now
                                         WHERE Key IN ({children}) AND ({anyNumbers}) AND {followsNumbers}"))
                cmd.ExecuteNonQuery();
            using (var cmd = Command($@"UPDATE {table} SET {string.Join(", ", always.Select(c => $"{c} = NULL"))}, LastChanged = @now
                                         WHERE Key IN ({children}) AND ({anyAlways})"))
                cmd.ExecuteNonQuery();
            using (var cmd = Command($@"UPDATE {table} SET IsEdited = 0
                                         WHERE Key IN ({children}) AND {string.Join(" AND ", allShadows.Select(c => $"{c} IS NULL"))}"))
                cmd.ExecuteNonQuery();
            return count;
        }
    }
}
