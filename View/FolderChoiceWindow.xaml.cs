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

            // load data for UI
            GameDataPathBox.Text = _settings.GameDataPath;
            ModDirectoryPathBox.Text = _settings.ModDirectoryPath;
            PluginsFilePathBox.Text = _settings.PluginsFilePath;
        }

        // Recolor the native title bar to match the dark theme, same as the other windows.
        private void FolderChoiceWindow_SourceInitialized(object? sender, EventArgs e)
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            var captionColor = ((SolidColorBrush)FindResource("ColorBackgroundBase")).Color;
            var textColor = ((SolidColorBrush)FindResource("ColorTextPrimary")).Color;
            DwmTitleBarService.ApplyAccentCaption(hwnd, captionColor, textColor);
        }

        private void SelectGameDataPath_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Select the game's Data folder" };
            if (dialog.ShowDialog() == true)
            {
                GameDataPathBox.Text = dialog.FolderName;
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
            }
        }

        private void SaveAndClose_Click(object sender, RoutedEventArgs e)
        {
            // validate
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

            // save
            _settings.GameDataPath = GameDataPathBox.Text;
            _settings.ModDirectoryPath = ModDirectoryPathBox.Text;
            _settings.PluginsFilePath = PluginsFilePathBox.Text;

            _settings.Save();

            DialogResult = true;
            Close();
        }
    }
}
