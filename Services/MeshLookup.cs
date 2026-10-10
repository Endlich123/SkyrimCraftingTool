using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;
using SkyrimCraftingTool.Model;

namespace SkyrimCraftingTool.Services
{
    // One mesh an item could be shown as. An item usually has several.
    public sealed record MeshHit(long MeshId, string PathNorm, string Slot, string AddonName, bool IsSkinPlaceholder)
    {
        // What the picker shows. The file name is part of it because two addons of the same slot are
        // otherwise indistinguishable - the four race variants of a steel helmet all say "WorldMale".
        public string Label
        {
            get
            {
                var file = Path.GetFileNameWithoutExtension(PathNorm);
                var slot = Slot switch
                {
                    "WorldMale" => "Male",
                    "WorldFemale" => "Female",
                    "FirstMale" => "Male, first person",
                    "FirstFemale" => "Female, first person",
                    "GroundMale" => "Ground model",
                    "GroundFemale" => "Ground model, female",
                    "Model" => "Model",
                    "Scope" => "Scope",
                    _ => Slot,
                };

                var name = AddonName.Length > 0 ? $"{slot} · {AddonName}" : slot;
                return IsSkinPlaceholder ? $"{name} · {file} (bare body)" : $"{name} · {file}";
            }
        }
    }

    // "Which mesh should I show for this item?" - the one question the preview needs answered and the
    // index cannot answer in a single column.
    //
    // A WEAPON names its model directly. AN ARMOR DOES NOT: it names a list of addons, and only the
    // addon has a path. Several addons is the normal case - the four race variants of a steel helmet are
    // four ARMA with four meshes, all equally correct - and each addon then has up to four slots of its
    // own. So there is no single right answer, and this returns the lot, best first.
    //
    // WHY "BEST" IS NOT SIMPLY "MALE": measured on a real 208-mod load order, 146 addons have a male
    // mesh from the base game and a female mesh from a mod. 22 of those point the male slot at BARE
    // VANILLA SKIN - malehands_1.nif, malefeet_1.nif - which is how a female-only mod says "this garment
    // has no male version". Taking the male slot first then shows bare hands instead of the gloves, and
    // nothing about the picture says why. The other 124 are genuine male variants (mostly amulets) and
    // are perfectly fine to show.
    //
    // So the default demotes a bare-skin placeholder and nothing else. Everything beyond that is the
    // user's to choose, because the data really does hold several equally correct answers.
    public static class MeshLookup
    {
        // Preference between slots, worn before carried. The ground model is the item lying in a chest -
        // useful, but not what "what does this look like" means.
        private static readonly string[] ArmorSlots = { "WorldMale", "WorldFemale", "FirstMale", "FirstFemale", "GroundMale", "GroundFemale" };
        private static readonly string[] WeaponSlots = { "Model", "Scope" };

        // The body meshes a female-only mod parks in the male slot. Narrow on purpose: an addon for the
        // naked body itself has skin in BOTH slots, and demoting one of those would be wrong - the rule
        // only ever fires when one gender is skin and the other is not.
        public static bool IsSkinPlaceholder(string pathNorm)
        {
            if (pathNorm.Length == 0) return false;
            if (!pathNorm.Contains(@"\character assets\", StringComparison.OrdinalIgnoreCase)) return false;

            var file = Path.GetFileName(pathNorm);
            foreach (var part in new[] { "body", "hands", "feet", "head" })
                if (file.Contains(part, StringComparison.OrdinalIgnoreCase)) return true;

            return false;
        }

        public static IReadOnlyList<MeshHit> AllForRecord(string dbPath, string recordKey, bool isArmor)
            => isArmor ? AllForArmor(dbPath, recordKey) : AllForWeapon(dbPath, recordKey);

        // The best single answer, for callers that only want something to show. Null when the item has no
        // mesh at all. A caller that can offer a CHOICE should take the list instead - see the class
        // comment for why a single answer is a compromise and not a fact.
        public static MeshHit? ForRecord(string dbPath, string recordKey, bool isArmor)
            => AllForRecord(dbPath, recordKey, isArmor).FirstOrDefault();

        public static MeshHit? ForArmor(string dbPath, string armorKey)
            => AllForArmor(dbPath, armorKey).FirstOrDefault();

        public static MeshHit? ForWeapon(string dbPath, string weaponKey)
            => AllForWeapon(dbPath, weaponKey).FirstOrDefault();

        public static IReadOnlyList<MeshHit> AllForArmor(string dbPath, string armorKey)
        {
            // The worn meshes hang off the addons, the ground model off the armor itself - two different
            // joins, which is why this is two queries rather than one with a filter.
            var worn = Query(dbPath, @"
                SELECT m.MeshId, m.PathNorm, r.Slot, COALESCE(aa.EditorID, ''), am.Ordinal
                FROM ArmorArmature am
                JOIN MeshRef r      ON r.RecordKey = am.AddonKey
                JOIN Mesh m         ON m.MeshId = r.MeshId
                LEFT JOIN ArmorAddon aa ON aa.Key = am.AddonKey
                WHERE am.ArmorKey = @key AND m.SourceKind <> 0;", armorKey);

            var ground = Query(dbPath, @"
                SELECT m.MeshId, m.PathNorm, r.Slot, '', 0
                FROM MeshRef r
                JOIN Mesh m ON m.MeshId = r.MeshId
                WHERE r.RecordKey = @key AND m.SourceKind <> 0;", armorKey);

            return Rank(worn.Concat(ground), ArmorSlots);
        }

        public static IReadOnlyList<MeshHit> AllForWeapon(string dbPath, string weaponKey)
        {
            var hits = Query(dbPath, @"
                SELECT m.MeshId, m.PathNorm, r.Slot, '', 0
                FROM MeshRef r
                JOIN Mesh m ON m.MeshId = r.MeshId
                WHERE r.RecordKey = @key AND m.SourceKind <> 0;", weaponKey);

            return Rank(hits, WeaponSlots);
        }

        // Best first, and stable: two runs over the same database give the same order, which matters
        // because the preview remembers nothing between openings.
        private static IReadOnlyList<MeshHit> Rank(IEnumerable<(MeshHit Hit, int Ordinal)> rows, string[] slotOrder)
        {
            var all = rows.ToList();

            // A slot is only a placeholder when its counterpart is NOT one. The naked-body addon has skin
            // in both slots and must keep its natural order.
            bool anyRealMesh = all.Any(r => !r.Hit.IsSkinPlaceholder);

            return all
                .GroupBy(r => r.Hit.MeshId * 100 + Array.IndexOf(slotOrder, r.Hit.Slot))
                .Select(g => g.First())
                .OrderBy(r => anyRealMesh && r.Hit.IsSkinPlaceholder ? 1 : 0)
                .ThenBy(r => SlotRank(r.Hit.Slot, slotOrder))
                .ThenBy(r => r.Ordinal)
                .ThenBy(r => r.Hit.PathNorm, StringComparer.OrdinalIgnoreCase)
                .Select(r => r.Hit)
                .ToList();
        }

        private static int SlotRank(string slot, string[] order)
        {
            int index = Array.IndexOf(order, slot);
            return index < 0 ? order.Length : index;
        }

        private static List<(MeshHit Hit, int Ordinal)> Query(string dbPath, string sql, string key)
        {
            var result = new List<(MeshHit, int)>();

            if (string.IsNullOrWhiteSpace(dbPath) || !File.Exists(dbPath)) return result;
            if (string.IsNullOrWhiteSpace(key)) return result;

            try
            {
                using var connection = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
                connection.Open();

                using var cmd = connection.CreateCommand();
                cmd.CommandText = sql;
                cmd.Parameters.AddWithValue("@key", key);

                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    var path = r.GetString(1);
                    result.Add((new MeshHit(r.GetInt64(0), path, r.GetString(2), r.GetString(3), IsSkinPlaceholder(path)),
                                r.GetInt32(4)));
                }
            }
            catch (Exception ex)
            {
                // A database written before the mesh index existed has no such tables. Not an error worth
                // a dialog: the preview button simply says there is nothing to show.
                AppLogger.LogWarning($"Mesh lookup for {key}: {ex.Message}");
            }

            return result;
        }
    }
}
