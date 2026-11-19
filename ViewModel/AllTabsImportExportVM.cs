using SkyrimCraftingTool.Model;
using SkyrimCraftingTool.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;

namespace SkyrimCraftingTool.ViewModel
{
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

        // Two loading screens, not one: reading + comparing, then writing + refreshing. The conflict
        // dialog sits between them and must not be covered. Both run their work on the UI thread
        // (blocksUiThread), so the overlay window on its own thread is the one that shows.
        public async Task ImportAsync()
        {
            var units = TabExportModules.AllUnits;
            ImportPlan plan;

            using (var busy = BusyService.Show("Import all: reading the export files…", blocksUiThread: true))
            {
                var items = ImportExportFlow.ReadAllTabs(Modules);
                if (items.Count == 0)
                {
                    busy.Dispose();
                    Info("Import", $"No export files found under{Environment.NewLine}{ExportFileStore.ExportsRoot}");
                    return;
                }

                BusyService.SetMessage("Import all: saving pending edits…");
                await FlushAllAsync();

                BusyService.SetMessage($"Import all: comparing {items.Count:N0} records…");
                try
                {
                    plan = ImportExportFlow.PreviewImport(_service, units, items);
                }
                catch (Exception ex)
                {
                    busy.Dispose();
                    ImportFailed(ex);
                    return;
                }
            }

            var useFileVersion = ImportExportFlow.ResolveConflicts(plan);
            if (useFileVersion == null)
                return; // conflict dialog cancelled - nothing written

            ImportResult result;
            using (var busy = BusyService.Show("Import all: writing…", blocksUiThread: true))
            {
                try
                {
                    result = _service.ApplyImport(units, plan, useFileVersion);
                }
                catch (Exception ex)
                {
                    busy.Dispose();
                    ImportFailed(ex);
                    return;
                }

                // Each tab on its own: one that fails to re-read must not leave the others stale.
                foreach (var tab in _tabs)
                {
                    BusyService.SetMessage($"Import all: refreshing {tab.Module.Folder}…");
                    try { await tab.RefreshAfterImportAsync(); }
                    catch (Exception ex) { AppLogger.LogError($"Import (all tabs): refreshing {tab.Module.Folder} failed", ex); }
                }

                await BusyService.UntilDrawnAsync();
            }

            Info("Import Complete", ImportExportFlow.SummaryText(result));
        }

        private static void ImportFailed(Exception ex)
        {
            AppLogger.LogError("Import (all tabs) failed", ex);
            MessageBox.Show($"Import failed:{Environment.NewLine}{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
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
