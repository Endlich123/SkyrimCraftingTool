using Microsoft.Data.Sqlite;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Order;
using Mutagen.Bethesda.Skyrim;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace SkyrimCraftingTool.Model
{
    public class FormIDDBHandler
    {
        private string FormIDFolder => Path.Combine(GlobalState.Tool.InputFolder, "FormID");
        private string FormIDdbPath => Path.Combine(FormIDFolder, "formid.db");

        public static string DefaultPath =>
            Path.Combine(GlobalState.Tool?.InputFolder ?? "", "FormID", "formid.db");

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
            _cache.AddRange(LoadTable(FormIDdbPath, "Keywords", "Keyword"));
            _cache.AddRange(LoadTable(FormIDdbPath, "Perks", "Perk"));
            _cache.AddRange(LoadQuestsWithStages());

            // FormLists was added after the other name tables. formid.db is fully DROP+CREATEd on
            // every scan, so it's present after any rescan — but an un-rescanned db from before this
            // change has no FormLists table and LoadTable would throw "no such table". Degrade to no
            // FLST names until the next scan (same spirit as LoadQuestsWithStages' catch).
            try { _cache.AddRange(LoadTable(FormIDdbPath, "FormLists", "FormList")); }
            catch (SqliteException) { }

            // Dictionary lookup, not a linear scan: GetByKey is called once per container item across
            // every plugin (ItemDBHandler.PutIntoDataBank), so a linear scan is O(items * cacheSize)
            // — noticeable on large modlists. DedupeByKey also reports collisions to the log.
            _cacheByKey = DedupeByKey(_cache, r => r.Key, "FormID name cache");
        }

        // Collapses duplicate keys AND says so in the log.
        //
        // The guard alone is enough to keep the scan alive, but a silent First() throws away the one
        // piece of evidence we need: a tester hit "An item with the same key has already been added"
        // twice, and by the time the report arrived formid.db had been rebuilt by the next scan and
        // the colliding rows were gone. Logging the keys turns an unreproducible crash into a line
        // anyone can paste. Costs nothing when there are no duplicates, which is the normal case.
        private static Dictionary<string, T> DedupeByKey<T>(
            List<T> items, Func<T, string> keyOf, string what)
        {
            var groups = items.GroupBy(keyOf, StringComparer.OrdinalIgnoreCase).ToList();

            if (groups.Count != items.Count)
            {
                var examples = groups
                    .Where(g => g.Count() > 1)
                    .Take(5)
                    .Select(g => $"{g.Key} (x{g.Count()}: {string.Join(" | ", g.Select(keyOf).Distinct())})");

                AppLogger.LogWarning(
                    $"{what}: {items.Count - groups.Count} duplicate key(s) collapsed. " +
                    "Keys are built from the plugin name as each plugin spells its masters, so two " +
                    "spellings of the same plugin land as separate rows. Examples: " +
                    string.Join("; ", examples));
            }

            return groups.ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        }

        // Same as LoadTable("Quests", "Quest") plus the Stages column parsed onto FormIDRecord.Stages.
        private List<FormIDRecord> LoadQuestsWithStages()
        {
            var list = LoadTable(FormIDdbPath, "Quests", "Quest");

            // Name is the EditorID, and a quest does not have to have one: 9 of 7,660 on the real
            // modlist (2026-10-24) don't - script-only quests of overlay/makeup mods and two CC plugins.
            // They showed as blank lines in the GetStage/GetStageDone pickers. The key at least says
            // where one comes from. Quests feed only those pickers, so the stand-in cannot leak into
            // anything that expects a real EditorID.
            foreach (var q in list)
                if (string.IsNullOrWhiteSpace(q.Name))
                    q.Name = $"(no EditorID) {q.Key}";

            // GroupBy/First for the same reason as _cacheByKey below, and it is NOT theoretical:
            // keys are built from FormKey.ModKey.FileName, i.e. the plugin name as spelled in the
            // master list of whichever plugin was being scanned. Two mods overriding the same record
            // while spelling that master differently (".. Patch.esp" vs ".. patch.esp") produce two
            // rows - SQLite TEXT compares case-sensitively, this dictionary does not. A plain
            // ToDictionary threw "An item with the same key has already been added" and took the
            // entire scan down on a modlist that was otherwise fine.
            var byKey = DedupeByKey(list, r => r.Key, "Quests");

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

                    var stages = ParseStagesColumn(reader.GetString(1));
                    if (stages.Count == 0) continue;

                    rec.Stages = stages.Select(s => s.Index).ToList();
                    var texts = stages
                        .Where(s => !string.IsNullOrEmpty(s.Text))
                        .ToDictionary(s => s.Index, s => s.Text!);
                    if (texts.Count > 0)
                        rec.StageTexts = texts;
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
                cmd.CommandText = $"SELECT Key, {EditorIdColumn(connection, "FormLists")} FROM FormLists;";
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

        // One name table, whole. Static so the item.db side can read perks without an instance (see
        // ReadPerks). FormIDRecord.Name is the EditorID - it always was; the in-game name is FullName.
        //
        // A formid.db from before 2026-11-07 has Key + Name (= EditorID) only. It is rebuilt on the next
        // scan; until then it reads as before, without game names, descriptions or skills.
        private static List<FormIDRecord> LoadTable(string dbPath, string table, string type)
        {
            var list = new List<FormIDRecord>();

            using var connection = new SqliteConnection($"Data Source={dbPath}");
            connection.Open();

            var columns = Columns(connection, table);
            string Optional(string column) => columns.Contains(column) ? column : "NULL";

            using var cmd = connection.CreateCommand();
            cmd.CommandText =
                $"SELECT Key, {EditorIdColumn(columns)}, " +
                $"{(columns.Contains("EditorID") ? Optional("Name") : "NULL")}, " +
                $"{Optional("Description")}, {Optional("Skill")} FROM {table};";

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var key = reader.GetString(0);

                var parts = key.Split('|');
                string plugin = parts[0];
                string formID = parts.Length > 1 ? parts[1] : "";

                list.Add(new FormIDRecord
                {
                    Key = key,
                    Name = reader.IsDBNull(1) ? "" : reader.GetString(1),
                    FullName = reader.IsDBNull(2) ? "" : reader.GetString(2),
                    Description = reader.IsDBNull(3) ? "" : reader.GetString(3),
                    Skill = reader.IsDBNull(4) ? "" : reader.GetString(4),
                    Plugin = plugin,
                    FormID = formID,
                    Type = type
                });
            }

            return list;
        }

        // Every perk with its game name, description and skill tree, read straight from the file. For
        // the item.db readers (NPC tab, NPC groups, Spells tab), which have no FormIDDBHandler instance.
        // Empty when there is no formid.db yet - no scan has run.
        public static List<FormIDRecord> ReadPerks(string? dbPath = null)
        {
            var path = dbPath ?? DefaultPath;
            if (!File.Exists(path)) return new List<FormIDRecord>();

            try { return LoadTable(path, "Perks", "Perk"); }
            catch (SqliteException ex)
            {
                AppLogger.LogWarning($"formid.db: perks could not be read ({ex.Message})");
                return new List<FormIDRecord>();
            }
        }

        private static HashSet<string> Columns(SqliteConnection connection, string table)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using var cmd = connection.CreateCommand();
            cmd.CommandText = $"PRAGMA table_info({table});";
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) set.Add(reader.GetString(1));
            return set;
        }

        // The column that holds the EditorID: "EditorID" since 2026-11-07, "Name" before. A table that
        // does not exist answers "Name", so the SELECT still fails with "no such table" - which the
        // FormLists caller relies on.
        private static string EditorIdColumn(HashSet<string> columns)
            => columns.Contains("EditorID") ? "EditorID" : "Name";

        public static string EditorIdColumn(SqliteConnection connection, string table)
            => EditorIdColumn(Columns(connection, table));


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
            using var insertPerk = PrepareInsert(connection, "Perks", "Skill");
            using var insertQuest = PrepareInsert(connection, "Quests", "Stages");
            using var insertFormList = PrepareInsert(connection, "FormLists");

            using var transaction = connection.BeginTransaction();
            insertKeyword.Transaction = transaction;
            insertPerk.Transaction = transaction;
            insertQuest.Transaction = transaction;
            insertFormList.Transaction = transaction;

            // Parse phase runs in parallel across plugins; the write phase below stays sequential
            // (see ItemDBHandler.PutIntoDataBank for the same split). PrepareInsert uses
            // "INSERT OR REPLACE" — the LAST row written for a given key wins, i.e. the winning
            // override (until 2026-11-07 it was the first, the record's origin: harmless for EditorIDs,
            // wrong for the names and perk descriptions an overhaul rewrites) — so results are
            // written into an array indexed by plugin position (not a ConcurrentBag), keeping write
            // order identical to allgamePathfromDB's load order regardless of parse thread timing.
            // Never the tool's own output - see KeyFactory.IsOwnOutputPlugin.
            var pluginFiles = allgamePathfromDB
                .Where(plugin => !SkyrimCraftingTool.Services.KeyFactory.IsOwnOutputPlugin(plugin.FileName))
                .SelectMany(plugin => plugin.FullPaths)
                .ToList();

            var parsedPlugins = new ParsedFormIdPluginData[pluginFiles.Count];
            Parallel.For(0, pluginFiles.Count, i =>
            {
                parsedPlugins[i] = ParsePluginForFormIdDB(pluginFiles[i]);
            });

            // Which skill tree each perk sits in - needs every plugin, because the tree (AVIF), the
            // perk and the next rank can come from three different ones.
            var perkSkills = BuildPerkSkills(parsedPlugins);

            // Write phase: strictly sequential — do not parallelize SQLite writes.
            foreach (var parsed in parsedPlugins)
            {
                foreach (var row in parsed.Keywords) Write(insertKeyword, row);
                foreach (var row in parsed.Perks)
                    Write(insertPerk, row, perkSkills.TryGetValue(row.Key, out var skill) ? skill : null);
                foreach (var (row, stages) in parsed.Quests) Write(insertQuest, row, stages);
                foreach (var row in parsed.FormLists) Write(insertFormList, row);
            }

            transaction.Commit();
            InvalidateCache();
        }

        // One row of any name table. Name (the in-game FULL name) and Description are optional: a
        // keyword, a leveled list or a form list has neither, and only perks carry a description.
        private readonly record struct NameRow(string Key, string EditorId, string? Name, string? Description);

        private sealed class ParsedFormIdPluginData
        {
            public List<NameRow> Keywords = new();
            public List<NameRow> Perks = new();
            public List<(NameRow row, string stages)> Quests = new();
            public List<NameRow> FormLists = new();

            // For the perk's skill tree (BuildPerkSkills): the next rank of each perk, and each AVIF
            // tree with the perks it lists.
            public List<(string perk, string? nextPerk)> PerkRanks = new();
            public List<(string key, string name, List<string> perks)> SkillTrees = new();
        }

        private static string KeyOf(FormKey formKey) => $"{formKey.ModKey.FileName}|{formKey.ID:X6}";

        private static string? Text(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

        // Pure parsing — no DB access — so this is safe to call concurrently from Parallel.ForEach.
        private ParsedFormIdPluginData ParsePluginForFormIdDB(string fullPath)
        {
            var result = new ParsedFormIdPluginData();
            var mod = SkyrimMod.CreateFromBinaryOverlay(
                fullPath, SkyrimRelease.SkyrimSE, Services.PluginReadParams.ForScan());

            foreach (var kw in mod.Keywords.Records)
                result.Keywords.Add(new NameRow(KeyOf(kw.FormKey), kw.EditorID ?? "", null, null));

            // No Materials (MISC, INGR, AMMO, SLGM, ALCH) since 2026-11-07: they are item.db WorldItem
            // rows, editable there (placement), and the material pickers read them from there with the
            // kind as a field (CacheManager.ToMaterial) instead of " (INGR)" in the name.

            foreach (var perk in mod.Perks.Records)
            {
                string key = KeyOf(perk.FormKey);
                result.Perks.Add(new NameRow(key, perk.EditorID ?? "", Text(perk.Name?.String), Text(perk.Description?.String)));

                var next = perk.NextPerk.IsNull ? null : KeyOf(perk.NextPerk.FormKey);
                result.PerkRanks.Add((key, next));
            }

            // A perk does not know its skill - the tree does. Every actor value with a perk tree:
            // the 18 skills, the Werewolf and Vampire Lord trees, and whatever a mod adds this way.
            foreach (var av in mod.ActorValueInformation.Records)
            {
                var perks = (av.PerkTree ?? Enumerable.Empty<IActorValuePerkNodeGetter>())
                    .Where(n => !n.Perk.IsNull)
                    .Select(n => KeyOf(n.Perk.FormKey))
                    .ToList();

                result.SkillTrees.Add((KeyOf(av.FormKey), Text(av.Name?.String) ?? av.EditorID ?? KeyOf(av.FormKey), perks));
            }

            foreach (var quest in mod.Quests.Records)
                result.Quests.Add((new NameRow(KeyOf(quest.FormKey), quest.EditorID ?? "", Text(quest.Name?.String), null), StagesColumn(quest)));

            // Every FLST in the plugin — not just enchant-referenced ones (decision #1). Members are
            // scanned into item.db by ItemDBHandler; this table is only the FLST's own identity/name.
            foreach (var fl in mod.FormLists.Records)
                result.FormLists.Add(new NameRow(KeyOf(fl.FormKey), fl.EditorID ?? "", null, null));

            return result;
        }

        // The Quests.Stages column: since 2026-11-08 a JSON array of {Index, Text}, the text being the
        // stage's journal entries (several when the stage has conditional variants). Before that a
        // plain "0,10,20" list - ParseStagesColumn reads both. Localized texts come from the
        // .DLSTRINGS in the BSAs, same read parameters as the names (PluginReadParams).
        internal sealed record StageRow(int Index, string? Text);

        internal static string StagesColumn(IQuestGetter quest)
        {
            var rows = quest.Stages
                .GroupBy(s => (int)s.Index)
                .OrderBy(g => g.Key)
                .Select(g =>
                {
                    var texts = g.SelectMany(s => s.LogEntries)
                        .Select(e => e.Entry?.String?.Trim())
                        .Where(t => !string.IsNullOrEmpty(t))
                        .Distinct()
                        .ToList();
                    return new StageRow(g.Key, texts.Count > 0 ? string.Join("\n\n", texts) : null);
                })
                .ToList();
            return JsonSerializer.Serialize(rows);
        }

        internal static List<StageRow> ParseStagesColumn(string value)
        {
            var trimmed = value.TrimStart();
            if (trimmed.StartsWith('['))
            {
                try { return JsonSerializer.Deserialize<List<StageRow>>(trimmed) ?? new(); }
                catch (JsonException) { return new(); }
            }

            return value.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => int.TryParse(s.Trim(), out var v) ? new StageRow(v, null) : null)
                .Where(r => r != null)
                .Select(r => r!)
                .ToList();
        }

        // Perk key -> the skill tree(s) it sits in, "Smithing" or "One-handed, Two-handed" in the rare
        // case of a perk in two trees. The winning version of every tree and every perk counts - perk
        // overhauls rebuild the trees - so the last plugin wins, as in the write phase.
        //
        // The tree lists the FIRST rank only; the others hang off it through NextPerk (Strong Back 1 ->
        // 2 -> ...), so the chain is followed. A perk in no tree gets nothing: NPC perks, hidden helper
        // perks, abilities - a player cannot pick those, which is worth seeing in itself.
        private static Dictionary<string, string> BuildPerkSkills(IEnumerable<ParsedFormIdPluginData> parsedPlugins)
        {
            var plugins = parsedPlugins.Where(p => p != null).ToList();
            return BuildPerkSkills(plugins.SelectMany(p => p.PerkRanks), plugins.SelectMany(p => p.SkillTrees));
        }

        // Inputs in load order; a later entry for the same key replaces an earlier one.
        internal static Dictionary<string, string> BuildPerkSkills(
            IEnumerable<(string perk, string? nextPerk)> perkRanks,
            IEnumerable<(string key, string name, List<string> perks)> skillTrees)
        {
            var next = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            var trees = new Dictionary<string, (string name, List<string> perks)>(StringComparer.OrdinalIgnoreCase);

            foreach (var (perk, nextPerk) in perkRanks) next[perk] = nextPerk;
            foreach (var (key, name, perks) in skillTrees) trees[key] = (name, perks);

            var skills = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

            foreach (var (name, perks) in trees.Values.OrderBy(t => t.name, StringComparer.OrdinalIgnoreCase))
            {
                foreach (var root in perks)
                {
                    // Guarded: a mod that links a rank back to an earlier one would loop forever.
                    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    for (string? perk = root; perk != null && seen.Add(perk); perk = next.GetValueOrDefault(perk))
                    {
                        if (!skills.TryGetValue(perk, out var list)) skills[perk] = list = new List<string>();
                        if (!list.Contains(name, StringComparer.OrdinalIgnoreCase)) list.Add(name);
                    }
                }
            }

            return skills.ToDictionary(kv => kv.Key, kv => string.Join(", ", kv.Value), StringComparer.OrdinalIgnoreCase);
        }

        // extra = the table's own extra column (Perks: Skill, Quests: Stages), when it has one.
        private static void Write(SqliteCommand cmd, NameRow row, string? extra = null)
        {
            cmd.Parameters["@key"].Value = row.Key;
            cmd.Parameters["@editorId"].Value = row.EditorId ?? "";
            cmd.Parameters["@name"].Value = (object?)row.Name ?? DBNull.Value;
            cmd.Parameters["@description"].Value = (object?)row.Description ?? DBNull.Value;
            if (cmd.Parameters.Contains("@extra"))
                cmd.Parameters["@extra"].Value = (object?)extra ?? DBNull.Value;
            cmd.ExecuteNonQuery();
        }

        private void ResetTables(SqliteConnection connection)
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText =
            @"
                DROP TABLE IF EXISTS Keywords;
                DROP TABLE IF EXISTS Materials;   -- not recreated since 2026-11-07 (WorldItem in item.db)
                DROP TABLE IF EXISTS Perks;
                DROP TABLE IF EXISTS Quests;
                DROP TABLE IF EXISTS LVLi;   -- not recreated since 2026-11-07; dropped so an old file loses it
                DROP TABLE IF EXISTS FormLists;
            ";
            cmd.ExecuteNonQuery();
        }

        private void CreateTables(SqliteConnection connection)
        {
            using var cmd = connection.CreateCommand();
            const string common =
                "Key TEXT PRIMARY KEY COLLATE NOCASE, EditorID TEXT NOT NULL, Name TEXT, Description TEXT";

            cmd.CommandText = $@"
                CREATE TABLE Keywords  ({common});
                CREATE TABLE Perks     ({common}, Skill TEXT);
                CREATE TABLE Quests    ({common}, Stages TEXT);
                CREATE TABLE FormLists ({common});
            ";

            cmd.ExecuteNonQuery();
        }


        private static SqliteCommand PrepareInsert(SqliteConnection connection, string table, string? extraColumn = null)
        {
            string columns = "Key, EditorID, Name, Description" + (extraColumn != null ? $", {extraColumn}" : "");
            string values = "@key, @editorId, @name, @description" + (extraColumn != null ? ", @extra" : "");

            var cmd = new SqliteCommand($"INSERT OR REPLACE INTO {table} ({columns}) VALUES ({values})", connection);

            cmd.Parameters.Add("@key", SqliteType.Text);
            cmd.Parameters.Add("@editorId", SqliteType.Text);
            cmd.Parameters.Add("@name", SqliteType.Text);
            cmd.Parameters.Add("@description", SqliteType.Text);
            if (extraColumn != null) cmd.Parameters.Add("@extra", SqliteType.Text);

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
