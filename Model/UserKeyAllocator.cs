using Microsoft.Data.Sqlite;
using SkyrimCraftingTool.Services;
using System;
using System.Globalization;

namespace SkyrimCraftingTool.Model
{
    internal static class UserKeyAllocator
    {
        internal const int RecordBaseline = 0x1000;
        internal const int ListBaseline = UserLeveledListStore.KeyBaseline;

        private static readonly string[] RecordTables = { "COBJ", "Enchantments", "Spells" };
        private static readonly object Gate = new();
        private static int _lastIssued;

        private static int _batchDepth;
        private static bool _batchScanned;

        public static IDisposable BeginBatch()
        {
            lock (Gate) _batchDepth++;
            return new BatchScope();
        }

        private sealed class BatchScope : IDisposable
        {
            private bool _done;
            public void Dispose()
            {
                if (_done) return;
                _done = true;
                lock (Gate)
                {
                    if (--_batchDepth == 0) _batchScanned = false;
                }
            }
        }

        // The next free key for a recipe, enchantment or spell.
        public static string NextRecordKey(SqliteConnection? connection = null)
        {
            lock (Gate)
            {
                int max = Math.Max(RecordBaseline, _lastIssued);

                if (_batchDepth > 0 && _batchScanned)
                {
                    // scanned once in this batch - counting on from _lastIssued
                }
                else if (connection != null)
                    max = Math.Max(max, HighestStored(connection));
                else
                {
                    using var own = new SqliteConnection(ItemDBHandler.ConnString);
                    own.Open();
                    max = Math.Max(max, HighestStored(own));
                }
                if (_batchDepth > 0) _batchScanned = true;

                _lastIssued = max + 1;
                return $"{KeyFactory.UserPluginName}|{_lastIssued:X6}";
            }
        }

        private static int _lastListIssued;

        // The next free key for an own leveled list, in the list range - for the import (TE-6), which
        // has to hand out several before any of them is stored. The same reading as
        // UserLeveledListStore.AllocateKey (own lists + the FormID map), plus the same floor as above.
        public static string NextListKey(SqliteConnection connection)
        {
            lock (Gate)
            {
                int max = Math.Max(ListBaseline, _lastListIssued);

                void Scan(string sql)
                {
                    using var cmd = connection.CreateCommand();
                    cmd.CommandText = sql;
                    cmd.Parameters.AddWithValue("@prefix", KeyFactory.UserPluginName + "|%");
                    using var r = cmd.ExecuteReader();
                    while (r.Read())
                    {
                        var key = r.GetString(0);
                        int bar = key.IndexOf('|');
                        if (bar >= 0
                            && int.TryParse(key.Substring(bar + 1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v)
                            && v > max)
                            max = v;
                    }
                }

                if (UserRecords.TableExists(connection, "LeveledList"))
                    Scan("SELECT Key FROM LeveledList WHERE Key LIKE @prefix");
                if (UserRecords.TableExists(connection, "PatchFormIdMap"))
                    Scan("SELECT ToolKey FROM PatchFormIdMap WHERE ToolKey LIKE @prefix");

                _lastListIssued = max + 1;
                return $"{KeyFactory.UserPluginName}|{_lastListIssued:X6}";
            }
        }

        // Every own key below the list range, in every record table and in the FormID map - the map
        // still remembers a deleted record's key.
        private static int HighestStored(SqliteConnection connection)
        {
            int max = 0;

            void Scan(string sql)
            {
                using var cmd = connection.CreateCommand();
                cmd.CommandText = sql;
                cmd.Parameters.AddWithValue("@prefix", KeyFactory.UserPluginName + "|%");
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    var key = r.GetString(0);
                    int bar = key.IndexOf('|');
                    if (bar >= 0
                        && int.TryParse(key.Substring(bar + 1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v)
                        && v < ListBaseline && v > max)
                        max = v;
                }
            }

            foreach (var table in RecordTables)
                if (UserRecords.TableExists(connection, table))
                    Scan($"SELECT Key FROM {table} WHERE Key LIKE @prefix");

            if (UserRecords.TableExists(connection, "PatchFormIdMap"))
                Scan("SELECT ToolKey FROM PatchFormIdMap WHERE ToolKey LIKE @prefix");

            return max;
        }
    }
}
