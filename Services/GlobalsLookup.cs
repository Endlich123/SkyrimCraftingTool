using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Data.Sqlite;
using SkyrimCraftingTool.Model;

namespace SkyrimCraftingTool.Services
{
    // One global variable, as the scan found it. Value is what it holds in the load order right now -
    // a starting value, not a constant: quests and scripts move it while playing.
    public sealed record GlobalInfo(string Key, string EditorId, double? Value)
    {
        public string Display => string.IsNullOrWhiteSpace(EditorId) ? Key : EditorId;

        public string ValueText => Value.HasValue
            ? Value.Value.ToString("0.##", System.Globalization.CultureInfo.CurrentCulture)
            : "?";
    }

    // The global a leveled list takes its chance from.
    //
    // One lookup, no listing: choosing a global was deliberately taken back out (2026-09-16), so the
    // only question left is "what does THIS list's global hold" - which the calculator needs, because
    // on a gated list the stored chance is a parked 100 the game never reads.
    public static class GlobalsLookup
    {
        private static string ItemDbPath =>
            Path.Combine(GlobalState.Tool?.InputFolder ?? "", "Item", "item.db");

        private static SqliteConnection? TryOpen(string? dbPath)
        {
            var path = string.IsNullOrWhiteSpace(dbPath) ? ItemDbPath : dbPath;
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;

            var c = new SqliteConnection($"Data Source={path};Mode=ReadOnly");
            c.Open();
            return c;
        }

        public static GlobalInfo? Read(string globalKey, string? dbPath = null)
        {
            if (string.IsNullOrWhiteSpace(globalKey)) return null;

            try
            {
                using var c = TryOpen(dbPath);
                if (c == null) return null;

                using var cmd = c.CreateCommand();
                cmd.CommandText = "SELECT Key, COALESCE(EditorID, ''), Value FROM Globals WHERE Key = @k";
                cmd.Parameters.AddWithValue("@k", globalKey);

                using var r = cmd.ExecuteReader();
                if (!r.Read()) return null;

                return new GlobalInfo(r.GetString(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetDouble(2));
            }
            catch (Exception ex)
            {
                AppLogger.LogWarning($"GlobalsLookup.Read({globalKey}): {ex.Message}");
                return null;
            }
        }
    }
}
