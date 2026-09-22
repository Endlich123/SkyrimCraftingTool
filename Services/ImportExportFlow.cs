using System;
using System.Collections.Generic;
using System.Linq;
using SkyrimCraftingTool.Model;

namespace SkyrimCraftingTool.Services
{
    // Shared glue for the Export / Import feature so the Items tab and the Enchantments tab don't
    // each hand-roll it. The per-VM parts (which items to gather, what to refresh afterwards, error
    // dialogs) stay at the call site; this owns file I/O and the preview -> conflict -> apply core.
    public static class ImportExportFlow
    {
        // One JSON file per edited item under Output\Exports\. Returns (files written, root path).
        public static (int Count, string Root) ExportItems(IEnumerable<EditedItemDto> items)
        {
            int count = 0;
            foreach (var item in items)
            {
                var path = ExportFileStore.GetItemFilePath(item.Key, item.DisplayName);
                ExportFileStore.WriteFile(path, new ExportFile
                {
                    ExportedAt = ItemDBHandler.NowIso(),
                    Items = new List<EditedItemDto> { item },
                });
                count++;
            }
            return (count, ExportFileStore.ExportsRoot);
        }

        // Flat list of every item across all export files, optionally filtered (e.g. by table).
        public static List<EditedItemDto> ReadAllExportedItems(Func<EditedItemDto, bool>? keep = null)
        {
            var all = new List<EditedItemDto>();
            foreach (var path in ExportFileStore.FindAllFiles())
            {
                try
                {
                    var file = ExportFileStore.ReadFile(path);
                    if (file?.Items != null)
                        all.AddRange(keep == null ? file.Items : file.Items.Where(keep));
                }
                catch (Exception ex)
                {
                    AppLogger.LogError($"ImportExportFlow: reading {path} failed", ex);
                }
            }
            return all;
        }

        // A DTO with no changed fields and no edited child rows carries nothing to apply — it comes
        // from an item that was edited then reset (the reset paths leave LastChanged non-null, so an
        // old export picked it up). Drop it so it never clutters the conflict window / preview.
        //
        // The child lists come from ChildTables.All rather than being listed here, so a fourth unit
        // is carried the day it is described and not the day someone remembers this method. Each is
        // tested for null, never for Count: an EMPTY list is a payload - it means "the user removed
        // them all" - and the export only ever sets a list when that unit's *Edited flag was up.
        // Counting instead made "cleared to empty" indistinguishable from "never touched", so an
        // enchantment stripped of its effects, or a recipe stripped of its conditions, was silently
        // dropped before the preview ever saw it.
        public static bool HasPayload(EditedItemDto d) =>
            d.Fields.Count > 0 || ChildTables.All.Any(spec => spec.Payload(d) != null);

        // Preview -> (conflict window if needed) -> Apply. Returns null if the user cancelled the
        // conflict dialog; the caller does its own refresh + summary around this.
        public static ImportResult? RunImport(IImportExportService service, List<EditedItemDto> items)
        {
            items = items.Where(HasPayload).ToList();
            var plan = service.PreviewImport(items);

            var useFileVersion = new HashSet<string>();
            if (plan.Conflicts.Count > 0)
            {
                var resolved = View.ImportConflictWindow.ShowDialog(plan.Conflicts);
                if (resolved == null)
                    return null;
                useFileVersion = resolved;
            }

            return service.ApplyImport(plan, useFileVersion);
        }

        public static string SummaryText(ImportResult r) =>
            $"Updated: {r.Applied}{Environment.NewLine}" +
            $"Skipped (identical): {r.SkippedEqual}{Environment.NewLine}" +
            $"Skipped (not present locally): {r.SkippedMissing.Count}{Environment.NewLine}" +
            // Own line, not folded into "not present locally": this means a corrupt export file
            // (unreadable LastChanged), not a missing plugin - very different fix for the user.
            $"Skipped (unreadable export file): {r.SkippedInvalid}{Environment.NewLine}" +
            $"Conflicts - used import: {r.ConflictsUsedFile}{Environment.NewLine}" +
            $"Conflicts - kept local: {r.ConflictsKeptLocal}";
    }
}
