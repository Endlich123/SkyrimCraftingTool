using SkyrimCraftingTool.Model;
using System;
using System.Collections.ObjectModel;
using System.Windows.Input;

namespace SkyrimCraftingTool.ViewModel
{
    // Root node of one Preset in the tree. Doubles as its own detail view (PresetName + Delete),
    // the "-" button the plan calls for living in the detail pane rather than the tree itself.
    public class PresetNodeVM : ViewModelBase
    {
        public PresetFile File { get; }
        private readonly Action<PresetNodeVM, string> _onRename;
        private readonly Action<PresetNodeVM> _onDelete;

        public PresetBranchNodeVM ArmorBranch { get; }
        public PresetBranchNodeVM WeaponBranch { get; }
        public ObservableCollection<PresetBranchNodeVM> Children { get; }

        public string PresetName
        {
            get => File.PresetName;
            set => _onRename(this, value ?? "");
        }

        public ICommand DeleteCommand { get; }

        public PresetNodeVM(PresetFile file, PresetBranchNodeVM armorBranch, PresetBranchNodeVM weaponBranch,
            Action<PresetNodeVM, string> onRename, Action<PresetNodeVM> onDelete)
        {
            File = file;
            ArmorBranch = armorBranch;
            WeaponBranch = weaponBranch;
            Children = new ObservableCollection<PresetBranchNodeVM> { armorBranch, weaponBranch };
            _onRename = onRename;
            _onDelete = onDelete;

            DeleteCommand = new RelayCommand(() => _onDelete(this));
        }

        // Called by PresetsConfigVM after a rename attempt, whether it succeeded (refresh the
        // display) or failed (force the bound TextBox back to the last valid PresetName).
        public void RaisePresetNameChanged() => OnPropertyChanged(nameof(PresetName));

        // WPF falls back to ToString() for a TreeViewItem's UI Automation Name when nothing else is
        // set — override it so accessibility tools/automation see the real preset name.
        public override string ToString() => PresetName;
    }
}
