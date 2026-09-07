using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Microsoft.Data.Sqlite;
using SkyrimCraftingTool.Model;

namespace SkyrimCraftingTool.Services
{
    // Builds the text that goes into a bug report.
    //
    // Everything here is produced LOCALLY and written to a file or the clipboard. The tool never
    // sends anything anywhere - users decide what to share and can read the whole report first.
    //
    // The point is to answer, without a back-and-forth, the questions that actually come up:
    // which build, which mod manager layout, did they rescan since the last schema change, and
    // what did the log say. A report missing any of those costs a round trip per bug.
    public static class DiagnosticsReport
    {
        public static string Build()
        {
            var sb = new StringBuilder();

            sb.AppendLine("=== Skyrim Crafting Tool - diagnostics ===");
            sb.AppendLine($"Generated : {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine();

            AppendSection(sb, "Environment", AppendEnvironment);
            AppendSection(sb, "Paths", AppendPaths);
            AppendSection(sb, "Databases", AppendDatabases);
            AppendSection(sb, "Last patch run", AppendLastPatchRun);
            AppendSection(sb, "Log", sb2 => sb2.AppendLine(AppLogger.ReadTail()));

            return sb.ToString();
        }

        private static void AppendSection(StringBuilder sb, string title, Action<StringBuilder> body)
        {
            sb.AppendLine($"--- {title} " + new string('-', Math.Max(0, 60 - title.Length)));
            try
            {
                body(sb);
            }
            catch (Exception ex)
            {
                // One broken section must not cost the user the rest of the report - that would
                // defeat the whole purpose on exactly the machines where things are going wrong.
                sb.AppendLine($"(could not be collected: {ex.Message})");
            }
            sb.AppendLine();
        }

        private static void AppendEnvironment(StringBuilder sb)
        {
            var asm = Assembly.GetExecutingAssembly();
            var version = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                          ?? asm.GetName().Version?.ToString()
                          ?? "unknown";

            sb.AppendLine($"Tool version : {version}");
            sb.AppendLine($".NET runtime : {Environment.Version}");
            sb.AppendLine($"OS           : {Environment.OSVersion.VersionString} ({(Environment.Is64BitOperatingSystem ? "x64" : "x86")})");
            sb.AppendLine($"Tool folder  : {Redact(AppContext.BaseDirectory)}");
        }

        private static void AppendPaths(StringBuilder sb)
        {
            sb.AppendLine($"Data folder  : {Describe(GlobalState.GameDataPath, isDir: true)}");
            sb.AppendLine($"Mod folder   : {Describe(GlobalState.ModDirectoryPath, isDir: true)}");
            sb.AppendLine($"plugins.txt  : {Describe(GlobalState.PluginsFilePath, isDir: false)}");

            // The commonest support case is a mod-manager layout the tool wasn't pointed at
            // correctly, so say plainly whether each path even exists.
            static string Describe(string? path, bool isDir)
            {
                if (string.IsNullOrWhiteSpace(path)) return "(not set)";
                bool exists = isDir ? Directory.Exists(path) : File.Exists(path);
                return $"{Redact(path)}  [{(exists ? "exists" : "MISSING")}]";
            }
        }

        private static void AppendDatabases(StringBuilder sb)
        {
            var itemDb = Path.Combine(GlobalState.Tool.InputFolder, "Item", "item.db");
            var formIdDb = Path.Combine(GlobalState.Tool.InputFolder, "FormID", "formid.db");

            AppendDbFile(sb, "item.db  ", itemDb);
            AppendDbFile(sb, "formid.db", formIdDb);

            if (!File.Exists(itemDb)) return;

            sb.AppendLine();
            using var c = new SqliteConnection($"Data Source={itemDb}");
            c.Open();

            foreach (var (table, label) in new[]
                     {
                         ("Armor", "Armor"), ("Weapons", "Weapons"), ("COBJ", "Recipes"),
                         ("Enchantments", "Enchantments"),
                     })
            {
                sb.AppendLine($"{label,-14}: {Count(c, $"SELECT COUNT(*) FROM {table} WHERE Active = 1")} active, " +
                              $"{Count(c, $"SELECT COUNT(*) FROM {table} WHERE IsEdited = 1")} edited");
            }

            sb.AppendLine($"{"Conditions",-14}: {Count(c, "SELECT COUNT(*) FROM COBJ_Conditions")} rows");

            // The single most useful line in the whole report right now. The condition columns are
            // added by the schema migration, but they only get FILLED by a rescan - so an empty
            // CompareOperator column means "has not rescanned since updating", which is the first
            // thing to ask about any condition or recipe complaint.
            bool hasOperatorColumn = Count(c,
                "SELECT COUNT(*) FROM pragma_table_info('COBJ_Conditions') WHERE name = 'CompareOperator'") > 0;

            if (!hasOperatorColumn)
            {
                sb.AppendLine($"{"Rescan state",-14}: OLD SCHEMA - migration has not run yet");
            }
            else
            {
                long filled = Count(c,
                    "SELECT COUNT(*) FROM COBJ_Conditions WHERE CompareOperator IS NOT NULL AND CompareOperator <> ''");
                sb.AppendLine($"{"Rescan state",-14}: " + (filled > 0
                    ? $"rescanned since the condition fix ({filled} rows carry operator data)"
                    : "NOT RESCANNED since the condition fix - condition edits will be withheld"));
            }
        }

        // The ACTION NEEDED and NOTE blocks plus every warning from the most recent patch run. This
        // used to be visible only in a message box, which meant asking testers for screenshots.
        private static void AppendLastPatchRun(StringBuilder sb)
        {
            var path = PatchGen.PatchReportText.LastRunPath;
            if (!File.Exists(path))
            {
                sb.AppendLine("(no patch has been generated yet on this install)");
                return;
            }

            sb.AppendLine(File.ReadAllText(path).TrimEnd());
        }

        private static void AppendDbFile(StringBuilder sb, string label, string path)
        {
            if (!File.Exists(path))
            {
                sb.AppendLine($"{label}: MISSING ({Redact(path)})");
                return;
            }

            var fi = new FileInfo(path);
            sb.AppendLine($"{label}: {fi.Length / 1024:N0} KB, last written {fi.LastWriteTime:yyyy-MM-dd HH:mm}");
        }

        private static long Count(SqliteConnection c, string sql)
        {
            try
            {
                using var cmd = c.CreateCommand();
                cmd.CommandText = sql;
                var result = cmd.ExecuteScalar();
                return result == null || result == DBNull.Value ? 0 : Convert.ToInt64(result);
            }
            catch
            {
                return 0; // missing table on an old database - not worth failing the report over
            }
        }

        // Paths are the most useful part of a report and also the part that carries a real name,
        // since Windows profiles are usually the user's. Swap the profile folder for a placeholder:
        // the shape of the path is what matters for diagnosing a mod-manager layout, not who owns it.
        private static string Redact(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;

            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrEmpty(profile) && path.StartsWith(profile, StringComparison.OrdinalIgnoreCase))
                return @"%USERPROFILE%" + path[profile.Length..];

            return path;
        }
    }
}
