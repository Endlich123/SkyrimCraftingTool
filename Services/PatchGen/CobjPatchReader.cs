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
        public IReadOnlyList<COBJConditionRecord> Conditions { get; init; } = Array.Empty<COBJConditionRecord>();

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

            var rows = new List<(string Key, bool IsNew, string Name, string Created, string Workbench, string Ingredients)>();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
                    SELECT Key, Original,
                        CASE WHEN IsEditedName            IS NOT NULL THEN IsEditedName            ELSE Name            END,
                        CASE WHEN IsEditedCreatedItem     IS NOT NULL THEN IsEditedCreatedItem     ELSE CreatedItem     END,
                        CASE WHEN IsEditedWorkbenchKeyword IS NOT NULL THEN IsEditedWorkbenchKeyword ELSE WorkbenchKeyword END,
                        CASE WHEN IsEditedIngredients     IS NOT NULL THEN IsEditedIngredients     ELSE Ingredients     END
                    FROM COBJ
                    WHERE LastChanged IS NOT NULL AND Active = 1";

                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    rows.Add((
                        r.GetString(0),
                        r.GetInt32(1) == 0,
                        Str(r, 2), Str(r, 3), Str(r, 4), Str(r, 5)));
                }
            }

            foreach (var row in rows)
            {
                entries.Add(new CobjPatchEntry
                {
                    ToolKey = row.Key,
                    IsNew = row.IsNew,
                    Name = row.Name,
                    CreatedItemKey = row.Created,
                    WorkbenchKey = row.Workbench,
                    Ingredients = ParseIngredients(row.Ingredients),
                    Conditions = ReadConditions(conn, row.Key),
                });
            }

            return entries;
        }

        private static List<COBJConditionRecord> ReadConditions(SqliteConnection conn, string cobjKey)
        {
            var list = new List<COBJConditionRecord>();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT ConditionType, Target, Value, Extra, RunOn FROM COBJ_Conditions WHERE COBJKey = @k";
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

        private static string Str(SqliteDataReader r, int i) => r.IsDBNull(i) ? "" : r.GetString(i);
    }
}
