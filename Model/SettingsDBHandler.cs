using Microsoft.Data.Sqlite;
using System.IO;

namespace SkyrimCraftingTool.Model
{
    public class SettingsDBHandler
    {
        private string SettingsFolder => Path.Combine(GlobalState.Tool.InputFolder, "Settings");
        private string SettingsDbPath => Path.Combine(SettingsFolder, "settings.db");

        // ============================
        // CACHE
        // ============================
        private List<AutoApplyProfile> _profileCache = new();
        private Dictionary<string, AutoApplyProfile> _profileByName = new();
        private bool _cacheLoaded = false;

        public SettingsDBHandler()
        {
            // Sicherstellen, dass die DB und Tabellen beim Start existieren
            InitializeDatabase();
        }

        private void InvalidateCache()
        {
            _cacheLoaded = false;
            _profileCache.Clear();
            _profileByName.Clear();
        }

        private void LoadCache()
        {
            if (_cacheLoaded) return;

            _profileCache = LoadAllProfilesFromDb();
            _profileByName = _profileCache.ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);

            _cacheLoaded = true;
        }

        // ============================
        // DB INITIALISIERUNG
        // ============================
        private void InitializeDatabase()
        {
            Directory.CreateDirectory(SettingsFolder);

            using var connection = new SqliteConnection($"Data Source={SettingsDbPath}");
            connection.Open();

            // WICHTIG: "IF NOT EXISTS", damit Benutzereinstellungen beim Start nicht gelöscht werden!
            using var cmd = connection.CreateCommand();
            cmd.CommandText = @"
                CREATE TABLE IF NOT EXISTS Profiles (
                    ProfileId INTEGER PRIMARY KEY AUTOINCREMENT,
                    ProfileName TEXT NOT NULL UNIQUE
                );

                CREATE TABLE IF NOT EXISTS SlotSettings (
                    SettingId INTEGER PRIMARY KEY AUTOINCREMENT,
                    ProfileId INTEGER NOT NULL,
                    SlotNumber INTEGER NOT NULL,
                    Weight REAL NOT NULL,
                    Value INTEGER NOT NULL,
                    FOREIGN KEY(ProfileId) REFERENCES Profiles(ProfileId) ON DELETE CASCADE
                );";
            cmd.ExecuteNonQuery();
        }

        // ============================
        // PROFIL SPEICHERN (CREATE / UPDATE)
        // ============================
        public void SaveProfile(AutoApplyProfile profile)
        {
            using var connection = new SqliteConnection($"Data Source={SettingsDbPath}");
            connection.Open();
            using var transaction = connection.BeginTransaction();

            try
            {
                long profileId;

                // 1. Prüfen, ob das Profil bereits existiert
                using (var checkCmd = connection.CreateCommand())
                {
                    checkCmd.Transaction = transaction;
                    checkCmd.CommandText = "SELECT ProfileId FROM Profiles WHERE ProfileName = @name";
                    checkCmd.Parameters.AddWithValue("@name", profile.Name);
                    var result = checkCmd.ExecuteScalar();

                    if (result != null)
                    {
                        profileId = (long)result;

                        // Wenn es existiert: Alte Slot-Settings löschen, um sie gleich neu zu schreiben
                        using var deleteCmd = connection.CreateCommand();
                        deleteCmd.Transaction = transaction;
                        deleteCmd.CommandText = "DELETE FROM SlotSettings WHERE ProfileId = @pId";
                        deleteCmd.Parameters.AddWithValue("@pId", profileId);
                        deleteCmd.ExecuteNonQuery();
                    }
                    else
                    {
                        // Wenn es neu ist: Profilnamen in Haupttabelle einfügen
                        using var insertCmd = connection.CreateCommand();
                        insertCmd.Transaction = transaction;
                        insertCmd.CommandText = "INSERT INTO Profiles (ProfileName) VALUES (@name); SELECT last_insert_rowid();";
                        insertCmd.Parameters.AddWithValue("@name", profile.Name);
                        profileId = (long)insertCmd.ExecuteScalar();
                    }
                }

                // 2. Die einzelnen Slot-Einstellungen einfügen (analog zu deiner PutIntoDataBank)
                using (var insertSlot = connection.CreateCommand())
                {
                    insertSlot.Transaction = transaction;
                    insertSlot.CommandText = @"
                        INSERT INTO SlotSettings (ProfileId, SlotNumber, Weight, Value) 
                        VALUES (@pId, @slot, @weight, @val)";

                    insertSlot.Parameters.Add("@pId", SqliteType.Integer).Value = profileId;
                    insertSlot.Parameters.Add("@slot", SqliteType.Integer);
                    insertSlot.Parameters.Add("@weight", SqliteType.Real);
                    insertSlot.Parameters.Add("@val", SqliteType.Integer);

                    foreach (var slotSetting in profile.SlotSettings)
                    {
                        insertSlot.Parameters["@slot"].Value = slotSetting.SlotNumber;
                        insertSlot.Parameters["@weight"].Value = slotSetting.Weight;
                        insertSlot.Parameters["@val"].Value = slotSetting.Value;
                        insertSlot.ExecuteNonQuery();
                    }
                }

                transaction.Commit();
                InvalidateCache();
            }
            catch
            {
                transaction.Rollback();
                throw;
            }
        }

        // ============================
        // PROFIL LÖSCHEN
        // ============================
        public void DeleteProfile(string profileName)
        {
            using var connection = new SqliteConnection($"Data Source={SettingsDbPath}");
            connection.Open();
            using var cmd = connection.CreateCommand();

            // Dank "ON DELETE CASCADE" im DB-Schema löscht SQLite die SlotSettings automatisch mit!
            cmd.CommandText = "DELETE FROM Profiles WHERE ProfileName = @name";
            cmd.Parameters.AddWithValue("@name", profileName);
            cmd.ExecuteNonQuery();

            InvalidateCache();
        }

        // ============================
        // LOAD FROM DB
        // ============================
        private List<AutoApplyProfile> LoadAllProfilesFromDb()
        {
            var idToProfile = new Dictionary<long, AutoApplyProfile>();

            if (!File.Exists(SettingsDbPath))
            {
                return new List<AutoApplyProfile>();
            }

            using var connection = new SqliteConnection($"Data Source={SettingsDbPath}");
            connection.Open();

            // Lädt alle Profile und deren Slots effizient über einen einzigen LEFT JOIN
            using var cmd = connection.CreateCommand();
            cmd.CommandText = @"
                SELECT p.ProfileId, p.ProfileName, s.SlotNumber, s.Weight, s.Value
                FROM Profiles p
                LEFT JOIN SlotSettings s ON p.ProfileId = s.ProfileId
                ORDER BY p.ProfileName, s.SlotNumber;";

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                long profileId = reader.GetInt64(0);
                string profileName = reader.GetString(1);

                if (!idToProfile.TryGetValue(profileId, out var profile))
                {
                    profile = new AutoApplyProfile
                    {
                        Name = profileName,
                        SlotSettings = new List<SlotSetting>()
                    };
                    idToProfile[profileId] = profile;
                }

                // Falls Slots vorhanden sind (verhindert Absturz bei leeren Profilen)
                if (!reader.IsDBNull(2))
                {
                    profile.SlotSettings.Add(new SlotSetting
                    {
                        SlotNumber = reader.GetInt32(2),
                        Weight = (float)reader.GetDouble(3),
                        Value = reader.GetInt32(4)
                    });
                }
            }

            return idToProfile.Values.ToList();
        }

        // ============================
        // CONFIG-API (SEARCH & RETRIEVAL)
        // ============================

        /// <summary>
        /// Gibt eine Liste aller registrierten Profile zurück (z. B. für Item-Listen in der UI).
        /// </summary>
        public List<AutoApplyProfile> GetAllProfiles()
        {
            LoadCache();
            return _profileCache;
        }

        /// <summary>
        /// Holt ein Profil über den exakten Namen aus dem schnellen Dictionary-Cache.
        /// </summary>
        public AutoApplyProfile? GetProfile(string name)
        {
            LoadCache();
            return _profileByName.TryGetValue(name, out var profile) ? profile : null;
        }

        /// <summary>
        /// Filtert Profile anhand von Texteingaben in der UI.
        /// </summary>
        public List<AutoApplyProfile> SearchProfilesByName(string name)
        {
            LoadCache();
            return _profileCache
                .Where(p => p.Name.Contains(name, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
    }

    // ============================
    // MODEL DATA STRUCTURES
    // ============================
    public class AutoApplyProfile
    {
        public string Name { get; set; } = string.Empty;
        public List<SlotSetting> SlotSettings { get; set; } = new();
    }

    public class SlotSetting
    {
        public int SlotNumber { get; set; } // z. B. 32 (Body), 33 (Hands)
        public float Weight { get; set; }
        public int Value { get; set; }
    }
}
