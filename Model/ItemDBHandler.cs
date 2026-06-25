using Microsoft.Data.Sqlite;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Skyrim;
using System.IO;
using System.Windows.Media.Media3D;
using static System.Net.Mime.MediaTypeNames;


namespace SkyrimCraftingTool.Model
{
    public class ItemDBHandler
    {
        private string ItemFolder => Path.Combine(GlobalState.Tool.InputFolder, "Item");
        private string ItemdbPath => Path.Combine(ItemFolder, "item.db");

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

            using var transaction = connection.BeginTransaction();
            insertArmor.Transaction = transaction;
            insertWeapon.Transaction = transaction;
            insertCOBJ.Transaction = transaction;
            insertEnch.Transaction = transaction;
            insertEnchEff.Transaction = transaction;
            insertWRK.Transaction = transaction;

            foreach (var plugin in allgamePathfromDB)
            {
                string pluginName = plugin.FileName;

                foreach (var fullPath in plugin.FullPaths)
                {
                    var mod = SkyrimMod.CreateFromBinaryOverlay(fullPath, SkyrimRelease.SkyrimSE);

                    // ARMOR
                    foreach (var armor in mod.Armors.Records)
                    {
                        string key = $"{pluginName}|{armor.FormKey.IDString()}";

                        insertArmor.Parameters["@key"].Value = key;
                        insertArmor.Parameters["@name"].Value = armor.EditorID ?? "";
                        insertArmor.Parameters["@weight"].Value = (float?)armor.Weight ?? 0f;
                        insertArmor.Parameters["@val"].Value = (int?)armor.Value ?? 0;
                        insertArmor.Parameters["@armorRating"].Value = (float?)armor.ArmorRating ?? 0f;
                        //insertArmor.Parameters["@armorType"].Value = armor.BodyTemplate?.ArmorType ?? 0;

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
                        string key = $"{pluginName}|{weap.FormKey.IDString()}";

                        insertWeapon.Parameters["@key"].Value = key;
                        insertWeapon.Parameters["@name"].Value = weap.EditorID ?? "";
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
                        string key = $"{pluginName}|{cobj.FormKey.IDString()}";
                        string createdKey = $"{pluginName}|{cobj.CreatedObject.FormKey.IDString()}";

                        insertCOBJ.Parameters["@key"].Value = key;
                        insertCOBJ.Parameters["@name"].Value = cobj.EditorID ?? "";
                        insertCOBJ.Parameters["@createdItem"].Value = createdKey;

                        string workbench = "";
                        if (cobj.WorkbenchKeyword != null)
                        {
                            var fk = cobj.WorkbenchKeyword.FormKey;
                            string wbPlugin = fk.ModKey.FileName;   // echtes Plugin
                            string wbID = fk.IDString();            // echte LocalFormID
                            workbench = $"{wbPlugin}|{wbID}";
                        }
                        insertCOBJ.Parameters["@workbench"].Value = workbench;

                        var ingredients = cobj.Items?
                            .Select(e =>
                            {
                                var fk = e.Item.Item.FormKey;
                                string ingPlugin = fk.ModKey.FileName;  // echtes Plugin des Ingredients
                                string ingID = fk.IDString();           // echte LocalFormID
                                return $"{ingPlugin}|{ingID}*{e.Item.Count}";
                            })
                            ?? Enumerable.Empty<string>();

                        insertCOBJ.Parameters["@ingredients"].Value = string.Join(",", ingredients);

                        insertCOBJ.ExecuteNonQuery();
                    }

                    // ENCHANTMENTS
                    foreach (var ench in mod.ObjectEffects.Records)
                    {
                        string enchKey = $"{pluginName}|{ench.FormKey.IDString()}";

                        insertEnch.Parameters["@key"].Value = enchKey;
                        insertEnch.Parameters["@name"].Value = ench.EditorID ?? "";
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

                            insertEnchEff.Parameters["@ench"].Value = enchKey;
                            insertEnchEff.Parameters["@mgef"].Value = mgefKey;
                            insertEnchEff.Parameters["@mag"].Value = eff.Data?.Magnitude ?? 0;
                            insertEnchEff.Parameters["@dur"].Value = eff.Data?.Duration ?? 0;
                            insertEnchEff.Parameters["@area"].Value = eff.Data?.Area ?? 0;

                            insertEnchEff.ExecuteNonQuery();
                        }
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
                "(Key, Name, CastType, TargetType, EnchantmentCost, WornRestrictionListKey) " +
                "VALUES (@key, @name, @cast, @target, @cost, @wrestr)",
                connection);

            cmd.Parameters.Add("@key", SqliteType.Text);
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
                "(EnchantmentKey, MagicEffectKey, Magnitude, Duration, Area) " +
                "VALUES (@ench, @mgef, @mag, @dur, @area)",
                connection);

            cmd.Parameters.Add("@ench", SqliteType.Text);
            cmd.Parameters.Add("@mgef", SqliteType.Text);
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
                    Name TEXT NOT NULL,
                    Weight REAL,
                    Value INTEGER,
                    ArmorRating REAL,
                    BodySlotMask INTEGER,
                    Keywords TEXT
                );

                CREATE TABLE Weapons (
                    Key TEXT PRIMARY KEY,
                    Name TEXT NOT NULL,
                    Weight REAL,
                    Value INTEGER,
                    Damage INTEGER,
                    Speed REAL,
                    Reach REAL,
                    Stagger REAL,
                    Keywords TEXT
                );

                CREATE TABLE COBJ (
                    Key TEXT PRIMARY KEY,
                    Name TEXT NOT NULL,
                    CreatedItem TEXT NOT NULL,
                    WorkbenchKeyword TEXT,
                    TemperKeyword TEXT,
                    Ingredients TEXT
                );

                CREATE TABLE Enchantments (
                    Key TEXT PRIMARY KEY,
                    Name TEXT NOT NULL,
                    CastType TEXT,
                    TargetType TEXT,
                    EnchantmentCost REAL,
                    WornRestrictionListKey TEXT
                );

                CREATE TABLE EnchantmentEffects (
                    EnchantmentKey TEXT NOT NULL,
                    MagicEffectKey TEXT NOT NULL,
                    Magnitude REAL,
                    Duration INTEGER,
                    Area INTEGER,
                    PRIMARY KEY (EnchantmentKey, MagicEffectKey)
                );

                CREATE TABLE WornRestrictionKeywords (
                    ListKey TEXT NOT NULL,
                    KeywordKey TEXT NOT NULL,
                    PRIMARY KEY (ListKey, KeywordKey)
                );
            ";
            cmd.ExecuteNonQuery();
        }

        private SqliteCommand PrepareInsertArmor(SqliteConnection connection)
        {
            var cmd = new SqliteCommand(
                "INSERT OR REPLACE INTO Armor " +
                "(Key, Name, Weight, Value, ArmorRating, BodySlotMask, Keywords) " +
                "VALUES (@key, @name, @weight, @val, @armorRating, @slotMask, @keywords)",
                connection);

            cmd.Parameters.Add("@key", SqliteType.Text);
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
                "(Key, Name, Weight, Value, Damage, Speed, Reach, Stagger, Keywords) " +
                "VALUES (@key, @name, @weight, @val, @dmg, @speed, @reach, @stagger, @keywords)",
                connection);

            cmd.Parameters.Add("@key", SqliteType.Text);
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
                "INSERT OR REPLACE INTO COBJ (Key, Name, CreatedItem, WorkbenchKeyword, Ingredients) " +
                "VALUES (@key, @name, @createdItem, @workbench, @ingredients)",
                connection);

            cmd.Parameters.Add("@key", SqliteType.Text);
            cmd.Parameters.Add("@name", SqliteType.Text);
            cmd.Parameters.Add("@createdItem", SqliteType.Text);
            cmd.Parameters.Add("@workbench", SqliteType.Text);
            cmd.Parameters.Add("@ingredients", SqliteType.Text);

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
                "SELECT Key, Name, Weight, Value, ArmorRating, BodySlotMask, Keywords FROM Armor;";

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var keywordsCsv = reader.IsDBNull(6) ? "" : reader.GetString(6);
                var keywords = string.IsNullOrWhiteSpace(keywordsCsv)
                    ? new List<string>()
                    : keywordsCsv.Split(',').ToList();

                list.Add(new ArmorRecord
                {
                    Key = reader.GetString(0),
                    Name = reader.GetString(1),
                    Weight = reader.IsDBNull(2) ? 0f : (float)reader.GetDouble(2),
                    Value = reader.IsDBNull(3) ? 0 : reader.GetInt32(3),
                    ArmorRating = reader.IsDBNull(4) ? 0f : (float)reader.GetDouble(4),

                    // NEW
                    BodySlotMask = reader.IsDBNull(5) ? 0u : (uint)reader.GetInt64(5),

                    Keywords = keywords
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
                "SELECT Key, Name, Weight, Value, Damage, Speed, Reach, Stagger, Keywords FROM Weapons;";

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var keywordsCsv = reader.IsDBNull(8) ? "" : reader.GetString(8);
                var keywords = string.IsNullOrWhiteSpace(keywordsCsv)
                    ? new List<string>()
                    : keywordsCsv.Split(',').ToList();

                list.Add(new WeaponRecord
                {
                    Key = reader.GetString(0),
                    Name = reader.GetString(1),
                    Weight = reader.IsDBNull(2) ? 0f : (float)reader.GetDouble(2),
                    Value = reader.IsDBNull(3) ? 0 : reader.GetInt32(3),
                    Damage = reader.IsDBNull(4) ? 0 : reader.GetInt32(4),

                    // NEW
                    Speed = reader.IsDBNull(5) ? 0f : (float)reader.GetDouble(5),
                    Reach = reader.IsDBNull(6) ? 0f : (float)reader.GetDouble(6),
                    Stagger = reader.IsDBNull(7) ? 0f : (float)reader.GetDouble(7),

                    Keywords = keywords
                });
            }

            return list;
        }


        private List<COBJRecord> LoadCOBJ()
        {
            var list = new List<COBJRecord>();

            // WICHTIG: Wenn keine DB da ist, gib einfach eine leere Liste zurück
            if (!File.Exists(ItemdbPath))
            {
                return list;
            }


            using var connection = new SqliteConnection($"Data Source={ItemdbPath}");
            connection.Open();

            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT Key, Name, CreatedItem, WorkbenchKeyword, Ingredients FROM COBJ;";

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var ingredientsCsv = reader.IsDBNull(4) ? "" : reader.GetString(4);
                var ingredients = string.IsNullOrWhiteSpace(ingredientsCsv)
                    ? new List<string>()
                    : ingredientsCsv.Split(',', StringSplitOptions.RemoveEmptyEntries).ToList();

                list.Add(new COBJRecord
                {
                    Key = reader.GetString(0),
                    Name = reader.GetString(1),
                    CreatedItemKey = reader.GetString(2),
                    WorkbenchKeywordKey = reader.IsDBNull(3) ? "" : reader.GetString(3),
                    IngredientKeys = ingredients
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
                "SELECT Key, Name, CastType, TargetType, EnchantmentCost, WornRestrictionListKey FROM Enchantments;";

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                list.Add(new EnchantmentRecord
                {
                    Key = reader.GetString(0),
                    Name = reader.GetString(1),
                    CastType = reader.IsDBNull(2) ? "" : reader.GetString(2),
                    TargetType = reader.IsDBNull(3) ? "" : reader.GetString(3),
                    EnchantmentCost = reader.IsDBNull(4) ? 0f : (float)reader.GetDouble(4),
                    WornRestrictionListKey = reader.IsDBNull(5) ? "" : reader.GetString(5)
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
                "SELECT EnchantmentKey, MagicEffectKey, Magnitude, Duration, Area FROM EnchantmentEffects;";

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                list.Add(new EnchantmentEffectRecord
                {
                    EnchantmentKey = reader.GetString(0),
                    MagicEffectKey = reader.GetString(1),
                    Magnitude = reader.IsDBNull(2) ? 0f : (float)reader.GetDouble(2),
                    Duration = reader.IsDBNull(3) ? 0 : reader.GetInt32(3),
                    Area = reader.IsDBNull(4) ? 0 : reader.GetInt32(4)
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
                "SELECT ListKey, KeywordKey FROM WornRestrictionKeywords;";

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
            // 1. Basisdaten laden
            var enchantments = LoadEnchantments();

            // 2. Effekte laden
            var effects = LoadEnchantmentEffects();

            // 3. Keywords laden
            var wornKeywords = LoadWornRestrictionKeywords();

            // 4. Effekte zuordnen
            foreach (var ench in enchantments)
            {
                var enchEffects = effects
                    .Where(e => e.EnchantmentKey == ench.Key);

                foreach (var eff in enchEffects)
                    ench.Effects.Add(eff);
            }

            // 5. Keywords zuordnen
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



    }
}
