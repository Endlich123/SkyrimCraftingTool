using System.Windows;

namespace SkyrimCraftingTool.ViewModel
{
    // The Presets tab: the modular building blocks, edited in place (docs/ItemPresets-Plan.md,
    // "Umbau" 2026-10-20). Replaced the slot-preset tree; the old preset files stay on disk as an
    // import source ("Import slot presets").
    //
    // Two catalogs since 2026-10-23 - armor and weapons (docs/WeaponPresets-Plan.md, W5) - each with
    // its own editor. Both stay open: switching does not lose unsaved edits, leaving the tab asks
    // for both.
    public sealed class PresetsTabVM : ViewModelBase
    {
        private readonly MainContentVM _main;

        private ModularCatalogEditorVM _editor;
        public ModularCatalogEditorVM Editor
        {
            get => _editor;
            private set { if (SetProperty(ref _editor, value)) OnPropertyChanged(nameof(ActiveEditor)); }
        }

        private ModularCatalogEditorVM _weaponEditor;
        public ModularCatalogEditorVM WeaponEditor
        {
            get => _weaponEditor;
            private set { if (SetProperty(ref _weaponEditor, value)) OnPropertyChanged(nameof(ActiveEditor)); }
        }

        private bool _showWeapons;
        public bool ShowWeapons
        {
            get => _showWeapons;
            set
            {
                if (!SetProperty(ref _showWeapons, value)) return;
                OnPropertyChanged(nameof(ShowArmor));
                OnPropertyChanged(nameof(ActiveEditor));
            }
        }
        public bool ShowArmor { get => !_showWeapons; set => ShowWeapons = !value; }

        public ModularCatalogEditorVM ActiveEditor => _showWeapons ? _weaponEditor : _editor;

        public PresetsTabVM(MainContentVM main)
        {
            _main = main;
            _editor = new ModularCatalogEditorVM(main);
            _weaponEditor = new ModularCatalogEditorVM(main, weapons: true);
        }

        // After a scan the editors' pickers hold the old keyword/material/perk lists. Rebuilt -
        // unless there are unsaved edits, which are worth more than fresh search lists.
        public void RefreshReferenceData()
        {
            if (!Editor.IsDirty)
                Editor = new ModularCatalogEditorVM(_main);
            if (!WeaponEditor.IsDirty)
                WeaponEditor = new ModularCatalogEditorVM(_main, weapons: true);
        }

        // Nothing autosaves on this tab (a half-made rule should not take effect on its own), so
        // leaving it with edits asks - once per catalog with changes. allowCancel: false on app exit,
        // where staying is not offered. Returns false when the user chose to stay.
        public bool ConfirmLeave(bool allowCancel = true)
            => ConfirmLeave(weapons: false, allowCancel) && ConfirmLeave(weapons: true, allowCancel);

        private bool ConfirmLeave(bool weapons, bool allowCancel)
        {
            var editor = weapons ? WeaponEditor : Editor;
            if (!editor.IsDirty) return true;

            var answer = MessageBox.Show(
                weapons ? "Save the changes to the weapon building blocks?" : "Save the changes to the armor building blocks?",
                "Presets", allowCancel ? MessageBoxButton.YesNoCancel : MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (answer == MessageBoxResult.Cancel) return false;
            if (answer == MessageBoxResult.Yes)
                return editor.Save() || !allowCancel; // a failed save (validation) keeps you here
            if (weapons) WeaponEditor = new ModularCatalogEditorVM(_main, weapons: true);    // discard
            else Editor = new ModularCatalogEditorVM(_main);
            return true;
        }
    }
}
