using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Data.Sqlite;
using SkyrimCraftingTool.Model;

namespace SkyrimCraftingTool.Services.PatchGen
{
    // One FLST whose contents the user edited: the pristine scanned member set alongside the
    // current one, so FormListRuleBuilder can emit the add/remove delta.
    public sealed record FormListPatchPair(
        string ListKey,
        IReadOnlyList<string> OriginalMembers,
        IReadOnlyList<string> EditedMembers);

    // Reads item.db for FLSTs with user-edited contents. See docs/EnchantmentPatch-Plan.md (E-P3).
    public sealed class FormListPatchReader
    {
        private readonly string _connString;

        public FormListPatchReader(string? connString = null)
        {
            _connString = connString
                ?? $"Data Source={Path.Combine(GlobalState.Tool.InputFolder, "Item", "item.db")}";
        }

        public IReadOnlyList<FormListPatchPair> ReadEditedFormLists()
        {
            var pairs = new List<FormListPatchPair>();
            using var conn = new SqliteConnection(_connString);
            conn.Open();

            // WornRestrictionListState is the per-list edit flag introduced in E3 - a list edit
            // marks the LIST, not the N enchantments pointing at it. ResetWornRestrictionKeywords
            // deletes the state row, so a reverted list simply isn't listed here any more.
            var editedLists = new List<string>();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"SELECT ListKey FROM WornRestrictionListState
                                     WHERE IsEdited = 1
                                       AND ListKey IS NOT NULL AND ListKey <> ''
                                       AND ListKey NOT LIKE 'Null|%'";
                using var r = cmd.ExecuteReader();
                while (r.Read()) editedLists.Add(r.GetString(0));
            }
            if (editedLists.Count == 0) return pairs;

            // Current contents, and the lazy pre-edit snapshot. _Original only has rows once the
            // list was first edited - which every list here is, by definition of the flag above.
            var live = ReadMembers(conn, "WornRestrictionKeywords");
            var snapshot = ReadMembers(conn, "WornRestrictionKeywords_Original");

            foreach (var listKey in editedLists)
            {
                pairs.Add(new FormListPatchPair(
                    listKey,
                    snapshot.TryGetValue(listKey, out var o) ? o : new List<string>(),
                    live.TryGetValue(listKey, out var e) ? e : new List<string>()));
            }
            return pairs;
        }

        private static Dictionary<string, List<string>> ReadMembers(SqliteConnection conn, string table)
        {
            var byList = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT ListKey, KeywordKey FROM {table}";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var listKey = r.IsDBNull(0) ? "" : r.GetString(0);
                if (listKey.Length == 0) continue;
                if (!byList.TryGetValue(listKey, out var list))
                    byList[listKey] = list = new List<string>();
                list.Add(r.IsDBNull(1) ? "" : r.GetString(1));
            }
            return byList;
        }
    }
}
