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
        // What the last RefreshPluginDatabase had to decide between. Empty on a setup where no
        // plugin file name occurs twice; one entry per duplicated name otherwise. Kept so the scan
        // report can name them instead of resolving them silently.
        public IReadOnlyList<Services.PluginPathConflict> LastPathConflicts { get; private set; }
            = System.Array.Empty<Services.PluginPathConflict>();

        public void RefreshPluginDatabase()
        {
            // 1. Read plugins from plugins.txt
            var activeNames = GetPluginsFromTxt();

            // 2. Search the disk for the real paths
            var allFoundFiles = ScanFileSystemForPlugins(activeNames);

            // 3. Reduce to ONE path per file name.
            //
            // Without this step a plugin that exists in two places was handed to the scan twice and
            // written twice, and the winner was whatever SQLite happened to return first - see
            // Services/PluginPathPicker for the measurements. Everything downstream
            // (GetActivePlugins -> PluginInfo.FullPaths -> SelectMany in both scans) assumes this
            // list is already resolved.
            var selection = Services.PluginPathPicker.PickOnePerFileName(
                allFoundFiles,
                GlobalState.GameDataPath,
                GlobalState.ModDirectoryPath,
                GlobalState.ManagerKind,
                Services.ModListOrder.Read(GlobalState.ModListFilePath));

            LastPathConflicts = selection.Conflicts;
            LogPathConflicts(selection.Conflicts);

            // 4. Sync the database
            SyncDatabase(activeNames, selection.Chosen);
        }

        private static void LogPathConflicts(IReadOnlyList<Services.PluginPathConflict> conflicts)
        {
            if (conflicts == null || conflicts.Count == 0) return;

            // One summary line plus the detail, because on a big modlist this is routinely dozens of
            // entries (96 on the setup it was found on - every vanilla master and every Creation
            // Club file) and a per-line warning would bury everything else in the log.
            AppLogger.LogWarning(
                $"Plugin paths: {conflicts.Count} file name(s) exist more than once; one copy was chosen per name.");

            foreach (var c in conflicts)
                AppLogger.LogWarning($"  {c.FileName}: using '{c.Chosen}' ({c.Reason}); ignored {c.Discarded.Count} other copy/copies.");
        }

        private List<string> GetPluginsFromTxt()
        {
            var pluginsTxt = GlobalState.PluginsFilePath;
            if (!File.Exists(pluginsTxt)) return VanillaPluginNames.ToList();

            return OrderWithVanillaFirst(File.ReadAllLines(pluginsTxt));
        }

        // Split out of GetPluginsFromTxt so it can be tested without a database - the constructor
        // opens plugins.db, and this rule was wrong for a long time without anything catching it.
        //
        // THE ORDER IS NOT COSMETIC. This list is what ExecuteFullScanAsync hands to both
        // FormIdService.PutIntoDataBank and ItemService.PutIntoDataBank, and the scan resolves a
        // conflict by "last plugin wins" (latestCobjByKey[key] = cobj and friends). Hand it the
        // wrong order and the wrong plugin's version of a record ends up in the database.
        //
        // What it used to do: `foreach (var v in VanillaPluginNames) names.Insert(0, v);` - each
        // master pushed in front of the one before it, so the five came out REVERSED
        // (Dragonborn, HearthFires, Dawnguard, Update, Skyrim). Skyrim.esm therefore won every
        // record the DLCs and Update.esm override, which is the exact opposite of the load order
        // and defeats the entire purpose of Update.esm. Visible in the item tree as well, which is
        // how it was spotted.
        //
        // Mods were never affected: they come out of plugins.txt in its own order and sit after
        // this block, so they still beat vanilla.
        internal static List<string> OrderWithVanillaFirst(IEnumerable<string> pluginsTxtLines)
        {
            var names = pluginsTxtLines
                .Where(l => !string.IsNullOrWhiteSpace(l) && l.StartsWith("*"))
                .Select(l => l.TrimStart('*').Trim())
                .ToList();

            // The vanilla masters are implicit - Skyrim SE's plugins.txt does not list them - so they
            // go in front of everything plugins.txt does name, in one go and in their own order.
            names.InsertRange(0, VanillaPluginNames
                .Where(v => !names.Contains(v, StringComparer.OrdinalIgnoreCase)));

            return names;
        }

        // The three extensions a plugin can have. The "*.es*" wildcard alone is NOT this list:
        // Windows matches it against "Skyrim.esm.ini" and ".eslcache" too, and those used to be
        // collected and carried around until the file-name filter further down happened to drop
        // them. Checking the real extension keeps the junk out of the candidate set entirely.
        private static readonly string[] PluginExtensions = { ".esm", ".esp", ".esl" };

        private List<string> ScanFileSystemForPlugins(List<string> filterList)
        {
            var paths = new List<string>();

            // Distinct: under Vortex and a manual install the mod folder IS the game folder, so
            // without this every file there is found twice.
            var searchDirs = new[] { GlobalState.GameDataPath, GlobalState.ModDirectoryPath }
                .Where(d => !string.IsNullOrWhiteSpace(d))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(Directory.Exists);

            var wanted = new HashSet<string>(filterList ?? new List<string>(), StringComparer.OrdinalIgnoreCase);

            foreach (var dir in searchDirs)
            {
                var files = Directory.GetFiles(dir, "*.es*", SearchOption.AllDirectories);

                foreach (var file in files)
                {
                    if (!PluginExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
                        continue;

                    // filterList was accepted and then ignored here; the filtering happened only in
                    // SyncDatabase. Doing it now means the duplicate resolution below never has to
                    // rank copies of a plugin that is not even active.
                    if (wanted.Count > 0 && !wanted.Contains(Path.GetFileName(file)))
                        continue;

                    paths.Add(file);
                }
            }
            return paths;
        }

        private void SyncDatabase(List<string> activeNames, IReadOnlyList<string> foundPaths)
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
