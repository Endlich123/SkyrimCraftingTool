using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using SkyrimCraftingTool.Services;

namespace SkyrimCraftingTool.ViewModel
{
    public sealed class ChangedListsVM : ViewModelBase
    {
        private readonly string? _dbPath;

        public ObservableCollection<ChangedListRowVM> Rows { get; } = new();

        public ICommand ResetAllCommand { get; }

        public ChangedListsVM(string? dbPath = null)
        {
            _dbPath = dbPath;
            ResetAllCommand = new RelayCommand(ResetAll);

            Load();
        }

        private void Load()
        {
            Rows.Clear();

            foreach (var detail in LeveledListEditStore.ReadAllDetailed(_dbPath))
                Rows.Add(new ChangedListRowVM(detail, Reset));

            OnPropertyChanged(nameof(Summary));
            OnPropertyChanged(nameof(HasRows));
        }

        public bool HasRows => Rows.Count > 0;

        public string Summary => Rows.Count == 0
            ? "You have not changed any leveled list. Chance and calculation are the load order's everywhere."
            : $"{Rows.Count} leveled list(s) changed by you. Each of these changes the odds for EVERY item " +
              "in that list, including other mods' - resetting one puts it back to what your load order says.";

        private void Reset(ChangedListRowVM row)
        {
            LeveledListEditStore.Delete(row.ListKey, _dbPath);
            Load();
        }

        private void ResetAll()
        {
            foreach (var row in Rows.ToList())
                LeveledListEditStore.Delete(row.ListKey, _dbPath);

            Load();
        }
    }

    public sealed class ChangedListRowVM
    {
        private readonly Action<ChangedListRowVM> _reset;

        public ChangedListRowVM(LeveledListEditDetail detail, Action<ChangedListRowVM> reset)
        {
            Detail = detail;
            _reset = reset;
            ResetCommand = new RelayCommand(() => _reset(this));
        }

        public LeveledListEditDetail Detail { get; }

        public string ListKey => Detail.ListKey;
        public string Display => Detail.Display;
        public string Changes => Detail.Changes;

        public ICommand ResetCommand { get; }
    }
}
