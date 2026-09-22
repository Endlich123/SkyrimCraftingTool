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
        public static string LastPreviewPath => Path.Combine(LogFolder, "last-patch-preview.txt");

        // Warnings shown in the dialog. The saved file keeps all of them - a message box with 400
        // lines is useless, a text file with 400 lines is exactly what you want when diagnosing.
        private const int DialogWarningLimit = 15;

        public static string Render(PatchGenReport report, string outputRoot, bool forFile)
        {
            var sb = new StringBuilder();

            // First line, before the numbers: everything below is what a real run WOULD produce, and
            // the counts on their own look exactly like a finished patch.
            if (report.DryRun)
            {
                sb.AppendLine("PREVIEW - nothing was written.");
                sb.AppendLine("This is what Generate Patch would produce right now.");
                sb.AppendLine();
            }

            sb.AppendLine(report.Summary);
            sb.AppendLine();

            foreach (var f in report.WrittenFiles)
                sb.AppendLine("  " + MakeRelative(outputRoot, f));

            if (report.DryRun)
                sb.AppendLine($"  Would be written under {outputRoot}");

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

            // The only non-additive thing this tool writes, so it gets named list by list rather
            // than counted. Not a warning - the user set these deliberately, with the calculator in
            // the list window showing what each one does - but a change to a list's own properties
            // reaches every mod feeding that list, and that belongs on the record.
            if (report.LeveledListPropertyEdits.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine($"Leveled lists changed by you ({report.LeveledListPropertyEdits.Count}):");
                sb.AppendLine("  These change the odds for EVERY item in the list, not just yours.");

                foreach (var edit in report.LeveledListPropertyEdits)
                    sb.AppendLine("  " + edit);
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
        //
        // A preview goes to its own file. It must not push the last real run's report out of the way
        // - that report is what a bug report is built from, and a preview run right after it would
        // otherwise replace the evidence. The separate file also gives the preview somewhere to put
        // the full warning list, which the dialog truncates.
        public static void SaveLastRun(PatchGenReport report, string outputRoot)
        {
            var path = report.DryRun ? LastPreviewPath : LastRunPath;
            var heading = report.DryRun ? "Patch preview" : "Patch generated";

            try
            {
                Directory.CreateDirectory(LogFolder);
                File.WriteAllText(path,
                    $"{heading} {DateTime.Now:yyyy-MM-dd HH:mm:ss}" + Environment.NewLine +
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
