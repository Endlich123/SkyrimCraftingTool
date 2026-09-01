using Microsoft.Data.Sqlite;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Order;
using Mutagen.Bethesda.Skyrim;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

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
        private Dictionary<string, FormIDRecord> _cacheByKey = new(StringComparer.OrdinalIgnoreCase);
        private bool _cacheLoaded = false;
        private readonly object _cacheLock = new();

        private void InvalidateCache()
        {
            _cacheLoaded = false;
            _cache.Clear();
            _cacheByKey.Clear();
        }

        // Called once, up front, before ItemDBHandler.PutIntoDataBank fans its plugin parsing out
        // across threads — those threads call GetByKey (via LoadCache) concurrently, so the cache
        // must already be built by the time they start. See LoadCache for the thread-safety note.
        public void EnsureCacheLoaded() => LoadCache();

        // GetByKey is called concurrently from ItemDBHandler's parallel plugin-parse phase, so the
        // load-once guard needs an actual lock, not just the bare bool check (that races: multiple
        // threads could all see _cacheLoaded == false and rebuild _cache/_cacheByKey at once).
        private void LoadCache()
        {
            if (_cacheLoaded) return;

            lock (_cacheLock)
            {
                if (_cacheLoaded) return;
                LoadCacheCore();
                _cacheLoaded = true;
            }
        }

        private void LoadCacheCore()
        {
            _cache = new List<FormIDRecord>();
            _cache.AddRange(LoadTable("Keywords", "Keyword"));
            _cache.AddRange(LoadTable("Materials", "Material"));
            _cache.AddRange(LoadTable("Perks", "Perk"));
            _cache.AddRange(LoadTable("Quests", "Quest"));
            _cache.AddRange(LoadTable("LVLi", "LVLi"));

            // Dictionary lookup, not a linear scan: GetByKey is called once per container item across
            // every plugin (ItemDBHandler.PutIntoDataBank), so a linear scan is O(items * cacheSize)
            // — noticeable on large modlists. GroupBy/First guards against an unexpected duplicate
            // key throwing here.
            _cacheByKey = _cache
                .GroupBy(r => r.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
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

            using (var pragma = connection.CreateCommand())
            {
                pragma.CommandText = "PRAGMA journal_mode=WAL;";
                pragma.ExecuteNonQuery();
            }

            ResetTables(connection);
            CreateTables(connection);

            using var insertKeyword = PrepareInsert(connection, "Keywords");
            using var insertMaterial = PrepareInsert(connection, "Materials");
            using var insertPerk = PrepareInsert(connection, "Perks");
            using var insertQuest = PrepareInsert(connection, "Quests");
            using var insertLVLI = PrepareInsert(connection, "LVLi");

            using var transaction = connection.BeginTransaction();
            insertKeyword.Transaction = transaction;
            insertMaterial.Transaction = transaction;
            insertPerk.Transaction = transaction;
            insertQuest.Transaction = transaction;
            insertLVLI.Transaction = transaction;

            // Parse phase runs in parallel across plugins; the write phase below stays sequential
            // (see ItemDBHandler.PutIntoDataBank for the same split). PrepareInsert uses
            // "INSERT OR IGNORE" — the FIRST row written for a given key wins — so results are
            // written into an array indexed by plugin position (not a ConcurrentBag), keeping write
            // order identical to allgamePathfromDB's load order regardless of parse thread timing.
            var pluginFiles = allgamePathfromDB
                .SelectMany(plugin => plugin.FullPaths)
                .ToList();

            var parsedPlugins = new ParsedFormIdPluginData[pluginFiles.Count];
            Parallel.For(0, pluginFiles.Count, i =>
            {
                parsedPlugins[i] = ParsePluginForFormIdDB(pluginFiles[i]);
            });

            // Write phase: strictly sequential — do not parallelize SQLite writes.
            foreach (var parsed in parsedPlugins)
            {
                foreach (var (key, name) in parsed.Keywords) ApplyKeyNameAndExecute(insertKeyword, key, name);
                foreach (var (key, name) in parsed.Materials) ApplyKeyNameAndExecute(insertMaterial, key, name);
                foreach (var (key, name) in parsed.Perks) ApplyKeyNameAndExecute(insertPerk, key, name);
                foreach (var (key, name) in parsed.Quests) ApplyKeyNameAndExecute(insertQuest, key, name);
                foreach (var (key, name) in parsed.LVLi) ApplyKeyNameAndExecute(insertLVLI, key, name);
            }

            transaction.Commit();
            InvalidateCache();
        }

        private sealed class ParsedFormIdPluginData
        {
            public List<(string key, string name)> Keywords = new();
            public List<(string key, string name)> Materials = new();
            public List<(string key, string name)> Perks = new();
            public List<(string key, string name)> Quests = new();
            public List<(string key, string name)> LVLi = new();
        }

        // Pure parsing — no DB access — so this is safe to call concurrently from Parallel.ForEach.
        private ParsedFormIdPluginData ParsePluginForFormIdDB(string fullPath)
        {
            var result = new ParsedFormIdPluginData();
            var mod = SkyrimMod.CreateFromBinaryOverlay(fullPath, SkyrimRelease.SkyrimSE);

            foreach (var kw in mod.Keywords.Records)
                result.Keywords.Add(($"{kw.FormKey.ModKey.FileName}|{kw.FormKey.ID:X6}", kw.EditorID));

            // "Materials" for us = anything a COBJ recipe can list as an ingredient. MISC covers
            // ingots/leather/etc., but vanilla + mods also use INGR (Daedra Heart, salt, …), AMMO
            // (arrows/bolts), SLGM (soul gems) and occasionally ALCH (food/potions). Without these,
            // such ingredients don't resolve and get flagged as dead references.
            foreach (var misc in mod.MiscItems.Records)
                result.Materials.Add(($"{misc.FormKey.ModKey.FileName}|{misc.FormKey.ID:X6}", misc.EditorID));

            // Non-MISC records get a " (TYPE)" suffix so they can be told apart in the material
            // picker (MISC, the common case, stays clean). Cosmetic only - the Key is what's stored.
            foreach (var ingr in mod.Ingredients.Records)
                result.Materials.Add(($"{ingr.FormKey.ModKey.FileName}|{ingr.FormKey.ID:X6}", $"{ingr.EditorID} (INGR)"));

            foreach (var ammo in mod.Ammunitions.Records)
                result.Materials.Add(($"{ammo.FormKey.ModKey.FileName}|{ammo.FormKey.ID:X6}", $"{ammo.EditorID} (AMMO)"));

            foreach (var slgm in mod.SoulGems.Records)
                result.Materials.Add(($"{slgm.FormKey.ModKey.FileName}|{slgm.FormKey.ID:X6}", $"{slgm.EditorID} (SLGM)"));

            foreach (var alch in mod.Ingestibles.Records)
                result.Materials.Add(($"{alch.FormKey.ModKey.FileName}|{alch.FormKey.ID:X6}", $"{alch.EditorID} (ALCH)"));

            foreach (var perk in mod.Perks.Records)
                result.Perks.Add(($"{perk.FormKey.ModKey.FileName}|{perk.FormKey.ID:X6}", perk.EditorID));

            foreach (var quest in mod.Quests.Records)
                result.Quests.Add(($"{quest.FormKey.ModKey.FileName}|{quest.FormKey.ID:X6}", quest.EditorID));

            foreach (var lvi in mod.LeveledItems.Records)
                result.LVLi.Add(($"{lvi.FormKey.ModKey.FileName}|{lvi.FormKey.ID:X6}", lvi.EditorID));

            return result;
        }

        private static void ApplyKeyNameAndExecute(SqliteCommand cmd, string key, string name)
        {
            cmd.Parameters["@key"].Value = key;
            cmd.Parameters["@name"].Value = name ?? "";
            cmd.ExecuteNonQuery();
        }

        private void ResetTables(SqliteConnection connection)
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText =
            @"
                DROP TABLE IF EXISTS Keywords;
                DROP TABLE IF EXISTS Materials;
                DROP TABLE IF EXISTS Perks;
                DROP TABLE IF EXISTS Quests;
                DROP TABLE IF EXISTS LVLi;
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

            CREATE TABLE Quests (
                Key TEXT PRIMARY KEY,
                Name TEXT NOT NULL
            );

            CREATE TABLE LVLi(
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

        // ============================
        //        SEARCH‑API
        // ============================

        // Unified key: Plugin|FormID
        public FormIDRecord? GetByKey(string key)
        {
            LoadCache();
            return _cacheByKey.TryGetValue(key, out var record) ? record : null;
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
