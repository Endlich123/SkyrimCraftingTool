using System.IO;
using System.Windows.Input;

namespace SkyrimCraftingTool.ViewModel
{
    // App-wide settings, reachable from the nav bar like the other three views.
    //
    // The settings themselves still live on MainContentVM, which owns the patch run and persists
    // them through AppPrefs. This VM only presents them, the same way PresetsConfigVM takes a
    // MainContentVM rather than duplicating its state — one owner, one persistence path.
    //
    // Theme selection applies through ThemeService, which owns both the palette and the AppPrefs
    // key for it - same principle: this VM presents, it does not own.
    public class SettingsVM : ViewModelBase
    {
        private readonly MainContentVM _content;

        public SettingsVM(MainContentVM content)
        {
            _content = content;

            BrowseOutputPathCommand = new RelayCommand(BrowseOutputPath);
            ResetOutputPathCommand = new RelayCommand(() => PatchOutputPath = "");

            // This VM presents MainContentVM's state, so anything that changes there has to reach
            // the bindings here. Only the lost-list count needs it today - it is the one value that
            // moves while this view exists, when a rescan rebuilds the scanned data underneath it.
            _content.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName != nameof(MainContentVM.LostListCount)) return;

                OnPropertyChanged(nameof(HasLostLists));
                OnPropertyChanged(nameof(LostListsText));
            };
        }

        // The lost-entry report lives here rather than in the container section it came from: it is
        // about the load order as a whole, not about the item you happen to have selected, and in
        // the container section it only appeared once you were already deep in one - which is the
        // same reason the report had to exist at all. Presented, not owned, like everything else on
        // this VM.
        public bool HasLostLists => _content.HasLostLists;

        public string LostListsText => _content.HasLostLists
            ? $"{_content.LostListCount} leveled list(s) lost entries to an override — an earlier plugin " +
              "had them and the plugin that won does not. This is a report: nothing is patched unless " +
              "you put it back yourself."
            : "No leveled list in your load order lost entries to an override.";

        public ICommand ShowLostListsCommand => _content.ShowLostListsCommand;

        // --- Patch output ---

        public string PatchOutputPath
        {
            get => _content.PatchOutputPath;
            set
            {
                if (_content.PatchOutputPath == (value ?? "").Trim()) return;
                _content.PatchOutputPath = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(EffectivePatchOutputPath));
                OnPropertyChanged(nameof(IsUsingDefaultOutputPath));
                OnPropertyChanged(nameof(OutputPathStatus));
            }
        }

        public string EffectivePatchOutputPath => _content.EffectivePatchOutputPath;

        public bool IsUsingDefaultOutputPath => string.IsNullOrWhiteSpace(PatchOutputPath);

        // Says what will happen on the next run, in one line. A configured folder that doesn't exist
        // is worth flagging here rather than at generation time: it is created on demand, but only
        // if its parent is reachable - a path on a drive that is gone fails the whole export.
        public string OutputPathStatus
        {
            get
            {
                if (IsUsingDefaultOutputPath)
                    return $"Default: the patch is written to {EffectivePatchOutputPath}";

                var parent = Path.GetDirectoryName(EffectivePatchOutputPath);
                bool reachable = Directory.Exists(EffectivePatchOutputPath)
                                 || (!string.IsNullOrEmpty(parent) && Directory.Exists(parent));

                return reachable
                    ? "The patch is written to the folder above. Enable that folder as a mod in your mod manager."
                    : "This folder can't be reached right now — the patch run would fail. Check the drive or pick another folder.";
            }
        }

        public ICommand BrowseOutputPathCommand { get; }
        public ICommand ResetOutputPathCommand { get; }

        private void BrowseOutputPath()
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "Select the folder the patch should be written to",
            };

            // Start where the user already points, so re-picking a neighbouring folder is one click.
            var current = EffectivePatchOutputPath;
            if (Directory.Exists(current))
                dialog.InitialDirectory = current;

            if (dialog.ShowDialog() == true)
                PatchOutputPath = dialog.FolderName;
        }

        // --- Patch layout ---

        public bool SplitPatchPerPlugin
        {
            get => _content.SplitPatchPerPlugin;
            set
            {
                if (_content.SplitPatchPerPlugin == value) return;
                _content.SplitPatchPerPlugin = value;
                OnPropertyChanged();
            }
        }

        // --- Appearance ---

        // The list comes from ThemeService rather than being spelled out here, so shipping a third
        // palette is a file plus one entry in ThemeService.Available - this view needs no change.
        public System.Collections.Generic.IReadOnlyList<string> AvailableThemes
            => Services.ThemeService.Available;

        public string SelectedTheme
        {
            get => Services.ThemeService.Current;
            set
            {
                if (string.IsNullOrWhiteSpace(value) || value == Services.ThemeService.Current) return;

                // Applies immediately (ThemeService writes into the shared brushes, so the whole UI
                // repaints without a restart) and remembers the choice in AppPrefs.
                Services.ThemeService.Apply(value);

                // Raised unconditionally on purpose: if Apply bailed out - a palette that failed to
                // load - Current is unchanged, and re-reading the getter snaps the combo box back to
                // the theme that is actually on screen instead of leaving it showing a lie.
                OnPropertyChanged();
            }
        }
    }
}
