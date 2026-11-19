using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using SkyrimCraftingTool.Model;

namespace SkyrimCraftingTool.View
{
    public class OpenFolderButton : Button
    {
        public static readonly DependencyProperty FolderPathProperty =
            DependencyProperty.Register(nameof(FolderPath), typeof(string), typeof(OpenFolderButton),
                new PropertyMetadata(""));

        public string FolderPath
        {
            get => (string)GetValue(FolderPathProperty);
            set => SetValue(FolderPathProperty, value);
        }

        protected override void OnClick()
        {
            base.OnClick();

            var path = FolderPath;
            if (string.IsNullOrWhiteSpace(path)) return;

            try
            {
                Directory.CreateDirectory(path);
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                AppLogger.LogError($"OpenFolderButton: {path}", ex);
                MessageBox.Show($"Could not open the folder:{Environment.NewLine}{path}{Environment.NewLine}{Environment.NewLine}{ex.Message}",
                    "Open Folder", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }
}
