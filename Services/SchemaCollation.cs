using System;
using System.Text.RegularExpressions;

namespace SkyrimCraftingTool.Services
{
    // String surgery on a table's stored CREATE TABLE text, used by the one-shot migration that
    // gives key columns a case-insensitive collation.
    //
    // WHY THIS EXISTS AT ALL: Skyrim treats plugin filenames case-insensitively, so the same plugin
    // legitimately appears as "ccBGSSSE001-Fish.esm" in one mod's master list and
    // "ccbgssse001-fish.esm" in another's. Our keys are "<plugin>|<formid>", SQLite's default TEXT
    // collation is BINARY, and the result is two rows for one record - visible as a duplicate entry
    // in the tree and as a double count in the scan report.
    //
    // SQLite cannot change a column's collation with ALTER TABLE, so the fix is a table rebuild, and
    // a rebuild needs the table's own DDL rather than the one in CreateTables: columns added later
    // by AddColumnIfMissing exist in the live table but not in that literal. Hence taking the text
    // from sqlite_master and editing it here - kept as pure functions so the editing is testable
    // without a database, because getting it wrong means rebuilding a table full of user edits from
    // a broken definition.
    public static class SchemaCollation
    {
        public const string Marker = "COLLATE NOCASE";

        // Matches "<name> <type>" at the start of a column definition line. The type is captured so
        // the collation can be placed right after it, which is where SQLite expects a column
        // constraint - before any PRIMARY KEY / NOT NULL that may follow on the same line.
        private static Regex ColumnDefinition(string column)
            => new(@"(?<indent>^[ \t]*)(?<name>" + Regex.Escape(column) + @")(?<space>[ \t]+)(?<type>TEXT)\b",
                   RegexOptions.IgnoreCase | RegexOptions.Multiline);

        public static bool HasNoCase(string ddl, string column)
        {
            if (string.IsNullOrEmpty(ddl)) return false;

            var m = ColumnDefinition(column).Match(ddl);
            if (!m.Success) return false;

            // Only this column's own definition counts, so look no further than the end of its line.
            int lineEnd = ddl.IndexOf('\n', m.Index);
            var line = lineEnd < 0 ? ddl.Substring(m.Index) : ddl.Substring(m.Index, lineEnd - m.Index);

            return line.Contains(Marker, StringComparison.OrdinalIgnoreCase);
        }

        // Adds COLLATE NOCASE to each named column that is a TEXT column and does not have it yet.
        // A column the table does not have is skipped rather than treated as an error: the same
        // column list is used for several tables, and an older database may predate one of them.
        public static string AddNoCase(string ddl, params string[] columns)
        {
            if (string.IsNullOrWhiteSpace(ddl)) return ddl;

            foreach (var column in columns)
            {
                if (string.IsNullOrWhiteSpace(column) || HasNoCase(ddl, column))
                    continue;

                ddl = ColumnDefinition(column).Replace(
                    ddl, m => $"{m.Groups["indent"].Value}{m.Groups["name"].Value}" +
                              $"{m.Groups["space"].Value}{m.Groups["type"].Value} {Marker}",
                    1);
            }

            return ddl;
        }

        // Points a CREATE TABLE at a different name, so the rebuilt table can be created alongside
        // the original and renamed into place once it is filled.
        public static string RenameTable(string ddl, string from, string to)
        {
            if (string.IsNullOrWhiteSpace(ddl)) return ddl;

            // Anchored at CREATE TABLE so a column or default value that happens to contain the
            // table's name is left alone.
            var pattern = @"(?<head>CREATE\s+TABLE\s+(IF\s+NOT\s+EXISTS\s+)?)(?<name>""?" + Regex.Escape(from) + @"""?)";
            return Regex.Replace(ddl, pattern, m => m.Groups["head"].Value + to,
                                 RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
        }
    }
}
