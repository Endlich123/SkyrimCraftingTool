using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;

namespace SkyrimCraftingTool.Services.TemplateChildren
{
    // What a template child (ARMO TNAM / WEAP CNAM) does on top of following its template
    // (docs/Plan_Aktuell/KinderPatcher-Plan.md). Written by the preset patch from the addons only the
    // child has - "Master Robes" multiplies the plain robe's value by 512, a tier plugin its rating,
    // a vanilla unique keeps its values or keywords. A child without a row follows its template as
    // it is (None).
    public sealed class ChildExtras
    {
        public string Key { get; set; } = "";
        public double ArmorRatingFactor { get; set; } = 1;
        public double ValueFactor { get; set; } = 1;
        public double WeightFactor { get; set; } = 1;
        public double DamageFactor { get; set; } = 1;
        public bool KeepValues { get; set; }
        public bool KeepKeywords { get; set; }
        public bool LeaveUntouched { get; set; }

        // The addon names behind the numbers, for the item view ("Master Robes, Tier Vigilant").
        public string Addons { get; set; } = "";

        public static ChildExtras None { get; } = new();

        public bool IsNone =>
            ArmorRatingFactor == 1 && ValueFactor == 1 && WeightFactor == 1 && DamageFactor == 1
            && !KeepValues && !KeepKeywords && !LeaveUntouched;

        public bool SameAs(ChildExtras other) =>
            ArmorRatingFactor == other.ArmorRatingFactor && ValueFactor == other.ValueFactor
            && WeightFactor == other.WeightFactor && DamageFactor == other.DamageFactor
            && KeepValues == other.KeepValues && KeepKeywords == other.KeepKeywords
            && LeaveUntouched == other.LeaveUntouched
            && string.Equals(Addons, other.Addons, StringComparison.Ordinal);
    }

    // The ChildInheritance table in item.db. Not part of the scan: it survives rescans like the
    // other user tables, and creates itself, so an older item.db (or a test fixture) simply has no
    // rows yet. A row equal to None is not stored - "no row" already means exactly that.
    public sealed class ChildExtrasStore
    {
        private readonly string _dbPath;

        public ChildExtrasStore(string dbPath) => _dbPath = dbPath;

        private bool Ready => !string.IsNullOrEmpty(_dbPath) && File.Exists(_dbPath);

        private SqliteConnection Open()
        {
            var connection = new SqliteConnection($"Data Source={_dbPath}");
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = @"
                CREATE TABLE IF NOT EXISTS ChildInheritance (
                    Key TEXT PRIMARY KEY COLLATE NOCASE,
                    ArmorRatingFactor REAL NOT NULL DEFAULT 1,
                    ValueFactor REAL NOT NULL DEFAULT 1,
                    WeightFactor REAL NOT NULL DEFAULT 1,
                    DamageFactor REAL NOT NULL DEFAULT 1,
                    KeepValues INTEGER NOT NULL DEFAULT 0,
                    KeepKeywords INTEGER NOT NULL DEFAULT 0,
                    LeaveUntouched INTEGER NOT NULL DEFAULT 0,
                    Addons TEXT);";
            cmd.ExecuteNonQuery();
            return connection;
        }

        public Dictionary<string, ChildExtras> LoadAll()
        {
            var rows = new Dictionary<string, ChildExtras>(StringComparer.OrdinalIgnoreCase);
            if (!Ready) return rows;

            using var connection = Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = @"SELECT Key, ArmorRatingFactor, ValueFactor, WeightFactor, DamageFactor,
                                       KeepValues, KeepKeywords, LeaveUntouched, Addons
                                  FROM ChildInheritance;";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var x = new ChildExtras
                {
                    Key = r.GetString(0),
                    ArmorRatingFactor = r.GetDouble(1),
                    ValueFactor = r.GetDouble(2),
                    WeightFactor = r.GetDouble(3),
                    DamageFactor = r.GetDouble(4),
                    KeepValues = r.GetInt32(5) != 0,
                    KeepKeywords = r.GetInt32(6) != 0,
                    LeaveUntouched = r.GetInt32(7) != 0,
                    Addons = r.IsDBNull(8) ? "" : r.GetString(8),
                };
                rows[x.Key] = x;
            }
            return rows;
        }

        // One transaction for the whole preset run: a template with 30 children writes 30 rows.
        public void Save(IEnumerable<ChildExtras> rows)
        {
            var list = rows.ToList();
            if (list.Count == 0 || !Ready) return;

            using var connection = Open();
            using var tx = connection.BeginTransaction();
            using var upsert = connection.CreateCommand();
            upsert.Transaction = tx;
            upsert.CommandText = @"
                INSERT INTO ChildInheritance (Key, ArmorRatingFactor, ValueFactor, WeightFactor, DamageFactor,
                                              KeepValues, KeepKeywords, LeaveUntouched, Addons)
                VALUES (@k, @ar, @v, @w, @d, @kv, @kk, @lu, @a)
                ON CONFLICT(Key) DO UPDATE SET
                    ArmorRatingFactor = excluded.ArmorRatingFactor, ValueFactor = excluded.ValueFactor,
                    WeightFactor = excluded.WeightFactor, DamageFactor = excluded.DamageFactor,
                    KeepValues = excluded.KeepValues, KeepKeywords = excluded.KeepKeywords,
                    LeaveUntouched = excluded.LeaveUntouched, Addons = excluded.Addons;";
            var k = upsert.Parameters.Add("@k", SqliteType.Text);
            var ar = upsert.Parameters.Add("@ar", SqliteType.Real);
            var v = upsert.Parameters.Add("@v", SqliteType.Real);
            var w = upsert.Parameters.Add("@w", SqliteType.Real);
            var d = upsert.Parameters.Add("@d", SqliteType.Real);
            var kv = upsert.Parameters.Add("@kv", SqliteType.Integer);
            var kk = upsert.Parameters.Add("@kk", SqliteType.Integer);
            var lu = upsert.Parameters.Add("@lu", SqliteType.Integer);
            var a = upsert.Parameters.Add("@a", SqliteType.Text);

            using var delete = connection.CreateCommand();
            delete.Transaction = tx;
            delete.CommandText = "DELETE FROM ChildInheritance WHERE Key = @k;";
            var dk = delete.Parameters.Add("@k", SqliteType.Text);

            foreach (var x in list)
            {
                if (x.IsNone)
                {
                    dk.Value = x.Key;
                    delete.ExecuteNonQuery();
                    continue;
                }
                k.Value = x.Key;
                ar.Value = x.ArmorRatingFactor;
                v.Value = x.ValueFactor;
                w.Value = x.WeightFactor;
                d.Value = x.DamageFactor;
                kv.Value = x.KeepValues ? 1 : 0;
                kk.Value = x.KeepKeywords ? 1 : 0;
                lu.Value = x.LeaveUntouched ? 1 : 0;
                a.Value = x.Addons ?? "";
                upsert.ExecuteNonQuery();
            }
            tx.Commit();
        }

        public void Delete(IEnumerable<string> keys)
        {
            var list = keys.ToList();
            if (list.Count == 0 || !Ready) return;

            using var connection = Open();
            using var tx = connection.BeginTransaction();
            using var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM ChildInheritance WHERE Key = @k;";
            var k = cmd.Parameters.Add("@k", SqliteType.Text);
            foreach (var key in list)
            {
                k.Value = key;
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }

        public void DeleteAll()
        {
            if (!Ready) return;
            using var connection = Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "DELETE FROM ChildInheritance;";
            cmd.ExecuteNonQuery();
        }
    }
}
