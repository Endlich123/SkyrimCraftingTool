using SkyrimCraftingTool.Model;
using SkyrimCraftingTool.Services;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;

namespace SkyrimCraftingTool.ViewModel
{
    // The Export / Import buttons of one tab (docs/TabExport-Plan.md §3.2). Every tab holds one of
    // these, built from its TabExportModule; the dialogs that used to be copied into MainContentVM,
    // PluginNodeVM, ItemNodeVM and EnchantmentMenuVM live here once.
    //
    // Both directions flush the tab's pending debounced saves FIRST. Export: an edit made inside the
    // debounce window isn't in the database yet and would silently be missing while the dialog
    // still reports success. Import: a pending save would fire after the import and write the
    // pre-import UI value straight back over what was just imported.
    public sealed class TabImportExportVM
    {
        private readonly IImportExportService _service;
        private readonly Func<Task> _flushPendingSaves;
        private readonly Func<Task> _refreshAfterImport;

        public TabImportExportVM(
            TabExportModule module,
            IImportExportService service,
            Func<Task>? flushPendingSaves = null,
            Func<Task>? refreshAfterImport = null)
        {
            Module = module;
            _service = service;
            _flushPendingSaves = flushPendingSaves ?? (() => Task.CompletedTask);
            _refreshAfterImport = refreshAfterImport ?? (() => Task.CompletedTask);

            ExportCommand = new RelayCommand(async () => await ExportAsync(ExportScope.All, null));
            ImportCommand = new RelayCommand(async () => await ImportAllAsync());
        }

        public TabExportModule Module { get; }

        // The tab toolbar's two buttons: everything this tab has, in both directions.
        public RelayCommand ExportCommand { get; }
        public RelayCommand ImportCommand { get; }

        private string Plural => Module.Noun + "(s)";

        // ---- export ----

        public async Task ExportAsync(ExportScope scope, string? scopeValue)
        {
            await _flushPendingSaves();

            var items = Collect(scope, scopeValue);
            if (items == null) return;

            string from = scope == ExportScope.Plugin ? $" from '{scopeValue}'" : "";
            if (items.Count == 0)
            {
                Info("Export", $"No edited {Plural}{from} - nothing to export.");
                return;
            }

            var (count, root) = ImportExportFlow.ExportItems(Module, items);
            Info("Export Successful", $"{count} {Plural}{from} exported to{Environment.NewLine}{root}");
        }

        public Task ExportPluginAsync(string plugin) => ExportAsync(ExportScope.Plugin, plugin);

        // Several records into ONE file named after their owner - an item together with its recipes.
        public async Task ExportBundleAsync(string ownerKey, string ownerName, IEnumerable<string> keys)
        {
            await _flushPendingSaves();

            var items = new List<EditedItemDto>();
            foreach (var key in keys)
            {
                var part = Collect(ExportScope.Item, key);
                if (part == null) return;
                items.AddRange(part);
            }

            if (items.Count == 0)
            {
                Info("Export", $"This {Module.Noun} has no edited data to export.");
                return;
            }

            var path = ImportExportFlow.ExportBundle(Module, ownerKey, ownerName, items);
            Info("Export Successful", $"Exported to{Environment.NewLine}{path}");
        }

        private List<EditedItemDto>? Collect(ExportScope scope, string? scopeValue)
        {
            try
            {
                return _service.GetEditedItems(Module.Units, scope, scopeValue);
            }
            catch (Exception ex)
            {
                AppLogger.LogError($"Export ({Module.Folder}, {scope} {scopeValue}) failed", ex);
                Error($"Export failed:{Environment.NewLine}{ex.Message}");
                return null;
            }
        }

        // ---- import ----

        public async Task ImportAllAsync()
        {
            var items = ImportExportFlow.ReadAllExportedItems(Module);
            if (items.Count == 0)
            {
                Info("Import", $"No {Module.Noun} export files found under{Environment.NewLine}{ExportFileStore.TabRoot(Module.Folder)}");
                return;
            }
            await ImportAsync(items);
        }

        public async Task ImportPluginAsync(string plugin)
        {
            var items = ImportExportFlow.ReadItems(Module, ExportFileStore.FindFilesForPlugin(Module.Folder, plugin));
            if (items.Count == 0)
            {
                Info("Import", $"No exported {Plural} for '{plugin}' found under{Environment.NewLine}{ExportFileStore.TabRoot(Module.Folder)}");
                return;
            }
            await ImportAsync(items);
        }

        // The per-item import: the bundle ExportBundleAsync wrote for this owner, or - for an export
        // made before TE-1 - the same file in the old layout.
        public async Task ImportBundleAsync(string ownerKey, string ownerName)
        {
            var path = ExportFileStore.GetItemFilePath(Module.Folder, ownerKey, ownerName);
            if (!System.IO.File.Exists(path))
            {
                var legacy = ExportFileStore.GetLegacyItemFilePath(ownerKey, ownerName);
                if (System.IO.File.Exists(legacy))
                    path = legacy;
                else
                {
                    Info("Import", $"No export file found for this {Module.Noun}:{Environment.NewLine}{path}");
                    return;
                }
            }

            ExportFile file;
            try
            {
                file = ExportFileStore.ReadFile(path);
            }
            catch (Exception ex)
            {
                AppLogger.LogError($"Import ({Module.Folder}): reading {path} failed", ex);
                Error($"File could not be read:{Environment.NewLine}{ex.Message}");
                return;
            }

            var items = new List<EditedItemDto>();
            if (file?.Items != null)
                items.AddRange(file.Items.FindAll(i => Module.Accepts(i)));
            await ImportAsync(items);
        }

        // Preview -> conflict window -> apply -> tab refresh -> summary.
        public async Task ImportAsync(List<EditedItemDto> items)
        {
            if (items == null || items.Count == 0)
            {
                Warning("Import", $"No importable {Plural} found.");
                return;
            }

            await _flushPendingSaves();

            ImportResult? result;
            try
            {
                result = ImportExportFlow.RunImport(_service, Module, items);
            }
            catch (Exception ex)
            {
                AppLogger.LogError($"Import ({Module.Folder}) failed", ex);
                Error($"Import failed:{Environment.NewLine}{ex.Message}");
                return;
            }

            if (result == null)
                return; // user cancelled the conflict dialog — conflicting records left untouched

            try
            {
                await _refreshAfterImport();
            }
            catch (Exception ex)
            {
                AppLogger.LogError($"Import ({Module.Folder}) refresh failed", ex);
            }

            Info("Import Complete", ImportExportFlow.SummaryText(result));
        }

        // For AllTabsImportExportVM, which runs every tab's flush and refresh around ONE import.
        internal Task FlushPendingSavesAsync() => _flushPendingSaves();
        internal Task RefreshAfterImportAsync() => _refreshAfterImport();

        // ---- dialogs ----

        private static void Info(string title, string text) =>
            MessageBox.Show(text, title, MessageBoxButton.OK, MessageBoxImage.Information);

        private static void Warning(string title, string text) =>
            MessageBox.Show(text, title, MessageBoxButton.OK, MessageBoxImage.Warning);

        private static void Error(string text) =>
            MessageBox.Show(text, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
