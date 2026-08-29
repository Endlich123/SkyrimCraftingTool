using SkyrimCraftingTool.Model;
using SkyrimCraftingTool.ViewModel;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;

namespace SkyrimCraftingTool.View
{
    // A single conflicting item: local data was edited more recently than the imported file's
    // snapshot of it. UseFileVersion defaults to false (keep local) — importing an older edit over a
    // newer local one is never the safe default.
    public class ConflictRowVM : INotifyPropertyChanged
    {
        public ImportConflict Conflict { get; }
        public string Table => Conflict.FileItem.Table;
        public string DisplayName => string.IsNullOrEmpty(Conflict.FileItem.DisplayName) ? Conflict.FileItem.Key : Conflict.FileItem.DisplayName;
        public string Key => Conflict.FileItem.Key;
        public string LocalLastChanged => Conflict.LocalLastChanged;
        public string FileLastChanged => Conflict.FileItem.LastChanged;

        private bool _useFileVersion;
        public bool UseFileVersion
        {
            get => _useFileVersion;
            set { _useFileVersion = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(UseFileVersion))); }
        }

        public ConflictRowVM(ImportConflict conflict)
        {
            Conflict = conflict;
        }

        public event PropertyChangedEventHandler PropertyChanged;
    }

    public partial class ImportConflictWindow : Window
    {
        public ObservableCollection<ConflictRowVM> Rows { get; } = new();

        private ImportConflictWindow(List<ImportConflict> conflicts)
        {
            InitializeComponent();
            foreach (var c in conflicts)
                Rows.Add(new ConflictRowVM(c));
            DataContext = this;
        }

        // Returns the set of "Table|Key" identifiers the user chose to overwrite with the imported
        // (older) version. Returns null if the user cancelled — the caller must then abort the whole
        // import, since leaving conflicts unresolved would silently apply nothing for those items.
        public static HashSet<string> ShowDialog(List<ImportConflict> conflicts)
        {
            var window = new ImportConflictWindow(conflicts) { Owner = System.Windows.Application.Current.MainWindow };
            bool? result = window.ShowDialog();
            if (result != true)
                return null;

            return new HashSet<string>(window.Rows.Where(r => r.UseFileVersion).Select(r => r.Table + "|" + r.Key));
        }

        private void KeepAllLocal_Click(object sender, RoutedEventArgs e)
        {
            foreach (var row in Rows) row.UseFileVersion = false;
        }

        private void UseAllFile_Click(object sender, RoutedEventArgs e)
        {
            foreach (var row in Rows) row.UseFileVersion = true;
        }

        private void Confirm_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
            Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
