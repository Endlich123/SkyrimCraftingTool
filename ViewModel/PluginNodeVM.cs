using SkyrimCraftingTool.Model;
using SkyrimCraftingTool.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;

namespace SkyrimCraftingTool.ViewModel
{
    public class PluginNodeVM : ViewModelBase
    {
        public string PluginName { get; set; }

        // Set by TreeBuilderService at construction (and carried over by FilterReference) — needed to
        // reach ImportExportService/RunImportAsync for the per-plugin Export/Import buttons.
        public MainContentVM Main { get; set; }

        private bool _isExpanded;
        public bool IsExpanded
        {
            get => _isExpanded;
            set => SetProperty(ref _isExpanded, value);
        }

        public ObservableCollection<CategoryNodeVM> Categories { get; set; }
            = new ObservableCollection<CategoryNodeVM>();

        // --------------------
        // Presets (Output/Presets/*.json) — Auto-Apply to every item in this plugin.
        // --------------------
        public IEnumerable<PresetFile> ConfigPresets => Main?.AllPresets ?? Enumerable.Empty<PresetFile>();

        private PresetFile? _selectedConfigPreset;
        public PresetFile? SelectedConfigPreset
        {
            get => _selectedConfigPreset;
            set => SetProperty(ref _selectedConfigPreset, value);
        }

        public ICommand ApplyPresetCommand => new RelayCommand(async () =>
        {
            if (SelectedConfigPreset == null || Main == null) return;

            var items = Categories.SelectMany(c => c.Items).ToList();
            int applied = 0;
            foreach (var item in items)
            {
                // Items in this plugin's tree may never have been individually clicked, so their
                // AllKeywords/CraftingRecipe/TemperRecipe/ContainerSelection could still be empty -
                // hydrate first so PresetApplyService sees each item's real existing state.
                Main.EnsureItemHydrated(item);

                var touchedFields = PresetApplyService.Apply(item, SelectedConfigPreset);
                foreach (var field in touchedFields)
                    await Main.PersistFieldAsync(item, field);

                if (touchedFields.Count > 0)
                    applied++;
            }

            System.Windows.MessageBox.Show(
                applied == 0
                    ? $"Preset '{SelectedConfigPreset.PresetName}' didn't match any of the {items.Count} item(s) in this plugin (no matching slots/types, or no fields enabled)."
                    : $"Preset '{SelectedConfigPreset.PresetName}' applied to {applied} of {items.Count} item(s) in this plugin.",
                "Auto-Apply", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
        });

        /// <summary>
        /// Creates a filtered copy of this plugin.
        /// </summary>
        public PluginNodeVM FilterReference(string text, bool onlyEdited = false)
        {
            bool pluginMatches = string.IsNullOrWhiteSpace(text) ||
                                 PluginName.Contains(text, StringComparison.OrdinalIgnoreCase);

            var filtered = new PluginNodeVM { PluginName = this.PluginName, Main = this.Main };

            foreach (var cat in Categories)
            {
                var filteredCat = cat.FilterReference(text, pluginMatches, onlyEdited);
                if (filteredCat != null)
                    filtered.Categories.Add(filteredCat);
            }

            return filtered.Categories.Count > 0 ? filtered : null;
        }

        // Export/Import for every edited item belonging to this plugin, under
        // Output/Exports/<PluginName>/ — see ExportFileStore for the path convention. Export writes
        // one file per item (same shape as MainContentVM.ExportAllCommand, just scoped to this
        // plugin); Import reads back everything found under this plugin's own folder.
        public ICommand ExportPluginCommand => new RelayCommand(async () =>
        {
            if (Main?.ImportExportService == null) return;

            // See MainContentVM.ExportAllAsync — a pending debounced save must land before we read
            // the edited set, or it silently misses from the export.
            await Main.FlushPendingSavesAsync();

            List<EditedItemDto> items;
            try
            {
                items = Main.ImportExportService.GetEditedItems(ExportScope.Plugin, PluginName);
            }
            catch (Exception ex)
            {
                AppLogger.LogError("PluginNodeVM.ExportPluginCommand failed", ex);
                System.Windows.MessageBox.Show($"Export failed:{Environment.NewLine}{ex.Message}",
                    "Error", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
                return;
            }

            if (items.Count == 0)
            {
                System.Windows.MessageBox.Show("This plugin has no edited items to export.",
                    "Export", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
                return;
            }

            foreach (var item in items)
            {
                var path = ExportFileStore.GetItemFilePath(item.Key, item.DisplayName);
                ExportFileStore.WriteFile(path, new ExportFile { ExportedAt = ItemDBHandler.NowIso(), Items = new List<EditedItemDto> { item } });
            }

            System.Windows.MessageBox.Show(
                $"{items.Count} item(s) exported to{Environment.NewLine}{System.IO.Path.Combine(ExportFileStore.ExportsRoot, PluginName)}",
                "Export Successful", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
        });

        public ICommand ImportPluginCommand => new RelayCommand(async () =>
        {
            if (Main == null) return;

            var files = ExportFileStore.FindFilesForPlugin(PluginName);
            if (files.Count == 0)
            {
                System.Windows.MessageBox.Show(
                    $"No export files found under{Environment.NewLine}{System.IO.Path.Combine(ExportFileStore.ExportsRoot, PluginName)}",
                    "Import", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
                return;
            }

            var allItems = new List<EditedItemDto>();
            foreach (var path in files)
            {
                try
                {
                    var file = ExportFileStore.ReadFile(path);
                    if (file?.Items != null)
                        allItems.AddRange(file.Items);
                }
                catch (Exception ex)
                {
                    AppLogger.LogError($"PluginNodeVM.ImportPluginCommand: failed reading {path}", ex);
                }
            }

            await Main.RunImportAsync(allItems);
        });
    }
}
