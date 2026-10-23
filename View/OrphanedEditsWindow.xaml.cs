using SkyrimCraftingTool.Model;
using SkyrimCraftingTool.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace SkyrimCraftingTool.View
{
    public sealed class OrphanRowVM : INotifyPropertyChanged
    {
        public string Table { get; }
        public string Key { get; }
        public string DisplayName { get; }
        public string LastChanged { get; }

        private bool _delete;
        public bool Delete
        {
            get => _delete;
            set { _delete = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Delete))); }
        }

        public OrphanRowVM(OrphanedEdit e)
        {
            Table = e.Table;
            Key = e.Key;
            DisplayName = string.IsNullOrEmpty(e.DisplayName) ? e.Key : e.DisplayName;
            LastChanged = e.LastChanged;
        }

        public event PropertyChangedEventHandler PropertyChanged;
    }

    public partial class OrphanedEditsWindow : Window
    {
        private readonly IItemService _itemService;
        public ObservableCollection<OrphanRowVM> Rows { get; } = new();
        public int DeletedCount { get; private set; }

        private OrphanedEditsWindow(IEnumerable<OrphanedEdit> orphans, IItemService itemService)
        {
            InitializeComponent();
            _itemService = itemService;
            foreach (var o in orphans)
                Rows.Add(new OrphanRowVM(o));
            DataContext = this;
            SourceInitialized += OrphanedEditsWindow_SourceInitialized;
        }

        private void OrphanedEditsWindow_SourceInitialized(object? sender, EventArgs e)
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            var captionColor = ((SolidColorBrush)FindResource("ColorBackgroundBase")).Color;
            var textColor = ((SolidColorBrush)FindResource("ColorTextPrimary")).Color;
            DwmTitleBarService.ApplyAccentCaption(hwnd, captionColor, textColor);
        }

        // Returns how many rows the user deleted.
        public static int ShowDialog(IEnumerable<OrphanedEdit> orphans, IItemService itemService, Window owner = null)
        {
            var window = new OrphanedEditsWindow(orphans, itemService)
            {
                Owner = owner ?? System.Windows.Application.Current.MainWindow,
            };
            window.ShowDialog();
            return window.DeletedCount;
        }

        private void SelectAll_Click(object sender, RoutedEventArgs e)
        {
            foreach (var r in Rows) r.Delete = true;
        }

        private void SelectNone_Click(object sender, RoutedEventArgs e)
        {
            foreach (var r in Rows) r.Delete = false;
        }

        private void DeleteSelected_Click(object sender, RoutedEventArgs e)
        {
            var toDelete = Rows.Where(r => r.Delete).ToList();
            if (toDelete.Count == 0)
            {
                System.Windows.MessageBox.Show("Nothing ticked.", "Orphaned Edits",
                    System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
                return;
            }

            var confirm = System.Windows.MessageBox.Show(
                $"Permanently delete {toDelete.Count} orphaned edit(s)?",
                "Confirm", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes) return;

            foreach (var row in toDelete)
            {
                try
                {
                    _itemService.DeleteItemRow(row.Table, row.Key);
                    Rows.Remove(row);
                    DeletedCount++;
                }
                catch (Exception ex)
                {
                    AppLogger.LogError($"Deleting orphaned edit failed ({row.Table}|{row.Key})", ex);
                }
            }

            if (Rows.Count == 0)
                Close();
        }

        private void Close_Click(object sender, RoutedEventArgs e) => Close();
    }
}
