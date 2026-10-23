using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using SkyrimCraftingTool.Model;

namespace SkyrimCraftingTool.View
{
    // "Open Folder": opens FolderPath in Explorer (user, 2026-10-18). A button type of its own, so a
    // view only says WHICH folder - the click, the creating and the error message are written once.
    //
    // The folder is created first: Presets and Exports only exist once something was written there,
    // and Explorer handed a missing path opens some other folder instead (usually Documents),
    // which looks like a wrong folder rather than an empty one.
    //
    // Looks like a StandardButtonStyle button, labelled "Open Folder", with the full path as tooltip -
    // the implicit style in Styles/Controls.xaml; a view can still set its own Content or ToolTip.
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
