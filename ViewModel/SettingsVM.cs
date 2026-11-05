using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Windows.Input;

namespace SkyrimCraftingTool.ViewModel
{
    // App-wide settings, reachable from the nav bar like the other three views.
    //
    // The settings themselves still live on MainContentVM, which owns the patch run and persists
    // them through AppPrefs. This VM only presents them, the same way PresetsTabVM takes a
    // MainContentVM rather than duplicating its state — one owner, one persistence path.
    //
    // Theme selection applies through ThemeService, which owns both the palette and the AppPrefs
    // key for it - same principle: this VM presents, it does not own.
    // One row per hideable tab, so the settings list cannot drift from the nav row it is about -
    // the label comes from NavTabService, which is what the buttons are named after.
    public sealed class NavTabToggleVM : ViewModelBase
    {
        private readonly Services.NavTab _tab;

        public NavTabToggleVM(Services.NavTab tab, string label)
        {
            _tab = tab;
            Label = label;
        }

        public string Label { get; }

        public bool IsVisible
        {
            get => Services.NavTabService.IsVisible(_tab);
            set
            {
                if (IsVisible == value) return;

                Services.NavTabService.SetVisible(_tab, value);
                OnPropertyChanged();
            }
        }
    }

    public class SettingsVM : ViewModelBase
    {
        private readonly MainContentVM _content;

        // The nav row the user can thin out. Items and Settings are not in this list - see
        // NavTabService for why they are not offered rather than forced on.
        public IReadOnlyList<NavTabToggleVM> NavTabs { get; }

        public SettingsVM(MainContentVM content, AllTabsImportExportVM importExport)
        {
            _content = content;
            ImportExport = importExport;

            BrowseOutputPathCommand = new RelayCommand(BrowseOutputPath);
            ResetOutputPathCommand = new RelayCommand(() => PatchOutputPath = "");

            NavTabs = Services.NavTabService.Hideable
                .Select(h => new NavTabToggleVM(h.Tab, h.Label))
                .ToList();

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

        // --- Template children (Kinder-Patcher, K5) ---

        // Edits template children carry in the fields they now take from their template - made before
        // the Kinder-Patcher and ignored by the patch since. Cleared on request, counted first.
        public ICommand CleanUpChildEditsCommand => new RelayCommand(async () =>
        {
            var dbPath = Path.Combine(Model.GlobalState.Tool.InputFolder, "Item", "item.db");
            if (!File.Exists(dbPath)) return;
            await _content.FlushPendingSavesAsync();

            // The extras first: a child's own addons (a master robe's x512) lived in the numbers this
            // clears, so they are computed from its detected addons before anything is removed.
            List<Services.TemplateChildren.ChildExtras> extras;
            using (Services.BusyService.Show("Template children: reading their addons…", blocksUiThread: true))
                extras = Services.ModularPresets.ChildExtrasRefresh.All(_content);
            var changedExtras = extras.Where(x => !_content.ChildExtrasOf(x.Key).SameAs(x)).ToList();

            var count = Services.TemplateChildren.ChildEditCleanup.Run(dbPath, apply: false);
            if (count.Total == 0 && changedExtras.Count == 0)
            {
                System.Windows.MessageBox.Show("Nothing to do: no template child carries edits in the fields it takes from its template, and their addons are up to date.",
                    "Template children", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
                return;
            }
            var answer = System.Windows.MessageBox.Show(
                $"{changedExtras.Count:N0} template children get their own addons recorded (e.g. Master Robes: value x512 on top of the plain robe).\n\n"
                + $"{count.Armor:N0} armor and {count.Weapons:N0} weapon template children carry edits in fields they take from their template "
                + "(slots, type, keywords; the numbers of enchanted ones). The patch ignores them already - they are cleared.\n\n"
                + "Name, enchantment, recipes and placements stay. This cannot be undone. Continue?",
                "Template children", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question);
            if (answer != System.Windows.MessageBoxResult.Yes) return;

            using (Services.BusyService.Show("Template children: clearing inherited edits…", blocksUiThread: false))
            {
                _content.SaveChildExtras(changedExtras);
                Services.TemplateChildren.ChildEditCleanup.Run(dbPath, apply: true);
                await _content.RefreshFromDatabaseAsync();
            }
            System.Windows.MessageBox.Show($"Recorded the addons of {changedExtras.Count:N0} and cleared the inherited edits of {count.Total:N0} template children.",
                "Template children", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
        });

        // --- Export / import of every tab at once ---

        // The tabs' own Export / Import, all at once - see AllTabsImportExportVM.
        public AllTabsImportExportVM ImportExport { get; }

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
