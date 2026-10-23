using SkyrimCraftingTool.Model;
using SkyrimCraftingTool.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;

namespace SkyrimCraftingTool.ViewModel
{
    // "Export all" / "Import all" in Settings (user, 2026-10-18). NOT a second export system: it runs
    // the tabs' own exports into the tabs' own folders, and reads back exactly what the tabs' own
    // imports would read. A file written here is the file the tab's button writes, and the other way
    // round.
    //
    // The one thing it adds is that the import is ONE pass over every tab's units - one preview, one
    // conflict window, one ImportKeyMap - so a reference between tabs (an item pointing at an own
    // enchantment that gets a new key on a collision) is remapped in the same run, whatever order the
    // tab imports would have needed.
    public sealed class AllTabsImportExportVM
    {
        private readonly IImportExportService _service;
        private readonly IReadOnlyList<TabImportExportVM> _tabs;

        public AllTabsImportExportVM(IImportExportService service, IReadOnlyList<TabImportExportVM> tabs)
        {
            _service = service;
            _tabs = tabs;

            ExportCommand = new RelayCommand(async () => await ExportAsync());
            ImportCommand = new RelayCommand(async () => await ImportAsync());
        }

        public RelayCommand ExportCommand { get; }
        public RelayCommand ImportCommand { get; }

        private IEnumerable<TabExportModule> Modules => _tabs.Select(t => t.Module);

        public async Task ExportAsync()
        {
            await FlushAllAsync();

            List<(TabExportModule Module, int Count)> written;
            try
            {
                written = ImportExportFlow.ExportAllTabs(_service, Modules);
            }
            catch (Exception ex)
            {
                AppLogger.LogError("Export (all tabs) failed", ex);
                MessageBox.Show($"Export failed:{Environment.NewLine}{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            if (written.All(w => w.Count == 0))
            {
                Info("Export", "No tab has edits - nothing to export.");
                return;
            }

            var lines = written.Select(w => $"{w.Module.Folder}: {w.Count}");
            Info("Export Successful",
                "Exported into each tab's own folder under" + Environment.NewLine + ExportFileStore.ExportsRoot +
                Environment.NewLine + Environment.NewLine + string.Join(Environment.NewLine, lines));
        }

        public async Task ImportAsync()
        {
            var items = ImportExportFlow.ReadAllTabs(Modules);
            if (items.Count == 0)
            {
                Info("Import", $"No export files found under{Environment.NewLine}{ExportFileStore.ExportsRoot}");
                return;
            }

            await FlushAllAsync();

            ImportResult? result;
            try
            {
                result = ImportExportFlow.RunImport(_service, TabExportModules.AllUnits, items);
            }
            catch (Exception ex)
            {
                AppLogger.LogError("Import (all tabs) failed", ex);
                MessageBox.Show($"Import failed:{Environment.NewLine}{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            if (result == null)
                return; // conflict dialog cancelled - nothing written

            // Each tab on its own: one that fails to re-read must not leave the others stale.
            foreach (var tab in _tabs)
            {
                try { await tab.RefreshAfterImportAsync(); }
                catch (Exception ex) { AppLogger.LogError($"Import (all tabs): refreshing {tab.Module.Folder} failed", ex); }
            }

            Info("Import Complete", ImportExportFlow.SummaryText(result));
        }

        private async Task FlushAllAsync()
        {
            foreach (var tab in _tabs)
                await tab.FlushPendingSavesAsync();
        }

        private static void Info(string title, string text) =>
            MessageBox.Show(text, title, MessageBoxButton.OK, MessageBoxImage.Information);
    }
}
