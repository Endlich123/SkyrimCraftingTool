using DynamicData;
using Microsoft.Data.Sqlite;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Skyrim;
using SkyrimCraftingTool.ViewModel;
using System.Collections.ObjectModel;
using System.Data;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media.Media3D;
using static System.Net.Mime.MediaTypeNames;
using SkyrimCraftingTool.Services;


namespace SkyrimCraftingTool.Model
{
    public class ItemDBHandler
    {
        private string ItemFolder => Path.Combine(GlobalState.Tool.InputFolder, "Item");
        private string ItemdbPath => Path.Combine(ItemFolder, "item.db");
        public static string ConnString
        => $"Data Source={Path.Combine(GlobalState.Tool.InputFolder, "Item", "item.db")}";

        // count
        private int _count = 893462;

        // ============================
        //            CACHE
        // ============================
        private List<ArmorRecord> _armorCache = new();
        private List<WeaponRecord> _weaponCache = new();
        private List<COBJRecord> _cobjCache = new();

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

        public IReadOnlyList<ContainerRecord> ContainerCache => _containerCache;
        public IReadOnlyList<ContainerLVLIRecord> ContainerLvliCache => _containerLvliCache;

        private bool _cacheLoaded = false;

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

        private void LoadCache()
        {
            if (_cacheLoaded) return;

            _armorCache = LoadArmor();
            _weaponCache = LoadWeapons();
            _cobjCache = LoadCOBJ();
            _enchantmentCache = LoadEnchantments();
            _enchantmentEffectsCache = LoadEnchantmentEffects();
            _wornRestrictionKeywords = LoadWornRestrictionKeywords();

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

            // Container
            _containerCache = LoadContainer();
            _containerLvliCache = LoadContainerLVLI();

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

            _magicEffectsCache = LoadMagicEffects();

            _cacheLoaded = true;
        }

        // ============================
        //        DB ERSTELLEN
        // ============================

        public void PutIntoDataBank(List<PluginInfo> allgamePathfromDB)
        {
            Directory.CreateDirectory(ItemFolder);

            using var connection = new SqliteConnection($"Data Source={ItemdbPath}");
            connection.Open();

            ResetTables(connection);
            CreateTables(connection);

            using var insertArmor = PrepareInsertArmor(connection);
            using var insertWeapon = PrepareInsertWeapon(connection);
            using var insertCOBJ = PrepareInsertCOBJ(connection);
            using var insertEnch = PrepareInsertEnchantment(connection);
            using var insertEnchEff = PrepareInsertEnchantmentEffect(connection);
            using var insertWRK = PrepareInsertWornRestrictionKeyword(connection);
            using var insertContainer = PrepareInsertContainer(connection);
            using var insertContainerLVLI = PrepareInsertContainerLVLI(connection);
            using var insertMagicEffects = PrepareInsertMagicEffects(connection);

            using var transaction = connection.BeginTransaction();
            insertArmor.Transaction = transaction;
            insertWeapon.Transaction = transaction;
            insertCOBJ.Transaction = transaction;
            insertEnch.Transaction = transaction;
            insertEnchEff.Transaction = transaction;
            insertWRK.Transaction = transaction;
            insertContainer.Transaction = transaction;
            insertContainerLVLI.Transaction = transaction;
            insertMagicEffects.Transaction = transaction;

            foreach (var plugin in allgamePathfromDB)
            {
                string pluginName = plugin.FileName;

                foreach (var fullPath in plugin.FullPaths)
                {
                    var mod = SkyrimMod.CreateFromBinaryOverlay(fullPath, SkyrimRelease.SkyrimSE);

                    // ARMOR
                    foreach (var armor in mod.Armors.Records)
                    {
                        //string key = $"{pluginName}|{armor.FormKey.IDString()}";
                        string key = KeyFactory.BuildItemKey(mod.ModKey, armor.FormKey);

                        insertArmor.Parameters["@key"].Value = key;
                        insertArmor.Parameters["@editorID"].Value = armor.EditorID ?? "";
                        insertArmor.Parameters["@name"].Value = armor.Name?.ToString() ?? "";
                        insertArmor.Parameters["@weight"].Value = (float?)armor.Weight ?? 0f;
                        insertArmor.Parameters["@val"].Value = (int?)armor.Value ?? 0;
                        insertArmor.Parameters["@armorRating"].Value = (float?)armor.ArmorRating ?? 0f;

                        // NEW: BodySlotMask (bitmask)
                        // Safe getter for BodyTemplate.FirstPersonFlagsRaw (works for record or getter types)
                        uint slotMask = 0;
                        if (armor != null)
                        {
                            // Try direct BodyTemplate access first
                            var bodyTemplateProp = armor.GetType().GetProperty("BodyTemplate");
                            var bodyTemplate = bodyTemplateProp?.GetValue(armor);

                            // If not found try Data.BodyTemplate (some Mutagen versions put it on Data)
                            if (bodyTemplate == null)
                            {
                                var dataProp = armor.GetType().GetProperty("Data");
                                var data = dataProp?.GetValue(armor);
                                bodyTemplate = data?.GetType().GetProperty("BodyTemplate")?.GetValue(data);
                            }

                            if (bodyTemplate != null)
                            {
                                // Look for likely property names and convert safely
                                var fpProp = bodyTemplate.GetType().GetProperty("FirstPersonFlagsRaw")
                                           ?? bodyTemplate.GetType().GetProperty("FirstPersonFlags")
                                           ?? bodyTemplate.GetType().GetProperty("FlagsRaw");

                                if (fpProp != null)
                                {
                                    var rawVal = fpProp.GetValue(bodyTemplate);
                                    if (rawVal != null)
                                        slotMask = Convert.ToUInt32(rawVal);
                                }
                            }
                        }

                        insertArmor.Parameters["@slotMask"].Value = (long)slotMask;

                        var kw = armor.Keywords?
                            .Select(k =>
                            {
                                var fk = k.FormKey;
                                string kwPlugin = fk.ModKey.FileName;
                                string kwID = fk.IDString();
                                return $"{kwPlugin}|{kwID}";
                            })
                            ?? Enumerable.Empty<string>();

                        insertArmor.Parameters["@keywords"].Value = string.Join(",", kw);

                        insertArmor.ExecuteNonQuery();
                    }

                    // WEAPONS
                    foreach (var weap in mod.Weapons.Records)
                    {
                        //string key = $"{pluginName}|{weap.FormKey.IDString()}";
                        string key = KeyFactory.BuildItemKey(mod.ModKey, weap.FormKey);

                        insertWeapon.Parameters["@key"].Value = key;
                        insertWeapon.Parameters["@editorID"].Value = weap.EditorID ?? "";
                        insertWeapon.Parameters["@name"].Value = weap.Name?.ToString() ?? "";
                        insertWeapon.Parameters["@weight"].Value = (weap.BasicStats?.Weight) ?? 0f;
                        insertWeapon.Parameters["@val"].Value = (int?)weap.BasicStats?.Value ?? 0;
                        insertWeapon.Parameters["@dmg"].Value = weap.BasicStats?.Damage ?? 0;

                        // NEW: Weapon stats
                        insertWeapon.Parameters["@speed"].Value = weap.Data?.Speed ?? 0f;
                        insertWeapon.Parameters["@reach"].Value = weap.Data?.Reach ?? 0f;
                        insertWeapon.Parameters["@stagger"].Value = weap.Data?.Stagger ?? 0f;

                        var kw = weap.Keywords?
                            .Select(k =>
                            {
                                var fk = k.FormKey;
                                string kwPlugin = fk.ModKey.FileName;
                                string kwID = fk.IDString();
                                return $"{kwPlugin}|{kwID}";
                            })
                            ?? Enumerable.Empty<string>();
                        insertWeapon.Parameters["@keywords"].Value = string.Join(",", kw);

                        insertWeapon.ExecuteNonQuery();
                    }

                    // COBJ
                    foreach (var cobj in mod.ConstructibleObjects.Records)
                    {
                        //string key = $"{pluginName}|{cobj.FormKey.IDString()}";
                        string key = KeyFactory.BuildMasterKey(cobj.FormKey);
                        //string createdKey = $"{pluginName}|{cobj.CreatedObject.FormKey.IDString()}";
                        string createdKey = KeyFactory.BuildMasterKey(cobj.CreatedObject.FormKey);

                        insertCOBJ.Parameters["@key"].Value = key;
                        insertCOBJ.Parameters["@name"].Value = cobj.EditorID ?? "";
                        insertCOBJ.Parameters["@createdItem"].Value = createdKey;

                        string workbench = "";
                        if (cobj.WorkbenchKeyword != null)
                        {
                            var fk = cobj.WorkbenchKeyword.FormKey;
                            string wbPlugin = fk.ModKey.FileName;
                            string wbID = fk.IDString();
                            workbench = $"{wbPlugin}|{wbID}";
                        }
                        insertCOBJ.Parameters["@workbench"].Value = workbench;

                        var ingredients = cobj.Items?
                            .Select(e =>
                            {
                                var fk = e.Item.Item.FormKey;
                                string ingPlugin = fk.ModKey.FileName;
                                string ingID = fk.IDString();
                                return $"{ingPlugin}|{ingID}*{e.Item.Count}";
                            })
                            ?? Enumerable.Empty<string>();

                        insertCOBJ.Parameters["@ingredients"].Value = string.Join(",", ingredients);
                        // -------------------------
                        // Required Perk (Conditions → HasPerk)
                        // -------------------------
                        string perkKey = "";

                        if (cobj.Conditions != null)
                        {
                            foreach (var cond in cobj.Conditions)
                            {
                                if (cond.Data is HasPerkConditionData perkData)
                                {
                                    var perkItem = perkData.Perk;

                                    if (perkItem.UsesLink())
                                    {
                                        var fk = perkItem.Link.FormKey;
                                        perkKey = $"{fk.ModKey.FileName}|{fk.IDString()}";
                                    }
                                    else
                                    {
                                        // Alias / PackageData → ignore
                                        perkKey = "";
                                    }

                                    break; // only one Perk pro COBJ
                                }
                            }
                        }
                        insertCOBJ.Parameters["@perk"].Value = perkKey;

                        insertCOBJ.ExecuteNonQuery();
                    }

                    // ENCHANTMENTS
                    foreach (var ench in mod.ObjectEffects.Records)
                    {
                        string enchKey = $"{pluginName}|{ench.FormKey.IDString()}";

                        insertEnch.Parameters["@key"].Value = enchKey;
                        insertEnch.Parameters["@editorID"].Value = ench.EditorID ?? "";
                        insertEnch.Parameters["@name"].Value = ench.Name?.ToString() ?? "";
                        insertEnch.Parameters["@cast"].Value = ench.CastType.ToString();
                        insertEnch.Parameters["@target"].Value = ench.TargetType.ToString();
                        insertEnch.Parameters["@cost"].Value = (float)ench.EnchantmentCost;

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

                                    insertWRK.Parameters["@list"].Value = listKey;
                                    insertWRK.Parameters["@kw"].Value = kwKey;
                                    insertWRK.ExecuteNonQuery();
                                }
                            }
                        }

                        insertEnch.Parameters["@wrestr"].Value = listKey;
                        insertEnch.ExecuteNonQuery();

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

                            insertEnchEff.Parameters["@ench"].Value = enchKey;
                            insertEnchEff.Parameters["@mgef"].Value = mgefKey;
                            insertEnchEff.Parameters["@editorID"].Value = editorid;
                            insertEnchEff.Parameters["@name"].Value = name;
                            insertEnchEff.Parameters["@mag"].Value = eff.Data?.Magnitude ?? 0;
                            insertEnchEff.Parameters["@dur"].Value = eff.Data?.Duration ?? 0;
                            insertEnchEff.Parameters["@area"].Value = eff.Data?.Area ?? 0;

                            insertEnchEff.ExecuteNonQuery();
                        }
                    }

                    // CONTAINER + LVLI
                    foreach (var container in mod.Containers.Records)
                    {
                        string containerKey = $"{pluginName}|{container.FormKey.IDString()}";

                        // Insert Container
                        insertContainer.Parameters["@key"].Value = containerKey;
                        insertContainer.Parameters["@name"].Value = container.EditorID ?? "";
                        insertContainer.ExecuteNonQuery();

                        // LVLI inside Container
                        if (container.Items != null)
                        {
                            foreach (var entry in container.Items)
                            {
                                // FormKey des Items
                                var fk = entry.Item.Item.FormKey;

                                // Prüfen ob dieser FormKey ein LVLI ist
                                if (mod.LeveledItems.ContainsKey(fk))
                                {
                                    // LVLI gefunden
                                    var lvli = mod.LeveledItems[fk];

                                    string lvliKey = $"{lvli.FormKey.ModKey.FileName}|{lvli.FormKey.IDString()}";
                                    string lvliName = lvli.EditorID ?? "";

                                    insertContainerLVLI.Parameters["@containerKey"].Value = containerKey;
                                    insertContainerLVLI.Parameters["@lvliKey"].Value = lvliKey;
                                    insertContainerLVLI.Parameters["@lvliName"].Value = lvliName;

                                    insertContainerLVLI.ExecuteNonQuery();
                                }
                            }
                        }
                    }

                    // MAGIC EFFECTS
                    foreach (var mgef in mod.MagicEffects.Records)
                    {
                        string mgefKey = $"{pluginName}|{mgef.FormKey.IDString()}";
                        insertMagicEffects.Parameters["@key"].Value = mgefKey;
                        insertMagicEffects.Parameters["@editorID"].Value = mgef.EditorID ?? "";
                        insertMagicEffects.Parameters["@name"].Value = mgef.Name?.ToString() ?? "";

                        bool hasMagnitude = !mgef.Flags.HasFlag(MagicEffect.Flag.NoMagnitude);
                        bool hasDuration = !mgef.Flags.HasFlag(MagicEffect.Flag.NoDuration);
                        insertMagicEffects.Parameters["@hasMag"].Value = hasMagnitude ? 1 : 0;
                        insertMagicEffects.Parameters["@hasDur"].Value = hasDuration ? 1 : 0;
                        if(mgef.TargetType == TargetType.Aimed || mgef.TargetType == TargetType.TargetLocation)
                        {
                            insertMagicEffects.Parameters["@hasAre"].Value = 1;
                        } else
                        {
                            insertMagicEffects.Parameters["@hasAre"].Value = 0;
                        }

                        insertMagicEffects.Parameters["@castType"].Value = mgef.CastType.ToString();
                        insertMagicEffects.Parameters["@targetType"].Value = mgef.TargetType.ToString();

                        insertMagicEffects.ExecuteNonQuery();
                    }
                }
            }
            transaction.Commit();
            InvalidateCache();
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

        private SqliteCommand PrepareInsertEnchantment(SqliteConnection connection)
        {
            var cmd = new SqliteCommand(
                "INSERT OR REPLACE INTO Enchantments " +
                "(Key, EditorID, Name, CastType, TargetType, EnchantmentCost, WornRestrictionListKey) " +
                "VALUES (@key, @editorID, @name, @cast, @target, @cost, @wrestr)",
                connection);

            cmd.Parameters.Add("@key", SqliteType.Text);
            cmd.Parameters.Add("@editorID", SqliteType.Text);
            cmd.Parameters.Add("@name", SqliteType.Text);
            cmd.Parameters.Add("@cast", SqliteType.Text);
            cmd.Parameters.Add("@target", SqliteType.Text);
            cmd.Parameters.Add("@cost", SqliteType.Real);
            cmd.Parameters.Add("@wrestr", SqliteType.Text);

            return cmd;
        }


        private SqliteCommand PrepareInsertEnchantmentEffect(SqliteConnection connection)
        {
            var cmd = new SqliteCommand(
                "INSERT OR REPLACE INTO EnchantmentEffects " +
                "(EnchantmentKey, MagicEffectKey, EditorID, Name, Magnitude, Duration, Area) " +
                "VALUES (@ench, @mgef, @editorID, @name, @mag, @dur, @area)",
                connection);

            cmd.Parameters.Add("@ench", SqliteType.Text);
            cmd.Parameters.Add("@mgef", SqliteType.Text);
            cmd.Parameters.Add("@editorID", SqliteType.Text);
            cmd.Parameters.Add("@name", SqliteType.Text);
            cmd.Parameters.Add("@mag", SqliteType.Real);
            cmd.Parameters.Add("@dur", SqliteType.Integer);
            cmd.Parameters.Add("@area", SqliteType.Integer);

            return cmd;
        }

        private SqliteCommand PrepareInsertWornRestrictionKeyword(SqliteConnection connection)
        {
            var cmd = new SqliteCommand(
                "INSERT OR REPLACE INTO WornRestrictionKeywords " +
                "(ListKey, KeywordKey) VALUES (@list, @kw)",
                connection);

            cmd.Parameters.Add("@list", SqliteType.Text);
            cmd.Parameters.Add("@kw", SqliteType.Text);

            return cmd;
        }   

        private void ResetTables(SqliteConnection connection)
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText =
            @"
                DROP TABLE IF EXISTS Armor;
                DROP TABLE IF EXISTS Weapons;
                DROP TABLE IF EXISTS COBJ;
                DROP TABLE IF EXISTS Enchantments;
                DROP TABLE IF EXISTS EnchantmentEffects;
                DROP TABLE IF EXISTS WornRestrictionKeywords;
                DROP TABLE IF EXISTS Container;
                DROP TABLE IF EXISTS ContainerLVLI;
            ";
            cmd.ExecuteNonQuery();
        }

        private void CreateTables(SqliteConnection connection)
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText =
            @"
                CREATE TABLE Armor (
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

                    IsEdited INTEGER DEFAULT 0
                );

                CREATE TABLE Weapons (
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

                    IsEdited INTEGER DEFAULT 0
                );

                CREATE TABLE COBJ (
                    Key TEXT PRIMARY KEY,
                    Original INTEGER NOT NULL DEFAULT 1,
                    Name TEXT NOT NULL,
                    CreatedItem TEXT NOT NULL,
                    WorkbenchKeyword TEXT,
                    Ingredients TEXT,
                    Perk TEXT,

                    IsEditedName TEXT,
                    IsEditedCreatedItem TEXT,
                    IsEditedWorkbenchKeyword TEXT,
                    IsEditedIngredients TEXT,
                    IsEditedPerk TEXT,

                    IsEdited INTEGER DEFAULT 0
                );

                CREATE TABLE Enchantments (
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
    
                    IsEdited INTEGER DEFAULT 0
                );

                CREATE TABLE EnchantmentEffects (
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

                CREATE TABLE WornRestrictionKeywords (
                    ListKey TEXT NOT NULL,
                    KeywordKey TEXT NOT NULL,
                    
                    IsEditedKeywordKey TEXT,

                    IsEdited INTEGER DEFAULT 0,

                    PRIMARY KEY (ListKey, KeywordKey)
                );

                CREATE TABLE Container (
                    ContainerKey TEXT PRIMARY KEY,
                    Name TEXT NOT NULL
                );

                CREATE TABLE ContainerLVLI (
                    ContainerKey TEXT NOT NULL,
                    LVLiKey TEXT NOT NULL,
                    LVLiName TEXT,
                    PRIMARY KEY (ContainerKey, LVLiKey)
                );

                CREATE TABLE MagicEffects (
                    Key TEXT PRIMARY KEY,
                    EditorID TEXT,
                    Name TEXT NOT NULL,
                    HasMagnitude INTEGER,
                    HasDuration INTEGER,
                    HasArea INTEGER,
                    CastType TEXT,
                    TargetType TEXT
                );
            ";
            cmd.ExecuteNonQuery();

            //DumpTable(connection, "Armor");
            //DumpTable(connection, "Weapons");
            //DumpTable(connection, "COBJ");
            //DumpTable(connection, "Enchantments");
            //DumpTable(connection, "EnchantmentEffects");
            //DumpTable(connection, "WornRestrictionKeywords");
            //DumpTable(connection, "Container");
            //DumpTable(connection, "ContainerLVLI");
        }

        private void DumpTable(SqliteConnection conn, string table)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"PRAGMA table_info({table});";
            using var r = cmd.ExecuteReader();

            Debug.WriteLine($"=== {table} ===");
            while (r.Read())
            {
                Debug.WriteLine($"{r.GetString(1)} ({r.GetString(2)})");
            }
        }

        private SqliteCommand PrepareInsertArmor(SqliteConnection connection)
        {
            var cmd = new SqliteCommand(
                "INSERT OR REPLACE INTO Armor " +
                "(Key, EditorID, Name, Weight, Value, ArmorRating, BodySlotMask, Keywords) " +
                "VALUES (@key, @editorID, @name, @weight, @val, @armorRating, @slotMask, @keywords)",
                connection);

            cmd.Parameters.Add("@key", SqliteType.Text);
            cmd.Parameters.Add("@editorID", SqliteType.Text);
            cmd.Parameters.Add("@name", SqliteType.Text);
            cmd.Parameters.Add("@weight", SqliteType.Real);
            cmd.Parameters.Add("@val", SqliteType.Integer);
            cmd.Parameters.Add("@armorRating", SqliteType.Real);

            // NEW
            cmd.Parameters.Add("@slotMask", SqliteType.Integer);

            cmd.Parameters.Add("@keywords", SqliteType.Text);

            return cmd;
        }


        private SqliteCommand PrepareInsertWeapon(SqliteConnection connection)
        {
            var cmd = new SqliteCommand(
                "INSERT OR REPLACE INTO Weapons " +
                "(Key, EditorID, Name, Weight, Value, Damage, Speed, Reach, Stagger, Keywords) " +
                "VALUES (@key, @editorID, @name, @weight, @val, @dmg, @speed, @reach, @stagger, @keywords)",
                connection);

            cmd.Parameters.Add("@key", SqliteType.Text);
            cmd.Parameters.Add("@editorID", SqliteType.Text);
            cmd.Parameters.Add("@name", SqliteType.Text);
            cmd.Parameters.Add("@weight", SqliteType.Real);
            cmd.Parameters.Add("@val", SqliteType.Integer);
            cmd.Parameters.Add("@dmg", SqliteType.Integer);

            // NEW
            cmd.Parameters.Add("@speed", SqliteType.Real);
            cmd.Parameters.Add("@reach", SqliteType.Real);
            cmd.Parameters.Add("@stagger", SqliteType.Real);

            cmd.Parameters.Add("@keywords", SqliteType.Text);

            return cmd;
        }


        private SqliteCommand PrepareInsertCOBJ(SqliteConnection connection)
        {
            var cmd = new SqliteCommand(
                "INSERT OR REPLACE INTO COBJ (Key, Name, CreatedItem, WorkbenchKeyword, Ingredients, Perk) " +
                "VALUES (@key, @name, @createdItem, @workbench, @ingredients, @perk)",
                connection);

            cmd.Parameters.Add("@key", SqliteType.Text);
            cmd.Parameters.Add("@name", SqliteType.Text);
            cmd.Parameters.Add("@createdItem", SqliteType.Text);
            cmd.Parameters.Add("@workbench", SqliteType.Text);
            cmd.Parameters.Add("@ingredients", SqliteType.Text);
            cmd.Parameters.Add("@perk", SqliteType.Text);

            return cmd;
        }

        private SqliteCommand PrepareInsertContainer(SqliteConnection connection)
        {
            var cmd = new SqliteCommand(
                "INSERT OR REPLACE INTO Container (ContainerKey, Name) " +
                "VALUES (@key, @name)",
                connection);
            cmd.Parameters.Add("@key", SqliteType.Text);
            cmd.Parameters.Add("@name", SqliteType.Text);
            return cmd;
        }

        private SqliteCommand PrepareInsertContainerLVLI(SqliteConnection connection)
        {
            var cmd = new SqliteCommand(
                "INSERT OR REPLACE INTO ContainerLVLI (ContainerKey, LVLiKey, LVLiName) " +
                "VALUES (@containerKey, @lvliKey, @lvliName)",
                connection);
            cmd.Parameters.Add("@containerKey", SqliteType.Text);
            cmd.Parameters.Add("@lvliKey", SqliteType.Text);
            cmd.Parameters.Add("@lvliName", SqliteType.Text);
            return cmd;
        }

        private SqliteCommand PrepareInsertMagicEffects(SqliteConnection connection)
        {
            var cmd = new SqliteCommand(
                "INSERT OR REPLACE INTO MagicEffects (Key, EditorID, Name, HasMagnitude, HasDuration, HasArea, CastType, TargetType) " +
                "VALUES (@key, @editorID, @name, @hasMag, @hasDur, @hasAre, @castType, @targetType)",
                connection);
            cmd.Parameters.Add("@key", SqliteType.Text);
            cmd.Parameters.Add("@editorID", SqliteType.Text);
            cmd.Parameters.Add("@name", SqliteType.Text);
            cmd.Parameters.Add("@hasMag", SqliteType.Integer);
            cmd.Parameters.Add("@hasDur", SqliteType.Integer);
            cmd.Parameters.Add("@hasAre", SqliteType.Integer);
            cmd.Parameters.Add("@castType", SqliteType.Integer);
            cmd.Parameters.Add("@targetType", SqliteType.Integer);
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
                    ContainerString
                FROM Armor;";

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
                    ContainerString
                FROM Weapons;";

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
                    END AS Ingredients,
                    CASE WHEN IsEdited = 1 AND IsEditedPerk IS NOT NULL 
                         THEN IsEditedPerk
                         ELSE Perk 
                    END AS Perk
                FROM COBJ;";

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
                    PerkKey = reader.IsDBNull(6) ? "" : reader.GetString(6)
                });
            }

            return list;
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
                FROM Enchantments;";

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
                FROM Container;";

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
                FROM MagicEffects;";

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

        // ============================
        //        SEARCH‑API
        // ============================

        public ArmorRecord? GetArmor(string key)
        {
            LoadCache();
            return _armorByKey.TryGetValue(key, out var rec) ? rec : null;
        }

        public WeaponRecord? GetWeapon(string key)
        {
            LoadCache();
            return _weaponByKey.TryGetValue(key, out var rec) ? rec : null;
        }

        public COBJRecord? GetCOBJ(string key)
        {
            LoadCache();
            return _cobjByKey.TryGetValue(key, out var rec) ? rec : null;
        }

        public List<COBJRecord> GetCOBJByCreatedItem(string createdKey)
        {
            LoadCache();
            return _cobjByCreatedItem.TryGetValue(createdKey, out var list)
                ? list
                : new List<COBJRecord>();
        }

        public List<ArmorRecord> SearchArmorByName(string name)
        {
            LoadCache();
            return _armorCache
                .Where(a => a.Name.Contains(name, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        public List<ArmorRecord> GetKeyFromPlugin(string plugin)
        {
            LoadCache();
            return _armorCache
                .Where(a => ExtractPlugin(a.Key) == plugin)
                .ToList();
        }

        public List<WeaponRecord> SearchWeaponsByName(string name)
        {
            LoadCache();
            return _weaponCache
                .Where(w => w.Name.Contains(name, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        public List<COBJRecord> SearchCOBJByName(string name)
        {
            LoadCache();
            return _cobjCache
                .Where(c => c.Name.Contains(name, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        public List<ArmorRecord> SearchArmorByKeyword(string keywordKey)
        {
            LoadCache();
            return _armorCache
                .Where(a => a.Keywords.Contains(keywordKey))
                .ToList();
        }

        public List<WeaponRecord> SearchWeaponsByKeyword(string keywordKey)
        {
            LoadCache();
            return _weaponCache
                .Where(w => w.Keywords.Contains(keywordKey))
                .ToList();
        }

        public List<COBJRecord> SearchCOBJByWorkbench(string workbenchKey)
        {
            LoadCache();
            return _cobjCache
                .Where(c => c.WorkbenchKeywordKey == workbenchKey)
                .ToList();
        }

        public List<COBJRecord> SearchCOBJByIngredient(string ingredientKey)
        {
            LoadCache();
            return _cobjCache
                .Where(c => c.IngredientKeys.Any(i => i.StartsWith(ingredientKey)))
                .ToList();
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

        public EnchantmentRecord? GetEnchantment(string key)
        {
            LoadCache();
            return _enchantmentByKey.TryGetValue(key, out var rec) ? rec : null;
        }

        public List<EnchantmentEffectRecord> GetEffectsForEnchantment(string key)
        {
            LoadCache();
            return _effectsByEnchantment.TryGetValue(key, out var list)
                ? list
                : new List<EnchantmentEffectRecord>();
        }

        public List<string> GetWornRestrictionsForEnchantment(string key)
        {
            LoadCache();

            if (!_enchantmentByKey.TryGetValue(key, out var ench))
                return new List<string>();

            if (string.IsNullOrWhiteSpace(ench.WornRestrictionListKey))
                return new List<string>();

            return _wornRestrictionKeywords.TryGetValue(ench.WornRestrictionListKey, out var list)
                ? list
                : new List<string>();
        }

        public List<EnchantmentRecord> SearchEnchantmentsByName(string name)
        {
            LoadCache();
            return _enchantmentCache
                .Where(e => e.Name.Contains(name, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        public List<EnchantmentRecord> SearchEnchantmentsByKeyword(string keywordKey)
        {
            LoadCache();
            return _enchantmentCache
                .Where(e =>
                {
                    var wr = GetWornRestrictionsForEnchantment(e.Key);
                    return wr.Contains(keywordKey);
                })
                .ToList();
        }

        public List<EnchantmentRecord> SearchEnchantmentsByAllowedItemType(string itemKeyword)
        {
            LoadCache();
            return _enchantmentCache
                .Where(e =>
                {
                    var wr = GetWornRestrictionsForEnchantment(e.Key);
                    return wr.Contains(itemKeyword);
                })
                .ToList();
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
        //Armor
        public static void UpdateArmorName(string key, string name)
        {
            using var conn = new SqliteConnection(ConnString);
            conn.Open();
            using var cmd = new SqliteCommand("UPDATE Armor SET IsEditedName = @val, IsEdited = 1 WHERE Key = @key", conn);
            cmd.Parameters.AddWithValue("@key", key);
            cmd.Parameters.AddWithValue("@val", name);
            cmd.ExecuteNonQuery();
        }

        public static void UpdateArmorWeight(string key, double weight)
        {
            using var conn = new SqliteConnection(ConnString);
            conn.Open();
            using var cmd = new SqliteCommand("UPDATE Armor SET IsEditedWeight = @val, IsEdited = 1 WHERE Key = @key", conn);
            cmd.Parameters.AddWithValue("@key", key);
            cmd.Parameters.AddWithValue("@val", weight);
            cmd.ExecuteNonQuery();
        }

        public static void UpdateArmorValue(string key, int value)
        {
            using var conn = new SqliteConnection(ConnString);
            conn.Open();
            using var cmd = new SqliteCommand("UPDATE Armor SET IsEditedValue = @val, IsEdited = 1 WHERE Key = @key", conn);
            cmd.Parameters.AddWithValue("@key", key);
            cmd.Parameters.AddWithValue("@val", value);
            cmd.ExecuteNonQuery();

        }

        public static void UpdateArmorRating(string key, double armorRating)
        {
            using var conn = new SqliteConnection(ConnString);
            conn.Open();
            using var cmd = new SqliteCommand("UPDATE Armor SET IsEditedArmorRating = @val, IsEdited = 1 WHERE Key = @key", conn);
            cmd.Parameters.AddWithValue("@key", key);
            cmd.Parameters.AddWithValue("@val", armorRating);
            cmd.ExecuteNonQuery();
        }

        public static void UpdateArmorBodySlotMask(string key, long bodySlotMask)
        {
            using var conn = new SqliteConnection(ConnString);
            conn.Open();
            using var cmd = new SqliteCommand("UPDATE Armor SET IsEditedBodySlotMask = @val, IsEdited = 1 WHERE Key = @key", conn);
            cmd.Parameters.AddWithValue("@key", key);
            cmd.Parameters.AddWithValue("@val", bodySlotMask);
            cmd.ExecuteNonQuery();
        }

        public static void UpdateArmorKeywords(string key, ObservableCollection<KeywordSelectionVM> keywords)
        {
            var csv = string.Join(",", keywords.Where(k => k.IsSelected).Select(k => k.Key));

            using var conn = new SqliteConnection(ConnString);
            conn.Open();
            using var cmd = new SqliteCommand(
                "UPDATE Armor SET IsEditedKeywords = @val, IsEdited = 1 WHERE Key = @key", conn);

            cmd.Parameters.AddWithValue("@key", key);
            cmd.Parameters.AddWithValue("@val", csv);
            cmd.ExecuteNonQuery();
        }

        // Weapon
        public static void UpdateWeaponName(string key, string name)
        {
            using var conn = new SqliteConnection(ConnString);
            conn.Open();
            using var cmd = new SqliteCommand("UPDATE Weapons SET IsEditedName = @val, IsEdited = 1 WHERE Key = @key", conn);
            cmd.Parameters.AddWithValue("@key", key);
            cmd.Parameters.AddWithValue("@val", name);
            cmd.ExecuteNonQuery();
        }

        public static void UpdateWeaponWeight(string key, double weight)
        {
            using var conn = new SqliteConnection(ConnString);
            conn.Open();
            using var cmd = new SqliteCommand("UPDATE Weapons SET IsEditedWeight = @val, IsEdited = 1 WHERE Key = @key", conn);
            cmd.Parameters.AddWithValue("@key", key);
            cmd.Parameters.AddWithValue("@val", weight);
            cmd.ExecuteNonQuery();
        }

        public static void UpdateWeaponValue(string key, int value)
        {
            using var conn = new SqliteConnection(ConnString);
            conn.Open();
            using var cmd = new SqliteCommand("UPDATE Weapons SET IsEditedValue = @val, IsEdited = 1 WHERE Key = @key", conn);
            cmd.Parameters.AddWithValue("@key", key);
            cmd.Parameters.AddWithValue("@val", value);
            cmd.ExecuteNonQuery();

        }

        public static void UpdateWeaponDamage(string key, double damage)
        {
            using var conn = new SqliteConnection(ConnString);
            conn.Open();
            using var cmd = new SqliteCommand("UPDATE Weapons SET IsEditedDamage = @val, IsEdited = 1 WHERE Key = @key", conn);
            cmd.Parameters.AddWithValue("@key", key);
            cmd.Parameters.AddWithValue("@val", damage);
            cmd.ExecuteNonQuery();
        }

        public static void UpdateWeaponSpeed(string key, double speed)
        {
            using var conn = new SqliteConnection(ConnString);
            conn.Open();
            using var cmd = new SqliteCommand("UPDATE Weapons SET IsEditedSpeed = @val, IsEdited = 1 WHERE Key = @key", conn);
            cmd.Parameters.AddWithValue("@key", key);
            cmd.Parameters.AddWithValue("@val", speed);
            cmd.ExecuteNonQuery();
        }

        public static void UpdateWeaponReach(string key, double reach)
        {
            using var conn = new SqliteConnection(ConnString);
            conn.Open();
            using var cmd = new SqliteCommand("UPDATE Weapons SET IsEditedReach = @val, IsEdited = 1 WHERE Key = @key", conn);
            cmd.Parameters.AddWithValue("@key", key);
            cmd.Parameters.AddWithValue("@val", reach);
            cmd.ExecuteNonQuery();
        }

        public static void UpdateWeaponStagger(string key, double stagger)
        {
            using var conn = new SqliteConnection(ConnString);
            conn.Open();
            using var cmd = new SqliteCommand("UPDATE Weapons SET IsEditedStagger = @val, IsEdited = 1 WHERE Key = @key", conn);
            cmd.Parameters.AddWithValue("@key", key);
            cmd.Parameters.AddWithValue("@val", stagger);
            cmd.ExecuteNonQuery();
        }

        public static void UpdateWeaponKeywords(string key, ObservableCollection<KeywordSelectionVM> keywords)
        {
            var csv = string.Join(",", keywords.Where(k => k.IsSelected).Select(k => k.Key));

            using var conn = new SqliteConnection(ConnString);
            conn.Open();
            using var cmd = new SqliteCommand(
                "UPDATE Weapons SET IsEditedKeywords = @val, IsEdited = 1 WHERE Key = @key", conn);

            cmd.Parameters.AddWithValue("@key", key);
            cmd.Parameters.AddWithValue("@val", csv);
            cmd.ExecuteNonQuery();
        }

        // -------------------------------------------------
        // COBJ: NAME / CREATEDITEM / WORKBENCH
        // -------------------------------------------------
        public static void UpdateCOBJName(string key, string name)
        {
            using var conn = new SqliteConnection(ConnString);
            conn.Open();
            using var cmd = new SqliteCommand(
                "UPDATE COBJ SET IsEditedName = @val, IsEdited = 1 WHERE Key = @key", conn);
            cmd.Parameters.AddWithValue("@key", key);
            cmd.Parameters.AddWithValue("@val", name);
            cmd.ExecuteNonQuery();
        }

        public static void UpdateCOBJCreatedItem(string key, string createdItemKey)
        {
            using var conn = new SqliteConnection(ConnString);
            conn.Open();
            using var cmd = new SqliteCommand(
                "UPDATE COBJ SET IsEditedCreatedItem = @val, IsEdited = 1 WHERE Key = @key", conn);
            cmd.Parameters.AddWithValue("@key", key);
            cmd.Parameters.AddWithValue("@val", createdItemKey);
            cmd.ExecuteNonQuery();
        }

        public static void UpdateCOBJWorkbenchKeyword(string key, string workbenchKeywordKey)
        {
            using var conn = new SqliteConnection(ConnString);
            conn.Open();
            using var cmd = new SqliteCommand(
                "UPDATE COBJ SET IsEditedWorkbenchKeyword = @val, IsEdited = 1 WHERE Key = @key", conn);
            cmd.Parameters.AddWithValue("@key", key);
            cmd.Parameters.AddWithValue("@val", workbenchKeywordKey);
            cmd.ExecuteNonQuery();
        }


        // -------------------------------------------------
        // COBJ: INGREDIENTS 
        // -------------------------------------------------
        public void UpdateCOBJIngredients(string cobjKey, IEnumerable<IngredientEntryVM> ingredients)
        {
            using var connection = new SqliteConnection(ConnString);
            connection.Open();

            var csv = string.Join(",", ingredients.Select(i => $"{i.Key}*{i.Count}"));

            using var updateCmd = connection.CreateCommand();
            updateCmd.CommandText =
                @"UPDATE COBJ SET
                    IsEdited = 1,
                    IsEditedIngredients = $Ingredients
                WHERE Key = $Key;";

            updateCmd.Parameters.AddWithValue("$Key", cobjKey);
            updateCmd.Parameters.AddWithValue("$Ingredients", csv);
            updateCmd.ExecuteNonQuery();
        }


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
                    Perk,
                    Ingredients,
                    IsEdited
                ) VALUES (
                    $Key,
                    0,
                    $Name,
                    $CreatedItem,
                    $WorkbenchKeyword,
                    $Perk,
                    $Ingredients,
                    1
                );";

            cmd.Parameters.AddWithValue("$Key", rec.Key);
            cmd.Parameters.AddWithValue("$Name", rec.Name);
            cmd.Parameters.AddWithValue("$CreatedItem", rec.CreatedItemKey);
            cmd.Parameters.AddWithValue("$WorkbenchKeyword", rec.WorkbenchKeywordKey);
            cmd.Parameters.AddWithValue("$Perk", rec.PerkKey);
            cmd.Parameters.AddWithValue("$Ingredients", string.Join(",", rec.IngredientKeys));

            cmd.ExecuteNonQuery();

            using var updateCmd = connection.CreateCommand();
            updateCmd.CommandText =
                @"UPDATE COBJ SET Original = 1 WHERE Key = $Key;";
            updateCmd.Parameters.AddWithValue("$Key", rec.Key);
            updateCmd.ExecuteNonQuery();

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
                    IsEditedPerk = $Perk,
                    IsEditedIngredients = $Ingredients
                WHERE Key = $Key;";

            cmd.Parameters.AddWithValue("$Key", rec.Key);
            cmd.Parameters.AddWithValue("$Name", rec.Name);
            cmd.Parameters.AddWithValue("$CreatedItem", rec.CreatedItemKey);
            cmd.Parameters.AddWithValue("$WorkbenchKeyword", rec.WorkbenchKeywordKey);
            cmd.Parameters.AddWithValue("$Perk", rec.PerkKey);
            cmd.Parameters.AddWithValue("$Ingredients", string.Join(",", rec.IngredientKeys));

            cmd.ExecuteNonQuery();
        }



        // -------------------------------------------------
        // COBJ: SAVE (auto save for Insert/Update)
        // -------------------------------------------------
        public void SaveCOBJ(COBJRecord rec)
        {
            if (rec.Original == 0)
                InsertCOBJ(rec);
            else
                UpdateCOBJ(rec);
        }


        // -------------------------------------------------
        // COBJ: create new recipe
        // -------------------------------------------------
        public COBJRecord CreateNewCOBJRecordForItem(ItemNodeVM item, bool isTemper)
        {
            string pluginName = "SkyrimCraftingTool.esp";
            // FormID generator
            string FormID = Count().ToString("X6");
            string newKey = pluginName + "|" + FormID;
            foreach (var existingCOBJ in _cobjCache)
            {
                if (existingCOBJ.Key == newKey)
                {
                    // Handle the case where the generated key already exists
                    FormID = Count().ToString("X6");
                    newKey = pluginName + "|" + FormID;
                }
            }
            Debug.WriteLine(newKey);


            string newName = item.Name;

            string workbenchKeyword = isTemper
                ? "Skyrim.esm|088108"   // Temper
                : "Skyrim.esm|0ADB78";  // Crafting
            if(isTemper == true)
            {
                if (item.IsArmor)
                {
                    workbenchKeyword = "Skyrim.esm|088108";
                } else
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
                PerkKey = "",
                Original = 0
            };

            return rec;
        }

        public int Count()
        {
            _count++; 
            return _count;
        }
        // Perk
        public static void UpdateCOBJPerk(string key, string perkKey)
        {
            using var conn = new SqliteConnection(ConnString);
            conn.Open();
            using var cmd = new SqliteCommand(
                "UPDATE COBJ SET IsEditedPerk = @val, IsEdited = 1 WHERE Key = @key", conn);
            cmd.Parameters.AddWithValue("@key", key);
            cmd.Parameters.AddWithValue("@val", perkKey);
            cmd.ExecuteNonQuery();
        }

        // Container
        public static void UpdateArmorContainerString(string itemKey, string containerString)
        {
            try
            {
                var dbPath = Path.Combine(GlobalState.Tool.InputFolder, "Item", "item.db");
                using var conn = new SqliteConnection($"Data Source={dbPath}");
                conn.Open();

                using var cmd = conn.CreateCommand();
                cmd.CommandText = "UPDATE Armor SET ContainerString = @container WHERE Key = @key;";
                cmd.Parameters.AddWithValue("@container", containerString ?? "");
                cmd.Parameters.AddWithValue("@key", itemKey ?? "");

                int rows = cmd.ExecuteNonQuery();
                Debug.WriteLine($"[DB] UpdateArmorContainerString rows={rows} key={itemKey}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[DB ERROR] UpdateArmorContainerString: {ex.Message}");
                throw;
            }
        }

        public static void UpdateWeaponContainerString(string itemKey, string containerString)
        {
            try
            {
                var dbPath = Path.Combine(GlobalState.Tool.InputFolder, "Item", "item.db");
                using var conn = new SqliteConnection($"Data Source={dbPath}");
                conn.Open();

                using var cmd = conn.CreateCommand();
                cmd.CommandText = "UPDATE Weapons SET ContainerString = @container WHERE Key = @key;";
                cmd.Parameters.AddWithValue("@container", containerString ?? "");
                cmd.Parameters.AddWithValue("@key", itemKey ?? "");

                int rows = cmd.ExecuteNonQuery();
                Debug.WriteLine($"[DB] UpdateWeaponContainerString rows={rows} key={itemKey}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[DB ERROR] UpdateWeaponContainerString: {ex.Message}");
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
    }
}
