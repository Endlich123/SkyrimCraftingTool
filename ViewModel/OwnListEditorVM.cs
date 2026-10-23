using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using SkyrimCraftingTool.Services;

namespace SkyrimCraftingTool.ViewModel
{
    // The panel for ONE of the user's own leveled lists, in the Leveled lists sub-tab
    // (docs/LeveledList-Create-Plan.md, L-P2 and L-P6): its name, deleting it, and where it hangs.
    //
    // NOT WHAT IS IN IT. That arrives the way it arrives in every list - an item's Container tab,
    // under a container this list hangs in - and the contents pane below already shows it, because a planned
    // placement is a planned placement whichever list it goes into. A second way in from here would
    // be a second truth about the same entries.
    //
    // Its own view model rather than more of OwnerContentsVM: that one is shared with the Containers
    // sub-tab, and none of this means anything for a container.
    public sealed class OwnListEditorVM : ViewModelBase
    {
        private readonly string? _dbPath;
        private readonly Action _ownersChanged;

        // The two catalogues the add box picks from, read once per editor. Thousands of rows, and the
        // panel is rebuilt only when another list is picked.
        private readonly IReadOnlyList<OwnerSummary> _lists;
        private readonly IReadOnlyList<OwnerSummary> _containers;

        public OwnListEditorVM(OwnerRowVM row, string? dbPath, Action ownersChanged)
        {
            ListKey = row.Key;
            _editorId = row.Name;
            _dbPath = dbPath;
            _ownersChanged = ownersChanged;

            // Not the list itself - a list cannot hang in itself, and offering it would only produce
            // a refusal.
            _lists = PlacementLookup.AllLeveledLists(dbPath)
                .Where(l => !string.Equals(l.Key, ListKey, StringComparison.OrdinalIgnoreCase))
                .ToList();
            _containers = PlacementLookup.AllContainers(dbPath);

            ReloadTargets();
        }

        public string ListKey { get; }

        // --------------------
        // Name
        // --------------------

        // Written on LostFocus (see the binding), so a rename is one write and one tree reload rather
        // than one per keystroke. A blank name is refused and the old one put back: the EditorID is
        // what the tree, xEdit and every rule comment show.
        private string _editorId;
        public string EditorId
        {
            get => _editorId;
            set
            {
                var trimmed = (value ?? "").Trim();
                if (trimmed.Length == 0 || trimmed == _editorId)
                {
                    OnPropertyChanged();
                    return;
                }

                if (!UserLeveledListStore.Rename(ListKey, trimmed, _dbPath)) return;

                _editorId = trimmed;
                OnPropertyChanged();
                _ownersChanged();
            }
        }

        public ICommand DeleteCommand => new RelayCommand(Delete);

        private void Delete()
        {
            int placed = PlacementLookup.PlannedFor(ListKey, _dbPath).Count;

            var detail = placed == 0
                ? "No item is placed into it."
                : $"{placed} item(s) are placed into it - those placements stop reaching the game, " +
                  "and the items keep their other placements.";

            var answer = System.Windows.MessageBox.Show(
                $"Delete '{EditorId}' for good? It is yours alone - no plugin defines it, so a rescan " +
                $"cannot bring it back.{Environment.NewLine}{Environment.NewLine}{detail}",
                "Delete leveled list", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning);
            if (answer != System.Windows.MessageBoxResult.Yes) return;

            if (UserLeveledListStore.Delete(ListKey, _dbPath))
                _ownersChanged();
        }

        // --------------------
        // Where it hangs (L-P6)
        // --------------------

        public ObservableCollection<OwnListTargetRowVM> Targets { get; } = new();

        public bool HasTargets => Targets.Count > 0;

        // Said while there is no target yet, because it is the one thing about an own list that is
        // not obvious: a list nothing references hands out nothing.
        public string TargetsNote => HasTargets
            ? "Each place below rolls this list. Items go INTO it from an item's Container tab, under any container listed here."
            : "Nowhere yet - and a leveled list nothing references hands out nothing in the game. " +
              "Hang it into a list or a container below. Once it hangs in a container, items go INTO it from an " +
              "item's Container tab, under that container.";

        private void ReloadTargets()
        {
            Targets.Clear();

            foreach (var t in UserLeveledListStore.ReadTargets(ListKey, _dbPath))
            {
                var catalogue = t.Kind == UserListTargetKind.Container ? _containers : _lists;
                var name = catalogue.FirstOrDefault(o => string.Equals(o.Key, t.TargetKey, StringComparison.OrdinalIgnoreCase))?.Name;

                Targets.Add(new OwnListTargetRowVM(t, name ?? t.TargetKey, Write, Remove));
            }

            OnPropertyChanged(nameof(HasTargets));
            OnPropertyChanged(nameof(TargetsNote));
        }

        private void Write(OwnListTargetRowVM row)
            => UserLeveledListStore.SetTarget(ListKey, row.TargetKey, row.Kind, row.Level, row.Count, _dbPath);

        private void Remove(OwnListTargetRowVM row)
        {
            UserLeveledListStore.RemoveTarget(ListKey, row.TargetKey, _dbPath);
            ReloadTargets();
        }

        // --- The add box ---

        // Into a list is the default and the quiet way: vanilla nests lists everywhere. Into a chest
        // is the one that needs a recent SkyPatcher, and the box says so when it is chosen.
        private bool _addIntoList = true;
        public bool AddIntoList
        {
            get => _addIntoList;
            set
            {
                if (!SetProperty(ref _addIntoList, value)) return;

                // A pick from the other catalogue means nothing here, and neither does its name.
                ClearCandidate();
                CandidateSearch = "";

                OnPropertyChanged(nameof(AddIntoContainer));
                OnPropertyChanged(nameof(Candidates));
                OnPropertyChanged(nameof(AddNote));
            }
        }

        public bool AddIntoContainer
        {
            get => !_addIntoList;
            set => AddIntoList = !value;
        }

        // ONE BOX: what is typed filters the drop-down, the same editable box as the enchantment
        // picker on an item. There used to be a separate search field in front of it, and the box
        // itself - editable by its style - looked like a second one that did nothing.
        //
        // Typing something other than the picked target's name lets go of the pick: otherwise "Add"
        // would add the old target while the box shows something else. Picking an entry writes its
        // name into the box, which matches and keeps it.
        private string _candidateSearch = "";
        public string CandidateSearch
        {
            get => _candidateSearch;
            set
            {
                if (!SetProperty(ref _candidateSearch, value ?? "")) return;

                if (_selectedCandidate != null
                    && !string.Equals(_candidateSearch, _selectedCandidate.Name, StringComparison.Ordinal))
                {
                    ClearCandidate();
                }

                OnPropertyChanged(nameof(Candidates));
            }
        }

        // Capped: the typing is what narrows thousands of lists down, and a drop-down of all of them
        // is a list nobody scrolls. Materialised, so one notification is one list for the box.
        private const int MaxCandidates = 200;

        public IReadOnlyList<OwnerSummary> Candidates
            => (AddIntoList ? _lists : _containers)
                .Where(o => string.IsNullOrWhiteSpace(CandidateSearch)
                            || o.Name.Contains(CandidateSearch, StringComparison.OrdinalIgnoreCase)
                            || o.Key.Contains(CandidateSearch, StringComparison.OrdinalIgnoreCase))
                .Take(MaxCandidates)
                .ToList();

        private OwnerSummary? _selectedCandidate;
        public OwnerSummary? SelectedCandidate
        {
            get => _selectedCandidate;
            set
            {
                // null arrives when the filter hides the picked row while someone types - taken at
                // face value it would drop the pick mid-keystroke. Letting go is CandidateSearch's
                // job, which knows whether the text still names the pick.
                if (value == null) return;
                if (!SetProperty(ref _selectedCandidate, value)) return;

                OnPropertyChanged(nameof(AddNote));
                OnPropertyChanged(nameof(CanAdd));
            }
        }

        private void ClearCandidate()
        {
            if (_selectedCandidate == null) return;

            _selectedCandidate = null;
            OnPropertyChanged(nameof(SelectedCandidate));
            OnPropertyChanged(nameof(AddNote));
            OnPropertyChanged(nameof(CanAdd));
        }

        public bool CanAdd => SelectedCandidate != null;

        private int _newLevel = 1;
        public int NewLevel
        {
            get => _newLevel;
            set => SetProperty(ref _newLevel, Math.Max(1, value));
        }

        private int _newCount = 1;
        public int NewCount
        {
            get => _newCount;
            set => SetProperty(ref _newCount, Math.Max(1, value));
        }

        // What the picked target means, before it is added: how full a list already is, and for a
        // container the version it needs.
        public string AddNote
        {
            get
            {
                if (!AddIntoList)
                    return "Needs SkyPatcher 7.1.3 or newer. Older versions ignore a leveled list put into a " +
                           "container unless iAllowLeveledListsAddedToContainers is switched on.";

                if (SelectedCandidate == null)
                    return "From the level you set, the target list can hand out this list - and through it, whatever is in it.";

                int count = SelectedCandidate.EntryCount;
                return count >= UserLeveledListStore.MaxEntries
                    ? $"{SelectedCandidate.Name} already holds {count} entries - a leveled list holds at most " +
                      $"{UserLeveledListStore.MaxEntries}, and SkyPatcher will not add past that."
                    : $"{SelectedCandidate.Name} holds {count} entries now.";
            }
        }

        private string _status = "";
        public string Status
        {
            get => _status;
            private set => SetProperty(ref _status, value);
        }

        public ICommand AddTargetCommand => new RelayCommand(AddTarget);

        private void AddTarget()
        {
            var target = SelectedCandidate;
            if (target == null) return;

            var kind = AddIntoList ? UserListTargetKind.LeveledList : UserListTargetKind.Container;
            var refusal = UserLeveledListStore.SetTarget(ListKey, target.Key, kind, NewLevel, NewCount, _dbPath);

            Status = refusal switch
            {
                UserListTargetRefusal.None => "",
                UserListTargetRefusal.Self => "A list cannot hang in itself.",
                UserListTargetRefusal.Cycle => $"{target.Name} already hangs inside this list - putting this list into it would make a loop.",
                UserListTargetRefusal.NotAnOwnList => "This is not one of your own lists any more.",
                _ => "That target cannot be used.",
            };

            if (refusal != UserListTargetRefusal.None) return;

            ClearCandidate();
            CandidateSearch = "";
            ReloadTargets();
        }
    }

    // One "this list hangs in that one" row. Level and count write straight through - each is one
    // row in a small table, and a save button would be one more thing to forget.
    public sealed class OwnListTargetRowVM : ViewModelBase
    {
        private readonly Action<OwnListTargetRowVM> _write;

        public OwnListTargetRowVM(UserListTarget target, string name, Action<OwnListTargetRowVM> write, Action<OwnListTargetRowVM> remove)
        {
            TargetKey = target.TargetKey;
            Kind = target.Kind;
            Name = name;
            _level = target.Level;
            _count = target.Count;
            _write = write;
            RemoveCommand = new RelayCommand(() => remove(this));
        }

        public string TargetKey { get; }
        public UserListTargetKind Kind { get; }
        public string Name { get; }

        public bool IsList => Kind == UserListTargetKind.LeveledList;

        public string KindText => IsList ? "list" : "container";

        private int _level;
        public int Level
        {
            get => _level;
            set
            {
                if (SetProperty(ref _level, Math.Max(1, value))) _write(this);
            }
        }

        private int _count;
        public int Count
        {
            get => _count;
            set
            {
                if (SetProperty(ref _count, Math.Max(1, value))) _write(this);
            }
        }

        public ICommand RemoveCommand { get; }
    }
}
