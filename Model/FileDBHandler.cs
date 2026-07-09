using Microsoft.Data.Sqlite;
using System.IO;

namespace SkyrimCraftingTool.Model
{
    public class FileDBHandler
    {
        private string PluginListFolder => Path.Combine(GlobalState.Tool.InputFolder, "Pluginlist");
        private string DbPath => Path.Combine(PluginListFolder, "plugins.db");

        private static readonly string[] VanillaPluginNames =
        {
            "Skyrim.esm", "Update.esm", "Dawnguard.esm", "HearthFires.esm", "Dragonborn.esm"
        };

        public FileDBHandler()
        {
            InitializeDatabase();
        }

        private void InitializeDatabase()
        {
            if (!Directory.Exists(PluginListFolder))
                Directory.CreateDirectory(PluginListFolder);

            using var connection = new SqliteConnection($"Data Source={DbPath}");
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = @"
                CREATE TABLE IF NOT EXISTS Plugins (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    FileName TEXT NOT NULL,
                    FullPath TEXT NOT NULL,
                    Active INTEGER NOT NULL DEFAULT 1,
                    UNIQUE(FileName, FullPath)
                );";
            cmd.ExecuteNonQuery();
        }

        // ---------------------------------------------------------
        // HAUPT-LOGIK: SCAN & SYNC
        // ---------------------------------------------------------

        /// <summary>
        /// Führt den kompletten Scan-Prozess durch und aktualisiert die Datenbank.
        /// </summary>
        public void RefreshPluginDatabase()
        {
            // 1. Plugins aus der plugins.txt lesen
            var activeNames = GetPluginsFromTxt();

            // 2. Festplatte nach den echten Pfaden absuchen
            var allFoundFiles = ScanFileSystemForPlugins(activeNames);

            // 3. Datenbank synchronisieren
            SyncDatabase(activeNames, allFoundFiles);

        }

        private List<string> GetPluginsFromTxt()
        {
            var pluginsTxt = GlobalState.PluginsFilePath;
            if (!File.Exists(pluginsTxt)) return VanillaPluginNames.ToList();

            var names = File.ReadAllLines(pluginsTxt)
                .Where(l => !string.IsNullOrWhiteSpace(l) && l.StartsWith("*"))
                .Select(l => l.TrimStart('*').Trim())
                .ToList();

            // Vanilla immer hinzufügen, falls nicht in Liste
            foreach (var v in VanillaPluginNames)
            {
                if (!names.Contains(v, StringComparer.OrdinalIgnoreCase))
                    names.Insert(0, v);
            }
            return names;
        }

        private List<string> ScanFileSystemForPlugins(List<string> filterList)
        {
            var paths = new List<string>();
            var searchDirs = new[] { GlobalState.GameDataPath, GlobalState.ModDirectoryPath };

            foreach (var dir in searchDirs.Where(Directory.Exists))
            {
                // Wir suchen alle .es* Dateien
                var files = Directory.GetFiles(dir, "*.es*", SearchOption.AllDirectories);
                paths.AddRange(files);
            }
            return paths;
        }

        private void SyncDatabase(List<string> activeNames, List<string> foundPaths)
        {
            using var connection = new SqliteConnection($"Data Source={DbPath}");
            connection.Open();
            using var transaction = connection.BeginTransaction();

            try
            {
                // 1. Alle Plugins auf inaktiv setzen
                using (var cmd = connection.CreateCommand())
                {
                    cmd.CommandText = "UPDATE Plugins SET Active = 0;";
                    cmd.ExecuteNonQuery();
                }

                // 2. Pfade abgleichen und in DB eintragen / auf aktiv setzen
                string upsertSql = @"
                    INSERT INTO Plugins (FileName, FullPath, Active) 
                    VALUES (@name, @path, 1)
                    ON CONFLICT(FileName, FullPath) DO UPDATE SET Active = 1;";

                foreach (var fullPath in foundPaths)
                {
                    string fileName = Path.GetFileName(fullPath);

                    // Nur verarbeiten, wenn das Plugin in unserer aktiven Liste ist
                    if (activeNames.Contains(fileName, StringComparer.OrdinalIgnoreCase))
                    {
                        using var cmd = new SqliteCommand(upsertSql, connection, transaction);
                        cmd.Parameters.AddWithValue("@name", fileName);
                        cmd.Parameters.AddWithValue("@path", fullPath);
                        cmd.ExecuteNonQuery();
                    }
                }
                transaction.Commit();
            }
            catch
            {
                transaction.Rollback();
                throw;
            }
        }

        // ---------------------------------------------------------
        // API METHODEN FÜR DAS VIEWMODEL
        // ---------------------------------------------------------

        /// <summary>
        /// Gibt alle Plugins zurück, die aktuell in der plugins.txt aktiv sind.
        /// </summary>
        public List<PluginInfo> GetActivePlugins()
        {
            var results = new List<PluginInfo>();

            using var connection = new SqliteConnection($"Data Source={DbPath}");
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT FileName, FullPath FROM Plugins WHERE Active = 1;";

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                string name = reader.GetString(0);
                string path = reader.GetString(1);

                var existing = results.FirstOrDefault(r => r.FileName == name);
                if (existing != null)
                    existing.FullPaths.Add(path);
                else
                    results.Add(new PluginInfo { FileName = name, FullPaths = new List<string> { path } });
            }

            return results;
        }
    }
}
