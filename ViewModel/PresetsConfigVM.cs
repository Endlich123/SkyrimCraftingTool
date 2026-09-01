using SkyrimCraftingTool.Model;
using SkyrimCraftingTool.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;

namespace SkyrimCraftingTool.ViewModel
{
    public class PresetsConfigVM : ViewModelBase
    {
        // Curated display order for the well-known vanilla WeapType keywords; anything else (modded
        // types) is appended alphabetically after. Nothing is filtered out or reclassified — every
        // WeapType* keyword found in the DB gets its own tree node, this list only controls sort order.
        private static readonly string[] KnownWeaponTypeOrder =
        {
            "WeapTypeSword", "WeapTypeDagger", "WeapTypeWarAxe", "WeapTypeMace",
            "WeapTypeGreatsword", "WeapTypeBattleaxe", "WeapTypeWarhammer",
            "WeapTypeBow", "WeapTypeCrossbow", "WeapTypeStaff"
        };

        private readonly MainContentVM _main;

        // Shared autosave debouncer - only holds ONE pending action, so the bulk apply path saves
        // each affected file directly instead (see SavePresetImmediate). Flushed on shutdown.
        private readonly Debouncer _saveDebouncer = new();

        public System.Threading.Tasks.Task FlushPendingSavesAsync() => _saveDebouncer.FlushAsync();

        // Reference-data source (keyword/workbench/material/perk/quest catalogs) for the bulk editor.
        internal MainContentVM Main => _main;

        public ObservableCollection<PresetNodeVM> Presets { get; } = new();

        private object _selectedNode;
        public object SelectedNode
        {
            get => _selectedNode;
            set
            {
                if (ReferenceEquals(_selectedNode, value)) return;

                // Only one slot node's heavy editor state (full keyword list, container catalog,
                // recipe editors) is kept live at a time — release the one we're leaving, build the
                // one we're entering. See PresetSlotNodeVM.EnsureLoaded / Unload.
                (_selectedNode as PresetSlotNodeVM)?.Unload();
                SetProperty(ref _selectedNode, value);
                (value as PresetSlotNodeVM)?.EnsureLoaded();
            }
        }

        public ICommand AddPresetCommand { get; }

        // --- Multi-selection (slot/type leaf level only) ---
        // The TreeView is single-select natively; slot-level clicks get intercepted in
        // PresetsConfigView (PreviewMouseLeftButtonDown on the branch template's ItemContainerStyle)
        // and evaluated here, mirroring MainContentVM's item multi-select. Preset-root and
        // Armor/Weapon-branch nodes keep normal single-select behavior.
        private PresetSlotNodeVM? _selectionAnchor;
        public ObservableCollection<PresetSlotNodeVM> SelectedSlots { get; } = new();

        // Drives the visibility of PresetMultiSelectView in PresetsConfigView.xaml.
        public bool IsMultiSelectActive => SelectedSlots.Count > 1;

        private PresetMultiSelectVM? _multiSelectVM;
        public PresetMultiSelectVM MultiSelectVM
        {
            get => _multiSelectVM ??= new PresetMultiSelectVM(this);
            private set => SetProperty(ref _multiSelectVM, value);
        }

        public PresetsConfigVM(MainContentVM main)
        {
            _main = main;
            AddPresetCommand = new RelayCommand(AddPreset);
            LoadAllPresets();
        }

        public void HandleSlotNodeClick(PresetSlotNodeVM clicked, bool ctrl, bool shift)
        {
            if (shift && _selectionAnchor != null)
            {
                var flat = GetFlatSlotNodes();
                int anchorIndex = flat.IndexOf(_selectionAnchor);
                int clickedIndex = flat.IndexOf(clicked);

                if (anchorIndex >= 0 && clickedIndex >= 0)
                {
                    if (!ctrl)
                        foreach (var slot in SelectedSlots.ToList())
                            SetSlotSelected(slot, false);

                    int lo = Math.Min(anchorIndex, clickedIndex);
                    int hi = Math.Max(anchorIndex, clickedIndex);
                    for (int i = lo; i <= hi; i++)
                        SetSlotSelected(flat[i], true);
                }
            }
            else if (ctrl)
            {
                SetSlotSelected(clicked, !clicked.IsSelected);
                _selectionAnchor = clicked;
            }
            else
            {
                foreach (var slot in SelectedSlots.ToList())
                    if (slot != clicked)
                        SetSlotSelected(slot, false);

                SetSlotSelected(clicked, true);
                _selectionAnchor = clicked;
            }

            // Single-slot editor stays active for exactly one selection (SelectedNode also runs the
            // slot's lazy EnsureLoaded/Unload); at 0 or 2+ it's PresetMultiSelectView's domain.
            SelectedNode = SelectedSlots.Count == 1 ? SelectedSlots[0] : null;
        }

        // Called from PresetsConfigView's native SelectedItemChanged handler whenever the user
        // navigates via single-select semantics (preset/branch clicks, arrow keys) after having
        // multi-selected slots.
        internal void ClearMultiSelection()
        {
            foreach (var slot in SelectedSlots.ToList())
                SetSlotSelected(slot, false);
            _selectionAnchor = null;
        }

        private void SetSlotSelected(PresetSlotNodeVM slot, bool value)
        {
            if (slot.IsSelected == value) return;
            slot.IsSelected = value;

            if (value) SelectedSlots.Add(slot);
            else SelectedSlots.Remove(slot);

            OnPropertyChanged(nameof(IsMultiSelectActive));
        }

        // Tree order: per preset, Armor slots then Weapon types. A Shift range can therefore also
        // span branches / presets - ApplyBulkTemplate targets each slot's own file, and
        // SavePresetImmediate is called once per distinct affected file.
        private List<PresetSlotNodeVM> GetFlatSlotNodes()
        {
            var result = new List<PresetSlotNodeVM>();
            foreach (var preset in Presets)
                foreach (var branch in preset.Children)
                    result.AddRange(branch.Children);
            return result;
        }

        internal PresetFile? GetOwnerFile(PresetSlotNodeVM slot)
        {
            foreach (var preset in Presets)
                foreach (var branch in preset.Children)
                    if (branch.Children.Contains(slot))
                        return preset.File;
            return null;
        }

        // Bulk apply mutates several slot configs across (possibly) several files in quick
        // succession; the shared _saveDebouncer would only ever flush the last one, so the bulk
        // path writes each distinct file straight through instead.
        internal void SavePresetImmediate(PresetFile? file)
        {
            if (file == null) return;
            try
            {
                PresetFileStore.WritePreset(file);
            }
            catch (Exception ex)
            {
                AppLogger.LogError($"Failed to save preset '{file.PresetName}'", ex);
            }
        }

        // Called after a (re-)scan completes, so newly discovered WeapType* keywords / materials /
        // workbenches show up without requiring the user to leave and reopen the Presets tab. Note:
        // this rebuilds the whole tree, so if the user is actively editing a slot on this tab exactly
        // when a rescan finishes, the current selection is reset (a rare, low-impact edge case — any
        // pending debounced save still targets the correct file by name, so no data is lost).
        public void RefreshReferenceData()
        {
            ClearMultiSelection();
            LoadAllPresets();
            // The bulk editor's keyword/workbench/material catalogs are a snapshot taken at creation
            // time - rebuild it (only if it was ever opened) so it reflects the fresh scan data.
            if (_multiSelectVM != null)
                MultiSelectVM = new PresetMultiSelectVM(this);
        }

        private void LoadAllPresets()
        {
            Presets.Clear();
            foreach (var path in PresetFileStore.FindAllPresetFiles())
            {
                PresetFile file;
                try
                {
                    file = PresetFileStore.ReadPreset(path);
                }
                catch (Exception ex)
                {
                    AppLogger.LogError($"Failed to load preset file '{path}'", ex);
                    continue;
                }
                if (file == null) continue;

                Presets.Add(BuildPresetNode(file));
            }
        }

        private PresetNodeVM BuildPresetNode(PresetFile file)
        {
            var armorBranch = new PresetBranchNodeVM("Armor");
            var weaponBranch = new PresetBranchNodeVM("Weapon");

            var allKeywords = _main?.AllAvailableKeywords ?? new List<FormIDRecord>();
            var allWorkbenches = _main?.AllAvailableWorkbenches ?? new List<FormIDRecord>();
            var allMaterials = _main?.AllAvailableMaterials ?? new List<FormIDRecord>();
            var allPerks = _main?.AllAvailablePerks ?? new List<FormIDRecord>();
            var allQuests = _main?.AllAvailableQuests ?? new List<FormIDRecord>();
            var allContainers = _main?.AllContainers ?? new List<ContainerRecord>();

            foreach (ArmorSlotMask slot in Enum.GetValues(typeof(ArmorSlotMask)))
            {
                if (slot == ArmorSlotMask.None) continue;

                int bit = (int)Math.Log((uint)slot, 2);
                string nodeKey = bit.ToString();
                string displayName = $"{slot} (Slot {bit + 30})";

                var config = file.ArmorSlots.FirstOrDefault(s => s.NodeKey == nodeKey)
                    ?? new PresetSlotConfig { NodeKey = nodeKey };

                armorBranch.Children.Add(new PresetSlotNodeVM(config, true, displayName,
                    allKeywords, allWorkbenches, allMaterials, allPerks, allQuests, allContainers,
                    () => OnSlotChanged(file, file.ArmorSlots, config), _main?.References));
            }

            foreach (var weapType in GetOrderedWeaponTypeKeywords(allKeywords))
            {
                var config = file.WeaponTypes.FirstOrDefault(s => s.NodeKey == weapType.Key)
                    ?? new PresetSlotConfig { NodeKey = weapType.Key };

                weaponBranch.Children.Add(new PresetSlotNodeVM(config, false, weapType.Name,
                    allKeywords, allWorkbenches, allMaterials, allPerks, allQuests, allContainers,
                    () => OnSlotChanged(file, file.WeaponTypes, config), _main?.References));
            }

            return new PresetNodeVM(file, armorBranch, weaponBranch, RenamePreset, DeletePreset);
        }

        private static List<FormIDRecord> GetOrderedWeaponTypeKeywords(List<FormIDRecord> allKeywords)
        {
            var weapTypes = allKeywords
                .Where(k => !string.IsNullOrEmpty(k.Name) && k.Name.StartsWith("WeapType", StringComparison.OrdinalIgnoreCase))
                .ToList();

            return weapTypes
                .OrderBy(k =>
                {
                    int idx = Array.IndexOf(KnownWeaponTypeOrder, k.Name);
                    return idx < 0 ? int.MaxValue : idx;
                })
                .ThenBy(k => k.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        // First real edit on a slot/type that had no prior data attaches its (until-now transient)
        // config into the file's list — keeps the JSON lean: only slots the user actually touched
        // are persisted, even though the tree always shows all 32 Armor slots + every WeapType found.
        private void OnSlotChanged(PresetFile file, List<PresetSlotConfig> owningList, PresetSlotConfig config)
        {
            if (!owningList.Contains(config))
                owningList.Add(config);

            ScheduleSave(file);
        }

        private void ScheduleSave(PresetFile file)
        {
            _saveDebouncer.Debounce(350, _ =>
            {
                try
                {
                    PresetFileStore.WritePreset(file);
                }
                catch (Exception ex)
                {
                    AppLogger.LogError($"Failed to save preset '{file.PresetName}'", ex);
                }
            });
        }

        private void AddPreset()
        {
            const string baseName = "New Preset";
            string name = baseName;
            int i = 1;
            while (PresetFileStore.Exists(name) || Presets.Any(p => p.File.PresetName == name))
            {
                i++;
                name = $"{baseName} {i}";
            }

            var file = new PresetFile { PresetName = name };
            PresetFileStore.WritePreset(file);

            var node = BuildPresetNode(file);
            Presets.Add(node);
            ClearMultiSelection();
            SelectedNode = node;
        }

        private void RenamePreset(PresetNodeVM node, string newName)
        {
            newName = (newName ?? "").Trim();
            var oldName = node.File.PresetName;

            if (newName.Length == 0 || newName == oldName)
            {
                node.RaisePresetNameChanged();
                return;
            }

            bool samePath = string.Equals(
                PresetFileStore.GetPresetFilePath(oldName),
                PresetFileStore.GetPresetFilePath(newName),
                StringComparison.OrdinalIgnoreCase);

            if (!samePath && PresetFileStore.Exists(newName))
            {
                System.Windows.MessageBox.Show(
                    $"A preset with the name '{newName}' already exists.",
                    "Rename Failed", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                node.RaisePresetNameChanged();
                return;
            }

            try
            {
                PresetFileStore.RenamePresetFile(oldName, newName);
            }
            catch (Exception ex)
            {
                AppLogger.LogError("Preset rename failed", ex);
                System.Windows.MessageBox.Show(
                    $"Rename failed:{Environment.NewLine}{ex.Message}",
                    "Error", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
                node.RaisePresetNameChanged();
                return;
            }

            node.File.PresetName = newName;
            node.RaisePresetNameChanged();
        }

        private void DeletePreset(PresetNodeVM node)
        {
            var result = System.Windows.MessageBox.Show(
                $"Really delete preset '{node.File.PresetName}'?",
                "Delete Preset", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning);
            if (result != System.Windows.MessageBoxResult.Yes) return;

            PresetFileStore.DeletePresetFile(node.File.PresetName);
            ClearMultiSelection();
            Presets.Remove(node);
            if (ReferenceEquals(SelectedNode, node))
                SelectedNode = null;
        }
    }
}
