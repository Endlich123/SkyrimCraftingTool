using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Data.Sqlite;
using SkyrimCraftingTool.Model;

namespace SkyrimCraftingTool.Services.PatchGen
{
    // One recipe to emit into the generated ESP.
    public sealed class CobjPatchEntry
    {
        // "Plugin|FormID": the source COBJ for an override, or "SkyrimCraftingTool.esp|..." for a
        // tool-created recipe (Original == 0).
        public string ToolKey { get; init; } = "";

        // Original == 0 -> brand-new record; else -> override of an existing master COBJ.
        public bool IsNew { get; init; }

        public string Name { get; init; } = "";
        public string CreatedItemKey { get; init; } = "";
        public string WorkbenchKey { get; init; } = "";
        public IReadOnlyList<(string Key, int Count)> Ingredients { get; init; } = Array.Empty<(string, int)>();

        // How many objects the recipe produces (NAM1). Always at least 1 - see COBJRecord.
        public int CreatedObjectCount { get; init; } = 1;

        // The user changed the count. Same role as ConditionsEdited, and for the same reason: an
        // override is a DEEP COPY of the winning record, so a field the user did not touch must be
        // left exactly as the copy has it. Writing the stored value unconditionally would push a 1
        // over the real count of every record whose item.db row predates the CreatedObjectCount
        // column - which is every row until the next scan.
        public bool CreatedObjectCountEdited { get; init; }

        // The user took this recipe out of the game. The ESP gets an override carrying the Deleted
        // record flag and nothing else - no field on this entry is written for such a record.
        //
        // Only ever set for a MOD's recipe: one the tool created is deleted row and all, so it
        // never reaches the patch at all.
        public bool IsDeleted { get; init; }
        public IReadOnlyList<COBJConditionRecord> Conditions { get; init; } = Array.Empty<COBJConditionRecord>();

        // The user replaced this recipe's condition set. Only then may the ESP builder overwrite the
        // conditions of the record it deep-copied from the load order - see CobjEspBuilder.BuildOne.
        public bool ConditionsEdited { get; init; }

        // The plugin this recipe "belongs to" for per-source-plugin ESP splitting: the overridden
        // COBJ's own plugin for an override, else the created item's plugin.
        public string SourcePlugin =>
            PluginOf(IsNew ? CreatedItemKey : ToolKey);

        private static string PluginOf(string key)
        {
            var bar = key.IndexOf('|');
            return bar > 0 ? key[..bar] : "";
        }
    }

    // Reads edited COBJ rows (+ their conditions) straight from item.db. The base columns already
    // hold the master's scanned values, so no plugin link cache is needed to build overrides — the
    // effective value is (shadow ?? base) for every field. See docs/PatchGenerator-Plan.md §3.
    public sealed class CobjPatchReader
    {
        private readonly string _connString;

        public CobjPatchReader(string? connString = null)
        {
            _connString = connString
                ?? $"Data Source={Path.Combine(GlobalState.Tool.InputFolder, "Item", "item.db")}";
        }

        public IReadOnlyList<CobjPatchEntry> ReadEditedCobj()
        {
            var entries = new List<CobjPatchEntry>();

            using var conn = new SqliteConnection(_connString);
            conn.Open();

            var rows = new List<(string Key, bool IsNew, string Name, string Created, string Workbench, string Ingredients, bool ConditionsEdited, int CreatedCount, bool CountEdited)>();
            using (var cmd = conn.CreateCommand())
            {
                // Two corrections over the original query, both about matching what the UI shows:
                //
                // 1. Every CASE is gated on IsEdited = 1, exactly like LoadCOBJ. Without it a row
                //    with IsEdited = 0 and a leftover shadow got PATCHED with a value the editor
                //    itself never displays.
                // 2. The WHERE is the same predicate as ItemDBHandler.GetEditedCOBJ instead of
                //    "LastChanged IS NOT NULL" — the reset paths clear the flags but deliberately
                //    keep LastChanged (it feeds the import conflict check). Unlike the ARMO/WEAP
                //    side there is no diff step here: every row read becomes a CobjPatchEntry and
                //    lands in the ESP, so a reset vanilla recipe used to get a pointless override
                //    that SkyrimCraftingTool.esp then owns (and wins with over later mods).
                //    Original = 0 = user-created recipe, always patch-worthy.
                cmd.CommandText = @"
                    SELECT Key, Original,
                        CASE WHEN IsEdited = 1 AND IsEditedName             IS NOT NULL THEN IsEditedName             ELSE Name             END,
                        CASE WHEN IsEdited = 1 AND IsEditedCreatedItem      IS NOT NULL THEN IsEditedCreatedItem      ELSE CreatedItem      END,
                        CASE WHEN IsEdited = 1 AND IsEditedWorkbenchKeyword IS NOT NULL THEN IsEditedWorkbenchKeyword ELSE WorkbenchKeyword END,
                        CASE WHEN IsEdited = 1 AND IsEditedIngredients      IS NOT NULL THEN IsEditedIngredients      ELSE Ingredients      END,
                        ConditionsEdited,
                        CASE WHEN IsEdited = 1 AND IsEditedCreatedObjectCount IS NOT NULL THEN IsEditedCreatedObjectCount ELSE CreatedObjectCount END,
                        CASE WHEN IsEdited = 1 AND IsEditedCreatedObjectCount IS NOT NULL THEN 1 ELSE 0 END
                    FROM COBJ
                    WHERE (IsEdited = 1 OR ConditionsEdited = 1 OR Original = 0) AND Active = 1";

                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    rows.Add((
                        r.GetString(0),
                        r.GetInt32(1) == 0,
                        Str(r, 2), Str(r, 3), Str(r, 4), Str(r, 5),
                        !r.IsDBNull(6) && r.GetInt32(6) == 1,
                        Model.ItemDBHandler.ReadCountOrOne(r, 7),
                        !r.IsDBNull(8) && r.GetInt32(8) == 1));
                }
            }

            // Recipes the user took out. They are a decision, not an edit, so they live in their own
            // table and the query above does not see them: a mod recipe that was only REMOVED has
            // no IsEdited, no ConditionsEdited and Original = 1, so nothing would have brought it
            // into the patch.
            var removed = ReadRemovedRecipeKeys(conn);

            // A recipe the TOOL created and the user then removed is simply not written. It never
            // existed in the game, so leaving it out IS its removal - there is nothing to flag as
            // deleted, and emitting an override for a FormID no plugin owns would inject it.
            var removedToolRecipes = rows
                .Where(r => r.IsNew && removed.Contains(r.Key))
                .Select(r => r.Key)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var row in rows)
            {
                // A removed recipe needs nothing but its key - every field below would be written
                // over a record that is about to be flagged deleted.
                if (removed.Contains(row.Key)) continue;

                entries.Add(new CobjPatchEntry
                {
                    ToolKey = row.Key,
                    IsNew = row.IsNew,
                    Name = row.Name,
                    CreatedItemKey = row.Created,
                    WorkbenchKey = row.Workbench,
                    Ingredients = ParseIngredients(row.Ingredients),
                    Conditions = ReadConditions(conn, row.Key),
                    ConditionsEdited = row.ConditionsEdited,
                    CreatedObjectCount = row.CreatedCount,
                    CreatedObjectCountEdited = row.CountEdited,
                });
            }

            foreach (var key in removed)
            {
                if (removedToolRecipes.Contains(key)) continue;

                entries.Add(new CobjPatchEntry
                {
                    ToolKey = key,
                    IsNew = false,
                    IsDeleted = true,
                });
            }

            return entries;
        }

        private static List<COBJConditionRecord> ReadConditions(SqliteConnection conn, string cobjKey)
        {
            var list = new List<COBJConditionRecord>();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT ConditionType, Target, Value, Extra, RunOn, CompareOperator, Flags FROM COBJ_Conditions WHERE COBJKey = @k";
            cmd.Parameters.AddWithValue("@k", cobjKey);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                list.Add(new COBJConditionRecord
                {
                    COBJKey = cobjKey,
                    ConditionType = Str(r, 0),
                    Target = Str(r, 1),
                    Value = Str(r, 2),
                    Extra = Str(r, 3),
                    RunOn = Str(r, 4),
                    CompareOperator = Str(r, 5),
                    Flags = Str(r, 6),
                });
            }
            return list;
        }

        // "Plugin|FormID*Count, Plugin|FormID*Count" -> [(key, count)]. Missing/garbage count -> 1.
        internal static List<(string Key, int Count)> ParseIngredients(string raw)
        {
            var result = new List<(string, int)>();
            if (string.IsNullOrWhiteSpace(raw)) return result;

            foreach (var part in raw.Split(','))
            {
                var token = part.Trim();
                if (token.Length == 0) continue;

                int star = token.LastIndexOf('*');
                string key = star >= 0 ? token[..star].Trim() : token;
                int count = 1;
                if (star >= 0 && int.TryParse(token[(star + 1)..].Trim(), out var c) && c > 0)
                    count = c;

                if (key.Length > 0)
                    result.Add((key, count));
            }
            return result;
        }

        // The COBJ keys the user removed. A missing table is not an error: an item.db from before
        // this feature simply has no removals, and the patch run must not fall over on it.
        private static HashSet<string> ReadRemovedRecipeKeys(SqliteConnection conn)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            using (var exists = conn.CreateCommand())
            {
                exists.CommandText =
                    "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'RecipeRemovedEntry'";
                if (exists.ExecuteScalar() == null) return result;
            }

            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT DISTINCT Reference FROM RecipeRemovedEntry";

            using var r = cmd.ExecuteReader();
            while (r.Read())
                if (!r.IsDBNull(0)) result.Add(r.GetString(0));

            return result;
        }

        private static string Str(SqliteDataReader r, int i) => r.IsDBNull(i) ? "" : r.GetString(i);
    }
}
