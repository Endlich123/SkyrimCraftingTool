using SkyrimCraftingTool.Model;
using System.IO;
using System.Windows;

namespace SkyrimCraftingTool.View
{
    public partial class FolderChoiceWindow : Window
    {
        private FolderSettings _settings;

        public FolderChoiceWindow()
        {
            InitializeComponent();

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

        private void SelectGameDataPath_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new System.Windows.Forms.FolderBrowserDialog();
            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                GameDataPathBox.Text = dialog.SelectedPath;
            }
        }

        private void SelectModDirectoryPath_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new System.Windows.Forms.FolderBrowserDialog();
            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                ModDirectoryPathBox.Text = dialog.SelectedPath;
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
