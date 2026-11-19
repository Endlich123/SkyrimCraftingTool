using System;
using System.Collections.Generic;
using System.Linq;
using SkyrimCraftingTool.Model;

namespace SkyrimCraftingTool.Services
{
    // Shared glue for Export / Import, scoped to one tab's module. Owns file I/O and the
    // preview -> conflict -> apply core; the dialogs around it live in TabImportExportVM, and what a
    // tab refreshes afterwards stays with the tab.
    public static class ImportExportFlow
    {
        // One JSON file per edited record under Output\Exports\<Tab>\. Returns (records written, root).
        //
        // Records that land on the SAME path share the file. The path is built from key and name
        // only, and several units describe one record under its own key: an edited armor and its
        // removed-recipe decision. One file each
        // meant the second write replaced the first, and the edit it held was gone from the
        // export without a word (found 2026-10-18).
        public static (int Count, string Root) ExportItems(TabExportModule module, IEnumerable<EditedItemDto> items)
        {
            var list = items.ToList();
            var byPath = list
                .GroupBy(i => ExportFileStore.GetItemFilePath(module.Folder, i.Key, i.DisplayName),
                         StringComparer.OrdinalIgnoreCase);
            foreach (var file in byPath)
            {
                ExportFileStore.WriteFile(file.Key, new ExportFile
                {
                    ExportedAt = ItemDBHandler.NowIso(),
                    Items = file.ToList(),
                });
            }
            return (list.Count, ExportFileStore.TabRoot(module.Folder));
        }

        // Several records in ONE file, named after the record that owns them - the per-item export,
        // where an item and its recipes travel together. Returns the path written.
        public static string ExportBundle(TabExportModule module, string ownerKey, string ownerName, List<EditedItemDto> items)
        {
            var path = ExportFileStore.GetItemFilePath(module.Folder, ownerKey, ownerName);
            ExportFileStore.WriteFile(path, new ExportFile { ExportedAt = ItemDBHandler.NowIso(), Items = items });
            return path;
        }

        // Settings' "Export all": each tab's own export into its own folder - nothing written anywhere a
        // tab would not write it. Returns, per tab, the records written.
        public static List<(TabExportModule Module, int Count)> ExportAllTabs(
            IImportExportService service, IEnumerable<TabExportModule> modules)
        {
            var written = new List<(TabExportModule, int)>();
            foreach (var module in modules)
            {
                var items = service.GetEditedItems(module.Units, ExportScope.All);
                written.Add((module, items.Count == 0 ? 0 : ExportItems(module, items).Count));
            }
            return written;
        }

        // Settings' "Import all": what each tab's own import would read, all of it. A legacy file is
        // read by every tab but each keeps only its own units, so no record comes back twice.
        public static List<EditedItemDto> ReadAllTabs(IEnumerable<TabExportModule> modules)
            => modules.SelectMany(ReadAllExportedItems).ToList();

        // Every DTO in the given files that belongs to the module. A file that can't be read is logged
        // and skipped, never aborts the whole read.
        public static List<EditedItemDto> ReadItems(TabExportModule module, IEnumerable<string> paths)
        {
            var all = new List<EditedItemDto>();
            foreach (var path in paths)
            {
                try
                {
                    var file = ExportFileStore.ReadFile(path);
                    if (file?.Items != null)
                        all.AddRange(file.Items.Where(module.Accepts));
                }
                catch (Exception ex)
                {
                    AppLogger.LogError($"ImportExportFlow: reading {path} failed", ex);
                }
            }
            return all;
        }

        // Everything this tab ever exported, plus the legacy files.
        public static List<EditedItemDto> ReadAllExportedItems(TabExportModule module)
            => ReadItems(module, ExportFileStore.FindAllFiles(module.Folder));

        public static bool HasPayload(EditedItemDto d) =>
            d.Fields.Count > 0
            || ChildTables.All.Any(spec => spec.Payload(d) != null)
            || (d.Rows != null && d.Rows.Values.Any(list => list != null));

        public static ImportResult? RunImport(IImportExportService service, TabExportModule module, List<EditedItemDto> items)
            => RunImport(service, module.Units, items.Where(module.Accepts).ToList());

        // Over any set of units - one tab's, or every tab's at once for "Import all".
        public static ImportResult? RunImport(IImportExportService service, IReadOnlyList<ExportUnit> units, List<EditedItemDto> items)
        {
            var plan = PreviewImport(service, units, items);
            var useFileVersion = ResolveConflicts(plan);
            if (useFileVersion == null)
                return null;

            return service.ApplyImport(units, plan, useFileVersion);
        }

        // The three steps of RunImport one by one, for a caller that shows a loading screen around
        // the preview and the apply but must take it down for the conflict dialog in between.
        public static ImportPlan PreviewImport(IImportExportService service, IReadOnlyList<ExportUnit> units, List<EditedItemDto> items)
        {
            var tables = new HashSet<string>(units.Select(u => u.Table), StringComparer.Ordinal);
            items = items.Where(i => i != null && tables.Contains(i.Table ?? "")).Where(HasPayload).ToList();
            return service.PreviewImport(units, items);
        }

        // The keys whose file version wins, or null when the dialog was cancelled (nothing written).
        public static HashSet<string>? ResolveConflicts(ImportPlan plan)
        {
            if (plan.Conflicts.Count == 0)
                return new HashSet<string>();

            return View.ImportConflictWindow.ShowDialog(plan.Conflicts);
        }

        public static string SummaryText(ImportResult r) =>
            $"Updated: {r.Applied}{Environment.NewLine}" +
            $"Skipped (identical): {r.SkippedEqual}{Environment.NewLine}" +
            $"Skipped (not present locally): {r.SkippedMissing.Count}{Environment.NewLine}" +
            $"Skipped (unreadable export file): {r.SkippedInvalid}{Environment.NewLine}" +
            $"Conflicts - used import: {r.ConflictsUsedFile}{Environment.NewLine}" +
            $"Conflicts - kept local: {r.ConflictsKeptLocal}" +
            (r.Notes is { Count: > 0 }
                ? Environment.NewLine + Environment.NewLine + string.Join(Environment.NewLine, r.Notes.Distinct())
                : "");
    }
}
