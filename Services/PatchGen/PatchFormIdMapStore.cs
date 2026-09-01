using System;
using System.IO;
using Microsoft.Data.Sqlite;
using SkyrimCraftingTool.Model;

namespace SkyrimCraftingTool.Services.PatchGen
{
    // Persistent toolKey -> (targetEsp, formId) allocation for generated COBJ records. Lives in
    // item.db so it travels with the edit state. Never reassigns an existing mapping, so the ESP
    // stays FormID-stable across regenerations. IDs run from 0x800 (ESL new-record range) upward
    // per target ESP. See docs/PatchGenerator-Plan.md §3.
    public sealed class PatchFormIdMapStore
    {
        // First user-record FormID; also the bottom of the ESL new-record range (0x800..0xFFF).
        public const uint FirstFormId = 0x800;

        private readonly string _connString;

        public PatchFormIdMapStore(string? connString = null)
        {
            _connString = connString
                ?? $"Data Source={Path.Combine(GlobalState.Tool.InputFolder, "Item", "item.db")}";
            EnsureTable();
        }

        private SqliteConnection Open()
        {
            var c = new SqliteConnection(_connString);
            c.Open();
            return c;
        }

        private void EnsureTable()
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = @"
                CREATE TABLE IF NOT EXISTS PatchFormIdMap (
                    ToolKey   TEXT PRIMARY KEY,
                    TargetEsp TEXT NOT NULL,
                    FormId    INTEGER NOT NULL
                );";
            cmd.ExecuteNonQuery();
        }

        // Existing mapping if present, else the next free id for that ESP (max + 1, from 0x800).
        // Persists immediately.
        public uint Allocate(string toolKey, string targetEsp)
        {
            using var c = Open();

            using (var get = c.CreateCommand())
            {
                get.CommandText = "SELECT FormId FROM PatchFormIdMap WHERE ToolKey = @k";
                get.Parameters.AddWithValue("@k", toolKey);
                var hit = get.ExecuteScalar();
                if (hit != null && hit != DBNull.Value)
                    return (uint)Convert.ToInt64(hit);
            }

            uint next;
            using (var max = c.CreateCommand())
            {
                max.CommandText = "SELECT MAX(FormId) FROM PatchFormIdMap WHERE TargetEsp = @e";
                max.Parameters.AddWithValue("@e", targetEsp);
                var m = max.ExecuteScalar();
                next = (m == null || m == DBNull.Value) ? FirstFormId : (uint)Convert.ToInt64(m) + 1;
            }

            using (var ins = c.CreateCommand())
            {
                ins.CommandText = "INSERT INTO PatchFormIdMap (ToolKey, TargetEsp, FormId) VALUES (@k, @e, @f)";
                ins.Parameters.AddWithValue("@k", toolKey);
                ins.Parameters.AddWithValue("@e", targetEsp);
                ins.Parameters.AddWithValue("@f", next);
                ins.ExecuteNonQuery();
            }

            return next;
        }

        public uint? Peek(string toolKey)
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT FormId FROM PatchFormIdMap WHERE ToolKey = @k";
            cmd.Parameters.AddWithValue("@k", toolKey);
            var hit = cmd.ExecuteScalar();
            return (hit == null || hit == DBNull.Value) ? null : (uint)Convert.ToInt64(hit);
        }
    }
}
