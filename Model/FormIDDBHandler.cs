using Microsoft.Data.Sqlite;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Order;
using Mutagen.Bethesda.Skyrim;
using System.Diagnostics;
using System.IO;

namespace SkyrimCraftingTool.Model
{
    public class FormIDDBHandler
    {
        private string FormIDFolder => Path.Combine(GlobalState.Tool.InputFolder, "FormID");
        private string FormIDdbPath => Path.Combine(FormIDFolder, "formid.db");

        // ============================
        //            CACHE
        // ============================
        private List<FormIDRecord> _cache = new();
        private bool _cacheLoaded = false;

        private void InvalidateCache()
        {
            _cacheLoaded = false;
            _cache.Clear();
        }

        private void LoadCache()
        {
            if (_cacheLoaded) return;

            _cache = new List<FormIDRecord>();
            _cache.AddRange(LoadTable("Keywords", "Keyword"));
            _cache.AddRange(LoadTable("Materials", "Material"));
            _cache.AddRange(LoadTable("Perks", "Perk"));

            _cacheLoaded = true;
        }

        private List<FormIDRecord> LoadTable(string table, string type)
        {
            var list = new List<FormIDRecord>();

            using var connection = new SqliteConnection($"Data Source={FormIDdbPath}");
            connection.Open();

            using var cmd = connection.CreateCommand();
            cmd.CommandText = $"SELECT Key, Name FROM {table};";

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var key = reader.GetString(0);
                var name = reader.GetString(1);

                var parts = key.Split('|');
                string plugin = parts[0];
                string formID = parts[1];

                list.Add(new FormIDRecord
                {
                    Key = key,
                    Name = name,
                    Plugin = plugin,
                    FormID = formID,
                    Type = type
                });
            }

            return list;
        }


        // ============================
        //        DB ERSTELLEN
        // ============================

        public void PutIntoDataBank(List<PluginInfo> allgamePathfromDB)
        {
            Directory.CreateDirectory(FormIDFolder);

            using var connection = new SqliteConnection($"Data Source={FormIDdbPath}");
            connection.Open();

            ResetTables(connection);
            CreateTables(connection);

            using var insertKeyword = PrepareInsert(connection, "Keywords");
            using var insertMaterial = PrepareInsert(connection, "Materials");
            using var insertPerk = PrepareInsert(connection, "Perks");

            using var transaction = connection.BeginTransaction();
            insertKeyword.Transaction = transaction;
            insertMaterial.Transaction = transaction;
            insertPerk.Transaction = transaction;

            foreach (var plugin in allgamePathfromDB)
            {
                string pluginName = plugin.FileName;

                foreach (var fullPath in plugin.FullPaths)
                {
                    var mod = SkyrimMod.CreateFromBinaryOverlay(fullPath, SkyrimRelease.SkyrimSE);

                    foreach (var kw in mod.Keywords.Records)
                        InsertRecord(insertKeyword, kw.FormKey.ID.ToString("X6"), kw.EditorID, kw.FormKey.ModKey.FileName);

                    foreach (var misc in mod.MiscItems.Records)
                        InsertRecord(insertMaterial, misc.FormKey.ID.ToString("X6"), misc.EditorID, misc.FormKey.ModKey.FileName);
                    
                    foreach (var perk in mod.Perks.Records)
                        InsertRecord(insertPerk, perk.FormKey.ID.ToString("X6"), perk.EditorID, perk.FormKey.ModKey.FileName);
                }
            }

            transaction.Commit();
            InvalidateCache();
        }

        private void ResetTables(SqliteConnection connection)
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText =
            @"
                DROP TABLE IF EXISTS Keywords;
                DROP TABLE IF EXISTS Materials;
                DROP TABLE IF EXISTS Perks;
            ";
            cmd.ExecuteNonQuery();
        }

        private void CreateTables(SqliteConnection connection)
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText =
            @"
                CREATE TABLE Keywords (
                Key TEXT PRIMARY KEY,
                Name TEXT NOT NULL
            );

            CREATE TABLE Materials (
                Key TEXT PRIMARY KEY,
                Name TEXT NOT NULL
            );

            CREATE TABLE Perks (
                Key TEXT PRIMARY KEY,
                Name TEXT NOT NULL
            );
            ";
            cmd.ExecuteNonQuery();
        }


        private SqliteCommand PrepareInsert(SqliteConnection connection, string table)
        {
            var cmd = new SqliteCommand(
                $"INSERT OR IGNORE INTO {table} (Key, Name) VALUES (@key, @name)",
                connection);

            cmd.Parameters.Add("@key", SqliteType.Text);
            cmd.Parameters.Add("@name", SqliteType.Text);

            return cmd;
        }

        private void InsertRecord(SqliteCommand cmd, string id, string name, string masterplugin)
        {
            string key = $"{masterplugin}|{id}";

            cmd.Parameters["@key"].Value = key;
            cmd.Parameters["@name"].Value = name ?? "";
            cmd.ExecuteNonQuery();
        }


        // ============================
        //        SEARCH‑API
        // ============================

        // Unified key: Plugin|FormID
        public FormIDRecord? GetByKey(string key)
        {
            LoadCache();
            return _cache.FirstOrDefault(x =>
                x.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
        }

        // plugin-aware lookup
        public FormIDRecord? GetByFormID(string plugin, string formID)
        {
            LoadCache();
            return _cache.FirstOrDefault(x =>
                x.FormID.Equals(formID, StringComparison.OrdinalIgnoreCase) &&
                x.Plugin.Equals(plugin, StringComparison.OrdinalIgnoreCase));
        }

        // convenience: "Plugin|FormID"
        public FormIDRecord? GetByFormID(string combinedKey)
        {
            var parts = combinedKey.Split('|');
            if (parts.Length != 2) return null;

            return GetByFormID(parts[0], parts[1]);
        }

        public List<FormIDRecord> SearchByName(string name)
        {
            LoadCache();
            return _cache.Where(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        public List<FormIDRecord> SearchByPrefix(string prefix)
        {
            LoadCache();
            return _cache.Where(x => x.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        public List<FormIDRecord> SearchByPlugin(string plugin)
        {
            LoadCache();
            return _cache.Where(x => x.Plugin.Equals(plugin, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        public List<FormIDRecord> SearchByType(string type)
        {
            LoadCache();
            return _cache.Where(x => x.Type.Equals(type, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        public List<FormIDRecord> Search(
            string? name = null,
            string? prefix = null,
            string? plugin = null,
            string? type = null,
            string? key = null)
        {
            LoadCache();

            IEnumerable<FormIDRecord> q = _cache;

            if (key != null)
                q = q.Where(x => x.Key.Equals(key, StringComparison.OrdinalIgnoreCase));

            if (name != null)
                q = q.Where(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

            if (prefix != null)
                q = q.Where(x => x.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

            if (plugin != null)
                q = q.Where(x => x.Plugin.Equals(plugin, StringComparison.OrdinalIgnoreCase));

            if (type != null)
                q = q.Where(x => x.Type.Equals(type, StringComparison.OrdinalIgnoreCase));

            return q.ToList();
        }
    }
}
