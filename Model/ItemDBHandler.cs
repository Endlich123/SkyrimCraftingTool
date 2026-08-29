using DynamicData;
using Microsoft.Data.Sqlite;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using SkyrimCraftingTool.Services;
using SkyrimCraftingTool.ViewModel;
using System.Collections.ObjectModel;
using System.Data;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Media3D;
using static System.Net.Mime.MediaTypeNames;


namespace SkyrimCraftingTool.Model
{
    public class ItemDBHandler
    {
        private string ItemFolder => Path.Combine(GlobalState.Tool.InputFolder, "Item");
        private string ItemdbPath => Path.Combine(ItemFolder, "item.db");
        public static string ConnString
        => $"Data Source={Path.Combine(GlobalState.Tool.InputFolder, "Item", "item.db")}";

        private readonly FormIDDBHandler _formIDDB = new FormIDDBHandler();


        // count
        private int? _count = null;

        // ============================
        //            CACHE
        // ============================
        private List<ArmorRecord> _armorCache = new();
        private List<WeaponRecord> _weaponCache = new();
        private List<COBJRecord> _cobjCache = new();
        private List<COBJConditionRecord> _cobjCondtionsCache = new();

        private Dictionary<string, ArmorRecord> _armorByKey = new();
        private Dictionary<string, WeaponRecord> _weaponByKey = new();
        private Dictionary<string, COBJRecord> _cobjByKey = new();
        private Dictionary<string, List<COBJRecord>> _cobjByCreatedItem = new();

        // Enchantments
        private List<EnchantmentRecord> _enchantmentCache = new();
        private Dictionary<string, EnchantmentRecord> _enchantmentByKey = new();
        private List<EnchantmentEffectRecord> _enchantmentEffectsCache = new();
        private Dictionary<string, List<EnchantmentEffectRecord>> _effectsByEnchantment = new();
        private Dictionary<string, List<string>> _wornRestrictionKeywords = new();
        private List<MagicEffectsRecords> _magicEffectsCache = new();

        // Container
        private List<ContainerRecord> _containerCache = new();
        private List<ContainerLVLIRecord> _containerLvliCache = new();

        private Dictionary<string, ContainerRecord> _containerByKey = new();
        private Dictionary<string, List<ContainerLVLIRecord>> _containerLvliByContainer = new();

        private bool _cacheLoaded = false;
        private readonly object _cacheLock = new();

        private void InvalidateCache()
        {
            _cacheLoaded = false;
            _armorCache.Clear();
            _weaponCache.Clear();
            _cobjCache.Clear();
            _armorByKey.Clear();
            _weaponByKey.Clear();
            _cobjByKey.Clear();
            _cobjByCreatedItem.Clear();
            _enchantmentCache.Clear();
            _enchantmentByKey.Clear();
            _enchantmentEffectsCache.Clear();
            _effectsByEnchantment.Clear();
            _wornRestrictionKeywords.Clear();
            _containerCache.Clear();
            _containerLvliCache.Clear();
        }

        // CacheManager.BuildCachesFromDB calls GetArmorByPlugin/GetWeaponsByPlugin/GetCOBJByPlugin
        // (all of which route through LoadCache) from multiple threads via Parallel.ForEach over
        // plugins. Without this lock, concurrent callers would all see _cacheLoaded == false and
        // race to reload + reassign the same shared lists/dictionaries at once.
        public void EnsureCacheLoaded() => LoadCache();

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

        // EnsureSchema/CreateTables were previously only ever run inside PutIntoDataBank (a full
        // scan) - fine for columns added via AddColumnIfMissing (a SELECT against a missing column
        // just returns nothing meaningful), but the new *_Original snapshot tables are WRITTEN to by
        // SaveCOBJConditions/SaveEnchantmentEffects/SaveWornRestrictionKeywords the moment a user
        // edits a condition/effect/keyword. On an existing item.db from before those tables existed,
        // that write would throw "no such table" the first time - reproduced and confirmed while
        // testing this feature (an un-rescanned DB left over from before this schema was added,
        // opened by a normal app launch that never runs PutIntoDataBank at all). Both methods are
        // idempotent (IF NOT EXISTS / column-exists checks), so running them here on every cache load
        // is a no-op once a DB is current - this just guarantees a normal launch (not just a rescan)
        // brings an older item.db up to date before anything can be edited.
        private void EnsureDatabaseSchema()
        {
            if (!File.Exists(ItemdbPath)) return;

            using var connection = new SqliteConnection($"Data Source={ItemdbPath}");
            connection.Open();
            EnsureSchema(connection);
            CreateTables(connection);
        }

        private void LoadCacheCore()
        {
            EnsureDatabaseSchema();

            // Each LoadX() opens its own SqliteConnection and only ever writes to a distinct local
            // below, so these 10 independent table reads can run concurrently without any race —
            // this is what actually keeps the (now single-threaded-by-lock) cache build fast, since
            // the per-plugin Parallel.ForEach in CacheManager can no longer parallelize the load itself.
            List<ArmorRecord> armor = null;
            List<WeaponRecord> weapons = null;
            List<COBJRecord> cobj = null;
            List<COBJConditionRecord> cobjConditions = null;
            List<EnchantmentRecord> enchantments = null;
            List<EnchantmentEffectRecord> enchantmentEffects = null;
            Dictionary<string, List<string>> wornRestrictionKeywords = null;
            List<ContainerRecord> containerRecords = null;
            List<ContainerLVLIRecord> containerLvli = null;
            List<MagicEffectsRecords> magicEffects = null;

            System.Threading.Tasks.Parallel.Invoke(
                () => armor = LoadArmor(),
                () => weapons = LoadWeapons(),
                () => cobj = LoadCOBJ(),
                () => cobjConditions = LoadCOBJConditions(),
                () => enchantments = LoadEnchantments(),
                () => enchantmentEffects = LoadEnchantmentEffects(),
                () => wornRestrictionKeywords = LoadWornRestrictionKeywords(),
                () => containerRecords = LoadContainer(),
                () => containerLvli = LoadContainerLVLI(),
                () => magicEffects = LoadMagicEffects()
            );

            _armorCache = armor;
            _weaponCache = weapons;
            _cobjCache = cobj;
            _cobjCondtionsCache = cobjConditions;
            _enchantmentCache = enchantments;
            _enchantmentEffectsCache = enchantmentEffects;
            _wornRestrictionKeywords = wornRestrictionKeywords;
            _containerCache = containerRecords;
            _containerLvliCache = containerLvli;
            _magicEffectsCache = magicEffects;

            // Dictionary: Plugin|FormID → Record
            _armorByKey = _armorCache.ToDictionary(a => a.Key);
            _weaponByKey = _weaponCache.ToDictionary(w => w.Key);
            _cobjByKey = _cobjCache.ToDictionary(c => c.Key);
            _enchantmentByKey = _enchantmentCache.ToDictionary(e => e.Key);

            // Reverse lookup: CreatedItem → List<COBJRecord>
            _cobjByCreatedItem = _cobjCache
                .GroupBy(c => c.CreatedItemKey)
                .ToDictionary(g => g.Key, g => g.ToList());

            _effectsByEnchantment = _enchantmentEffectsCache
                .GroupBy(e => e.EnchantmentKey)
                .ToDictionary(g => g.Key, g => g.ToList());

            // Dictionary: ContainerKey → ContainerRecord
            _containerByKey = _containerCache.ToDictionary(c => c.ContainerKey);

            // Dictionary: ContainerKey → List<LVLI>
            _containerLvliByContainer = _containerLvliCache
                .GroupBy(l => l.ContainerKey)
                .ToDictionary(g => g.Key, g => g.ToList());

            // Attach LVLI entries to their ContainerRecord instances so callers
            // that create ContainerEntryVM from ContainerRecord get the LVLi list.
            foreach (var container in _containerCache)
            {
                if (_containerLvliByContainer.TryGetValue(container.ContainerKey, out var lvliList))
                    container.LVLIEntries = lvliList;
                else
                    container.LVLIEntries = new List<ContainerLVLIRecord>();
            }
        }

        // ============================
        //        DB ERSTELLEN
        // ============================

        public void PutIntoDataBank(List<PluginInfo> allgamePathfromDB)
        {
            Directory.CreateDirectory(ItemFolder);

            using var connection = new SqliteConnection($"Data Source={ItemdbPath}");
            connection.Open();

            using (var pragma = connection.CreateCommand())
            {
                pragma.CommandText = "PRAGMA journal_mode=WAL;";
                pragma.ExecuteNonQuery();
            }

            EnsureSchema(connection);
            CreateTables(connection);

            // Which COBJ/Enchantment records has the user hand-edited Conditions/Effects/
            // WornRestrictionKeywords for? Unlike Armor/Weapon/COBJ/Enchantment's own fields (which
            // have IsEdited* shadow columns on the SAME row), these three child tables have no
            // shadow-column mechanism — a user edit is a full replace of the base child rows. So the
            // only way to protect a hand-edited child set from being silently overwritten by this scan
            // is a flag on the PARENT row, checked before we touch its children below.
            var conditionsEditedKeys = ReadFlaggedKeys(connection, "SELECT Key FROM COBJ WHERE ConditionsEdited = 1;");
            var effectsEditedKeys = ReadFlaggedKeys(connection, "SELECT Key FROM Enchantments WHERE EffectsEdited = 1;");
            var keywordsEditedKeys = ReadFlaggedKeys(connection, "SELECT Key FROM Enchantments WHERE KeywordsEdited = 1;");

            using var insertArmor = PrepareUpsert(connection, "Armor", ArmorColumnNames, ArmorParamNames);
            using var insertWeapon = PrepareUpsert(connection, "Weapons", WeaponColumnNames, WeaponParamNames);
            using var insertCOBJ = PrepareUpsert(connection, "COBJ", CobjColumnNames, CobjParamNames);
            using var insertCOBJCondition = PrepareInsert(connection, "COBJ_Conditions", CobjConditionColumnNames, CobjConditionParamNames);
            using var insertEnch = PrepareUpsert(connection, "Enchantments", EnchantmentColumnNames, EnchantmentParamNames);
            using var insertEnchEff = PrepareInsert(connection, "EnchantmentEffects", EnchantmentEffectColumnNames, EnchantmentEffectParamNames);
            using var insertWRK = PrepareInsert(connection, "WornRestrictionKeywords", WornRestrictionKeywordColumnNames, WornRestrictionKeywordParamNames);
            using var insertContainer = PrepareUpsert(connection, "Container", ContainerColumnNames, ContainerParamNames);
            using var insertContainerLVLI = PrepareInsert(connection, "ContainerLVLI", ContainerLvliColumnNames, ContainerLvliParamNames);
            using var insertMagicEffects = PrepareUpsert(connection, "MagicEffects", MagicEffectColumnNames, MagicEffectParamNames);

            // Multi-row "batch" counterparts of the commands above. At the row counts a full scan
            // produces (100k+), one ExecuteNonQuery() per row was the dominant cost (~140k round
            // trips). These insert BatchSize rows per statement instead; the tail that doesn't fill a
            // full batch falls back to the single-row commands above. Row count (<=90) and per-row
            // column count (<=10) keep every batch statement's parameter count well under SQLite's
            // historical 999 host-parameter limit and its 500-row multi-VALUES limit.
            using var insertArmorBatch = PrepareUpsertBatch(connection, "Armor", ArmorColumnNames, ArmorParamNames, BatchSize);
            using var insertWeaponBatch = PrepareUpsertBatch(connection, "Weapons", WeaponColumnNames, WeaponParamNames, BatchSize);
            using var insertCOBJBatch = PrepareUpsertBatch(connection, "COBJ", CobjColumnNames, CobjParamNames, BatchSize);
            using var insertCOBJConditionBatch = PrepareInsertBatch(connection, "COBJ_Conditions", CobjConditionColumnNames, CobjConditionParamNames, BatchSize);
            using var insertEnchBatch = PrepareUpsertBatch(connection, "Enchantments", EnchantmentColumnNames, EnchantmentParamNames, BatchSize);
            using var insertEnchEffBatch = PrepareInsertBatch(connection, "EnchantmentEffects", EnchantmentEffectColumnNames, EnchantmentEffectParamNames, BatchSize);
            using var insertWRKBatch = PrepareInsertBatch(connection, "WornRestrictionKeywords", WornRestrictionKeywordColumnNames, WornRestrictionKeywordParamNames, BatchSize);
            using var insertContainerBatch = PrepareUpsertBatch(connection, "Container", ContainerColumnNames, ContainerParamNames, BatchSize);
            using var insertContainerLVLIBatch = PrepareInsertBatch(connection, "ContainerLVLI", ContainerLvliColumnNames, ContainerLvliParamNames, BatchSize);
            using var insertMagicEffectsBatch = PrepareUpsertBatch(connection, "MagicEffects", MagicEffectColumnNames, MagicEffectParamNames, BatchSize);

            using var transaction = connection.BeginTransaction();
            insertArmor.Transaction = transaction;
            insertWeapon.Transaction = transaction;
            insertCOBJ.Transaction = transaction;
            insertCOBJCondition.Transaction = transaction;
            insertEnch.Transaction = transaction;
            insertEnchEff.Transaction = transaction;
            insertWRK.Transaction = transaction;
            insertContainer.Transaction = transaction;
            insertContainerLVLI.Transaction = transaction;
            insertMagicEffects.Transaction = transaction;
            insertArmorBatch.Transaction = transaction;
            insertWeaponBatch.Transaction = transaction;
            insertCOBJBatch.Transaction = transaction;
            insertCOBJConditionBatch.Transaction = transaction;
            insertEnchBatch.Transaction = transaction;
            insertEnchEffBatch.Transaction = transaction;
            insertWRKBatch.Transaction = transaction;
            insertContainerBatch.Transaction = transaction;
            insertContainerLVLIBatch.Transaction = transaction;
            insertMagicEffectsBatch.Transaction = transaction;

            // Parse phase runs in parallel across plugins (CPU-bound, no DB access); a second,
            // strictly sequential phase then does all SQLite writes (one connection/transaction).
            //
            // Results go into an array indexed by plugin position, not a ConcurrentBag: with
            // "INSERT OR REPLACE", the last write for a given key wins, so override precedence
            // between plugins depends on write order matching allgamePathfromDB's load order
            // exactly. Parallel.For writes each result into its own reserved slot, preserving that
            // order regardless of thread scheduling.
            _formIDDB.EnsureCacheLoaded();

            var pluginFiles = allgamePathfromDB
                .SelectMany(plugin => plugin.FullPaths.Select(fullPath => (plugin.FileName, fullPath)))
                .ToList();

            var parseSw = Stopwatch.StartNew();
            var parsedPlugins = new ParsedPluginData[pluginFiles.Count];
            Parallel.For(0, pluginFiles.Count, i =>
            {
                parsedPlugins[i] = ParsePluginForItemDB(pluginFiles[i].FileName, pluginFiles[i].fullPath);
            });
            parseSw.Stop();

            int armorCount = 0, weaponCount = 0, cobjCount = 0, condCount = 0, enchCount = 0, effCount = 0, containerCount = 0, lvliCount = 0, mgefCount = 0;
            foreach (var p in parsedPlugins)
            {
                armorCount += p.ArmorRows.Count;
                weaponCount += p.WeaponRows.Count;
                cobjCount += p.Cobjs.Count;
                condCount += p.Cobjs.Sum(c => c.ConditionRows.Count);
                enchCount += p.Enchantments.Count;
                effCount += p.Enchantments.Sum(e => e.EffectRows.Count);
                containerCount += p.Containers.Count;
                lvliCount += p.Containers.Sum(c => c.LvliRows.Count);
                mgefCount += p.MagicEffectRows.Count;
            }
            Debug.WriteLine($"[ItemDB] Parse phase: {parseSw.ElapsedMilliseconds} ms ({pluginFiles.Count} plugins, " +
                $"Armor={armorCount} Weapon={weaponCount} COBJ={cobjCount} COBJCond={condCount} " +
                $"Ench={enchCount} EnchEff={effCount} Container={containerCount} LVLI={lvliCount} MGEF={mgefCount})");

            // --- Flatten: gather every row across all plugins per table before writing ---
            // parsedPlugins is index-ordered (see above), so this walks plugins in exactly
            // allgamePathfromDB's given order — matching the original single-threaded loop's write
            // order, which is what "INSERT OR REPLACE" override resolution (last write for a given
            // key wins) depends on for records overridden by more than one plugin.
            //
            // COBJ/Enchantment/Container each own a set of child rows (Conditions/Effects+
            // WornRestrictionKeywords/LVLI). A Bethesda plugin override replaces a record as a whole
            // — including its child rows — not incrementally, so when more than one plugin touches
            // the same parent key, only the LAST plugin's full child set should survive. Naively
            // AddRange-ing every plugin's child rows here doesn't do that: for COBJ_Conditions (no
            // matching unique constraint on its columns, only a meaningless autoincrement Id) it
            // straight up duplicates every earlier plugin's conditions alongside the new ones; for
            // the other three tables (which do have a composite key matching child identity) it
            // still leaves rows behind that a later plugin's version had actually removed, since nothing
            // ever deletes a child row that simply stops reappearing. So COBJ/Enchantment/Container are
            // reduced to "last plugin per parent key" first, and only that plugin's child rows are used.
            var allArmor = new List<object[]>();
            var allWeapon = new List<object[]>();
            var allMagicEffects = new List<object[]>();

            var latestCobjByKey = new Dictionary<string, ParsedCobj>();
            var latestEnchantmentByKey = new Dictionary<string, ParsedEnchantment>();
            var latestContainerByKey = new Dictionary<string, ParsedContainer>();

            foreach (var parsed in parsedPlugins)
            {
                allArmor.AddRange(parsed.ArmorRows);
                allWeapon.AddRange(parsed.WeaponRows);
                allMagicEffects.AddRange(parsed.MagicEffectRows);

                foreach (var cobj in parsed.Cobjs)
                    latestCobjByKey[(string)cobj.Values[0]] = cobj;

                foreach (var ench in parsed.Enchantments)
                    latestEnchantmentByKey[(string)ench.Values[0]] = ench;

                foreach (var container in parsed.Containers)
                    latestContainerByKey[(string)container.Values[0]] = container;
            }

            // Records whose ConditionsEdited/EffectsEdited/KeywordsEdited flag is set keep their
            // existing child rows untouched — excluded from both the rewrite-key lists (used for the
            // DELETE below) and the flattened row lists (so nothing gets added back either).
            var allCobj = new List<object[]>();
            var allCobjConditions = new List<object[]>();
            var cobjConditionRewriteKeys = new List<string>();
            foreach (var kv in latestCobjByKey)
            {
                allCobj.Add(kv.Value.Values);
                if (conditionsEditedKeys.Contains(kv.Key))
                    continue;
                cobjConditionRewriteKeys.Add(kv.Key);
                allCobjConditions.AddRange(kv.Value.ConditionRows);
            }

            var allEnchantments = new List<object[]>();
            var allWornRestrictionKeywords = new List<object[]>();
            var seenWornRestrictionKeywordRows = new HashSet<(string ListKey, string KeywordKey)>();
            var allEnchantmentEffects = new List<object[]>();
            var latestEffectRow = new Dictionary<(string EnchantmentKey, string MagicEffectKey), object[]>();
            var enchantmentEffectRewriteKeys = new List<string>();
            var wornRestrictionListKeysToRewrite = new List<string>();
            foreach (var kv in latestEnchantmentByKey)
            {
                allEnchantments.Add(kv.Value.Values);

                if (!keywordsEditedKeys.Contains(kv.Key))
                {
                    // EnchantmentColumnNames[6] is WornRestrictionListKey — WornRestrictionKeywords is
                    // keyed by that list key, not by the enchantment's own key. Multiple enchantments
                    // commonly share the same list (FLST), so the same (ListKey, KeywordKey) row would
                    // otherwise be queued once per enchantment that references it — dedupe here since
                    // this table has a real PRIMARY KEY(ListKey, KeywordKey) and is written via plain
                    // INSERT.
                    var listKey = (string)kv.Value.Values[6];
                    if (!string.IsNullOrEmpty(listKey))
                        wornRestrictionListKeysToRewrite.Add(listKey);
                    foreach (var row in kv.Value.WornRestrictionKeywordRows)
                    {
                        if (seenWornRestrictionKeywordRows.Add(((string)row[0], (string)row[1])))
                            allWornRestrictionKeywords.Add(row);
                    }
                }

                if (!effectsEditedKeys.Contains(kv.Key))
                {
                    enchantmentEffectRewriteKeys.Add(kv.Key);
                    // A single enchantment record's own Effects list can legitimately reference the
                    // same base MagicEffect more than once (e.g. two entries with different
                    // magnitude/duration) — this table has PRIMARY KEY(EnchantmentKey, MagicEffectKey),
                    // which can't represent that distinction. Dedupe here (last entry in the record's
                    // own list wins), matching what the previous INSERT OR REPLACE write path did
                    // silently instead of the plain INSERT crashing on the collision.
                    foreach (var row in kv.Value.EffectRows)
                        latestEffectRow[((string)row[0], (string)row[1])] = row;
                }
            }
            allEnchantmentEffects.AddRange(latestEffectRow.Values);

            var allContainers = new List<object[]>();
            var allContainerLvli = new List<object[]>();
            var latestLvliRow = new Dictionary<(string ContainerKey, string LvliKey), object[]>();
            foreach (var container in latestContainerByKey.Values)
            {
                allContainers.Add(container.Values);
                // Same rationale as EnchantmentEffects above — ContainerLVLI has
                // PRIMARY KEY(ContainerKey, LVLiKey), but a container's own item list can list the same
                // leveled item more than once (e.g. duplicate entries from a patch).
                foreach (var row in container.LvliRows)
                    latestLvliRow[((string)row[0], (string)row[1])] = row;
            }
            allContainerLvli.AddRange(latestLvliRow.Values);

            // --- Write phase: strictly sequential — do not parallelize SQLite writes ---
            var writeSw = Stopwatch.StartNew();

            // Child tables: delete existing rows for the parent keys we're about to rewrite, then
            // insert the freshly parsed set. Necessary because COBJ_Conditions has no unique
            // constraint to upsert against, and even for the two tables that do (EnchantmentEffects,
            // WornRestrictionKeywords) an upsert alone would never remove a row a later plugin's
            // version had actually dropped. Keys protected by an *Edited flag were already excluded
            // above, so their rows are never deleted or reinserted here.
            DeleteChildRowsForKeys(connection, transaction, "COBJ_Conditions", "COBJKey", cobjConditionRewriteKeys);
            DeleteChildRowsForKeys(connection, transaction, "EnchantmentEffects", "EnchantmentKey", enchantmentEffectRewriteKeys);
            DeleteChildRowsForKeys(connection, transaction, "WornRestrictionKeywords", "ListKey", wornRestrictionListKeysToRewrite.Distinct().ToList());
            // Container has no *Edited protection flag (it's a pure reference table), so every
            // scanned container's LVLI rows are simply cleared and rewritten each scan — otherwise a
            // rescan's insert collides with the previous scan's still-present rows for the same
            // (ContainerKey, LVLiKey), and a removed item would linger as a stale row.
            DeleteChildRowsForKeys(connection, transaction, "ContainerLVLI", "ContainerKey", latestContainerByKey.Keys.ToList());

            ExecuteRowsBatched(insertArmor, insertArmorBatch, ArmorParamNames, allArmor, BatchSize);
            ExecuteRowsBatched(insertWeapon, insertWeaponBatch, WeaponParamNames, allWeapon, BatchSize);
            ExecuteRowsBatched(insertCOBJ, insertCOBJBatch, CobjParamNames, allCobj, BatchSize);
            ExecuteRowsBatched(insertCOBJCondition, insertCOBJConditionBatch, CobjConditionParamNames, allCobjConditions, BatchSize);
            ExecuteRowsBatched(insertWRK, insertWRKBatch, WornRestrictionKeywordParamNames, allWornRestrictionKeywords, BatchSize);
            ExecuteRowsBatched(insertEnch, insertEnchBatch, EnchantmentParamNames, allEnchantments, BatchSize);
            ExecuteRowsBatched(insertEnchEff, insertEnchEffBatch, EnchantmentEffectParamNames, allEnchantmentEffects, BatchSize);
            ExecuteRowsBatched(insertContainer, insertContainerBatch, ContainerParamNames, allContainers, BatchSize);
            ExecuteRowsBatched(insertContainerLVLI, insertContainerLVLIBatch, ContainerLvliParamNames, allContainerLvli, BatchSize);
            ExecuteRowsBatched(insertMagicEffects, insertMagicEffectsBatch, MagicEffectParamNames, allMagicEffects, BatchSize);

            // Parent tables: anything not touched by this scan is no longer defined by any currently
            // active plugin — mark it inactive (hidden from Load*) instead of deleting, so its
            // IsEdited*/Original data survives in case the plugin comes back. COBJ additionally
            // excludes Original=0 (user-created recipes, which never appear in a scan's key-set by
            // definition — without this exemption every user recipe would be marked inactive on every
            // single scan, since nothing ever "scans" them).
            MarkInactiveExcept(connection, transaction, "Armor", "Key", allArmor.Select(r => (string)r[0]));
            MarkInactiveExcept(connection, transaction, "Weapons", "Key", allWeapon.Select(r => (string)r[0]));
            MarkInactiveExcept(connection, transaction, "COBJ", "Key", latestCobjByKey.Keys, extraWhere: "Original = 1");
            MarkInactiveExcept(connection, transaction, "Enchantments", "Key", latestEnchantmentByKey.Keys);
            MarkInactiveExcept(connection, transaction, "Container", "ContainerKey", latestContainerByKey.Keys);
            MarkInactiveExcept(connection, transaction, "MagicEffects", "Key", allMagicEffects.Select(r => (string)r[0]));

            writeSw.Stop();
            Debug.WriteLine($"[ItemDB] Write phase: {writeSw.ElapsedMilliseconds} ms");

            var commitSw = Stopwatch.StartNew();
            transaction.Commit();
            commitSw.Stop();
            Debug.WriteLine($"[ItemDB] Commit: {commitSw.ElapsedMilliseconds} ms");

            InvalidateCache();
        }

        private static HashSet<string> ReadFlaggedKeys(SqliteConnection connection, string sql)
        {
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using var cmd = connection.CreateCommand();
            cmd.CommandText = sql;
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                keys.Add(reader.GetString(0));
            return keys;
        }

        // Creates a fresh TEMP TABLE holding `keys` (one column, "Key") and returns its name. Batches
        // the inserts (same reasoning as BatchSize elsewhere in this file — one row per statement was
        // the original scan's dominant cost) so this stays fast even for tens of thousands of keys.
        // Caller is responsible for dropping the returned table when done with it.
        private static string PopulateKeyTempTable(SqliteConnection connection, SqliteTransaction transaction, string tempTableName, IReadOnlyList<string> keys)
        {
            using (var createCmd = connection.CreateCommand())
            {
                createCmd.Transaction = transaction;
                createCmd.CommandText = $"CREATE TEMP TABLE {tempTableName} (Key TEXT PRIMARY KEY);";
                createCmd.ExecuteNonQuery();
            }

            const int chunkSize = 400;
            for (int i = 0; i < keys.Count; i += chunkSize)
            {
                int count = Math.Min(chunkSize, keys.Count - i);
                var placeholders = string.Join(", ", Enumerable.Range(0, count).Select(r => $"(@k{r})"));

                using var cmd = connection.CreateCommand();
                cmd.Transaction = transaction;
                cmd.CommandText = $"INSERT OR IGNORE INTO {tempTableName} (Key) VALUES {placeholders};";
                for (int r = 0; r < count; r++)
                    cmd.Parameters.AddWithValue($"@k{r}", keys[i + r]);
                cmd.ExecuteNonQuery();
            }

            return tempTableName;
        }

        private static void DeleteChildRowsForKeys(SqliteConnection connection, SqliteTransaction transaction, string table, string parentKeyColumn, IReadOnlyList<string> keys)
        {
            if (keys.Count == 0) return;

            var tempTable = PopulateKeyTempTable(connection, transaction, "_DeleteKeys", keys);
            try
            {
                using var deleteCmd = connection.CreateCommand();
                deleteCmd.Transaction = transaction;
                deleteCmd.CommandText = $"DELETE FROM {table} WHERE {parentKeyColumn} IN (SELECT Key FROM {tempTable});";
                deleteCmd.ExecuteNonQuery();
            }
            finally
            {
                using var dropCmd = connection.CreateCommand();
                dropCmd.Transaction = transaction;
                dropCmd.CommandText = $"DROP TABLE {tempTable};";
                dropCmd.ExecuteNonQuery();
            }
        }

        // Marks rows in `table` inactive when their key wasn't among `scannedKeys` — i.e. no currently
        // active plugin defines/touches them anymore, whether because the whole plugin was removed or
        // just that one record was dropped from an updated version of it. Verified against a real
        // SQLite instance before wiring in (temp table + NOT EXISTS, not a giant IN-list, to stay fast
        // at the tens-of-thousands-of-keys scale this app's scans run at).
        private static void MarkInactiveExcept(SqliteConnection connection, SqliteTransaction transaction, string table, string keyColumn, IEnumerable<string> scannedKeys, string extraWhere = null)
        {
            var keys = scannedKeys.Distinct().ToList();
            var tempTable = PopulateKeyTempTable(connection, transaction, "_ActiveKeys", keys);
            try
            {
                var where = $"Active = 1 AND NOT EXISTS (SELECT 1 FROM {tempTable} WHERE {tempTable}.Key = {table}.{keyColumn})";
                if (!string.IsNullOrEmpty(extraWhere))
                    where += $" AND {extraWhere}";

                using var updateCmd = connection.CreateCommand();
                updateCmd.Transaction = transaction;
                updateCmd.CommandText = $"UPDATE {table} SET Active = 0 WHERE {where};";
                updateCmd.ExecuteNonQuery();
            }
            finally
            {
                using var dropCmd = connection.CreateCommand();
                dropCmd.Transaction = transaction;
                dropCmd.CommandText = $"DROP TABLE {tempTable};";
                dropCmd.ExecuteNonQuery();
            }
        }

        private sealed class ParsedPluginData
        {
            public List<object[]> ArmorRows = new();
            public List<object[]> WeaponRows = new();
            public List<ParsedCobj> Cobjs = new();
            public List<ParsedEnchantment> Enchantments = new();
            public List<ParsedContainer> Containers = new();
            public List<object[]> MagicEffectRows = new();
        }

        private sealed class ParsedCobj
        {
            public object[] Values;
            public List<object[]> ConditionRows = new();
        }

        private sealed class ParsedEnchantment
        {
            public object[] Values;
            public List<object[]> WornRestrictionKeywordRows = new();
            public List<object[]> EffectRows = new();
        }

        private sealed class ParsedContainer
        {
            public object[] Values;
            public List<object[]> LvliRows = new();
        }

        private static readonly string[] ArmorParamNames =
            { "@key", "@editorID", "@name", "@weight", "@val", "@armorRating", "@slotMask", "@keywords" };
        private static readonly string[] WeaponParamNames =
            { "@key", "@editorID", "@name", "@weight", "@val", "@dmg", "@speed", "@reach", "@stagger", "@keywords" };
        private static readonly string[] CobjParamNames =
            { "@key", "@name", "@createdItem", "@workbench", "@ingredients" };
        private static readonly string[] CobjConditionParamNames =
            { "@cobjKey", "@extra", "@runOn", "@type", "@target", "@value" };
        private static readonly string[] EnchantmentParamNames =
            { "@key", "@editorID", "@name", "@cast", "@target", "@cost", "@wrestr" };
        private static readonly string[] EnchantmentEffectParamNames =
            { "@ench", "@mgef", "@editorID", "@name", "@mag", "@dur", "@area" };
        private static readonly string[] WornRestrictionKeywordParamNames = { "@list", "@kw" };
        private static readonly string[] ContainerParamNames = { "@key", "@name" };
        private static readonly string[] ContainerLvliParamNames = { "@containerKey", "@lvliKey", "@lvliName" };
        private static readonly string[] MagicEffectParamNames =
            { "@key", "@editorID", "@name", "@hasMag", "@hasDur", "@hasAre", "@castType", "@targetType" };

        // Real table column names, in the same order as the matching *ParamNames array above.
        // Several of the single-row PrepareInsertX commands bind a parameter name that doesn't match
        // its column name (e.g. column "Value" <- @val, "BodySlotMask" <- @slotMask) — PrepareBatchInsert
        // needs the actual column names for its generated INSERT column list; deriving them from the
        // param names (stripping "@") is wrong wherever they differ and was the cause of the
        // "table Armor has no column named val" crash.
        private static readonly string[] ArmorColumnNames =
            { "Key", "EditorID", "Name", "Weight", "Value", "ArmorRating", "BodySlotMask", "Keywords" };
        private static readonly string[] WeaponColumnNames =
            { "Key", "EditorID", "Name", "Weight", "Value", "Damage", "Speed", "Reach", "Stagger", "Keywords" };
        private static readonly string[] CobjColumnNames =
            { "Key", "Name", "CreatedItem", "WorkbenchKeyword", "Ingredients" };
        private static readonly string[] CobjConditionColumnNames =
            { "COBJKey", "Extra", "RunOn", "ConditionType", "Target", "Value" };
        private static readonly string[] EnchantmentColumnNames =
            { "Key", "EditorID", "Name", "CastType", "TargetType", "EnchantmentCost", "WornRestrictionListKey" };
        private static readonly string[] EnchantmentEffectColumnNames =
            { "EnchantmentKey", "MagicEffectKey", "EditorID", "Name", "Magnitude", "Duration", "Area" };
        private static readonly string[] WornRestrictionKeywordColumnNames = { "ListKey", "KeywordKey" };
        private static readonly string[] ContainerColumnNames = { "ContainerKey", "Name" };
        private static readonly string[] ContainerLvliColumnNames = { "ContainerKey", "LVLiKey", "LVLiName" };
        private static readonly string[] MagicEffectColumnNames =
            { "Key", "EditorID", "Name", "HasMagnitude", "HasDuration", "HasArea", "CastType", "TargetType" };

        private static void ApplyRowAndExecute(SqliteCommand cmd, string[] paramNames, object[] values)
        {
            for (int i = 0; i < paramNames.Length; i++)
                cmd.Parameters[paramNames[i]].Value = values[i] ?? DBNull.Value;
            cmd.ExecuteNonQuery();
        }

        // Rows per batch statement. Bounded so that even the widest table (Weapon, 10 columns) stays
        // at 900 bound parameters — comfortably under SQLite's historical 999 host-parameter limit —
        // and 90 rows is well under the ~500-row limit for a multi-row VALUES list.
        private const int BatchSize = 90;

        // Plain multi-row INSERT (no OR REPLACE / no ON CONFLICT) for the 4 child tables
        // (COBJ_Conditions/EnchantmentEffects/WornRestrictionKeywords/ContainerLVLI). PutIntoDataBank
        // always DELETEs the parent keys it's about to rewrite children for first (see
        // DeleteChildRowsForKeys), so a plain insert never conflicts — and if it somehow did, that
        // would mean a real bug, which a plain INSERT surfaces as an error instead of silently
        // replacing/duplicating rows the way "OR REPLACE" used to.
        private static SqliteCommand PrepareInsertBatch(SqliteConnection connection, string table, string[] columnNames, string[] paramNames, int batchSize)
        {
            var columns = string.Join(", ", columnNames);

            var rowGroups = new string[batchSize];
            for (int r = 0; r < batchSize; r++)
                rowGroups[r] = "(" + string.Join(", ", paramNames.Select(p => $"{p}_{r}")) + ")";

            var cmd = connection.CreateCommand();
            cmd.CommandText = $"INSERT INTO {table} ({columns}) VALUES {string.Join(", ", rowGroups)}";

            for (int r = 0; r < batchSize; r++)
                foreach (var p in paramNames)
                    cmd.Parameters.Add(new SqliteParameter($"{p}_{r}", DBNull.Value));

            return cmd;
        }

        // Multi-row UPSERT for the 6 "parent" tables (Armor/Weapons/COBJ/Enchantments/Container/
        // MagicEffects) — columnNames/paramNames only ever list the scanned base columns (never
        // IsEdited*/Original/*Edited), so the ON CONFLICT DO UPDATE clause built from them can never
        // touch those columns. That's the whole point: a rescan updates the plugin-derived data and
        // flips Active back to 1, but never overwrites a user's manual edits. columnNames[0] is always
        // that table's natural key (Key, or ContainerKey for Container) and is used as the conflict
        // target. Verified against a real SQLite instance (3.49.1, same as this app) before wiring in.
        private static SqliteCommand PrepareUpsertBatch(SqliteConnection connection, string table, string[] columnNames, string[] paramNames, int batchSize)
        {
            var columns = string.Join(", ", columnNames) + ", Active";
            var updateSet = string.Join(", ", columnNames.Skip(1).Select(c => $"{c} = excluded.{c}")) + ", Active = 1";

            var rowGroups = new string[batchSize];
            for (int r = 0; r < batchSize; r++)
                rowGroups[r] = "(" + string.Join(", ", paramNames.Select(p => $"{p}_{r}")) + ", 1)";

            var cmd = connection.CreateCommand();
            cmd.CommandText =
                $"INSERT INTO {table} ({columns}) VALUES {string.Join(", ", rowGroups)} " +
                $"ON CONFLICT({columnNames[0]}) DO UPDATE SET {updateSet}";

            for (int r = 0; r < batchSize; r++)
                foreach (var p in paramNames)
                    cmd.Parameters.Add(new SqliteParameter($"{p}_{r}", DBNull.Value));

            return cmd;
        }

        // Writes `rows` using `batchCmd` (BatchSize rows per ExecuteNonQuery) for as many full
        // batches as fit, then falls back to `singleCmd` (the plain one-row-per-call command) for
        // the remainder — avoids re-preparing a differently-sized batch statement for the tail.
        private static void ExecuteRowsBatched(SqliteCommand singleCmd, SqliteCommand batchCmd, string[] paramNames, List<object[]> rows, int batchSize)
        {
            int i = 0;
            for (; i + batchSize <= rows.Count; i += batchSize)
            {
                for (int r = 0; r < batchSize; r++)
                {
                    var row = rows[i + r];
                    for (int c = 0; c < paramNames.Length; c++)
                        batchCmd.Parameters[$"{paramNames[c]}_{r}"].Value = row[c] ?? DBNull.Value;
                }
                batchCmd.ExecuteNonQuery();
            }

            for (; i < rows.Count; i++)
                ApplyRowAndExecute(singleCmd, paramNames, rows[i]);
        }

        // Pure parsing — no DB access — so this is safe to call concurrently from Parallel.ForEach.
        // Mirrors the original single-threaded loop body exactly, just capturing values into rows
        // instead of writing straight to a shared SqliteCommand's parameters.
        private ParsedPluginData ParsePluginForItemDB(string pluginName, string fullPath)
        {
            var result = new ParsedPluginData();
            var mod = SkyrimMod.CreateFromBinaryOverlay(fullPath, SkyrimRelease.SkyrimSE);

            // ARMOR
            foreach (var armor in mod.Armors.Records)
            {
                string key = KeyFactory.BuildMasterKey(armor.FormKey);

                // BodySlotMask (bitmask) — direct typed access, no reflection
                uint slotMask = (uint)(armor.BodyTemplate?.FirstPersonFlags ?? 0);

                var kw = (armor.Keywords?
                    .Select(k =>
                    {
                        var fk = k.FormKey;
                        return $"{fk.ModKey.FileName}|{fk.IDString()}";
                    })
                    ?? Enumerable.Empty<string>())
                    .ToList();

                // Override-order trace, disabled — re-enable (and adjust the keyword suffixes) if a
                // similar load-order investigation is needed again.
                //if (kw.Any(k => k.EndsWith("6BBD9", StringComparison.OrdinalIgnoreCase)
                //              || k.EndsWith("6BBE6", StringComparison.OrdinalIgnoreCase)))
                //{
                //    Debug.WriteLine($"[TRACE Armor] plugin={pluginName} key={key} editorID={armor.EditorID} keywords={string.Join(",", kw)}");
                //}

                result.ArmorRows.Add(new object[]
                {
                    key,
                    armor.EditorID ?? "",
                    armor.Name?.ToString() ?? "",
                    (float?)armor.Weight ?? 0f,
                    (int?)armor.Value ?? 0,
                    (float?)armor.ArmorRating ?? 0f,
                    (long)slotMask,
                    string.Join(",", kw)
                });
            }

            // WEAPONS
            foreach (var weap in mod.Weapons.Records)
            {
                string key = KeyFactory.BuildMasterKey(weap.FormKey);

                var kw = weap.Keywords?
                    .Select(k =>
                    {
                        var fk = k.FormKey;
                        return $"{fk.ModKey.FileName}|{fk.IDString()}";
                    })
                    ?? Enumerable.Empty<string>();

                result.WeaponRows.Add(new object[]
                {
                    key,
                    weap.EditorID ?? "",
                    weap.Name?.ToString() ?? "",
                    (weap.BasicStats?.Weight) ?? 0f,
                    (int?)weap.BasicStats?.Value ?? 0,
                    weap.BasicStats?.Damage ?? 0,
                    weap.Data?.Speed ?? 0f,
                    weap.Data?.Reach ?? 0f,
                    weap.Data?.Stagger ?? 0f,
                    string.Join(",", kw)
                });
            }

            // COBJ
            foreach (var cobj in mod.ConstructibleObjects.Records)
            {
                string key = KeyFactory.BuildMasterKey(cobj.FormKey);
                string createdKey = KeyFactory.BuildMasterKey(cobj.CreatedObject.FormKey);

                string workbench = "";
                if (cobj.WorkbenchKeyword != null)
                {
                    var fk = cobj.WorkbenchKeyword.FormKey;
                    workbench = $"{fk.ModKey.FileName}|{fk.IDString()}";
                }

                var ingredients = cobj.Items?
                    .Select(e =>
                    {
                        var fk = e.Item.Item.FormKey;
                        return $"{fk.ModKey.FileName}|{fk.IDString()}*{e.Item.Count}";
                    })
                    ?? Enumerable.Empty<string>();

                var parsedCobj = new ParsedCobj
                {
                    Values = new object[] { key, cobj.EditorID ?? "", createdKey, workbench, string.Join(",", ingredients) }
                };

                // --------------------------------
                // Conditions → COBJ_Conditions
                // --------------------------------
                foreach (var cond in cobj.Conditions)
                {
                    // ConditionFloat compares against a fixed float value, ConditionGlobal compares
                    // against a Global. Both get produced by ESP/ESL/ESM files, so we need to read
                    // both. When reading from plugin files, Mutagen returns overlay types (e.g.
                    // ConditionFloatBinaryOverlay) that only implement the getter interfaces, not the
                    // concrete classes. So match against the interfaces instead of against
                    // ConditionFloat/ConditionGlobal.
                    IConditionDataGetter data;
                    string comparisonValue;

                    if (cond is IConditionFloatGetter cf)
                    {
                        data = cf.Data;
                        comparisonValue = cf.ComparisonValue.ToString();
                    }
                    else if (cond is IConditionGlobalGetter cg)
                    {
                        data = cg.Data;
                        var globalFk = cg.ComparisonValue.FormKey;
                        comparisonValue = !globalFk.IsNull
                            ? $"{globalFk.ModKey.FileName}|{globalFk.IDString()}"
                            : "";
                    }
                    else
                    {
                        Debug.WriteLine($"Unknown condition type: {cond.GetType().Name}");
                        continue;
                    }

                    string extra = ""; // currently unused
                    string runOn = data.RunOnType.ToString();

                    // HasPerk
                    if (data is IHasPerkConditionDataGetter perkData)
                    {
                        var perkItem = perkData.Perk;
                        string target = "";

                        if (perkItem.UsesLink())
                        {
                            var fk = perkItem.Link.FormKey;
                            target = $"{fk.ModKey.FileName}|{fk.IDString()}";
                        }

                        parsedCobj.ConditionRows.Add(new object[] { key, extra, runOn, "HasPerk", target, comparisonValue });
                        continue;
                    }

                    // GetIsSex
                    if (data is IGetIsSexConditionDataGetter sexData)
                    {
                        string target = sexData.MaleFemaleGender.ToString(); // Male / Female
                        parsedCobj.ConditionRows.Add(new object[] { key, extra, runOn, "GetIsSex", target, comparisonValue }); // sollte 1 sein
                        continue;
                    }

                    // GetActorValue
                    if (data is IGetActorValueConditionDataGetter avData)
                    {
                        string target = avData.ActorValue.ToString();
                        parsedCobj.ConditionRows.Add(new object[] { key, extra, runOn, "GetActorValue", target, comparisonValue });
                        continue;
                    }

                    // GetLevel
                    if (data is IGetLevelConditionDataGetter)
                    {
                        parsedCobj.ConditionRows.Add(new object[] { key, extra, runOn, "GetLevel", "", comparisonValue }); // no Target
                        continue;
                    }

                    // GetStageDone
                    if (data is IGetStageDoneConditionDataGetter stageData)
                    {
                        string target = "";
                        if (stageData.Quest.UsesLink())
                        {
                            var fk = stageData.Quest.Link.FormKey;
                            target = $"{fk.ModKey.FileName}|{fk.IDString()}";
                        }

                        parsedCobj.ConditionRows.Add(new object[] { key, extra, runOn, "GetStageDone", target, stageData.Stage.ToString() });
                        continue;
                    }
                }

                result.Cobjs.Add(parsedCobj);
            }

            // ENCHANTMENTS
            foreach (var ench in mod.ObjectEffects.Records)
            {
                string enchKey = $"{pluginName}|{ench.FormKey.IDString()}";
                var parsedEnch = new ParsedEnchantment();

                // WornRestrictions (FLST)
                string listKey = "";
                if (ench.WornRestrictions != null)
                {
                    var fk = ench.WornRestrictions.FormKey;
                    listKey = $"{fk.ModKey.FileName}|{fk.IDString()}";

                    if (mod.FormLists.TryGetValue(fk, out var flst))
                    {
                        foreach (var entry in flst.Items)
                        {
                            var kwfk = entry.FormKey;
                            string kwKey = $"{kwfk.ModKey.FileName}|{kwfk.IDString()}";
                            parsedEnch.WornRestrictionKeywordRows.Add(new object[] { listKey, kwKey });
                        }
                    }
                }

                parsedEnch.Values = new object[]
                {
                    enchKey,
                    ench.EditorID ?? "",
                    ench.Name?.ToString() ?? "",
                    ench.CastType.ToString(),
                    ench.TargetType.ToString(),
                    (float)ench.EnchantmentCost,
                    listKey
                };

                // Effects
                foreach (var eff in ench.Effects)
                {
                    var fk = eff.BaseEffect.FormKey;
                    string mgefKey = $"{fk.ModKey.FileName}|{fk.IDString()}";

                    string editorid = "";
                    string name = "";

                    // MagicEffect
                    if (mod.MagicEffects.TryGetValue(fk, out var magicEffect))
                    {
                        editorid = magicEffect.EditorID ?? "";
                        name = magicEffect.Name?.ToString() ?? "";
                    }

                    parsedEnch.EffectRows.Add(new object[]
                    {
                        enchKey, mgefKey, editorid, name,
                        eff.Data?.Magnitude ?? 0,
                        eff.Data?.Duration ?? 0,
                        eff.Data?.Area ?? 0
                    });
                }

                result.Enchantments.Add(parsedEnch);
            }

            // CONTAINER + LVLI
            foreach (var container in mod.Containers.Records)
            {
                string containerKey = KeyFactory.BuildMasterKey(container.FormKey);
                var parsedContainer = new ParsedContainer
                {
                    Values = new object[] { containerKey, container.EditorID ?? "" }
                };

                // LVLI inside Container
                if (container.Items != null)
                {
                    foreach (var entry in container.Items)
                    {
                        var fk = entry.Item.Item.FormKey;
                        string lvliKey = KeyFactory.BuildMasterKey(fk);

                        // Check whether the LVLI exists
                        var lvliRecord = _formIDDB.GetByKey(lvliKey);
                        if (lvliRecord != null && lvliRecord.Type == "LVLi")
                        {
                            parsedContainer.LvliRows.Add(new object[] { containerKey, lvliKey, lvliRecord.Name });
                        }
                    }
                }

                result.Containers.Add(parsedContainer);
            }

            // MAGIC EFFECTS
            foreach (var mgef in mod.MagicEffects.Records)
            {
                string mgefKey = $"{pluginName}|{mgef.FormKey.IDString()}";

                bool hasMagnitude = !mgef.Flags.HasFlag(MagicEffect.Flag.NoMagnitude);
                bool hasDuration = !mgef.Flags.HasFlag(MagicEffect.Flag.NoDuration);
                int hasArea = (mgef.TargetType == TargetType.Aimed || mgef.TargetType == TargetType.TargetLocation) ? 1 : 0;

                result.MagicEffectRows.Add(new object[]
                {
                    mgefKey,
                    mgef.EditorID ?? "",
                    mgef.Name?.ToString() ?? "",
                    hasMagnitude ? 1 : 0,
                    hasDuration ? 1 : 0,
                    hasArea,
                    mgef.CastType.ToString(),
                    mgef.TargetType.ToString()
                });
            }

            return result;
        }
        public struct ParsedIngredient
        {
            public string Plugin;
            public string FormID;
            public int Count;
        }
        public static ParsedIngredient ParseIngredient(string raw)
        {
            // raw = "Skyrim.esm|05ACE5*2"

            var parts = raw.Split('*');
            string key = parts[0];          // "Skyrim.esm|05ACE5"
            int count = parts.Length > 1 ? int.Parse(parts[1]) : 1;

            var keyParts = key.Split('|');
            string plugin = keyParts[0];
            string formID = keyParts[1];

            return new ParsedIngredient
            {
                Plugin = plugin,
                FormID = formID,
                Count = count
            };
        }

        // CreateTables() only CREATE TABLE IF NOT EXISTS's; this method ALTER TABLEs in any column
        // a table is still missing, via ADD COLUMN with a DEFAULT. Idempotent, safe on every scan —
        // deliberately not a DROP+rebuild, since that would wipe every IsEdited*/Original value (the
        // only place manual user edits live) and destroy user-created COBJ recipes (Original=0,
        // never present in any scanned plugin) on every single rescan.
        private static void EnsureSchema(SqliteConnection connection)
        {
            AddColumnIfMissing(connection, "Armor", "Active", "INTEGER NOT NULL DEFAULT 1");
            AddColumnIfMissing(connection, "Weapons", "Active", "INTEGER NOT NULL DEFAULT 1");
            AddColumnIfMissing(connection, "COBJ", "Active", "INTEGER NOT NULL DEFAULT 1");
            AddColumnIfMissing(connection, "COBJ", "ConditionsEdited", "INTEGER NOT NULL DEFAULT 0");
            AddColumnIfMissing(connection, "Enchantments", "Active", "INTEGER NOT NULL DEFAULT 1");
            AddColumnIfMissing(connection, "Enchantments", "EffectsEdited", "INTEGER NOT NULL DEFAULT 0");
            AddColumnIfMissing(connection, "Enchantments", "KeywordsEdited", "INTEGER NOT NULL DEFAULT 0");
            AddColumnIfMissing(connection, "Container", "Active", "INTEGER NOT NULL DEFAULT 1");
            AddColumnIfMissing(connection, "MagicEffects", "Active", "INTEGER NOT NULL DEFAULT 1");

            // LastChanged/LastPatched are only meaningful on the 4 tables with live IsEdited* tracking.
            // LastChanged is set by every user-edit write path (UpdateField, SaveCOBJConditions,
            // SaveEnchantmentEffects, SaveWornRestrictionKeywords, CreateNewCOBJRecordForItem) and is
            // deliberately excluded from the UPSERT column lists below so a rescan never touches it.
            // LastPatched is a schema placeholder for a future patch-export feature — not written yet.
            foreach (var table in new[] { "Armor", "Weapons", "COBJ", "Enchantments" })
            {
                AddColumnIfMissing(connection, table, "LastChanged", "TEXT");
                AddColumnIfMissing(connection, table, "LastPatched", "TEXT");
            }

            // ContainerString used to be written directly to the base column (UpdateArmor/
            // WeaponContainerString), bypassing the shadow-column protection every other editable
            // field has — it never set IsEdited/LastChanged, so a container assignment alone never
            // marked the item as "edited" and Import/Export silently never saw it. Brought in line
            // with the rest of Armor/Weapons here.
            AddColumnIfMissing(connection, "Armor", "IsEditedContainerString", "TEXT");
            AddColumnIfMissing(connection, "Weapons", "IsEditedContainerString", "TEXT");
        }

        private static void AddColumnIfMissing(SqliteConnection connection, string table, string column, string columnDefSql)
        {
            // sqlite_master only has an entry for a table once it exists — on a brand-new DB file,
            // CreateTables() (called right after this) creates it with the column already present, so
            // there's nothing to migrate and this is a safe no-op.
            using (var existsCmd = connection.CreateCommand())
            {
                existsCmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=@table;";
                existsCmd.Parameters.AddWithValue("@table", table);
                if (Convert.ToInt64(existsCmd.ExecuteScalar()) == 0)
                    return;
            }

            using (var pragmaCmd = connection.CreateCommand())
            {
                pragmaCmd.CommandText = $"PRAGMA table_info({table});";
                using var reader = pragmaCmd.ExecuteReader();
                while (reader.Read())
                {
                    // column 1 = column name
                    if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                        return;
                }
            }

            using var alterCmd = connection.CreateCommand();
            alterCmd.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {columnDefSql};";
            alterCmd.ExecuteNonQuery();
        }

        private void CreateTables(SqliteConnection connection)
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText =
            @"
                CREATE TABLE IF NOT EXISTS Armor (
                    Key TEXT PRIMARY KEY,
                    EditorID TEXT NOT NULL,
                    Name TEXT,
                    Weight REAL,
                    Value INTEGER,
                    ArmorRating REAL,
                    BodySlotMask INTEGER,
                    Keywords TEXT,
                    ContainerString TEXT,

                    IsEditedName Text,
                    IsEditedWeight REAL,
                    IsEditedValue INTEGER,
                    IsEditedArmorRating REAL,
                    IsEditedBodySlotMask INTEGER,
                    IsEditedKeywords TEXT,
                    IsEditedContainerString TEXT,

                    IsEdited INTEGER DEFAULT 0,
                    Active INTEGER NOT NULL DEFAULT 1,
                    LastChanged TEXT,
                    LastPatched TEXT
                );

                CREATE TABLE IF NOT EXISTS Weapons (
                    Key TEXT PRIMARY KEY,
                    EditorID TEXT NOT NULL,
                    Name TEXT,
                    Weight REAL,
                    Value INTEGER,
                    Damage INTEGER,
                    Speed REAL,
                    Reach REAL,
                    Stagger REAL,
                    Keywords TEXT,
                    ContainerString TEXT,

                    IsEditedName Text,
                    IsEditedWeight REAL,
                    IsEditedValue INTEGER,
                    IsEditedDamage INTEGER,
                    IsEditedSpeed REAL,
                    IsEditedReach REAL,
                    IsEditedStagger REAL,
                    IsEditedKeywords TEXT,
                    IsEditedContainerString TEXT,

                    IsEdited INTEGER DEFAULT 0,
                    Active INTEGER NOT NULL DEFAULT 1,
                    LastChanged TEXT,
                    LastPatched TEXT
                );

                CREATE TABLE IF NOT EXISTS COBJ (
                    Key TEXT PRIMARY KEY,
                    Original INTEGER NOT NULL DEFAULT 1,
                    Name TEXT NOT NULL,
                    CreatedItem TEXT NOT NULL,
                    WorkbenchKeyword TEXT,
                    Ingredients TEXT,

                    IsEditedName TEXT,
                    IsEditedCreatedItem TEXT,
                    IsEditedWorkbenchKeyword TEXT,
                    IsEditedIngredients TEXT,

                    IsEdited INTEGER DEFAULT 0,
                    Active INTEGER NOT NULL DEFAULT 1,
                    ConditionsEdited INTEGER NOT NULL DEFAULT 0,
                    LastChanged TEXT,
                    LastPatched TEXT
                );

                CREATE TABLE IF NOT EXISTS COBJ_Conditions (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    COBJKey TEXT NOT NULL,
                    ConditionType TEXT NOT NULL,

                    Target TEXT,
                    Value TEXT,
                    Extra TEXT,
                    RunOn TEXT,

                    IsEditedTarget TEXT,
                    IsEditedValue TEXT,
                    IsEditedExtra TEXT,
                    IsEditedRunOn TEXT,

                    IsEdited INTEGER DEFAULT 0,

                    FOREIGN KEY (COBJKey) REFERENCES COBJ(Key)
                );

                -- Lazily-populated, permanently-frozen snapshot of a COBJ's conditions as they
                -- looked right before the first user edit (see SaveCOBJConditions) - COBJ_Conditions
                -- itself is destructively DELETE+INSERTed on every save, so there is nothing else to
                -- revert to once the user has edited a condition. No Id/IsEdited* columns: this table
                -- is only ever bulk-replaced per COBJKey, never updated in place.
                CREATE TABLE IF NOT EXISTS COBJ_Conditions_Original (
                    COBJKey TEXT NOT NULL,
                    ConditionType TEXT NOT NULL,
                    Target TEXT,
                    Value TEXT,
                    Extra TEXT,
                    RunOn TEXT
                );


                CREATE TABLE IF NOT EXISTS Enchantments (
                    Key TEXT PRIMARY KEY,
                    EditorID TEXT NOT NULL,
                    Name TEXT,
                    CastType TEXT,
                    TargetType TEXT,
                    EnchantmentCost REAL,
                    WornRestrictionListKey TEXT,

                    IsEditedName TEXT,
                    IsEditedCastType TEXT,
                    IsEditedTargetType TEXT,
                    IsEditedEnchantmentCost REAL,
                    IsEditedWornRestrictionListKey TEXT,

                    IsEdited INTEGER DEFAULT 0,
                    Active INTEGER NOT NULL DEFAULT 1,
                    EffectsEdited INTEGER NOT NULL DEFAULT 0,
                    KeywordsEdited INTEGER NOT NULL DEFAULT 0,
                    LastChanged TEXT,
                    LastPatched TEXT
                );

                CREATE TABLE IF NOT EXISTS EnchantmentEffects (
                    EnchantmentKey TEXT NOT NULL,
                    MagicEffectKey TEXT NOT NULL,
                    EditorID TEXT,
                    Name TEXT,
                    Magnitude REAL,
                    Duration INTEGER,
                    Area INTEGER,

                    IsEditedMagnitude REAL,
                    IsEditedDuration INTEGER,
                    IsEditedArea INTEGER,

                    IsEdited INTEGER DEFAULT 0,

                    PRIMARY KEY (EnchantmentKey, MagicEffectKey)
                );

                -- Same lazy-snapshot pattern as COBJ_Conditions_Original - see that table's comment.
                CREATE TABLE IF NOT EXISTS EnchantmentEffects_Original (
                    EnchantmentKey TEXT NOT NULL,
                    MagicEffectKey TEXT NOT NULL,
                    EditorID TEXT,
                    Name TEXT,
                    Magnitude REAL,
                    Duration INTEGER,
                    Area INTEGER
                );

                CREATE TABLE IF NOT EXISTS WornRestrictionKeywords (
                    ListKey TEXT NOT NULL,
                    KeywordKey TEXT NOT NULL,

                    IsEditedKeywordKey TEXT,

                    IsEdited INTEGER DEFAULT 0,

                    PRIMARY KEY (ListKey, KeywordKey)
                );

                -- Same lazy-snapshot pattern as COBJ_Conditions_Original - see that table's comment.
                CREATE TABLE IF NOT EXISTS WornRestrictionKeywords_Original (
                    ListKey TEXT NOT NULL,
                    KeywordKey TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS Container (
                    ContainerKey TEXT PRIMARY KEY,
                    Name TEXT NOT NULL,
                    Active INTEGER NOT NULL DEFAULT 1
                );

                CREATE TABLE IF NOT EXISTS ContainerLVLI (
                    ContainerKey TEXT NOT NULL,
                    LVLiKey TEXT NOT NULL,
                    LVLiName TEXT,
                    PRIMARY KEY (ContainerKey, LVLiKey)
                );

                CREATE TABLE IF NOT EXISTS MagicEffects (
                    Key TEXT PRIMARY KEY,
                    EditorID TEXT,
                    Name TEXT NOT NULL,
                    HasMagnitude INTEGER,
                    HasDuration INTEGER,
                    HasArea INTEGER,
                    CastType TEXT,
                    TargetType TEXT,
                    Active INTEGER NOT NULL DEFAULT 1
                );
            ";
            cmd.ExecuteNonQuery();

        }

        // Single-row UPSERT for the 6 "parent" tables — same semantics as PrepareUpsertBatch, used
        // for the tail of rows that doesn't fill a full batch. Driven by the same ColumnNames/
        // ParamNames arrays as the batch versions, so there's exactly one place that knows each
        // table's real columns (an "INSERT OR REPLACE" here would delete+reinsert the whole row,
        // wiping IsEdited*/Original on every conflict).
        private static SqliteCommand PrepareUpsert(SqliteConnection connection, string table, string[] columnNames, string[] paramNames)
        {
            var columns = string.Join(", ", columnNames) + ", Active";
            var values = string.Join(", ", paramNames) + ", 1";
            var updateSet = string.Join(", ", columnNames.Skip(1).Select(c => $"{c} = excluded.{c}")) + ", Active = 1";

            var cmd = connection.CreateCommand();
            cmd.CommandText =
                $"INSERT INTO {table} ({columns}) VALUES ({values}) " +
                $"ON CONFLICT({columnNames[0]}) DO UPDATE SET {updateSet}";

            foreach (var p in paramNames)
                cmd.Parameters.Add(new SqliteParameter(p, DBNull.Value));

            return cmd;
        }

        // Single-row plain INSERT for the 4 child tables — see PrepareInsertBatch for why "plain"
        // (no OR REPLACE) is correct here: callers always DELETE the relevant parent keys' rows first.
        private static SqliteCommand PrepareInsert(SqliteConnection connection, string table, string[] columnNames, string[] paramNames)
        {
            var columns = string.Join(", ", columnNames);
            var values = string.Join(", ", paramNames);

            var cmd = connection.CreateCommand();
            cmd.CommandText = $"INSERT INTO {table} ({columns}) VALUES ({values})";

            foreach (var p in paramNames)
                cmd.Parameters.Add(new SqliteParameter(p, DBNull.Value));

            return cmd;
        }

        // ============================
        //        LOAD FROM DB
        // ============================

        private List<ArmorRecord> LoadArmor()
        {
            var list = new List<ArmorRecord>();

            if (!File.Exists(ItemdbPath))
                return list;

            using var connection = new SqliteConnection($"Data Source={ItemdbPath}");
            connection.Open();

            using var cmd = connection.CreateCommand();
            cmd.CommandText =
                @"SELECT 
                    Key,
                    EditorID,
                    CASE WHEN IsEdited = 1 AND IsEditedName IS NOT NULL 
                         THEN IsEditedName 
                         ELSE Name 
                    END AS Name,
                    CASE WHEN IsEdited = 1 AND IsEditedWeight IS NOT NULL 
                         THEN IsEditedWeight 
                         ELSE Weight 
                    END AS Weight,
                    CASE WHEN IsEdited = 1 AND IsEditedValue IS NOT NULL 
                         THEN IsEditedValue 
                         ELSE Value 
                    END AS Value,
                    CASE WHEN IsEdited = 1 AND IsEditedArmorRating IS NOT NULL 
                         THEN IsEditedArmorRating 
                         ELSE ArmorRating 
                    END AS ArmorRating,
                    CASE WHEN IsEdited = 1 AND IsEditedBodySlotMask IS NOT NULL 
                         THEN IsEditedBodySlotMask
                         ELSE BodySlotMask
                    END AS BodySlotMask,
                    CASE WHEN IsEdited = 1 AND IsEditedKeywords IS NOT NULL
                         THEN IsEditedKeywords
                         ELSE Keywords
                    END AS Keywords,
                    CASE WHEN IsEdited = 1 AND IsEditedContainerString IS NOT NULL
                         THEN IsEditedContainerString
                         ELSE ContainerString
                    END AS ContainerString
                FROM Armor WHERE Active = 1;";

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var keywordsCsv = reader.IsDBNull(7) ? "" : reader.GetString(7);
                var keywords = string.IsNullOrWhiteSpace(keywordsCsv)
                    ? new List<string>()
                    : keywordsCsv.Split(',').ToList();

                list.Add(new ArmorRecord
                {
                    Key = reader.GetString(0),
                    EditorID = reader.GetString(1),
                    Name = reader.GetString(2),
                    Weight = reader.IsDBNull(3) ? 0f : (float)reader.GetDouble(3),
                    Value = reader.IsDBNull(4) ? 0 : reader.GetInt32(4),
                    ArmorRating = reader.IsDBNull(5) ? 0f : (float)reader.GetDouble(5),

                    // NEW
                    BodySlotMask = reader.IsDBNull(6) ? 0u : (uint)reader.GetInt64(6),

                    Keywords = keywords,
                    ContainerString = reader.IsDBNull(8) ? "{}" : reader.GetString(8),
                });
            }

            return list;
        }


        private List<WeaponRecord> LoadWeapons()
        {
            var list = new List<WeaponRecord>();

            if (!File.Exists(ItemdbPath))
                return list;

            using var connection = new SqliteConnection($"Data Source={ItemdbPath}");
            connection.Open();

            using var cmd = connection.CreateCommand();
            cmd.CommandText =
                @"SELECT 
                    Key,
                    EditorID,
                    CASE WHEN IsEdited = 1 AND IsEditedName IS NOT NULL 
                         THEN IsEditedName 
                         ELSE Name 
                    END AS Name,
                    CASE WHEN IsEdited = 1 AND IsEditedWeight IS NOT NULL 
                         THEN IsEditedWeight 
                         ELSE Weight 
                    END AS Weight,
                    CASE WHEN IsEdited = 1 AND IsEditedValue IS NOT NULL 
                         THEN IsEditedValue 
                         ELSE Value 
                    END AS Value,
                    CASE WHEN IsEdited = 1 AND IsEditedDamage IS NOT NULL 
                         THEN IsEditedDamage 
                         ELSE Damage
                    END AS Damage,
                    CASE WHEN IsEdited = 1 AND IsEditedSpeed IS NOT NULL 
                         THEN IsEditedSpeed
                         ELSE Speed 
                    END AS Speed,
                    CASE WHEN IsEdited = 1 AND IsEditedReach IS NOT NULL 
                         THEN IsEditedReach
                         ELSE Reach 
                    END AS Reach,
                    CASE WHEN IsEdited = 1 AND IsEditedStagger IS NOT NULL 
                         THEN IsEditedStagger
                         ELSE Stagger 
                    END AS Stagger,
                    CASE WHEN IsEdited = 1 AND IsEditedKeywords IS NOT NULL
                         THEN IsEditedKeywords
                         ELSE Keywords
                    END AS Keywords,
                    CASE WHEN IsEdited = 1 AND IsEditedContainerString IS NOT NULL
                         THEN IsEditedContainerString
                         ELSE ContainerString
                    END AS ContainerString
                FROM Weapons WHERE Active = 1;";

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var keywordsCsv = reader.IsDBNull(9) ? "" : reader.GetString(9);
                var keywords = string.IsNullOrWhiteSpace(keywordsCsv)
                    ? new List<string>()
                    : keywordsCsv.Split(',').ToList();

                list.Add(new WeaponRecord
                {
                    Key = reader.GetString(0),
                    EditorID = reader.GetString(1),
                    Name = reader.GetString(2),
                    Weight = reader.IsDBNull(3) ? 0f : (float)reader.GetDouble(3),
                    Value = reader.IsDBNull(4) ? 0 : reader.GetInt32(4),
                    Damage = reader.IsDBNull(5) ? 0 : reader.GetInt32(5),

                    // NEW
                    Speed = reader.IsDBNull(6) ? 0f : (float)reader.GetDouble(6),
                    Reach = reader.IsDBNull(7) ? 0f : (float)reader.GetDouble(7),
                    Stagger = reader.IsDBNull(8) ? 0f : (float)reader.GetDouble(8),

                    Keywords = keywords,
                    ContainerString = reader.IsDBNull(10) ? "{}" : reader.GetString(10)
                });
            }

            return list;
        }


        private List<COBJRecord> LoadCOBJ()
        {
            var list = new List<COBJRecord>();

            if (!File.Exists(ItemdbPath))
            {
                return list;
            }


            using var connection = new SqliteConnection($"Data Source={ItemdbPath}");
            connection.Open();

            using var cmd = connection.CreateCommand();
            cmd.CommandText =
               @"SELECT 
                    Key,
                    Original,
                    CASE WHEN IsEdited = 1 AND IsEditedName IS NOT NULL 
                         THEN IsEditedName 
                         ELSE Name 
                    END AS Name,
                    CASE WHEN IsEdited = 1 AND IsEditedCreatedItem IS NOT NULL 
                         THEN IsEditedCreatedItem 
                         ELSE CreatedItem 
                    END AS CreatedItem,
                    CASE WHEN IsEdited = 1 AND IsEditedWorkbenchKeyword IS NOT NULL 
                         THEN IsEditedWorkbenchKeyword 
                         ELSE WorkbenchKeyword 
                    END AS WorkbenchKeyword,
                    CASE WHEN IsEdited = 1 AND IsEditedIngredients IS NOT NULL 
                         THEN IsEditedIngredients
                         ELSE Ingredients 
                    END AS Ingredients
                FROM COBJ WHERE Active = 1;";

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var ingredientsCsv = reader.IsDBNull(5) ? "" : reader.GetString(5);
                var ingredients = string.IsNullOrWhiteSpace(ingredientsCsv)
                    ? new List<string>()
                    : ingredientsCsv.Split(',', StringSplitOptions.RemoveEmptyEntries).ToList();

                list.Add(new COBJRecord
                {
                    Key = reader.GetString(0),
                    Original = reader.GetInt32(1),
                    Name = reader.GetString(2),
                    CreatedItemKey = reader.GetString(3),
                    WorkbenchKeywordKey = reader.IsDBNull(4) ? "" : reader.GetString(4),
                    IngredientKeys = ingredients,
                });
            }

            return list;
        }

        private List<COBJConditionRecord> LoadCOBJConditions()
        {
            var list = new List<COBJConditionRecord>();
            if (!File.Exists(ItemdbPath))
            {
                return list;
            }
            using var connection = new SqliteConnection($"Data Source={ItemdbPath}");
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText =
                @"SELECT 
                    COBJKey,
                    ConditionType,
					CASE WHEN IsEdited = 1 AND IsEditedTarget IS NOT NULL 
                         THEN IsEditedTarget 
                         ELSE Target
                    END AS Target,
                    CASE WHEN IsEdited = 1 AND IsEditedValue IS NOT NULL 
                         THEN IsEditedValue
                         ELSE Value 
                    END AS Value,
                    CASE WHEN IsEdited = 1 AND IsEditedExtra IS NOT NULL 
                         THEN IsEditedExtra
                         ELSE Extra 
                    END AS Extra,
                    CASE WHEN IsEdited = 1 AND IsEditedRunOn IS NOT NULL 
                         THEN IsEditedRunOn
                         ELSE RunOn 
                    END AS RunOn
                FROM COBJ_Conditions;";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                list.Add(new COBJConditionRecord
                {
                    COBJKey = reader.GetString(0),
                    ConditionType = reader.GetString(1),
                    Target = reader.IsDBNull(2) ? "" : reader.GetString(2),
                    Value = reader.IsDBNull(3) ? "" : reader.GetString(3),
                    Extra = reader.IsDBNull(4) ? "" : reader.GetString(4),
                    RunOn = reader.IsDBNull(5) ? "" : reader.GetString(5)
                });
            }
            return list;
        }

        // -------------------------------------------------
        // COBJ_Conditions: query by COBJ key
        // -------------------------------------------------
        public List<COBJConditionRecord> GetCOBJConditions(string cobjKey)
        {
            LoadCache();
            return _cobjCondtionsCache
                .Where(c => c.COBJKey == cobjKey)
                .ToList();
        }

        // -------------------------------------------------
        // COBJ_Conditions: replace all conditions for a COBJ
        // -------------------------------------------------
        public void SaveCOBJConditions(string cobjKey, List<COBJConditionRecord> conditions)
        {
            using var connection = new SqliteConnection(ConnString);
            connection.Open();
            using var transaction = connection.BeginTransaction();

            // First edit ever for this COBJ's conditions: freeze whatever is currently in
            // COBJ_Conditions (still pristine - nothing has touched it before now) into
            // COBJ_Conditions_Original before it gets destroyed by the delete+insert below. Gated on
            // the ConditionsEdited flag rather than "does _Original already have rows" - a recipe
            // whose true original is zero conditions would otherwise look indistinguishable from
            // "never snapshotted yet" and get re-snapshotted (with already-edited data) on every
            // subsequent save. Later edits are a no-op here since the flag is already 1 by then, so
            // this permanently captures "what it looked like right before the first edit" - see
            // ResetCOBJConditions and GetOriginalCOBJConditions (which falls back to the live table
            // when ConditionsEdited is still 0, for the same "empty original" reason).
            using (var checkCmd = connection.CreateCommand())
            {
                checkCmd.Transaction = transaction;
                checkCmd.CommandText = "SELECT ConditionsEdited FROM COBJ WHERE Key = @cobjKey";
                checkCmd.Parameters.AddWithValue("@cobjKey", cobjKey);
                var flagResult = checkCmd.ExecuteScalar();
                bool alreadyEdited = flagResult != null && flagResult != DBNull.Value && Convert.ToInt64(flagResult) == 1;
                if (!alreadyEdited)
                {
                    using var snapshotCmd = connection.CreateCommand();
                    snapshotCmd.Transaction = transaction;
                    snapshotCmd.CommandText = @"INSERT INTO COBJ_Conditions_Original (COBJKey, ConditionType, Target, Value, Extra, RunOn)
                                                 SELECT COBJKey, ConditionType, Target, Value, Extra, RunOn
                                                 FROM COBJ_Conditions WHERE COBJKey = @cobjKey";
                    snapshotCmd.Parameters.AddWithValue("@cobjKey", cobjKey);
                    snapshotCmd.ExecuteNonQuery();
                }
            }

            using (var deleteCmd = connection.CreateCommand())
            {
                deleteCmd.Transaction = transaction;
                deleteCmd.CommandText = "DELETE FROM COBJ_Conditions WHERE COBJKey = @cobjKey";
                deleteCmd.Parameters.AddWithValue("@cobjKey", cobjKey);
                deleteCmd.ExecuteNonQuery();
            }

            if (conditions != null)
            {
                using var insertCmd = PrepareInsert(connection, "COBJ_Conditions", CobjConditionColumnNames, CobjConditionParamNames);
                insertCmd.Transaction = transaction;

                foreach (var cond in conditions)
                {
                    insertCmd.Parameters["@cobjKey"].Value = cobjKey;
                    insertCmd.Parameters["@type"].Value = cond.ConditionType ?? "";
                    insertCmd.Parameters["@target"].Value = cond.Target ?? "";
                    insertCmd.Parameters["@value"].Value = cond.Value ?? "";
                    insertCmd.Parameters["@extra"].Value = cond.Extra ?? "";
                    insertCmd.Parameters["@runOn"].Value = cond.RunOn ?? "";
                    insertCmd.ExecuteNonQuery();
                }
            }

            // Marks this COBJ's conditions as user-edited so a rescan leaves them untouched instead
            // of silently overwriting them with whatever the plugin scan finds (see PutIntoDataBank).
            using (var flagCmd = connection.CreateCommand())
            {
                flagCmd.Transaction = transaction;
                flagCmd.CommandText = "UPDATE COBJ SET ConditionsEdited = 1, LastChanged = @now WHERE Key = @cobjKey";
                flagCmd.Parameters.AddWithValue("@cobjKey", cobjKey);
                flagCmd.Parameters.AddWithValue("@now", NowIso());
                flagCmd.ExecuteNonQuery();
            }

            transaction.Commit();

            // Refresh in-memory cache for this COBJ
            _cobjCondtionsCache.RemoveAll(c => c.COBJKey == cobjKey);
            if (conditions != null)
                _cobjCondtionsCache.AddRange(conditions);

            if (_cobjByKey.TryGetValue(cobjKey, out var cobj))
                cobj.Conditions = GetCOBJConditions(cobjKey);
        }

        // -------------------------------------------------
        // COBJ_Conditions: Reset - see COBJ_Conditions_Original's schema comment and
        // SaveCOBJConditions' lazy-snapshot step above for how the pristine copy gets there.
        // -------------------------------------------------
        public List<COBJConditionRecord> GetOriginalCOBJConditions(string cobjKey)
        {
            var list = new List<COBJConditionRecord>();
            using var connection = new SqliteConnection(ConnString);
            connection.Open();

            // Conditions have never been edited yet: COBJ_Conditions_Original is only populated
            // lazily on the first edit (see SaveCOBJConditions), so it's empty here regardless of
            // whether the true original is "no conditions" or just "not snapshotted yet" - the live
            // table is still the pristine state in that case, so read from it instead.
            bool conditionsEdited;
            using (var flagCmd = connection.CreateCommand())
            {
                flagCmd.CommandText = "SELECT ConditionsEdited FROM COBJ WHERE Key = @key";
                flagCmd.Parameters.AddWithValue("@key", cobjKey);
                var flagResult = flagCmd.ExecuteScalar();
                conditionsEdited = flagResult != null && flagResult != DBNull.Value && Convert.ToInt64(flagResult) == 1;
            }

            var table = conditionsEdited ? "COBJ_Conditions_Original" : "COBJ_Conditions";
            using var cmd = connection.CreateCommand();
            cmd.CommandText = $"SELECT ConditionType, Target, Value, Extra, RunOn FROM {table} WHERE COBJKey = @key";
            cmd.Parameters.AddWithValue("@key", cobjKey);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                list.Add(new COBJConditionRecord
                {
                    COBJKey = cobjKey,
                    ConditionType = reader.IsDBNull(0) ? "" : reader.GetString(0),
                    Target = reader.IsDBNull(1) ? "" : reader.GetString(1),
                    Value = reader.IsDBNull(2) ? "" : reader.GetString(2),
                    Extra = reader.IsDBNull(3) ? "" : reader.GetString(3),
                    RunOn = reader.IsDBNull(4) ? "" : reader.GetString(4),
                });
            }
            return list;
        }

        public void ResetCOBJConditions(string cobjKey)
        {
            try
            {
                using var connection = new SqliteConnection(ConnString);
                connection.Open();
                using var transaction = connection.BeginTransaction();

                // Callers (ResetCraftingRecipeEdits/ResetTemperRecipeEdits) call this unconditionally
                // whenever the user resets a recipe, even if Conditions specifically were never
                // touched - in which case COBJ_Conditions_Original was never populated, and blindly
                // restoring "from" it would DELETE the still-pristine live conditions and replace them
                // with nothing. Bail out here if there's nothing to revert.
                using (var checkCmd = connection.CreateCommand())
                {
                    checkCmd.Transaction = transaction;
                    checkCmd.CommandText = "SELECT ConditionsEdited FROM COBJ WHERE Key = @key";
                    checkCmd.Parameters.AddWithValue("@key", cobjKey);
                    var flagResult = checkCmd.ExecuteScalar();
                    bool wasEdited = flagResult != null && flagResult != DBNull.Value && Convert.ToInt64(flagResult) == 1;
                    if (!wasEdited) return;
                }

                using (var deleteCmd = connection.CreateCommand())
                {
                    deleteCmd.Transaction = transaction;
                    deleteCmd.CommandText = "DELETE FROM COBJ_Conditions WHERE COBJKey = @key";
                    deleteCmd.Parameters.AddWithValue("@key", cobjKey);
                    deleteCmd.ExecuteNonQuery();
                }

                using (var restoreCmd = connection.CreateCommand())
                {
                    restoreCmd.Transaction = transaction;
                    restoreCmd.CommandText = @"INSERT INTO COBJ_Conditions (COBJKey, ConditionType, Target, Value, Extra, RunOn)
                                                SELECT COBJKey, ConditionType, Target, Value, Extra, RunOn
                                                FROM COBJ_Conditions_Original WHERE COBJKey = @key";
                    restoreCmd.Parameters.AddWithValue("@key", cobjKey);
                    restoreCmd.ExecuteNonQuery();
                }

                // Clear the snapshot now that it's been consumed - otherwise the next first-ever
                // edit after this reset would INSERT another copy alongside it (SaveCOBJConditions'
                // snapshot step only ever adds, never replaces), leaving stale duplicate rows that
                // GetOriginalCOBJConditions would then read back as if the condition existed twice.
                using (var clearCmd = connection.CreateCommand())
                {
                    clearCmd.Transaction = transaction;
                    clearCmd.CommandText = "DELETE FROM COBJ_Conditions_Original WHERE COBJKey = @key";
                    clearCmd.Parameters.AddWithValue("@key", cobjKey);
                    clearCmd.ExecuteNonQuery();
                }

                using (var flagCmd = connection.CreateCommand())
                {
                    flagCmd.Transaction = transaction;
                    flagCmd.CommandText = "UPDATE COBJ SET ConditionsEdited = 0, LastChanged = @now WHERE Key = @key";
                    flagCmd.Parameters.AddWithValue("@key", cobjKey);
                    flagCmd.Parameters.AddWithValue("@now", NowIso());
                    flagCmd.ExecuteNonQuery();
                }

                transaction.Commit();

                // Refresh in-memory cache for this COBJ (same as SaveCOBJConditions does)
                var restored = GetOriginalCOBJConditions(cobjKey);
                _cobjCondtionsCache.RemoveAll(c => c.COBJKey == cobjKey);
                _cobjCondtionsCache.AddRange(restored);
                if (_cobjByKey.TryGetValue(cobjKey, out var cobj))
                    cobj.Conditions = restored;
            }
            catch (Exception ex)
            {
                AppLogger.LogError($"ResetCOBJConditions failed (cobjKey={cobjKey})", ex);
                throw;
            }
        }

        private List<EnchantmentRecord> LoadEnchantments()
        {
            var list = new List<EnchantmentRecord>();
            if (!File.Exists(ItemdbPath)) return list;

            using var connection = new SqliteConnection($"Data Source={ItemdbPath}");
            connection.Open();

            using var cmd = connection.CreateCommand();
            cmd.CommandText =
               @"SELECT 
                    Key,
                    EditorID,
                    CASE WHEN IsEdited = 1 AND IsEditedName IS NOT NULL 
                         THEN IsEditedName 
                         ELSE Name 
                    END AS Name,
                    CASE WHEN IsEdited = 1 AND IsEditedCastType IS NOT NULL 
                         THEN IsEditedCastType 
                         ELSE CastType 
                    END AS CastType,
                    CASE WHEN IsEdited = 1 AND IsEditedTargetType IS NOT NULL 
                         THEN IsEditedTargetType 
                         ELSE TargetType 
                    END AS TargetType,
                    CASE WHEN IsEdited = 1 AND IsEditedEnchantmentCost IS NOT NULL 
                         THEN IsEditedEnchantmentCost
                         ELSE EnchantmentCost 
                    END AS EnchantmentCost,
                    CASE WHEN IsEdited = 1 AND IsEditedWornRestrictionListKey IS NOT NULL 
                         THEN IsEditedWornRestrictionListKey
                         ELSE WornRestrictionListKey 
                    END AS WornRestrictionListKey
                FROM Enchantments WHERE Active = 1;";

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                list.Add(new EnchantmentRecord
                {
                    Key = reader.GetString(0),
                    EditorID = reader.GetString(1),
                    Name = reader.GetString(2),
                    CastType = reader.IsDBNull(3) ? "" : reader.GetString(3),
                    TargetType = reader.IsDBNull(4) ? "" : reader.GetString(4),
                    EnchantmentCost = reader.IsDBNull(5) ? 0f : (float)reader.GetDouble(5),
                    WornRestrictionListKey = reader.IsDBNull(6) ? "" : reader.GetString(6)
                });
            }

            return list;
        }


        private List<EnchantmentEffectRecord> LoadEnchantmentEffects()
        {
            var list = new List<EnchantmentEffectRecord>();
            if (!File.Exists(ItemdbPath)) return list;

            using var connection = new SqliteConnection($"Data Source={ItemdbPath}");
            connection.Open();

            using var cmd = connection.CreateCommand();
            cmd.CommandText =
                @"SELECT 
                    EnchantmentKey,
                    MagicEffectKey,
                    EditorID,
                    Name,
                    CASE WHEN IsEdited = 1 AND IsEditedMagnitude IS NOT NULL 
                         THEN IsEditedMagnitude 
                         ELSE Magnitude 
                    END AS Magnitude,
                    CASE WHEN IsEdited = 1 AND IsEditedDuration IS NOT NULL 
                         THEN IsEditedDuration 
                         ELSE Duration 
                    END AS Duration,
                    CASE WHEN IsEdited = 1 AND IsEditedArea IS NOT NULL 
                         THEN IsEditedArea 
                         ELSE Area 
                    END AS Area
                FROM EnchantmentEffects;";


            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                list.Add(new EnchantmentEffectRecord
                {
                    EnchantmentKey = reader.GetString(0),
                    MagicEffectKey = reader.GetString(1),
                    EditorID = reader.GetString(2),
                    Name = reader.GetString(3),
                    Magnitude = reader.IsDBNull(4) ? 0f : (float)reader.GetDouble(4),
                    Duration = reader.IsDBNull(5) ? 0 : reader.GetInt32(5),
                    Area = reader.IsDBNull(6) ? 0 : reader.GetInt32(6)
                });
            }

            return list;
        }

        private Dictionary<string, List<string>> LoadWornRestrictionKeywords()
        {
            var dict = new Dictionary<string, List<string>>();
            if (!File.Exists(ItemdbPath)) return dict;

            using var connection = new SqliteConnection($"Data Source={ItemdbPath}");
            connection.Open();

            using var cmd = connection.CreateCommand();
            cmd.CommandText =
                @"SELECT 
                    ListKey,
                    CASE WHEN IsEdited = 1 AND IsEditedKeywordKey IS NOT NULL 
                         THEN IsEditedKeywordKey 
                         ELSE KeywordKey  
                    END AS KeywordKey
                FROM WornRestrictionKeywords;";


            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                string listKey = reader.GetString(0);
                string kw = reader.GetString(1);

                if (!dict.ContainsKey(listKey))
                    dict[listKey] = new List<string>();

                dict[listKey].Add(kw);
            }

            return dict;
        }

        private List<ContainerRecord> LoadContainer()
        {
            var list = new List<ContainerRecord>();

            if (!File.Exists(ItemdbPath))
                return list;

            using var connection = new SqliteConnection($"Data Source={ItemdbPath}");
            connection.Open();

            using var cmd = connection.CreateCommand();
            cmd.CommandText =
                @"SELECT 
                    ContainerKey,
                    Name
                FROM Container WHERE Active = 1;";

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                list.Add(new ContainerRecord
                {
                    ContainerKey = reader.GetString(0),
                    Name = reader.GetString(1)
                });
            }

            return list;
        }

        private List<ContainerLVLIRecord> LoadContainerLVLI()
        {
            var list = new List<ContainerLVLIRecord>();

            if (!File.Exists(ItemdbPath))
                return list;

            using var connection = new SqliteConnection($"Data Source={ItemdbPath}");
            connection.Open();

            using var cmd = connection.CreateCommand();
            cmd.CommandText =
                @"SELECT 
                    ContainerKey,
                    LVLiKey,
                    LVLiName
                FROM ContainerLVLI;";

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                list.Add(new ContainerLVLIRecord
                {
                    ContainerKey = reader.GetString(0),
                    LVLiKey = reader.GetString(1),
                    LVLiName = reader.IsDBNull(2) ? "" : reader.GetString(2)
                });
            }

            return list;
        }

        private List<MagicEffectsRecords> LoadMagicEffects()
        {
            var list = new List<MagicEffectsRecords>();

            if (!File.Exists(ItemdbPath))
                return list;

            using var connection = new SqliteConnection($"Data Source={ItemdbPath}");
            connection.Open();

            using var cmd = connection.CreateCommand();
            cmd.CommandText =
                @"SELECT 
                    Key,
                    EditorID,
                    Name,
                    HasMagnitude,
                    HasDuration,
                    HasArea,
                    CastType,
                    TargetType
                FROM MagicEffects WHERE Active = 1;";

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                list.Add(new MagicEffectsRecords
                {
                    Key = reader.GetString(0),
                    EditorID = reader.GetString(1),
                    Name = reader.GetString(2),
                    HasMagnitude = reader.GetBoolean(3),
                    HasDuration = reader.GetBoolean(4),
                    HasArea = reader.GetBoolean(5),
                    CastType = reader.GetString(6),
                    TargetType = reader.GetString(7)
                });
            }

            return list;
        }

        private string ExtractPlugin(string key)
        {
            int idx = key.IndexOf('|');
            return idx > 0 ? key[..idx] : key;
        }

        public IEnumerable<ArmorRecord> GetArmorByPlugin(string plugin)
        {
            LoadCache();
            return _armorCache.Where(a => ExtractPlugin(a.Key)
                .Equals(plugin, StringComparison.OrdinalIgnoreCase));
        }


        public IEnumerable<WeaponRecord> GetWeaponsByPlugin(string plugin)
        {
            LoadCache();
            return _weaponCache.Where(w => ExtractPlugin(w.Key)
                .Equals(plugin, StringComparison.OrdinalIgnoreCase));
        }


        public IEnumerable<COBJRecord> GetCOBJByPlugin(string plugin)
        {
            LoadCache();
            return _cobjCache.Where(c => ExtractPlugin(c.Key)
                .Equals(plugin, StringComparison.OrdinalIgnoreCase));
        }

        public List<EnchantmentRecord> GetAllEnchantments()
        {
            // 1. load basisdata
            var enchantments = LoadEnchantments();

            // 2. load effect
            var effects = LoadEnchantmentEffects();

            // 3. load keyword
            var wornKeywords = LoadWornRestrictionKeywords();

            // 4. match effect
            foreach (var ench in enchantments)
            {
                var enchEffects = effects
                    .Where(e => e.EnchantmentKey == ench.Key);

                foreach (var eff in enchEffects)
                    ench.Effects.Add(eff);
            }

            // 5. match keyword
            foreach (var ench in enchantments)
            {
                if (!string.IsNullOrEmpty(ench.WornRestrictionListKey) &&
                    wornKeywords.TryGetValue(ench.WornRestrictionListKey, out var kws))
                {
                    foreach (var kw in kws)
                        ench.WornRestrictionKeywords.Add(kw);
                }
            }

            return enchantments;
        }


        // API for updating edited values in the database
        //
        // All single-field "IsEdited*" updates share the same shape (open connection, run one
        // UPDATE, mark IsEdited=1). Table/column are always internal constants (never user input),
        // so building the UPDATE text from them is safe. Centralizing here also means every one of
        // these ~20 call sites now gets logging on failure instead of silently swallowing DB errors.
        // Canonical ISO-8601 UTC round-trip timestamp used everywhere LastChanged is written or
        // compared (DB writes, JSON export, import conflict resolution) — must stay identical
        // everywhere or import conflict detection breaks (see plan risk 1).
        internal static string NowIso() => DateTime.UtcNow.ToString("o");

        private static void UpdateField(string table, string column, string key, object value)
        {
            try
            {
                using var conn = new SqliteConnection(ConnString);
                conn.Open();
                using var cmd = new SqliteCommand(
                    $"UPDATE {table} SET {column} = @val, IsEdited = 1, LastChanged = @now WHERE Key = @key", conn);
                cmd.Parameters.AddWithValue("@key", key);
                cmd.Parameters.AddWithValue("@val", value ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@now", NowIso());
                cmd.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                AppLogger.LogError($"UpdateField failed (table={table}, column={column}, key={key})", ex);
                throw;
            }
        }

        private static string SelectedKeywordsCsv(ObservableCollection<KeywordSelectionVM> keywords)
            => string.Join(",", keywords.Where(k => k.IsSelected).Select(k => k.Key));

        //Armor
        public static void UpdateArmorName(string key, string name)
            => UpdateField("Armor", "IsEditedName", key, name);

        public static void UpdateArmorWeight(string key, double weight)
            => UpdateField("Armor", "IsEditedWeight", key, weight);

        public static void UpdateArmorValue(string key, int value)
            => UpdateField("Armor", "IsEditedValue", key, value);

        public static void UpdateArmorRating(string key, double armorRating)
            => UpdateField("Armor", "IsEditedArmorRating", key, armorRating);

        public static void UpdateArmorBodySlotMask(string key, long bodySlotMask)
            => UpdateField("Armor", "IsEditedBodySlotMask", key, bodySlotMask);

        public static void UpdateArmorKeywords(string key, ObservableCollection<KeywordSelectionVM> keywords)
            => UpdateField("Armor", "IsEditedKeywords", key, SelectedKeywordsCsv(keywords));

        // Weapon
        public static void UpdateWeaponName(string key, string name)
            => UpdateField("Weapons", "IsEditedName", key, name);

        public static void UpdateWeaponWeight(string key, double weight)
            => UpdateField("Weapons", "IsEditedWeight", key, weight);

        public static void UpdateWeaponValue(string key, int value)
            => UpdateField("Weapons", "IsEditedValue", key, value);

        public static void UpdateWeaponDamage(string key, double damage)
            => UpdateField("Weapons", "IsEditedDamage", key, damage);

        public static void UpdateWeaponSpeed(string key, double speed)
            => UpdateField("Weapons", "IsEditedSpeed", key, speed);

        public static void UpdateWeaponReach(string key, double reach)
            => UpdateField("Weapons", "IsEditedReach", key, reach);

        public static void UpdateWeaponStagger(string key, double stagger)
            => UpdateField("Weapons", "IsEditedStagger", key, stagger);

        public static void UpdateWeaponKeywords(string key, ObservableCollection<KeywordSelectionVM> keywords)
            => UpdateField("Weapons", "IsEditedKeywords", key, SelectedKeywordsCsv(keywords));

        // -------------------------------------------------
        // COBJ: INSERT (only new recipe)
        // -------------------------------------------------
        public void InsertCOBJ(COBJRecord rec)
        {
            using var connection = new SqliteConnection(ConnString);
            connection.Open();

            using var cmd = connection.CreateCommand();
            cmd.CommandText =
                @"INSERT INTO COBJ (
                    Key,
                    Original,
                    Name,
                    CreatedItem,
                    WorkbenchKeyword,
                    Ingredients,
                    IsEdited,
                    LastChanged
                ) VALUES (
                    $Key,
                    0,
                    $Name,
                    $CreatedItem,
                    $WorkbenchKeyword,
                    $Ingredients,
                    1,
                    $Now
                );";

            cmd.Parameters.AddWithValue("$Key", rec.Key);
            cmd.Parameters.AddWithValue("$Name", rec.Name);
            cmd.Parameters.AddWithValue("$CreatedItem", rec.CreatedItemKey);
            cmd.Parameters.AddWithValue("$WorkbenchKeyword", rec.WorkbenchKeywordKey);
            cmd.Parameters.AddWithValue("$Ingredients", string.Join(",", rec.IngredientKeys));
            cmd.Parameters.AddWithValue("$Now", NowIso());

            cmd.ExecuteNonQuery();

            // NOTE: the persisted COBJ.Original column stays 0 — it's what protects this
            // user-created recipe from MarkInactiveExcept's inactive-sweep on rescan (see
            // PutIntoDataBank's "Original = 1" guard). rec.Original is flipped to 1 here purely as
            // in-memory bookkeeping so a later SaveCOBJ() call on this same rec routes to UpdateCOBJ
            // instead of re-INSERTing — it must never be written back to the DB (a prior version of
            // this method did exactly that via an extra UPDATE, which silently made every newly
            // created recipe eligible for deactivation on the very next rescan).
            rec.Original = 1;
        }


        // -------------------------------------------------
        // COBJ: UPDATE (only existing recipe)
        // -------------------------------------------------
        public void UpdateCOBJ(COBJRecord rec)
        {
            using var connection = new SqliteConnection(ConnString);
            connection.Open();

            using var cmd = connection.CreateCommand();
            cmd.CommandText =
                @"UPDATE COBJ SET
                    IsEdited = 1,
                    IsEditedName = $Name,
                    IsEditedCreatedItem = $CreatedItem,
                    IsEditedWorkbenchKeyword = $WorkbenchKeyword,
                    IsEditedIngredients = $Ingredients,
                    LastChanged = $Now
                WHERE Key = $Key;";

            cmd.Parameters.AddWithValue("$Key", rec.Key);
            cmd.Parameters.AddWithValue("$Name", rec.Name);
            cmd.Parameters.AddWithValue("$CreatedItem", rec.CreatedItemKey);
            cmd.Parameters.AddWithValue("$WorkbenchKeyword", rec.WorkbenchKeywordKey);
            cmd.Parameters.AddWithValue("$Ingredients", string.Join(",", rec.IngredientKeys));
            cmd.Parameters.AddWithValue("$Now", NowIso());

            cmd.ExecuteNonQuery();
        }



        // -------------------------------------------------
        // COBJ: SAVE (auto save for Insert/Update)
        // -------------------------------------------------
        // NOT based on rec.Original: that column stays 0 forever in the DB for every user-created
        // recipe (see InsertCOBJ's comment - it's the "protect from rescan sweep" marker, not an
        // "already inserted" flag). rec.Original only gets flipped to 1 in-memory, on the exact same
        // object, right after InsertCOBJ runs. Any code path that re-reads a user-created recipe from
        // the DB (e.g. bulk-apply hydrating a multi-selected item that already has a real recipe but
        // was never individually clicked before) gets back a fresh COBJRecord with Original==0 again,
        // even though the row already exists - trusting that field here made SaveCOBJ try to
        // re-INSERT it and crash with "UNIQUE constraint failed: COBJ.Key". Asking the DB directly
        // whether the key exists is correct regardless of where rec came from.
        public void SaveCOBJ(COBJRecord rec)
        {
            if (CobjKeyExists(rec.Key))
                UpdateCOBJ(rec);
            else
                InsertCOBJ(rec);
        }

        // -------------------------------------------------
        // COBJ: Reset (Workbench + Ingredients - Name/CreatedItem aren't user-editable anywhere in
        // the UI today, and PerkKey isn't a persisted column at all). Conditions are a separate
        // concern (COBJ_Conditions) and untouched here - see the feasibility note on why per-field
        // condition revert isn't safe on the current schema (SaveCOBJConditions destructively
        // overwrites the base rows, so there's no pristine copy left to revert to).
        // -------------------------------------------------
        public COBJRecord GetOriginalCOBJ(string key)
        {
            using var connection = new SqliteConnection(ConnString);
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT Original, Name, CreatedItem, WorkbenchKeyword, Ingredients FROM COBJ WHERE Key = @key";
            cmd.Parameters.AddWithValue("@key", key);

            using var reader = cmd.ExecuteReader();
            if (!reader.Read()) return null;

            var ingredientsCsv = reader.IsDBNull(4) ? "" : reader.GetString(4);
            var ingredients = string.IsNullOrWhiteSpace(ingredientsCsv)
                ? new List<string>()
                : ingredientsCsv.Split(',', StringSplitOptions.RemoveEmptyEntries).ToList();

            return new COBJRecord
            {
                Key = key,
                Original = reader.GetInt32(0),
                Name = reader.IsDBNull(1) ? "" : reader.GetString(1),
                CreatedItemKey = reader.IsDBNull(2) ? "" : reader.GetString(2),
                WorkbenchKeywordKey = reader.IsDBNull(3) ? "" : reader.GetString(3),
                IngredientKeys = ingredients,
            };
        }

        public void ResetCOBJEdits(string key)
        {
            try
            {
                using var connection = new SqliteConnection(ConnString);
                connection.Open();
                using var cmd = connection.CreateCommand();
                cmd.CommandText = @"UPDATE COBJ SET
                                        IsEditedName = NULL,
                                        IsEditedCreatedItem = NULL,
                                        IsEditedWorkbenchKeyword = NULL,
                                        IsEditedIngredients = NULL,
                                        IsEdited = 0,
                                        LastChanged = @now
                                     WHERE Key = @key";
                cmd.Parameters.AddWithValue("@key", key);
                cmd.Parameters.AddWithValue("@now", NowIso());
                cmd.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                AppLogger.LogError($"ResetCOBJEdits failed (key={key})", ex);
                throw;
            }
        }

        // -------------------------------------------------
        // COBJ: Delete (only ever valid for a user-created recipe - Original stays 0 forever for
        // those, see InsertCOBJ's comment, precisely because a plugin-original recipe must survive a
        // rescan/export untouched). ResetCOBJEdits alone only clears the shadow edit columns, leaving
        // the just-created row behind - Original == 0 is what protects it from the normal inactive
        // sweep on rescan, so without this the row sits in the DB forever and still gets patched into
        // the ESP as a near-empty COBJ despite the user having "reset" it. Callers (ResetCrafting/
        // TemperRecipeEdits) must check GetOriginalCOBJ(key).Original == 0 before calling this; the
        // Original == 1 check here is a second guard so a bug there can never delete a real recipe.
        // -------------------------------------------------
        public void DeleteCOBJ(string key)
        {
            try
            {
                using var connection = new SqliteConnection(ConnString);
                connection.Open();
                using var transaction = connection.BeginTransaction();

                using (var checkCmd = connection.CreateCommand())
                {
                    checkCmd.Transaction = transaction;
                    checkCmd.CommandText = "SELECT Original FROM COBJ WHERE Key = @key";
                    checkCmd.Parameters.AddWithValue("@key", key);
                    var result = checkCmd.ExecuteScalar();
                    if (result == null) return; // already gone
                    if (Convert.ToInt64(result) != 0)
                    {
                        AppLogger.LogError($"DeleteCOBJ refused: {key} is a plugin-original recipe (Original=1)", new InvalidOperationException("Refusing to delete a plugin-original COBJ row"));
                        return;
                    }
                }

                using (var cmd = connection.CreateCommand())
                {
                    cmd.Transaction = transaction;
                    cmd.CommandText = "DELETE FROM COBJ_Conditions WHERE COBJKey = @key";
                    cmd.Parameters.AddWithValue("@key", key);
                    cmd.ExecuteNonQuery();
                }
                using (var cmd = connection.CreateCommand())
                {
                    cmd.Transaction = transaction;
                    cmd.CommandText = "DELETE FROM COBJ_Conditions_Original WHERE COBJKey = @key";
                    cmd.Parameters.AddWithValue("@key", key);
                    cmd.ExecuteNonQuery();
                }
                using (var cmd = connection.CreateCommand())
                {
                    cmd.Transaction = transaction;
                    cmd.CommandText = "DELETE FROM COBJ WHERE Key = @key";
                    cmd.Parameters.AddWithValue("@key", key);
                    cmd.ExecuteNonQuery();
                }

                transaction.Commit();

                // Keep the in-memory caches consistent with the DB - see LoadCacheCore for how these
                // are populated.
                _cobjCache.RemoveAll(c => c.Key == key);
                _cobjByKey.Remove(key);
                _cobjCondtionsCache.RemoveAll(c => c.COBJKey == key);
                foreach (var list in _cobjByCreatedItem.Values)
                    list.RemoveAll(c => c.Key == key);
            }
            catch (Exception ex)
            {
                AppLogger.LogError($"DeleteCOBJ failed (key={key})", ex);
                throw;
            }
        }


        // -------------------------------------------------
        // COBJ: create new recipe
        // -------------------------------------------------
        public COBJRecord CreateNewCOBJRecordForItem(ItemNodeVM item, bool isTemper)
        {
            string pluginName = KeyFactory.UserPluginName;
            // FormID generator: retry until we find a key that doesn't exist in the DB yet
            // (checked directly against the table, not just the Active-filtered in-memory cache,
            // since an inactive row still occupies its Key in the UNIQUE index).
            string newKey;
            do
            {
                string FormID = Count().ToString("X6");
                newKey = pluginName + "|" + FormID;
            } while (CobjKeyExists(newKey));
            string newName = item.Name;

            string workbenchKeyword = isTemper
                ? "Skyrim.esm|088108"   // Temper
                : "Skyrim.esm|0ADB78";  // Crafting
            if (isTemper == true)
            {
                if (item.IsArmor)
                {
                    workbenchKeyword = "Skyrim.esm|088108";
                }
                else
                {
                    workbenchKeyword = "Skyrim.esm|0ADB78";
                }

            }
            else
            {
                workbenchKeyword = "Skyrim.esm|088105";
            }

            var rec = new COBJRecord
            {
                Key = newKey,
                Name = newName,
                CreatedItemKey = item.Key,
                WorkbenchKeywordKey = workbenchKeyword,
                IngredientKeys = new List<string>(),
                Original = 0
            };

            return rec;
        }

        public int Count()
        {
            if (_count == null)
                _count = ComputeInitialCobjCounter();
            _count++;
            return _count.Value;
        }

        // _count used to be a hardcoded constant that reset on every app launch, so repeated
        // sessions could regenerate the same low FormIDs and collide with keys already used in
        // earlier sessions. Seed it from the actual max FormID ever used for this plugin instead.
        private int ComputeInitialCobjCounter()
        {
            const int fallbackBaseline = 893462;
            try
            {
                using var connection = new SqliteConnection(ConnString);
                connection.Open();
                using var cmd = connection.CreateCommand();
                cmd.CommandText = "SELECT Key FROM COBJ WHERE Key LIKE @prefix";
                cmd.Parameters.AddWithValue("@prefix", KeyFactory.UserPluginName + "|%");
                int max = fallbackBaseline;
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var key = reader.GetString(0);
                    var idx = key.IndexOf('|');
                    if (idx < 0) continue;
                    var hex = key.Substring(idx + 1);
                    if (int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var val) && val > max)
                        max = val;
                }
                return max;
            }
            catch
            {
                return fallbackBaseline;
            }
        }

        private bool CobjKeyExists(string key)
        {
            using var connection = new SqliteConnection(ConnString);
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM COBJ WHERE Key = @key";
            cmd.Parameters.AddWithValue("@key", key);
            var count = (long)cmd.ExecuteScalar();
            return count > 0;
        }

        // Container — routed through the same UpdateField helper (shadow column + IsEdited +
        // LastChanged) as every other editable field, so container assignments are protected from
        // rescans and visible to Import/Export like everything else (previously wrote straight to the
        // base ContainerString column, silently invisible to both).
        public static void UpdateArmorContainerString(string itemKey, string containerString)
            => UpdateField("Armor", "IsEditedContainerString", itemKey, containerString ?? "");

        public static void UpdateWeaponContainerString(string itemKey, string containerString)
            => UpdateField("Weapons", "IsEditedContainerString", itemKey, containerString ?? "");

        // -------------------------------------------------
        // Reset: read the pristine (pre-edit) base columns, and clear the shadow overrides that
        // ItemNodeVM's "Reset Changes" button reverts (Name/Weight/Value/ArmorRating-or-Weapon-
        // stats/Keywords/ContainerString). Used so change-tracking and reset survive an app restart
        // instead of only comparing against whatever was already edited when this session loaded the
        // item (see ItemNodeVM.CaptureOriginalSnapshot).
        // -------------------------------------------------
        public static ArmorRecord GetOriginalArmor(string key)
        {
            using var connection = new SqliteConnection(ConnString);
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = @"SELECT EditorID, Name, Weight, Value, ArmorRating, BodySlotMask, Keywords, ContainerString
                                 FROM Armor WHERE Key = @key";
            cmd.Parameters.AddWithValue("@key", key);

            using var reader = cmd.ExecuteReader();
            if (!reader.Read()) return null;

            var keywordsCsv = reader.IsDBNull(6) ? "" : reader.GetString(6);
            return new ArmorRecord
            {
                Key = key,
                EditorID = reader.IsDBNull(0) ? "" : reader.GetString(0),
                Name = reader.IsDBNull(1) ? "" : reader.GetString(1),
                Weight = reader.IsDBNull(2) ? 0f : (float)reader.GetDouble(2),
                Value = reader.IsDBNull(3) ? 0 : reader.GetInt32(3),
                ArmorRating = reader.IsDBNull(4) ? 0f : (float)reader.GetDouble(4),
                BodySlotMask = reader.IsDBNull(5) ? 0u : (uint)reader.GetInt64(5),
                Keywords = string.IsNullOrWhiteSpace(keywordsCsv) ? new List<string>() : keywordsCsv.Split(',').ToList(),
                ContainerString = reader.IsDBNull(7) ? "{}" : reader.GetString(7),
            };
        }

        public static WeaponRecord GetOriginalWeapon(string key)
        {
            using var connection = new SqliteConnection(ConnString);
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = @"SELECT EditorID, Name, Weight, Value, Damage, Speed, Reach, Stagger, Keywords, ContainerString
                                 FROM Weapons WHERE Key = @key";
            cmd.Parameters.AddWithValue("@key", key);

            using var reader = cmd.ExecuteReader();
            if (!reader.Read()) return null;

            var keywordsCsv = reader.IsDBNull(8) ? "" : reader.GetString(8);
            return new WeaponRecord
            {
                Key = key,
                EditorID = reader.IsDBNull(0) ? "" : reader.GetString(0),
                Name = reader.IsDBNull(1) ? "" : reader.GetString(1),
                Weight = reader.IsDBNull(2) ? 0f : (float)reader.GetDouble(2),
                Value = reader.IsDBNull(3) ? 0 : reader.GetInt32(3),
                Damage = reader.IsDBNull(4) ? 0 : reader.GetInt32(4),
                Speed = reader.IsDBNull(5) ? 0f : (float)reader.GetDouble(5),
                Reach = reader.IsDBNull(6) ? 0f : (float)reader.GetDouble(6),
                Stagger = reader.IsDBNull(7) ? 0f : (float)reader.GetDouble(7),
                Keywords = string.IsNullOrWhiteSpace(keywordsCsv) ? new List<string>() : keywordsCsv.Split(',').ToList(),
                ContainerString = reader.IsDBNull(9) ? "{}" : reader.GetString(9),
            };
        }

        public static void ResetArmorEdits(string key)
        {
            try
            {
                using var connection = new SqliteConnection(ConnString);
                connection.Open();
                using var cmd = connection.CreateCommand();
                cmd.CommandText = @"UPDATE Armor SET
                                        IsEditedName = NULL,
                                        IsEditedWeight = NULL,
                                        IsEditedValue = NULL,
                                        IsEditedArmorRating = NULL,
                                        IsEditedBodySlotMask = NULL,
                                        IsEditedKeywords = NULL,
                                        IsEditedContainerString = NULL,
                                        IsEdited = 0,
                                        LastChanged = @now
                                     WHERE Key = @key";
                cmd.Parameters.AddWithValue("@key", key);
                cmd.Parameters.AddWithValue("@now", NowIso());
                cmd.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                AppLogger.LogError($"ResetArmorEdits failed (key={key})", ex);
                throw;
            }
        }

        public static void ResetWeaponEdits(string key)
        {
            try
            {
                using var connection = new SqliteConnection(ConnString);
                connection.Open();
                using var cmd = connection.CreateCommand();
                cmd.CommandText = @"UPDATE Weapons SET
                                        IsEditedName = NULL,
                                        IsEditedWeight = NULL,
                                        IsEditedValue = NULL,
                                        IsEditedDamage = NULL,
                                        IsEditedSpeed = NULL,
                                        IsEditedReach = NULL,
                                        IsEditedStagger = NULL,
                                        IsEditedKeywords = NULL,
                                        IsEditedContainerString = NULL,
                                        IsEdited = 0,
                                        LastChanged = @now
                                     WHERE Key = @key";
                cmd.Parameters.AddWithValue("@key", key);
                cmd.Parameters.AddWithValue("@now", NowIso());
                cmd.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                AppLogger.LogError($"ResetWeaponEdits failed (key={key})", ex);
                throw;
            }
        }

        internal System.Collections.Generic.IList<object> SearchByType(string type)
        {
            LoadCache();

            // ContainerRecord 
            if (string.Equals(type, "Container", StringComparison.OrdinalIgnoreCase))
                return System.Linq.Enumerable.Cast<object>(_containerCache).ToList();

            // MagicEffect records
            if (string.Equals(type, "MagicEffect", StringComparison.OrdinalIgnoreCase))
                return System.Linq.Enumerable.Cast<object>(_magicEffectsCache).ToList();

            // For other types, return an empty list.
            return new System.Collections.Generic.List<object>();
        }

        // -------------------------------------------------
        // Enchantment: Updates
        // -------------------------------------------------

        public static void UpdateEnchantmentName(string key, string name)
            => UpdateField("Enchantments", "IsEditedName", key, name);

        // UpdateEnchantmentEditorID used to live here, writing to a column ("IsEditedEditorID") that
        // was never actually in the Enchantments schema — it threw a SqliteException the moment it
        // was called. Removed rather than fixed: EditorID isn't editable in the UI (EnchantmentMenuView
        // shows it read-only), so there was no working feature to preserve, just dead/broken plumbing.

        public static void UpdateEnchantmentCastType(string key, string castType)
            => UpdateField("Enchantments", "IsEditedCastType", key, castType);

        public static void UpdateEnchantmentTargetType(string key, string targetType)
            => UpdateField("Enchantments", "IsEditedTargetType", key, targetType);

        public static void UpdateEnchantmentCost(string key, float cost)
            => UpdateField("Enchantments", "IsEditedEnchantmentCost", key, cost);

        public static void UpdateEnchantmentWornRestrictionListKey(string key, string listKey)
            => UpdateField("Enchantments", "IsEditedWornRestrictionListKey", key, listKey);

        // -------------------------------------------------
        // Enchantment: Reset (Name + Cost - CastType/TargetType/WornRestrictionListKey aren't
        // directly user-editable anywhere in the UI today)
        // -------------------------------------------------
        public static EnchantmentRecord GetOriginalEnchantment(string key)
        {
            using var connection = new SqliteConnection(ConnString);
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT EditorID, Name, EnchantmentCost FROM Enchantments WHERE Key = @key";
            cmd.Parameters.AddWithValue("@key", key);

            using var reader = cmd.ExecuteReader();
            if (!reader.Read()) return null;

            return new EnchantmentRecord
            {
                Key = key,
                EditorID = reader.IsDBNull(0) ? "" : reader.GetString(0),
                Name = reader.IsDBNull(1) ? "" : reader.GetString(1),
                EnchantmentCost = reader.IsDBNull(2) ? 0f : (float)reader.GetDouble(2),
            };
        }

        public static void ResetEnchantmentEdits(string key)
        {
            try
            {
                using var connection = new SqliteConnection(ConnString);
                connection.Open();
                using var cmd = connection.CreateCommand();
                cmd.CommandText = @"UPDATE Enchantments SET
                                        IsEditedName = NULL,
                                        IsEditedEnchantmentCost = NULL,
                                        IsEdited = CASE WHEN IsEditedCastType IS NOT NULL
                                                         OR IsEditedTargetType IS NOT NULL
                                                         OR IsEditedWornRestrictionListKey IS NOT NULL
                                                    THEN 1 ELSE 0 END,
                                        LastChanged = @now
                                     WHERE Key = @key";
                cmd.Parameters.AddWithValue("@key", key);
                cmd.Parameters.AddWithValue("@now", NowIso());
                cmd.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                AppLogger.LogError($"ResetEnchantmentEdits failed (key={key})", ex);
                throw;
            }
        }

        public static void SaveEnchantmentEffects(string enchantmentKey, List<EnchantmentEffectRecord> effects)
        {
            try
            {
                using var conn = new SqliteConnection(ConnString);
                conn.Open();
                using var transaction = conn.BeginTransaction();

                // First edit ever for this enchantment's effects: freeze the current (still
                // pristine) rows into EnchantmentEffects_Original before the delete+insert below
                // destroys them. Gated on the EffectsEdited flag rather than "does _Original already
                // have rows" - see SaveCOBJConditions' comment for why (an enchantment whose true
                // original has zero effects would otherwise be indistinguishable from "never
                // snapshotted yet").
                using (var checkCmd = new SqliteCommand("SELECT EffectsEdited FROM Enchantments WHERE Key = @key", conn, transaction))
                {
                    checkCmd.Parameters.AddWithValue("@key", enchantmentKey);
                    var flagResult = checkCmd.ExecuteScalar();
                    bool alreadyEdited = flagResult != null && flagResult != DBNull.Value && Convert.ToInt64(flagResult) == 1;
                    if (!alreadyEdited)
                    {
                        using var snapshotCmd = new SqliteCommand(
                            @"INSERT INTO EnchantmentEffects_Original (EnchantmentKey, MagicEffectKey, EditorID, Name, Magnitude, Duration, Area)
                              SELECT EnchantmentKey, MagicEffectKey, EditorID, Name, Magnitude, Duration, Area
                              FROM EnchantmentEffects WHERE EnchantmentKey = @key", conn, transaction);
                        snapshotCmd.Parameters.AddWithValue("@key", enchantmentKey);
                        snapshotCmd.ExecuteNonQuery();
                    }
                }

                // Delete existing effects for the enchantment
                using (var deleteCmd = new SqliteCommand("DELETE FROM EnchantmentEffects WHERE EnchantmentKey = @key", conn, transaction))
                {
                    deleteCmd.Parameters.AddWithValue("@key", enchantmentKey);
                    deleteCmd.ExecuteNonQuery();
                }

                // Insert new effects. Column was previously "EffectKey" (doesn't exist — the real
                // column is MagicEffectKey) with no transaction: a failed insert here left the
                // preceding delete committed, silently wiping the enchantment's effects.
                foreach (var effect in effects)
                {
                    using var insertCmd = new SqliteCommand(
                        @"INSERT INTO EnchantmentEffects (EnchantmentKey, MagicEffectKey, EditorID, Name, Magnitude, Duration, Area)
                          VALUES (@enchantKey, @effectKey, @editorID, @name, @magnitude, @duration, @area)", conn, transaction);
                    insertCmd.Parameters.AddWithValue("@enchantKey", enchantmentKey);
                    insertCmd.Parameters.AddWithValue("@effectKey", effect.MagicEffectKey ?? "");
                    insertCmd.Parameters.AddWithValue("@editorID", effect.EditorID ?? "");
                    insertCmd.Parameters.AddWithValue("@name", effect.Name ?? "");
                    insertCmd.Parameters.AddWithValue("@magnitude", effect.Magnitude);
                    insertCmd.Parameters.AddWithValue("@duration", effect.Duration);
                    insertCmd.Parameters.AddWithValue("@area", effect.Area);
                    insertCmd.ExecuteNonQuery();
                }

                // Marks these effects as user-edited so a rescan leaves them untouched instead of
                // silently overwriting them with whatever the plugin scan finds (see PutIntoDataBank).
                using (var flagCmd = new SqliteCommand("UPDATE Enchantments SET EffectsEdited = 1, LastChanged = @now WHERE Key = @key", conn, transaction))
                {
                    flagCmd.Parameters.AddWithValue("@key", enchantmentKey);
                    flagCmd.Parameters.AddWithValue("@now", NowIso());
                    flagCmd.ExecuteNonQuery();
                }

                transaction.Commit();
            }
            catch (Exception ex)
            {
                AppLogger.LogError($"SaveEnchantmentEffects failed (enchantmentKey={enchantmentKey})", ex);
                throw;
            }
        }

        public static void SaveWornRestrictionKeywords(string listKey, List<string> keywordKeys)
        {
            try
            {
                using var conn = new SqliteConnection(ConnString);
                conn.Open();
                using var transaction = conn.BeginTransaction();

                // First edit ever for this worn-restriction list: freeze the current (still
                // pristine) rows into WornRestrictionKeywords_Original before the delete+insert below
                // destroys them. Gated on whether any Enchantments row referencing this ListKey
                // already has KeywordsEdited=1 rather than "does _Original already have rows" - see
                // SaveCOBJConditions' comment for why (a list whose true original has zero keywords
                // would otherwise be indistinguishable from "never snapshotted yet"). Checking via the
                // referencing enchantments (rather than a flag on this table, which has none) mirrors
                // how the flag itself is written further below - by ListKey, not by a single Key.
                using (var checkCmd = new SqliteCommand("SELECT COUNT(*) FROM Enchantments WHERE WornRestrictionListKey = @key AND KeywordsEdited = 1", conn, transaction))
                {
                    checkCmd.Parameters.AddWithValue("@key", listKey);
                    bool alreadyEdited = Convert.ToInt64(checkCmd.ExecuteScalar()) > 0;
                    if (!alreadyEdited)
                    {
                        using var snapshotCmd = new SqliteCommand(
                            @"INSERT INTO WornRestrictionKeywords_Original (ListKey, KeywordKey)
                              SELECT ListKey, KeywordKey FROM WornRestrictionKeywords WHERE ListKey = @key", conn, transaction);
                        snapshotCmd.Parameters.AddWithValue("@key", listKey);
                        snapshotCmd.ExecuteNonQuery();
                    }
                }

                // Delete existing keywords for the list
                using (var deleteCmd = new SqliteCommand("DELETE FROM WornRestrictionKeywords WHERE ListKey = @key", conn, transaction))
                {
                    deleteCmd.Parameters.AddWithValue("@key", listKey);
                    deleteCmd.ExecuteNonQuery();
                }

                // Insert new keywords
                foreach (var keyword in keywordKeys)
                {
                    using var insertCmd = new SqliteCommand(
                        @"INSERT INTO WornRestrictionKeywords (ListKey, KeywordKey)
                          VALUES (@listKey, @keywordKey)", conn, transaction);
                    insertCmd.Parameters.AddWithValue("@listKey", listKey);
                    insertCmd.Parameters.AddWithValue("@keywordKey", keyword);
                    insertCmd.ExecuteNonQuery();
                }

                // Marks the enchantment(s) referencing this worn-restriction list as user-edited so a
                // rescan leaves its keywords untouched (see PutIntoDataBank). WornRestrictionKeywords
                // is keyed by ListKey, not by an enchantment's own Key, hence the WHERE below.
                using (var flagCmd = new SqliteCommand("UPDATE Enchantments SET KeywordsEdited = 1, LastChanged = @now WHERE WornRestrictionListKey = @listKey", conn, transaction))
                {
                    flagCmd.Parameters.AddWithValue("@listKey", listKey);
                    flagCmd.Parameters.AddWithValue("@now", NowIso());
                    flagCmd.ExecuteNonQuery();
                }

                transaction.Commit();
            }
            catch (Exception ex)
            {
                AppLogger.LogError($"SaveWornRestrictionKeywords failed (listKey={listKey})", ex);
                throw;
            }
        }

        // -------------------------------------------------
        // EnchantmentEffects: Reset - see EnchantmentEffects_Original's schema comment and
        // SaveEnchantmentEffects' lazy-snapshot step above for how the pristine copy gets there.
        // -------------------------------------------------
        public static List<EnchantmentEffectRecord> GetOriginalEnchantmentEffects(string enchantmentKey)
        {
            var list = new List<EnchantmentEffectRecord>();
            using var connection = new SqliteConnection(ConnString);
            connection.Open();

            // Same "not edited yet -> read the live table instead" reasoning as
            // GetOriginalCOBJConditions - see its comment.
            bool effectsEdited;
            using (var flagCmd = connection.CreateCommand())
            {
                flagCmd.CommandText = "SELECT EffectsEdited FROM Enchantments WHERE Key = @key";
                flagCmd.Parameters.AddWithValue("@key", enchantmentKey);
                var flagResult = flagCmd.ExecuteScalar();
                effectsEdited = flagResult != null && flagResult != DBNull.Value && Convert.ToInt64(flagResult) == 1;
            }

            var table = effectsEdited ? "EnchantmentEffects_Original" : "EnchantmentEffects";
            using var cmd = connection.CreateCommand();
            cmd.CommandText = $"SELECT MagicEffectKey, EditorID, Name, Magnitude, Duration, Area FROM {table} WHERE EnchantmentKey = @key";
            cmd.Parameters.AddWithValue("@key", enchantmentKey);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                list.Add(new EnchantmentEffectRecord
                {
                    EnchantmentKey = enchantmentKey,
                    MagicEffectKey = reader.IsDBNull(0) ? "" : reader.GetString(0),
                    EditorID = reader.IsDBNull(1) ? "" : reader.GetString(1),
                    Name = reader.IsDBNull(2) ? "" : reader.GetString(2),
                    Magnitude = reader.IsDBNull(3) ? 0f : (float)reader.GetDouble(3),
                    Duration = reader.IsDBNull(4) ? 0 : reader.GetInt32(4),
                    Area = reader.IsDBNull(5) ? 0 : reader.GetInt32(5),
                });
            }
            return list;
        }

        public static void ResetEnchantmentEffects(string enchantmentKey)
        {
            try
            {
                using var conn = new SqliteConnection(ConnString);
                conn.Open();
                using var transaction = conn.BeginTransaction();

                // ResetEnchantmentCommand calls this unconditionally whenever the user resets an
                // enchantment, even if Effects specifically were never touched - in which case
                // EnchantmentEffects_Original was never populated, and blindly restoring "from" it
                // would DELETE the still-pristine live effects and replace them with nothing. Bail
                // out here if there's nothing to revert - see ResetCOBJConditions' identical guard.
                using (var checkCmd = new SqliteCommand("SELECT EffectsEdited FROM Enchantments WHERE Key = @key", conn, transaction))
                {
                    checkCmd.Parameters.AddWithValue("@key", enchantmentKey);
                    var flagResult = checkCmd.ExecuteScalar();
                    bool wasEdited = flagResult != null && flagResult != DBNull.Value && Convert.ToInt64(flagResult) == 1;
                    if (!wasEdited) return;
                }

                using (var deleteCmd = new SqliteCommand("DELETE FROM EnchantmentEffects WHERE EnchantmentKey = @key", conn, transaction))
                {
                    deleteCmd.Parameters.AddWithValue("@key", enchantmentKey);
                    deleteCmd.ExecuteNonQuery();
                }

                using (var restoreCmd = new SqliteCommand(
                    @"INSERT INTO EnchantmentEffects (EnchantmentKey, MagicEffectKey, EditorID, Name, Magnitude, Duration, Area)
                      SELECT EnchantmentKey, MagicEffectKey, EditorID, Name, Magnitude, Duration, Area
                      FROM EnchantmentEffects_Original WHERE EnchantmentKey = @key", conn, transaction))
                {
                    restoreCmd.Parameters.AddWithValue("@key", enchantmentKey);
                    restoreCmd.ExecuteNonQuery();
                }

                // Clear the snapshot now that it's been consumed - see ResetCOBJConditions' comment
                // for why (otherwise the next first-ever edit would duplicate it).
                using (var clearCmd = new SqliteCommand("DELETE FROM EnchantmentEffects_Original WHERE EnchantmentKey = @key", conn, transaction))
                {
                    clearCmd.Parameters.AddWithValue("@key", enchantmentKey);
                    clearCmd.ExecuteNonQuery();
                }

                using (var flagCmd = new SqliteCommand("UPDATE Enchantments SET EffectsEdited = 0, LastChanged = @now WHERE Key = @key", conn, transaction))
                {
                    flagCmd.Parameters.AddWithValue("@key", enchantmentKey);
                    flagCmd.Parameters.AddWithValue("@now", NowIso());
                    flagCmd.ExecuteNonQuery();
                }

                transaction.Commit();
            }
            catch (Exception ex)
            {
                AppLogger.LogError($"ResetEnchantmentEffects failed (enchantmentKey={enchantmentKey})", ex);
                throw;
            }
        }

        // -------------------------------------------------
        // WornRestrictionKeywords: Reset - see WornRestrictionKeywords_Original's schema comment and
        // SaveWornRestrictionKeywords' lazy-snapshot step above for how the pristine copy gets there.
        // -------------------------------------------------
        public static List<string> GetOriginalWornRestrictionKeywords(string listKey)
        {
            var list = new List<string>();
            using var connection = new SqliteConnection(ConnString);
            connection.Open();

            // Same "not edited yet -> read the live table instead" reasoning as
            // GetOriginalCOBJConditions - see its comment. Checked via referencing enchantments
            // (this table has no edited flag of its own), same as the SaveWornRestrictionKeywords gate.
            bool keywordsEdited;
            using (var flagCmd = connection.CreateCommand())
            {
                flagCmd.CommandText = "SELECT COUNT(*) FROM Enchantments WHERE WornRestrictionListKey = @key AND KeywordsEdited = 1";
                flagCmd.Parameters.AddWithValue("@key", listKey);
                keywordsEdited = Convert.ToInt64(flagCmd.ExecuteScalar()) > 0;
            }

            var table = keywordsEdited ? "WornRestrictionKeywords_Original" : "WornRestrictionKeywords";
            using var cmd = connection.CreateCommand();
            cmd.CommandText = $"SELECT KeywordKey FROM {table} WHERE ListKey = @key";
            cmd.Parameters.AddWithValue("@key", listKey);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                list.Add(reader.IsDBNull(0) ? "" : reader.GetString(0));
            return list;
        }

        public static void ResetWornRestrictionKeywords(string listKey)
        {
            try
            {
                using var conn = new SqliteConnection(ConnString);
                conn.Open();
                using var transaction = conn.BeginTransaction();

                // ResetEnchantmentCommand calls this unconditionally whenever the user resets an
                // enchantment that has a WornRestrictionListKey, even if the Keywords specifically
                // were never touched - in which case WornRestrictionKeywords_Original was never
                // populated, and blindly restoring "from" it would DELETE the still-pristine live
                // keywords and replace them with nothing. Bail out here if there's nothing to revert -
                // see ResetCOBJConditions' identical guard.
                using (var checkCmd = new SqliteCommand("SELECT COUNT(*) FROM Enchantments WHERE WornRestrictionListKey = @key AND KeywordsEdited = 1", conn, transaction))
                {
                    checkCmd.Parameters.AddWithValue("@key", listKey);
                    bool wasEdited = Convert.ToInt64(checkCmd.ExecuteScalar()) > 0;
                    if (!wasEdited) return;
                }

                using (var deleteCmd = new SqliteCommand("DELETE FROM WornRestrictionKeywords WHERE ListKey = @key", conn, transaction))
                {
                    deleteCmd.Parameters.AddWithValue("@key", listKey);
                    deleteCmd.ExecuteNonQuery();
                }

                using (var restoreCmd = new SqliteCommand(
                    @"INSERT INTO WornRestrictionKeywords (ListKey, KeywordKey)
                      SELECT ListKey, KeywordKey FROM WornRestrictionKeywords_Original WHERE ListKey = @key", conn, transaction))
                {
                    restoreCmd.Parameters.AddWithValue("@key", listKey);
                    restoreCmd.ExecuteNonQuery();
                }

                // Clear the snapshot now that it's been consumed - see ResetCOBJConditions' comment
                // for why (otherwise the next first-ever edit would duplicate it).
                using (var clearCmd = new SqliteCommand("DELETE FROM WornRestrictionKeywords_Original WHERE ListKey = @key", conn, transaction))
                {
                    clearCmd.Parameters.AddWithValue("@key", listKey);
                    clearCmd.ExecuteNonQuery();
                }

                using (var flagCmd = new SqliteCommand("UPDATE Enchantments SET KeywordsEdited = 0, LastChanged = @now WHERE WornRestrictionListKey = @listKey", conn, transaction))
                {
                    flagCmd.Parameters.AddWithValue("@listKey", listKey);
                    flagCmd.Parameters.AddWithValue("@now", NowIso());
                    flagCmd.ExecuteNonQuery();
                }

                transaction.Commit();
            }
            catch (Exception ex)
            {
                AppLogger.LogError($"ResetWornRestrictionKeywords failed (listKey={listKey})", ex);
                throw;
            }
        }

        // ===================================================
        // Import/Export
        // ===================================================

        private static string SafeStr(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? "" : reader.GetString(ordinal);

        private static string BuildScopeWhere(ExportScope scope, string scopeValue, SqliteCommand cmd)
        {
            switch (scope)
            {
                case ExportScope.Item:
                    cmd.Parameters.AddWithValue("@scopeKey", scopeValue);
                    return " AND Key = @scopeKey";
                case ExportScope.Plugin:
                    cmd.Parameters.AddWithValue("@scopePrefix", scopeValue + "|%");
                    return " AND Key LIKE @scopePrefix";
                default:
                    return "";
            }
        }

        public List<EditedItemDto> GetEditedItems(ExportScope scope, string scopeValue = null)
        {
            var items = new List<EditedItemDto>();
            using var connection = new SqliteConnection(ConnString);
            connection.Open();

            items.AddRange(GetEditedArmorOrWeapons(connection, "Armor",
                new[] { "IsEditedName", "IsEditedWeight", "IsEditedValue", "IsEditedArmorRating", "IsEditedBodySlotMask", "IsEditedKeywords", "IsEditedContainerString" },
                scope, scopeValue));
            items.AddRange(GetEditedArmorOrWeapons(connection, "Weapons",
                new[] { "IsEditedName", "IsEditedWeight", "IsEditedValue", "IsEditedDamage", "IsEditedSpeed", "IsEditedReach", "IsEditedStagger", "IsEditedKeywords", "IsEditedContainerString" },
                scope, scopeValue));
            items.AddRange(GetEditedCOBJ(connection, scope, scopeValue));
            items.AddRange(GetEditedEnchantments(connection, scope, scopeValue));

            return items;
        }

        private static List<EditedItemDto> GetEditedArmorOrWeapons(SqliteConnection connection, string table, string[] editedColumns, ExportScope scope, string scopeValue)
        {
            var result = new List<EditedItemDto>();
            using var cmd = connection.CreateCommand();
            var columns = "Key, LastChanged, Name, " + string.Join(", ", editedColumns);
            cmd.CommandText = $"SELECT {columns} FROM {table} WHERE LastChanged IS NOT NULL" + BuildScopeWhere(scope, scopeValue, cmd);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var dto = new EditedItemDto { Table = table, Key = reader.GetString(0), LastChanged = reader.GetString(1), DisplayName = SafeStr(reader, 2) };
                for (int i = 0; i < editedColumns.Length; i++)
                {
                    int ordinal = 3 + i;
                    if (!reader.IsDBNull(ordinal))
                        dto.Fields[editedColumns[i]] = reader.GetValue(ordinal).ToString();
                }
                result.Add(dto);
            }
            return result;
        }

        private static List<EditedItemDto> GetEditedCOBJ(SqliteConnection connection, ExportScope scope, string scopeValue)
        {
            var rows = new List<(string Key, bool ConditionsEdited, EditedItemDto Dto)>();
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = @"SELECT Key, LastChanged, Original, ConditionsEdited,
                        Name, CreatedItem, WorkbenchKeyword, Ingredients,
                        IsEditedName, IsEditedCreatedItem, IsEditedWorkbenchKeyword, IsEditedIngredients
                    FROM COBJ WHERE LastChanged IS NOT NULL" + BuildScopeWhere(scope, scopeValue, cmd);

                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    string key = reader.GetString(0);
                    int original = reader.GetInt32(2);
                    bool conditionsEdited = reader.GetInt32(3) == 1;

                    var dto = new EditedItemDto { Table = "COBJ", Key = key, LastChanged = reader.GetString(1), Original = original };

                    if (original == 0)
                    {
                        // User-created: no scan to fall back to, so export the full effective row
                        // (shadow value if set, else the base value) rather than just the shadow diff.
                        dto.Fields["Name"] = reader.IsDBNull(8) ? SafeStr(reader, 4) : reader.GetString(8);
                        dto.Fields["CreatedItem"] = reader.IsDBNull(9) ? SafeStr(reader, 5) : reader.GetString(9);
                        dto.Fields["WorkbenchKeyword"] = reader.IsDBNull(10) ? SafeStr(reader, 6) : reader.GetString(10);
                        dto.Fields["Ingredients"] = reader.IsDBNull(11) ? SafeStr(reader, 7) : reader.GetString(11);
                        dto.DisplayName = dto.Fields["Name"];
                    }
                    else
                    {
                        if (!reader.IsDBNull(8)) dto.Fields["IsEditedName"] = reader.GetString(8);
                        if (!reader.IsDBNull(9)) dto.Fields["IsEditedCreatedItem"] = reader.GetString(9);
                        if (!reader.IsDBNull(10)) dto.Fields["IsEditedWorkbenchKeyword"] = reader.GetString(10);
                        if (!reader.IsDBNull(11)) dto.Fields["IsEditedIngredients"] = reader.GetString(11);
                        dto.DisplayName = SafeStr(reader, 4);
                    }

                    rows.Add((key, conditionsEdited, dto));
                }
            }

            var result = new List<EditedItemDto>();
            foreach (var row in rows)
            {
                if (row.ConditionsEdited)
                    row.Dto.ConditionRows = GetCOBJConditionRowsForExport(connection, row.Key);
                result.Add(row.Dto);
            }
            return result;
        }

        private static List<Dictionary<string, string>> GetCOBJConditionRowsForExport(SqliteConnection connection, string cobjKey)
        {
            var rows = new List<Dictionary<string, string>>();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT ConditionType, Target, Value, Extra, RunOn FROM COBJ_Conditions WHERE COBJKey = @key";
            cmd.Parameters.AddWithValue("@key", cobjKey);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                rows.Add(new Dictionary<string, string>
                {
                    ["ConditionType"] = SafeStr(reader, 0),
                    ["Target"] = SafeStr(reader, 1),
                    ["Value"] = SafeStr(reader, 2),
                    ["Extra"] = SafeStr(reader, 3),
                    ["RunOn"] = SafeStr(reader, 4),
                });
            }
            return rows;
        }

        private static List<EditedItemDto> GetEditedEnchantments(SqliteConnection connection, ExportScope scope, string scopeValue)
        {
            var rows = new List<(string Key, bool EffectsEdited, bool KeywordsEdited, string EffectiveListKey, EditedItemDto Dto)>();
            string[] editedCols = { "IsEditedName", "IsEditedCastType", "IsEditedTargetType", "IsEditedEnchantmentCost", "IsEditedWornRestrictionListKey" };

            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = @"SELECT Key, LastChanged, EffectsEdited, KeywordsEdited,
                        Name, WornRestrictionListKey,
                        IsEditedName, IsEditedCastType, IsEditedTargetType, IsEditedEnchantmentCost, IsEditedWornRestrictionListKey
                    FROM Enchantments WHERE LastChanged IS NOT NULL" + BuildScopeWhere(scope, scopeValue, cmd);

                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    string key = reader.GetString(0);
                    bool effectsEdited = reader.GetInt32(2) == 1;
                    bool keywordsEdited = reader.GetInt32(3) == 1;
                    string baseListKey = SafeStr(reader, 5);

                    var dto = new EditedItemDto { Table = "Enchantments", Key = key, LastChanged = reader.GetString(1), DisplayName = SafeStr(reader, 4) };
                    for (int i = 0; i < editedCols.Length; i++)
                    {
                        int ordinal = 6 + i;
                        if (!reader.IsDBNull(ordinal))
                            dto.Fields[editedCols[i]] = reader.GetValue(ordinal).ToString();
                    }

                    string effectiveListKey = dto.Fields.TryGetValue("IsEditedWornRestrictionListKey", out var editedListKey) ? editedListKey : baseListKey;

                    rows.Add((key, effectsEdited, keywordsEdited, effectiveListKey, dto));
                }
            }

            var result = new List<EditedItemDto>();
            foreach (var row in rows)
            {
                if (row.EffectsEdited)
                    row.Dto.EffectRows = GetEnchantmentEffectRowsForExport(connection, row.Key);
                if (row.KeywordsEdited && !string.IsNullOrEmpty(row.EffectiveListKey))
                    row.Dto.WornRestrictionKeywords = GetWornRestrictionKeywordsForExport(connection, row.EffectiveListKey);
                result.Add(row.Dto);
            }
            return result;
        }

        private static List<Dictionary<string, string>> GetEnchantmentEffectRowsForExport(SqliteConnection connection, string enchantmentKey)
        {
            var rows = new List<Dictionary<string, string>>();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT MagicEffectKey, EditorID, Name, Magnitude, Duration, Area FROM EnchantmentEffects WHERE EnchantmentKey = @key";
            cmd.Parameters.AddWithValue("@key", enchantmentKey);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                rows.Add(new Dictionary<string, string>
                {
                    ["MagicEffectKey"] = SafeStr(reader, 0),
                    ["EditorID"] = SafeStr(reader, 1),
                    ["Name"] = SafeStr(reader, 2),
                    ["Magnitude"] = reader.IsDBNull(3) ? "0" : reader.GetValue(3).ToString(),
                    ["Duration"] = reader.IsDBNull(4) ? "0" : reader.GetValue(4).ToString(),
                    ["Area"] = reader.IsDBNull(5) ? "0" : reader.GetValue(5).ToString(),
                });
            }
            return rows;
        }

        private static List<string> GetWornRestrictionKeywordsForExport(SqliteConnection connection, string listKey)
        {
            var list = new List<string>();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT KeywordKey FROM WornRestrictionKeywords WHERE ListKey = @key";
            cmd.Parameters.AddWithValue("@key", listKey);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                list.Add(reader.GetString(0));
            return list;
        }

        // Real DateTime comparison (not string comparison) so minor formatting differences between
        // export runs can't cause a false conflict/mismatch — see plan risk 1.
        private static int CompareIso(string a, string b)
        {
            var da = DateTime.Parse(a, null, System.Globalization.DateTimeStyles.RoundtripKind);
            var db = DateTime.Parse(b, null, System.Globalization.DateTimeStyles.RoundtripKind);
            return da.CompareTo(db);
        }

        public ImportPlan PreviewImport(List<EditedItemDto> fileItems)
        {
            var plan = new ImportPlan();
            using var connection = new SqliteConnection(ConnString);
            connection.Open();

            foreach (var item in fileItems)
            {
                // item.Table comes straight from an imported file — an untrusted or corrupted one —
                // and gets interpolated into SQL below (table names can't be bound as parameters).
                // Reject anything outside the 4 known tables before it ever reaches a query.
                if (!AllowedImportFields.ContainsKey(item.Table))
                    continue;

                using var cmd = connection.CreateCommand();
                cmd.CommandText = $"SELECT LastChanged FROM {item.Table} WHERE Key = @key";
                cmd.Parameters.AddWithValue("@key", item.Key);
                var localResult = cmd.ExecuteScalar();

                // ExecuteScalar returns C# null only when the query matched zero rows (no local row
                // at all). A row that exists but has never been edited still matches — its LastChanged
                // column is SQL NULL, which ExecuteScalar surfaces as DBNull.Value, not null. Treating
                // both cases the same here previously misrouted every never-before-edited row (the
                // normal case for a first-time import) into ToSkipMissing, so imports onto Armor/
                // Weapons/Enchantments silently did nothing whenever the target had no prior edits.
                if (localResult == null)
                {
                    // No local row at all. A user-created (Original=0) COBJ recipe carries its own
                    // full data and needs no scanned base row to attach to, so it's always safe to
                    // insert fresh. Everything else has no scan to build a real row from — skip it
                    // (see plan risk: missing item usually means the owning plugin isn't installed).
                    if (item.Table == "COBJ" && item.Original == 0)
                        plan.ToApply.Add(item);
                    else
                        plan.ToSkipMissing.Add(item);
                    continue;
                }

                string localLastChanged = localResult as string;
                if (string.IsNullOrEmpty(localLastChanged))
                {
                    // Row exists but was never edited locally (LastChanged IS NULL) — nothing to
                    // conflict with.
                    plan.ToApply.Add(item);
                    continue;
                }

                int cmp = CompareIso(item.LastChanged, localLastChanged);
                if (cmp == 0)
                    plan.ToSkipEqual.Add(item);
                else if (cmp > 0)
                    plan.ToApply.Add(item);
                else
                    plan.Conflicts.Add(new ImportConflict { FileItem = item, LocalLastChanged = localLastChanged });
            }

            return plan;
        }

        public ImportResult ApplyImport(ImportPlan plan, HashSet<string> conflictKeysToUseFileVersion)
        {
            var result = new ImportResult
            {
                SkippedEqual = plan.ToSkipEqual.Count,
                SkippedMissing = plan.ToSkipMissing
            };

            var toWrite = new List<EditedItemDto>(plan.ToApply);
            foreach (var conflict in plan.Conflicts)
            {
                if (conflictKeysToUseFileVersion.Contains(conflict.FileItem.Table + "|" + conflict.FileItem.Key))
                {
                    toWrite.Add(conflict.FileItem);
                    result.ConflictsUsedFile++;
                }
                else
                {
                    result.ConflictsKeptLocal++;
                }
            }

            using (var connection = new SqliteConnection(ConnString))
            {
                connection.Open();
                using var transaction = connection.BeginTransaction();

                foreach (var item in toWrite)
                {
                    ApplyImportedItem(connection, transaction, item);
                    result.Applied++;
                }

                transaction.Commit();
            }

            // The in-memory caches (armor/weapon/COBJ/enchantment lists this handler serves to the
            // rest of the app) are now stale — force the next read to reload from the DB, same as
            // PutIntoDataBank already does after a rescan.
            InvalidateCache();

            return result;
        }

        private void ApplyImportedItem(SqliteConnection connection, SqliteTransaction transaction, EditedItemDto item)
        {
            switch (item.Table)
            {
                case "Armor":
                case "Weapons":
                    ApplySimpleFieldItem(connection, transaction, item.Table, item);
                    break;
                case "COBJ":
                    ApplyCobjItem(connection, transaction, item);
                    break;
                case "Enchantments":
                    ApplyEnchantmentItem(connection, transaction, item);
                    break;
            }
        }

        private static bool RowExists(SqliteConnection connection, SqliteTransaction transaction, string table, string key)
        {
            using var cmd = connection.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = $"SELECT COUNT(*) FROM {table} WHERE Key = @key";
            cmd.Parameters.AddWithValue("@key", key);
            return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
        }

        // Field-name whitelist per table. item.Fields keys come straight out of an imported JSON file
        // — an untrusted or corrupted one — and would otherwise be interpolated directly into the
        // UPDATE's SET clause below (SQLite parameters can only bind values, not column names). Any
        // key not on this list is silently dropped instead of reaching the SQL text.
        private static readonly Dictionary<string, HashSet<string>> AllowedImportFields = new()
        {
            ["Armor"] = new HashSet<string> { "IsEditedName", "IsEditedWeight", "IsEditedValue", "IsEditedArmorRating", "IsEditedBodySlotMask", "IsEditedKeywords", "IsEditedContainerString" },
            ["Weapons"] = new HashSet<string> { "IsEditedName", "IsEditedWeight", "IsEditedValue", "IsEditedDamage", "IsEditedSpeed", "IsEditedReach", "IsEditedStagger", "IsEditedKeywords", "IsEditedContainerString" },
            ["COBJ"] = new HashSet<string> { "IsEditedName", "IsEditedCreatedItem", "IsEditedWorkbenchKeyword", "IsEditedIngredients" },
            ["Enchantments"] = new HashSet<string> { "IsEditedName", "IsEditedCastType", "IsEditedTargetType", "IsEditedEnchantmentCost", "IsEditedWornRestrictionListKey" },
        };

        private static void ApplyFieldUpdate(SqliteConnection connection, SqliteTransaction transaction, string table, string key, string lastChanged, Dictionary<string, string> fields)
        {
            var allowed = AllowedImportFields[table];
            var setClauses = new List<string> { "IsEdited = 1", "LastChanged = @now" };
            using var cmd = connection.CreateCommand();
            cmd.Transaction = transaction;
            int i = 0;
            foreach (var kv in fields)
            {
                if (!allowed.Contains(kv.Key))
                    continue;
                var p = $"@f{i++}";
                setClauses.Add($"{kv.Key} = {p}");
                cmd.Parameters.AddWithValue(p, kv.Value);
            }
            cmd.CommandText = $"UPDATE {table} SET {string.Join(", ", setClauses)} WHERE Key = @key";
            cmd.Parameters.AddWithValue("@now", lastChanged);
            cmd.Parameters.AddWithValue("@key", key);
            cmd.ExecuteNonQuery();
        }

        private static void ApplySimpleFieldItem(SqliteConnection connection, SqliteTransaction transaction, string table, EditedItemDto item)
        {
            // Preview already routed missing keys to ToSkipMissing — this is just a defensive guard.
            if (!RowExists(connection, transaction, table, item.Key))
                return;

            ApplyFieldUpdate(connection, transaction, table, item.Key, item.LastChanged, item.Fields);
        }

        private static void ApplyCobjItem(SqliteConnection connection, SqliteTransaction transaction, EditedItemDto item)
        {
            bool exists = RowExists(connection, transaction, "COBJ", item.Key);

            if (item.Original == 0 && !exists)
            {
                using var insertCmd = connection.CreateCommand();
                insertCmd.Transaction = transaction;
                insertCmd.CommandText = @"INSERT INTO COBJ (Key, Original, Name, CreatedItem, WorkbenchKeyword, Ingredients, IsEdited, LastChanged)
                                           VALUES (@key, 0, @name, @created, @wbk, @ingr, 1, @now)";
                insertCmd.Parameters.AddWithValue("@key", item.Key);
                insertCmd.Parameters.AddWithValue("@name", item.Fields.GetValueOrDefault("Name", ""));
                insertCmd.Parameters.AddWithValue("@created", item.Fields.GetValueOrDefault("CreatedItem", ""));
                insertCmd.Parameters.AddWithValue("@wbk", item.Fields.GetValueOrDefault("WorkbenchKeyword", ""));
                insertCmd.Parameters.AddWithValue("@ingr", item.Fields.GetValueOrDefault("Ingredients", ""));
                insertCmd.Parameters.AddWithValue("@now", item.LastChanged);
                insertCmd.ExecuteNonQuery();
            }
            else if (!exists)
            {
                return; // Original==1 but missing locally — Preview already routes this to ToSkipMissing.
            }
            else if (item.Original == 0)
            {
                // Existing user-created recipe: overwrite base columns directly — a rescan never
                // touches Original=0 rows, so there is no shadow/base split to preserve here.
                using var updateCmd = connection.CreateCommand();
                updateCmd.Transaction = transaction;
                updateCmd.CommandText = @"UPDATE COBJ SET Name = @name, CreatedItem = @created, WorkbenchKeyword = @wbk,
                                           Ingredients = @ingr, IsEdited = 1, LastChanged = @now WHERE Key = @key";
                updateCmd.Parameters.AddWithValue("@name", item.Fields.GetValueOrDefault("Name", ""));
                updateCmd.Parameters.AddWithValue("@created", item.Fields.GetValueOrDefault("CreatedItem", ""));
                updateCmd.Parameters.AddWithValue("@wbk", item.Fields.GetValueOrDefault("WorkbenchKeyword", ""));
                updateCmd.Parameters.AddWithValue("@ingr", item.Fields.GetValueOrDefault("Ingredients", ""));
                updateCmd.Parameters.AddWithValue("@now", item.LastChanged);
                updateCmd.Parameters.AddWithValue("@key", item.Key);
                updateCmd.ExecuteNonQuery();
            }
            else
            {
                ApplyFieldUpdate(connection, transaction, "COBJ", item.Key, item.LastChanged, item.Fields);
            }

            if (item.ConditionRows != null)
            {
                using (var deleteCmd = connection.CreateCommand())
                {
                    deleteCmd.Transaction = transaction;
                    deleteCmd.CommandText = "DELETE FROM COBJ_Conditions WHERE COBJKey = @key";
                    deleteCmd.Parameters.AddWithValue("@key", item.Key);
                    deleteCmd.ExecuteNonQuery();
                }
                foreach (var cond in item.ConditionRows)
                {
                    using var insertCmd = connection.CreateCommand();
                    insertCmd.Transaction = transaction;
                    insertCmd.CommandText = @"INSERT INTO COBJ_Conditions (COBJKey, ConditionType, Target, Value, Extra, RunOn)
                                               VALUES (@key, @type, @target, @value, @extra, @runOn)";
                    insertCmd.Parameters.AddWithValue("@key", item.Key);
                    insertCmd.Parameters.AddWithValue("@type", cond.GetValueOrDefault("ConditionType", ""));
                    insertCmd.Parameters.AddWithValue("@target", cond.GetValueOrDefault("Target", ""));
                    insertCmd.Parameters.AddWithValue("@value", cond.GetValueOrDefault("Value", ""));
                    insertCmd.Parameters.AddWithValue("@extra", cond.GetValueOrDefault("Extra", ""));
                    insertCmd.Parameters.AddWithValue("@runOn", cond.GetValueOrDefault("RunOn", ""));
                    insertCmd.ExecuteNonQuery();
                }
                using (var flagCmd = connection.CreateCommand())
                {
                    flagCmd.Transaction = transaction;
                    flagCmd.CommandText = "UPDATE COBJ SET ConditionsEdited = 1 WHERE Key = @key";
                    flagCmd.Parameters.AddWithValue("@key", item.Key);
                    flagCmd.ExecuteNonQuery();
                }
            }
        }

        private static void ApplyEnchantmentItem(SqliteConnection connection, SqliteTransaction transaction, EditedItemDto item)
        {
            // Preview already routed missing keys to ToSkipMissing — this is just a defensive guard.
            if (!RowExists(connection, transaction, "Enchantments", item.Key))
                return;

            ApplyFieldUpdate(connection, transaction, "Enchantments", item.Key, item.LastChanged, item.Fields);

            if (item.EffectRows != null)
            {
                using (var deleteCmd = connection.CreateCommand())
                {
                    deleteCmd.Transaction = transaction;
                    deleteCmd.CommandText = "DELETE FROM EnchantmentEffects WHERE EnchantmentKey = @key";
                    deleteCmd.Parameters.AddWithValue("@key", item.Key);
                    deleteCmd.ExecuteNonQuery();
                }
                foreach (var eff in item.EffectRows)
                {
                    using var insertCmd = connection.CreateCommand();
                    insertCmd.Transaction = transaction;
                    insertCmd.CommandText = @"INSERT INTO EnchantmentEffects (EnchantmentKey, MagicEffectKey, EditorID, Name, Magnitude, Duration, Area)
                                               VALUES (@key, @mgef, @editorId, @name, @magnitude, @duration, @area)";
                    insertCmd.Parameters.AddWithValue("@key", item.Key);
                    insertCmd.Parameters.AddWithValue("@mgef", eff.GetValueOrDefault("MagicEffectKey", ""));
                    insertCmd.Parameters.AddWithValue("@editorId", eff.GetValueOrDefault("EditorID", ""));
                    insertCmd.Parameters.AddWithValue("@name", eff.GetValueOrDefault("Name", ""));
                    insertCmd.Parameters.AddWithValue("@magnitude", eff.GetValueOrDefault("Magnitude", "0"));
                    insertCmd.Parameters.AddWithValue("@duration", eff.GetValueOrDefault("Duration", "0"));
                    insertCmd.Parameters.AddWithValue("@area", eff.GetValueOrDefault("Area", "0"));
                    insertCmd.ExecuteNonQuery();
                }
                using (var flagCmd = connection.CreateCommand())
                {
                    flagCmd.Transaction = transaction;
                    flagCmd.CommandText = "UPDATE Enchantments SET EffectsEdited = 1 WHERE Key = @key";
                    flagCmd.Parameters.AddWithValue("@key", item.Key);
                    flagCmd.ExecuteNonQuery();
                }
            }

            if (item.WornRestrictionKeywords != null)
            {
                // Resolve the effective WornRestrictionListKey the same way GetEditedEnchantments did
                // at export time: an edited shadow value on this item wins, else the base column.
                string listKey = item.Fields.TryGetValue("IsEditedWornRestrictionListKey", out var editedListKey)
                    ? editedListKey
                    : GetScalarString(connection, transaction, "SELECT WornRestrictionListKey FROM Enchantments WHERE Key = @key", item.Key);

                if (!string.IsNullOrEmpty(listKey))
                {
                    using (var deleteCmd = connection.CreateCommand())
                    {
                        deleteCmd.Transaction = transaction;
                        deleteCmd.CommandText = "DELETE FROM WornRestrictionKeywords WHERE ListKey = @listKey";
                        deleteCmd.Parameters.AddWithValue("@listKey", listKey);
                        deleteCmd.ExecuteNonQuery();
                    }
                    foreach (var kw in item.WornRestrictionKeywords)
                    {
                        using var insertCmd = connection.CreateCommand();
                        insertCmd.Transaction = transaction;
                        insertCmd.CommandText = "INSERT INTO WornRestrictionKeywords (ListKey, KeywordKey) VALUES (@listKey, @kw)";
                        insertCmd.Parameters.AddWithValue("@listKey", listKey);
                        insertCmd.Parameters.AddWithValue("@kw", kw);
                        insertCmd.ExecuteNonQuery();
                    }
                    using (var flagCmd = connection.CreateCommand())
                    {
                        flagCmd.Transaction = transaction;
                        flagCmd.CommandText = "UPDATE Enchantments SET KeywordsEdited = 1 WHERE WornRestrictionListKey = @listKey";
                        flagCmd.Parameters.AddWithValue("@listKey", listKey);
                        flagCmd.ExecuteNonQuery();
                    }
                }
            }
        }

        private static string GetScalarString(SqliteConnection connection, SqliteTransaction transaction, string sql, string key)
        {
            using var cmd = connection.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = sql;
            cmd.Parameters.AddWithValue("@key", key);
            var result = cmd.ExecuteScalar();
            return result == null || result == DBNull.Value ? null : (string)result;
        }
    }
}
