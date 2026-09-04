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
            _cache.AddRange(LoadQuestsWithStages());
            _cache.AddRange(LoadTable("LVLi", "LVLi"));

            // FormLists was added after the other name tables. formid.db is fully DROP+CREATEd on
            // every scan, so it's present after any rescan — but an un-rescanned db from before this
            // change has no FormLists table and LoadTable would throw "no such table". Degrade to no
            // FLST names until the next scan (same spirit as LoadQuestsWithStages' catch).
            try { _cache.AddRange(LoadTable("FormLists", "FormList")); }
            catch (SqliteException) { }

            // Dictionary lookup, not a linear scan: GetByKey is called once per container item across
            // every plugin (ItemDBHandler.PutIntoDataBank), so a linear scan is O(items * cacheSize)
            // — noticeable on large modlists. GroupBy/First guards against an unexpected duplicate
            // key throwing here.
            _cacheByKey = _cache
                .GroupBy(r => r.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        }

        // Same as LoadTable("Quests", "Quest") plus the Stages column parsed onto FormIDRecord.Stages.
        private List<FormIDRecord> LoadQuestsWithStages()
        {
            var list = LoadTable("Quests", "Quest");
            var byKey = list.ToDictionary(r => r.Key, StringComparer.OrdinalIgnoreCase);

            try
            {
                using var connection = new SqliteConnection($"Data Source={FormIDdbPath}");
                connection.Open();
                using var cmd = connection.CreateCommand();
                cmd.CommandText = "SELECT Key, Stages FROM Quests;";

                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    if (reader.IsDBNull(1)) continue;
                    if (!byKey.TryGetValue(reader.GetString(0), out var rec)) continue;

                    var stages = reader.GetString(1)
                        .Split(',', StringSplitOptions.RemoveEmptyEntries)
                        .Select(s => int.TryParse(s.Trim(), out var v) ? v : (int?)null)
                        .Where(v => v.HasValue)
                        .Select(v => v!.Value)
                        .ToList();

                    if (stages.Count > 0)
                        rec.Stages = stages;
                }
            }
            catch (SqliteException)
            {
                // Pre-existing formid.db from before the Stages column — degrades to no stage list
                // until the next scan (which drops + recreates the table).
            }

            return list;
        }

        // FLST Key -> EditorID, read straight from formid.db (NOT the in-memory cache). The cache on
        // this instance is loaded once and never invalidated after a rescan, so a cache read would
        // keep returning the pre-rescan set (empty, on a db from before the FormLists table existed).
        // This table is small and read rarely (worn-restriction picker refresh), so a direct query
        // is fine and always current.
        public Dictionary<string, string> GetFormListNamesDirect()
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (!File.Exists(FormIDdbPath)) return map;

            try
            {
                using var connection = new SqliteConnection($"Data Source={FormIDdbPath}");
                connection.Open();
                using var cmd = connection.CreateCommand();
                cmd.CommandText = "SELECT Key, Name FROM FormLists;";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var key = reader.GetString(0);
                    if (!map.ContainsKey(key))
                        map[key] = reader.IsDBNull(1) ? "" : reader.GetString(1);
                }
            }
            catch (SqliteException)
            {
                // formid.db from before the FormLists table — no names until the next scan.
            }
            return map;
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
            using var insertQuest = PrepareQuestInsert(connection);
            using var insertLVLI = PrepareInsert(connection, "LVLi");
            using var insertFormList = PrepareInsert(connection, "FormLists");

            using var transaction = connection.BeginTransaction();
            insertKeyword.Transaction = transaction;
            insertMaterial.Transaction = transaction;
            insertPerk.Transaction = transaction;
            insertQuest.Transaction = transaction;
            insertLVLI.Transaction = transaction;
            insertFormList.Transaction = transaction;

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
                foreach (var (key, name, stages) in parsed.Quests) ApplyQuestRowAndExecute(insertQuest, key, name, stages);
                foreach (var (key, name) in parsed.LVLi) ApplyKeyNameAndExecute(insertLVLI, key, name);
                foreach (var (key, name) in parsed.FormLists) ApplyKeyNameAndExecute(insertFormList, key, name);
            }

            transaction.Commit();
            InvalidateCache();
        }

        private sealed class ParsedFormIdPluginData
        {
            public List<(string key, string name)> Keywords = new();
            public List<(string key, string name)> Materials = new();
            public List<(string key, string name)> Perks = new();
            public List<(string key, string name, string stages)> Quests = new();
            public List<(string key, string name)> LVLi = new();
            public List<(string key, string name)> FormLists = new();
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
            {
                var stages = string.Join(",", quest.Stages
                    .Select(s => s.Index)
                    .Distinct()
                    .OrderBy(i => i));
                result.Quests.Add(($"{quest.FormKey.ModKey.FileName}|{quest.FormKey.ID:X6}", quest.EditorID, stages));
            }

            foreach (var lvi in mod.LeveledItems.Records)
                result.LVLi.Add(($"{lvi.FormKey.ModKey.FileName}|{lvi.FormKey.ID:X6}", lvi.EditorID));

            // Every FLST in the plugin — not just enchant-referenced ones (decision #1). Members are
            // scanned into item.db by ItemDBHandler; this table is only the FLST's own identity/name.
            foreach (var fl in mod.FormLists.Records)
                result.FormLists.Add(($"{fl.FormKey.ModKey.FileName}|{fl.FormKey.ID:X6}", fl.EditorID));

            return result;
        }

        private static void ApplyKeyNameAndExecute(SqliteCommand cmd, string key, string name)
        {
            cmd.Parameters["@key"].Value = key;
            cmd.Parameters["@name"].Value = name ?? "";
            cmd.ExecuteNonQuery();
        }

        // Quests carry an extra comma-separated stage-index list (see FormIDRecord.Stages).
        private static SqliteCommand PrepareQuestInsert(SqliteConnection connection)
        {
            var cmd = new SqliteCommand(
                "INSERT OR IGNORE INTO Quests (Key, Name, Stages) VALUES (@key, @name, @stages)",
                connection);
            cmd.Parameters.Add("@key", SqliteType.Text);
            cmd.Parameters.Add("@name", SqliteType.Text);
            cmd.Parameters.Add("@stages", SqliteType.Text);
            return cmd;
        }

        private static void ApplyQuestRowAndExecute(SqliteCommand cmd, string key, string name, string stages)
        {
            cmd.Parameters["@key"].Value = key;
            cmd.Parameters["@name"].Value = name ?? "";
            cmd.Parameters["@stages"].Value = stages ?? "";
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
                DROP TABLE IF EXISTS FormLists;
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
                Name TEXT NOT NULL,
                Stages TEXT
            );

            CREATE TABLE LVLi(
                Key TEXT PRIMARY KEY,
                Name TEXT NOT NULL
            );

            CREATE TABLE FormLists (
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
