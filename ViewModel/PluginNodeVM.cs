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

        // Scope is the WHOLE plugin, independent of the tree filter — same rule as
        // Export/Import/ResetPluginCommand below, and the reason the item list comes from
        // Main.ModItemsTree instead of this node's own `Categories`: with a search or "only edited"
        // active, this node is a filtered COPY (MainContentVM.ApplyFilter → FilterReference), so
        // running over `Categories` used to apply the preset to just the visible subset without
        // saying so. Applying to *some* items has its own, explicit home — select them in the tree
        // and use the multi-selection's Auto-Apply — which is what the dialog points at.
        public ICommand ApplyPresetCommand => new RelayCommand(async () =>
        {
            if (SelectedConfigPreset == null || Main == null) return;

            var source = Main.ModItemsTree.FirstOrDefault(p => p.PluginName == PluginName) ?? this;
            var items = source.Categories.SelectMany(c => c.Items).ToList();

            // Asks first now that the filter can no longer bound this: on a master like Skyrim.esm
            // that's 5000+ items, and the count is the only warning the user gets before the run.
            var answer = System.Windows.MessageBox.Show(
                $"Apply preset '{SelectedConfigPreset.PresetName}' to all {items.Count} item(s) in '{PluginName}'?" +
                $"{Environment.NewLine}{Environment.NewLine}This covers the whole plugin, regardless of any active tree filter. To apply a preset to only some items, select them in the tree and use the multi-selection's Auto-Apply instead.",
                "Auto-Apply", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question);
            if (answer != System.Windows.MessageBoxResult.Yes) return;

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

        // --------------------
        // Reset — the inverse of ApplyPresetCommand above.
        // --------------------
        // Reverts item fields + crafting + temper on every edited item of this plugin, through the
        // same ItemNodeVM.ResetAllChanges the single-item and multi-select Reset buttons use.
        // Auto-Apply can rewrite a whole plugin with one click, so it needs a way back that isn't
        // "re-select hundreds of items by hand".
        //
        // Scope is the WHOLE plugin, deliberately independent of the tree filter — the same rule
        // every button in this row follows (see ApplyPresetCommand above for why that means reading
        // Main.ModItemsTree rather than this node's own `Categories`). The dialog says so, because
        // unlike Apply this one can't be repeated away.
        public ICommand ResetPluginCommand => new RelayCommand(async () =>
        {
            if (Main == null) return;

            // See ExportPluginCommand below: a pending debounced save has to land BEFORE the edited
            // set is read and the shadow columns are cleared. Otherwise it fires afterwards and
            // re-marks a just-reset item as edited — with a shadow value that merely matches the
            // original, which is exactly the state MainContentVM.ResetItemEdits exists to avoid.
            await Main.FlushPendingSavesAsync();

            var source = Main.ModItemsTree.FirstOrDefault(p => p.PluginName == PluginName) ?? this;
            var candidates = source.Categories.SelectMany(c => c.Items).Where(i => i.IsEdited).ToList();

            if (candidates.Count == 0)
            {
                System.Windows.MessageBox.Show("This plugin has no edited items to reset.",
                    "Reset plugin", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
                return;
            }

            var answer = System.Windows.MessageBox.Show(
                $"Revert ALL edits on {candidates.Count} item(s) in '{PluginName}' - item fields, crafting recipes and temper recipes - back to the scanned state?" +
                $"{Environment.NewLine}{Environment.NewLine}This covers the whole plugin, regardless of any active tree filter, and cannot be undone.",
                "Reset plugin", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning);
            if (answer != System.Windows.MessageBoxResult.Yes) return;

            // Same hydrate-first rule as ApplyPresetCommand above, and the same IsEdited pre-filter
            // as MultiSelectDetailVM.ResetSelectionAsync: a never-clicked item has no recipe VMs and no
            // original-values snapshot, so all its change flags would read false and real, persisted
            // edits would be skipped. Only the flagged items get hydrated, so a plugin with
            // thousands of untouched records stays cheap.
            int reset = 0;
            foreach (var item in candidates)
            {
                Main.EnsureItemHydrated(item);
                if (item.ResetAllChanges()) reset++;
            }

            System.Windows.MessageBox.Show(
                reset == 0
                    ? $"No change - the items in '{PluginName}' were already at their scanned state."
                    : $"{reset} item(s) in '{PluginName}' reverted to the scanned state.",
                "Reset plugin", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
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
