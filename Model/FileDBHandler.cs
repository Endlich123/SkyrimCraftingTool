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
        // MAIN LOGIC: SCAN & SYNC
        // ---------------------------------------------------------

        /// <summary>
        /// Runs the complete scan process and updates the database.
        /// </summary>
        public void RefreshPluginDatabase()
        {
            // 1. Read plugins from plugins.txt
            var activeNames = GetPluginsFromTxt();

            // 2. Search the disk for the real paths
            var allFoundFiles = ScanFileSystemForPlugins(activeNames);

            // 3. Sync the database
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

            // Always add vanilla, if not already in the list
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
                // We search for all .es* files
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
                // 1. Set all plugins to inactive
                using (var cmd = connection.CreateCommand())
                {
                    cmd.CommandText = "UPDATE Plugins SET Active = 0;";
                    cmd.ExecuteNonQuery();
                }

                // 2. Match up paths and insert into DB / set active
                string upsertSql = @"
                    INSERT INTO Plugins (FileName, FullPath, Active) 
                    VALUES (@name, @path, 1)
                    ON CONFLICT(FileName, FullPath) DO UPDATE SET Active = 1;";

                foreach (var fullPath in foundPaths)
                {
                    string fileName = Path.GetFileName(fullPath);

                    // Only process if the plugin is in our active list
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
        // API METHODS FOR THE VIEWMODEL
        // ---------------------------------------------------------

        /// <summary>
        /// Returns all plugins currently active in plugins.txt.
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

        public List<PluginInfo> GetActivePluginsInLoadOrder()
        {
            // 1. Get the load order from plugins.txt
            var loadOrderNames = GetPluginsFromTxt(); // exact same order as SSEEdit

            // 2. Get active plugins from DB (contains paths)
            var dbPlugins = GetActivePlugins(); // unsorted

            // 3. Sort by load order
            var sorted = new List<PluginInfo>();

            foreach (var name in loadOrderNames)
            {
                var match = dbPlugins.FirstOrDefault(p =>
                    p.FileName.Equals(name, StringComparison.OrdinalIgnoreCase));

                if (match != null)
                    sorted.Add(match);
            }

            return sorted;
        }

    }
}
