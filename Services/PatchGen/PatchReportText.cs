using System;
using System.IO;
using System.Linq;
using System.Text;
using SkyrimCraftingTool.Model;

namespace SkyrimCraftingTool.Services.PatchGen
{
    // Renders a PatchGenReport as text, and keeps a copy of the last run on disk.
    //
    // One renderer for both the dialog and the saved file on purpose: the ACTION NEEDED and NOTE
    // blocks are the most useful lines in a bug report, and if the two ever drifted apart, the copy
    // in the report would be the one that got stale.
    //
    // The file exists because those blocks used to live only in a message box - the standing advice
    // to testers was "send me a screenshot of the patch dialog", which is a poor substitute for text
    // and impossible once the box is closed.
    public static class PatchReportText
    {
        private static string LogFolder => Path.Combine(AppContext.BaseDirectory, "Logs");
        public static string LastRunPath => Path.Combine(LogFolder, "last-patch-report.txt");

        // Warnings shown in the dialog. The saved file keeps all of them - a message box with 400
        // lines is useless, a text file with 400 lines is exactly what you want when diagnosing.
        private const int DialogWarningLimit = 15;

        public static string Render(PatchGenReport report, string outputRoot, bool forFile)
        {
            var sb = new StringBuilder();
            sb.AppendLine(report.Summary);
            sb.AppendLine();

            foreach (var f in report.WrittenFiles)
                sb.AppendLine("  " + MakeRelative(outputRoot, f));

            // Above masters and warnings on purpose: an ESP override is not an error, but it is a
            // consequence the user should take in knowingly rather than discover later.
            if (report.StaleScanNotice != null)
            {
                sb.AppendLine();
                sb.AppendLine("ACTION NEEDED");
                sb.AppendLine("  " + report.StaleScanNotice);
            }

            if (report.EspOverrideNotice != null)
            {
                sb.AppendLine();
                sb.AppendLine("NOTE");
                sb.AppendLine("  " + report.EspOverrideNotice);
            }

            if (report.CobjMasters.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine($"ESP masters: {string.Join(", ", report.CobjMasters)}");
            }

            if (report.Warnings.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine($"Warnings ({report.Warnings.Count}):");

                var shown = forFile ? report.Warnings : report.Warnings.Take(DialogWarningLimit);
                foreach (var w in shown)
                    sb.AppendLine("  " + w);

                if (!forFile && report.Warnings.Count > DialogWarningLimit)
                    sb.AppendLine($"  ... and {report.Warnings.Count - DialogWarningLimit} more " +
                                  "(full list in Log / Bug report).");
            }

            return sb.ToString();
        }

        // Overwrites rather than appends: this is "what the last run did", and the interesting run is
        // always the most recent one. Errors keep their own history in error.log.
        public static void SaveLastRun(PatchGenReport report, string outputRoot)
        {
            try
            {
                Directory.CreateDirectory(LogFolder);
                File.WriteAllText(LastRunPath,
                    $"Patch generated {DateTime.Now:yyyy-MM-dd HH:mm:ss}" + Environment.NewLine +
                    $"Output root: {outputRoot}" + Environment.NewLine +
                    new string('-', 80) + Environment.NewLine +
                    Render(report, outputRoot, forFile: true));
            }
            catch (Exception ex)
            {
                // Never let report-keeping break an otherwise successful patch run.
                AppLogger.LogError("PatchReportText.SaveLastRun", ex);
            }
        }

        private static string MakeRelative(string root, string path)
        {
            try { return Path.GetRelativePath(root, path); }
            catch { return path; }
        }
    }
}
