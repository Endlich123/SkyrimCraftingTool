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
    public partial class ItemDBHandler
    {
        private string ItemFolder => Path.Combine(GlobalState.Tool.InputFolder, "Item");
        private string ItemdbPath => Path.Combine(ItemFolder, "item.db");
        public static string ConnString
        => $"Data Source={Path.Combine(GlobalState.Tool.InputFolder, "Item", "item.db")}";

        // The static read helpers below can run before any scan has created the DB
        // (EnchantmentMenuVM ctor -> RefreshData -> RefreshWornRestrictionListChoices). If
        // Input\Item\ doesn't exist yet, opening a connection throws SQLITE_CANTOPEN (error 14,
        // "unable to open database file") instead of creating the file — the instance Load* methods
        // all guard with File.Exists(ItemdbPath); these need the same, from a static context.
        private static bool ItemDbExists
            => File.Exists(Path.Combine(GlobalState.Tool.InputFolder, "Item", "item.db"));

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
                    END AS RunOn,
                    CompareOperator,
                    Flags
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
                    RunOn = reader.IsDBNull(5) ? "" : reader.GetString(5),
                    CompareOperator = reader.IsDBNull(6) ? "" : reader.GetString(6),
                    Flags = reader.IsDBNull(7) ? "" : reader.GetString(7)
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
                    snapshotCmd.CommandText = @"INSERT INTO COBJ_Conditions_Original (COBJKey, ConditionType, Target, Value, Extra, RunOn, CompareOperator, Flags)
                                                 SELECT COBJKey, ConditionType, Target, Value, Extra, RunOn, CompareOperator, Flags
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
                    insertCmd.Parameters["@op"].Value = cond.CompareOperator ?? "";
                    insertCmd.Parameters["@flags"].Value = cond.Flags ?? "";
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
            cmd.CommandText = $"SELECT ConditionType, Target, Value, Extra, RunOn, CompareOperator, Flags FROM {table} WHERE COBJKey = @key";
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
                    CompareOperator = reader.IsDBNull(5) ? "" : reader.GetString(5),
                    Flags = reader.IsDBNull(6) ? "" : reader.GetString(6),
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
                    restoreCmd.CommandText = @"INSERT INTO COBJ_Conditions (COBJKey, ConditionType, Target, Value, Extra, RunOn, CompareOperator, Flags)
                                                SELECT COBJKey, ConditionType, Target, Value, Extra, RunOn, CompareOperator, Flags
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
                    END AS WornRestrictionListKey,
                    -- Read-only scan value (no shadow column) — drives the derived tree tag + filter.
                    BaseEnchantmentKey,
                    -- Currently-edited, NOT ever-touched: the flags are cleared by the reset paths,
                    -- whereas LastChanged is left non-null by them. E3: KeywordsEdited dropped — a
                    -- worn-restriction-list content edit marks the LIST (WornRestrictionListState),
                    -- not the enchantments pointing at it.
                    CASE WHEN IsEdited = 1 OR EffectsEdited = 1
                         THEN 1 ELSE 0 END AS IsEditedFlag
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
                    WornRestrictionListKey = reader.IsDBNull(6) ? "" : reader.GetString(6),
                    BaseEnchantmentKey = reader.IsDBNull(7) ? "" : reader.GetString(7),
                    IsEdited = !reader.IsDBNull(8) && reader.GetInt64(8) == 1,
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

            // 4. match effect — index once instead of re-scanning the whole effect list per
            // enchantment (that was O(enchantments × effects); ~1200 × ~4000 on a big load order).
            // Default (ordinal) comparer on purpose — the replaced "e.EnchantmentKey == ench.Key"
            // was case-sensitive too, and _effectsByEnchantment in LoadCacheCore groups the same way.
            var effectsByEnchantment = effects.ToLookup(e => e.EnchantmentKey);
            foreach (var ench in enchantments)
            {
                foreach (var eff in effectsByEnchantment[ench.Key])
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

        // --- Reset: the shadow columns each editable table carries ---
        //
        // Every Reset*Edits below is the same statement: NULL every IsEdited* shadow, clear the
        // IsEdited flag, stamp LastChanged. LastChanged is deliberately SET (not cleared) — it feeds
        // the import conflict check ("local is newer than this export file"). What counts as
        // "currently edited" is the IsEdited flag, which is why GetEdited* filters on that.
        private static readonly string[] ArmorShadowColumns =
            { "IsEditedName", "IsEditedWeight", "IsEditedValue", "IsEditedArmorRating", "IsEditedBodySlotMask", "IsEditedKeywords", "IsEditedContainerString" };
        private static readonly string[] WeaponShadowColumns =
            { "IsEditedName", "IsEditedWeight", "IsEditedValue", "IsEditedDamage", "IsEditedSpeed", "IsEditedReach", "IsEditedStagger", "IsEditedKeywords", "IsEditedContainerString" };
        private static readonly string[] CobjShadowColumns =
            { "IsEditedName", "IsEditedCreatedItem", "IsEditedWorkbenchKeyword", "IsEditedIngredients" };
        // CastType/TargetType have no UI edit path but ARE importable (AllowedImportFields), so they
        // must be cleared too — otherwise a later edit revives the orphaned shadow.
        private static readonly string[] EnchantmentShadowColumns =
            { "IsEditedName", "IsEditedCastType", "IsEditedTargetType", "IsEditedEnchantmentCost", "IsEditedWornRestrictionListKey" };

        private static void ResetEditShadows(string table, string[] shadowColumns, string key)
        {
            try
            {
                using var connection = new SqliteConnection(ConnString);
                connection.Open();
                using var cmd = connection.CreateCommand();
                var sets = string.Join(", ", shadowColumns.Select(c => $"{c} = NULL"));
                cmd.CommandText = $"UPDATE {table} SET {sets}, IsEdited = 0, LastChanged = @now WHERE Key = @key";
                cmd.Parameters.AddWithValue("@key", key);
                cmd.Parameters.AddWithValue("@now", NowIso());
                cmd.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                AppLogger.LogError($"ResetEditShadows failed (table={table}, key={key})", ex);
                throw;
            }
        }

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
            => ResetEditShadows("COBJ", CobjShadowColumns, key);

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
            => ResetEditShadows("Armor", ArmorShadowColumns, key);

        // Armor/Weapons rows that still carry ACTIVE edits (IsEdited = 1) but whose plugin dropped
        // out of the last scan (Active = 0). Feeds the orphaned-edits cleanup window. IsEdited, not
        // "LastChanged IS NOT NULL": a row that was edited, reset, then had its plugin removed has
        // LastChanged set but nothing to clean up — it's not an orphaned *edit*.
        public static List<OrphanedEdit> GetOrphanedItemEdits()
        {
            var list = new List<OrphanedEdit>();
            using var connection = new SqliteConnection(ConnString);
            connection.Open();

            foreach (var table in new[] { "Armor", "Weapons" })
            {
                using var cmd = connection.CreateCommand();
                cmd.CommandText = $@"SELECT Key, COALESCE(NULLIF(Name, ''), EditorID, Key), LastChanged
                                     FROM {table}
                                     WHERE IsEdited = 1 AND Active = 0";
                using var r = cmd.ExecuteReader();
                while (r.Read())
                    list.Add(new OrphanedEdit(table, r.GetString(0), r.GetString(1),
                        r.IsDBNull(2) ? "" : r.GetString(2)));
            }
            return list;
        }

        // Hard-deletes a single Armor/Weapons row (used to clear an orphaned edit). If the plugin
        // ever returns, the next scan recreates the row fresh.
        public static void DeleteItemRow(string table, string key)
        {
            if (table is not ("Armor" or "Weapons"))
                throw new ArgumentException($"Unsupported table: {table}", nameof(table));

            using var connection = new SqliteConnection(ConnString);
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = $"DELETE FROM {table} WHERE Key = @key";
            cmd.Parameters.AddWithValue("@key", key);
            cmd.ExecuteNonQuery();
        }

        public static void ResetWeaponEdits(string key)
            => ResetEditShadows("Weapons", WeaponShadowColumns, key);

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

        // FLST identity map (Plugin|FormID -> EditorID), from formid.db's FormLists name table
        // (scanned from every mod.FormLists record). Used by the worn-restriction picker to show a
        // real FLST name instead of a raw FormID. Read directly (not via _formIDDB's cache, which is
        // loaded once per session and never refreshed after a rescan). Empty until the first scan
        // with FormLists support has run.
        public Dictionary<string, string> GetFormListNamesByKey()
            => _formIDDB.GetFormListNamesDirect();

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
            cmd.CommandText = "SELECT EditorID, Name, EnchantmentCost, WornRestrictionListKey FROM Enchantments WHERE Key = @key";
            cmd.Parameters.AddWithValue("@key", key);

            using var reader = cmd.ExecuteReader();
            if (!reader.Read()) return null;

            return new EnchantmentRecord
            {
                Key = key,
                EditorID = reader.IsDBNull(0) ? "" : reader.GetString(0),
                Name = reader.IsDBNull(1) ? "" : reader.GetString(1),
                EnchantmentCost = reader.IsDBNull(2) ? 0f : (float)reader.GetDouble(2),
                WornRestrictionListKey = reader.IsDBNull(3) ? "" : reader.GetString(3),
            };
        }

        public static void ResetEnchantmentEdits(string key)
            => ResetEditShadows("Enchantments", EnchantmentShadowColumns, key);

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
            // The flag UPDATE below is "WHERE WornRestrictionListKey = @listKey". An FLST-less
            // enchantment's key is "" or "Null|000000" (shared by ~1100 rows) - writing for one of
            // those would mass-mark every FLST-less enchantment. Never write for an unset key.
            if (KeyFactory.IsUnsetKey(listKey))
                return;

            try
            {
                using var conn = new SqliteConnection(ConnString);
                conn.Open();
                using var transaction = conn.BeginTransaction();

                // First edit ever for this list: freeze the current (still pristine) rows into
                // WornRestrictionKeywords_Original before the delete+insert below destroys them.
                // Gated on WornRestrictionListState.IsEdited (E3 — was "any referencing Enchantments
                // row has KeywordsEdited=1"), so "true original has zero members" is still
                // distinguishable from "never snapshotted yet". See SaveCOBJConditions' comment.
                using (var checkCmd = new SqliteCommand("SELECT IsEdited FROM WornRestrictionListState WHERE ListKey = @key", conn, transaction))
                {
                    checkCmd.Parameters.AddWithValue("@key", listKey);
                    var res = checkCmd.ExecuteScalar();
                    bool alreadyEdited = res != null && res != DBNull.Value && Convert.ToInt64(res) == 1;
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

                // E3: the edit is recorded on the LIST, not smeared onto every enchantment that
                // references it. Enchantments.KeywordsEdited is left untouched (deprecated).
                using (var stateCmd = new SqliteCommand(
                    @"INSERT INTO WornRestrictionListState (ListKey, IsEdited, LastChanged) VALUES (@key, 1, @now)
                      ON CONFLICT(ListKey) DO UPDATE SET IsEdited = 1, LastChanged = @now", conn, transaction))
                {
                    stateCmd.Parameters.AddWithValue("@key", listKey);
                    stateCmd.Parameters.AddWithValue("@now", NowIso());
                    stateCmd.ExecuteNonQuery();
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
            if (!ItemDbExists) return list;

            using var connection = new SqliteConnection(ConnString);
            connection.Open();

            // Same "not edited yet -> read the live table instead" reasoning as
            // GetOriginalCOBJConditions. E3: gated on the per-list WornRestrictionListState flag.
            bool keywordsEdited;
            using (var flagCmd = connection.CreateCommand())
            {
                flagCmd.CommandText = "SELECT IsEdited FROM WornRestrictionListState WHERE ListKey = @key";
                flagCmd.Parameters.AddWithValue("@key", listKey);
                var res = flagCmd.ExecuteScalar();
                keywordsEdited = res != null && res != DBNull.Value && Convert.ToInt64(res) == 1;
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

        // E3.5: is this FLST's content user-edited (drives the list-scoped "Reset list" button)?
        public static bool IsWornRestrictionListEdited(string listKey)
        {
            if (KeyFactory.IsUnsetKey(listKey) || !ItemDbExists) return false;
            using var connection = new SqliteConnection(ConnString);
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT IsEdited FROM WornRestrictionListState WHERE ListKey = @key";
            cmd.Parameters.AddWithValue("@key", listKey);
            var res = cmd.ExecuteScalar();
            return res != null && res != DBNull.Value && Convert.ToInt64(res) == 1;
        }

        // E3.5: how many enchantments reference this FLST as their worn-restriction list — the
        // "changes affect all of them" hint. Counts the scanned base column (the picker's shadow
        // reassignments are per-enchantment and don't change what the list itself is used for).
        public static int CountEnchantmentsUsingWornRestrictionList(string listKey)
        {
            if (KeyFactory.IsUnsetKey(listKey) || !ItemDbExists) return 0;
            using var connection = new SqliteConnection(ConnString);
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM Enchantments WHERE WornRestrictionListKey = @key AND Active = 1";
            cmd.Parameters.AddWithValue("@key", listKey);
            return Convert.ToInt32(cmd.ExecuteScalar());
        }

        // Live (not "original") content of one FLST — used to populate the keyword panel right after
        // the user attaches an enchantment to an existing list via the picker.
        public static List<string> GetWornRestrictionKeywordsForList(string listKey)
        {
            var list = new List<string>();
            if (KeyFactory.IsUnsetKey(listKey) || !ItemDbExists) return list;

            using var connection = new SqliteConnection(ConnString);
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT KeywordKey FROM WornRestrictionKeywords WHERE ListKey = @key";
            cmd.Parameters.AddWithValue("@key", listKey);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                list.Add(reader.IsDBNull(0) ? "" : reader.GetString(0));
            return list;
        }

        // Every FLST the scan found members for, with those member keys — the assignable set for the
        // worn-restriction-list picker (the tool never creates new FLSTs, only attaches an
        // enchantment to one that already exists in the load order).
        //
        // IsEdited says the user has hand-edited this list's contents (WornRestrictionListState).
        // The picker needs it for two reasons: a list the user deliberately emptied has NO rows in
        // WornRestrictionKeywords at all and would otherwise vanish from the dropdown, and a
        // user-curated list must not be dropped by the picker's "members must look like keywords"
        // heuristic.
        public static List<(string ListKey, List<string> KeywordKeys, bool IsEdited)> GetKnownWornRestrictionLists()
        {
            var result = new List<(string, List<string>, bool)>();
            if (!ItemDbExists) return result;

            var byList = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            var edited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            using var connection = new SqliteConnection(ConnString);
            connection.Open();

            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = @"SELECT ListKey, KeywordKey FROM WornRestrictionKeywords
                                     WHERE ListKey IS NOT NULL AND ListKey <> '' AND ListKey NOT LIKE 'Null|%'
                                     ORDER BY ListKey";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var listKey = reader.GetString(0);
                    var kwKey = reader.IsDBNull(1) ? "" : reader.GetString(1);
                    if (!byList.TryGetValue(listKey, out var kws))
                        byList[listKey] = kws = new List<string>();
                    kws.Add(kwKey);
                }
            }

            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = @"SELECT ListKey FROM WornRestrictionListState
                                     WHERE IsEdited = 1
                                       AND ListKey IS NOT NULL AND ListKey <> '' AND ListKey NOT LIKE 'Null|%'";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var listKey = reader.GetString(0);
                    edited.Add(listKey);
                    // Deliberately emptied list: no member rows left, but it must stay pickable.
                    if (!byList.ContainsKey(listKey))
                        byList[listKey] = new List<string>();
                }
            }

            foreach (var kv in byList)
                result.Add((kv.Key, kv.Value, edited.Contains(kv.Key)));
            return result;
        }

        public static void ResetWornRestrictionKeywords(string listKey)
        {
            if (KeyFactory.IsUnsetKey(listKey))
                return; // see SaveWornRestrictionKeywords - an unset key must never drive a mass UPDATE

            try
            {
                using var conn = new SqliteConnection(ConnString);
                conn.Open();
                using var transaction = conn.BeginTransaction();

                // ResetEnchantmentCommand calls this unconditionally whenever the user resets an
                // enchantment that has a WornRestrictionListKey, even if the list content was never
                // touched - in which case WornRestrictionKeywords_Original was never populated, and
                // blindly restoring "from" it would DELETE the still-pristine live keywords and
                // replace them with nothing. Bail out here if there's nothing to revert - see
                // ResetCOBJConditions' identical guard. E3: gated on the per-list state flag.
                using (var checkCmd = new SqliteCommand("SELECT IsEdited FROM WornRestrictionListState WHERE ListKey = @key", conn, transaction))
                {
                    checkCmd.Parameters.AddWithValue("@key", listKey);
                    var res = checkCmd.ExecuteScalar();
                    bool wasEdited = res != null && res != DBNull.Value && Convert.ToInt64(res) == 1;
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

                // E3: the edit flag lives on the list. Clear its state row entirely (it's now back to
                // the pristine scanned content). Enchantments.KeywordsEdited is left untouched
                // (deprecated - RepairBlankWornRestrictionEdits clears any legacy value).
                using (var stateCmd = new SqliteCommand("DELETE FROM WornRestrictionListState WHERE ListKey = @listKey", conn, transaction))
                {
                    stateCmd.Parameters.AddWithValue("@listKey", listKey);
                    stateCmd.ExecuteNonQuery();
                }

                transaction.Commit();
            }
            catch (Exception ex)
            {
                AppLogger.LogError($"ResetWornRestrictionKeywords failed (listKey={listKey})", ex);
                throw;
            }
        }

    }
}
