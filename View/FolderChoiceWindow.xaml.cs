using SkyrimCraftingTool.Model;
using SkyrimCraftingTool.Services;
using System;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace SkyrimCraftingTool.View
{
    public partial class FolderChoiceWindow : Window
    {
        private FolderSettings _settings;

        // The radio buttons fire Checked while the constructor is still filling the window in.
        // Without this guard the prefill below would overwrite the paths that were just loaded from
        // settings.json with freshly guessed ones - i.e. the user's own corrections would be lost
        // on every start.
        private bool _loading = true;

        public FolderChoiceWindow()
        {
            InitializeComponent();
            SourceInitialized += FolderChoiceWindow_SourceInitialized;

            try
            {
                _settings = FolderSettings.LoadSavedSettings();
            }
            catch
            {
                _settings = new FolderSettings();
            }

            // A settings.json written before ManagerKind existed holds only the three old paths.
            // Work out what it describes rather than asking again: a modlist.txt next to
            // plugins.txt means MO2 and nothing else does. Nothing is written until Save.
            if (ModManagerDetector.TryInferForExistingSettings(_settings, out var inferred))
                _settings = inferred;

            // load data for UI
            GameDataPathBox.Text = _settings.GameDataPath;
            ModDirectoryPathBox.Text = _settings.ModDirectoryPath;
            PluginsFilePathBox.Text = _settings.PluginsFilePath;
            ModListFilePathBox.Text = _settings.ModListFilePath;

            switch (_settings.ManagerKind)
            {
                case ModManagerKind.ModOrganizer2: Mo2Radio.IsChecked = true; break;
                case ModManagerKind.Other: OtherRadio.IsChecked = true; break;
                    // Unknown: nothing preselected. That only happens on a genuine first start,
                    // where the question has not been answered yet and guessing would be wrong.
            }

            _loading = false;
            ApplyManagerVisibility();
        }

        // Recolor the native title bar to match the dark theme, same as the other windows.
        private void FolderChoiceWindow_SourceInitialized(object? sender, EventArgs e)
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            var captionColor = ((SolidColorBrush)FindResource("ColorBackgroundBase")).Color;
            var textColor = ((SolidColorBrush)FindResource("ColorTextPrimary")).Color;
            DwmTitleBarService.ApplyAccentCaption(hwnd, captionColor, textColor);
        }

        private ModManagerKind SelectedKind()
        {
            if (Mo2Radio.IsChecked == true) return ModManagerKind.ModOrganizer2;
            if (OtherRadio.IsChecked == true) return ModManagerKind.Other;
            return ModManagerKind.Unknown;
        }

        private void ManagerChanged(object sender, RoutedEventArgs e)
        {
            if (_loading) return;

            if (SelectedKind() == ModManagerKind.Other)
            {
                // Deployment already put the real files in the game folder, so that folder IS the
                // mod source. Only fill in what is still blank - a user who deliberately pointed
                // this somewhere else keeps their choice.
                if (string.IsNullOrWhiteSpace(ModDirectoryPathBox.Text))
                    ModDirectoryPathBox.Text = GameDataPathBox.Text;

                if (string.IsNullOrWhiteSpace(PluginsFilePathBox.Text))
                    PluginsFilePathBox.Text = ModManagerDetector.DefaultPluginsFilePath();

                ModListFilePathBox.Text = "";
            }
            else
            {
                FillModListFromPluginsFile();
            }

            ApplyManagerVisibility();
        }

        // modlist.txt lives in the same profile folder as plugins.txt. Deriving it rather than
        // asking for it also pins both to the SAME profile - hand-picking two files does not, and a
        // modlist.txt from the wrong profile would apply the wrong mod priority without a word.
        private void FillModListFromPluginsFile()
        {
            if (SelectedKind() != ModManagerKind.ModOrganizer2) return;

            var derived = ModManagerDetector.ModListPathFor(PluginsFilePathBox.Text);
            ModListFilePathBox.Text = File.Exists(derived) ? derived : "";
        }

        private void ApplyManagerVisibility()
        {
            var isMo2 = SelectedKind() == ModManagerKind.ModOrganizer2;

            ModListPanel.Visibility = isMo2 ? Visibility.Visible : Visibility.Collapsed;

            ModsFolderLabel.Text = isMo2
                ? "Mods folder"
                : "Mods folder (the game's Data folder — your manager deploys into it)";

            StatusText.Text = SelectedKind() switch
            {
                ModManagerKind.ModOrganizer2 when string.IsNullOrWhiteSpace(ModListFilePathBox.Text) =>
                    "No modlist.txt found next to plugins.txt. The scan still works, but when the same plugin "
                    + "file exists in several mods it cannot tell which copy MO2 would load.",
                ModManagerKind.ModOrganizer2 =>
                    "modlist.txt found. When the same plugin file exists in several mods, its priority decides "
                    + "which copy is read.",
                ModManagerKind.Other =>
                    "Plugins are read from the game's Data folder — that is where your manager deploys them. "
                    + "Do not point the mods folder at a staging folder.",
                _ => "Choose your mod manager first — it decides how duplicate plugin files are resolved.",
            };
        }

        private void SelectGameDataPath_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Select the game's Data folder" };
            if (dialog.ShowDialog() == true)
            {
                GameDataPathBox.Text = dialog.FolderName;

                // Under a deploying manager the two are the same folder, and saying so here saves
                // the second trip through the dialog.
                if (SelectedKind() == ModManagerKind.Other
                    && string.IsNullOrWhiteSpace(ModDirectoryPathBox.Text))
                {
                    ModDirectoryPathBox.Text = dialog.FolderName;
                }
            }
        }

        private void SelectModDirectoryPath_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Select the mods folder" };
            if (dialog.ShowDialog() == true)
            {
                ModDirectoryPathBox.Text = dialog.FolderName;
            }
        }

        private void SelectPluginsFilePath_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "Plugins.txt|plugins.txt|All Files|*.*"
            };

            if (dialog.ShowDialog() == true)
            {
                PluginsFilePathBox.Text = dialog.FileName;
                FillModListFromPluginsFile();
                ApplyManagerVisibility();
            }
        }

        private void SelectModListFilePath_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "Modlist.txt|modlist.txt|All Files|*.*"
            };

            if (dialog.ShowDialog() == true)
            {
                ModListFilePathBox.Text = dialog.FileName;
                ApplyManagerVisibility();
            }
        }

        private void SaveAndClose_Click(object sender, RoutedEventArgs e)
        {
            // validate
            if (SelectedKind() == ModManagerKind.Unknown)
            {
                System.Windows.MessageBox.Show("Choose your mod manager first.");
                return;
            }

            if (!Directory.Exists(GameDataPathBox.Text))
            {
                System.Windows.MessageBox.Show("GameDataPath does not exist.");
                return;
            }

            if (!Directory.Exists(ModDirectoryPathBox.Text))
            {
                System.Windows.MessageBox.Show("ModDirectoryPath does not exist.");
                return;
            }

            if (!File.Exists(PluginsFilePathBox.Text))
            {
                System.Windows.MessageBox.Show("PluginsFilePath does not exist.");
                return;
            }

            // Empty is fine - modlist.txt is optional. A path that was typed but does not exist is
            // not: it would be read as "no priority information" and silently change which copy of
            // a duplicated plugin wins, which is the one thing this field is here to prevent.
            var modList = ModListFilePathBox.Text;
            if (!string.IsNullOrWhiteSpace(modList) && !File.Exists(modList))
            {
                System.Windows.MessageBox.Show("modlist.txt does not exist. Clear the field or pick the right file.");
                return;
            }

            // save
            _settings.ManagerKind = SelectedKind();
            _settings.GameDataPath = GameDataPathBox.Text;
            _settings.ModDirectoryPath = ModDirectoryPathBox.Text;
            _settings.PluginsFilePath = PluginsFilePathBox.Text;
            _settings.ModListFilePath = _settings.ManagerKind == ModManagerKind.ModOrganizer2 ? modList : "";

            _settings.Save();

            DialogResult = true;
            Close();
        }
    }
}
