using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using SkyrimCraftingTool.Model;
using SkyrimCraftingTool.Services;

namespace SkyrimCraftingTool.ViewModel
{
    // One placement sub-tab of the Container/LeveledList tab: a list of records on the left, the
    // shared container panel on the right.
    //
    // A HOST FOR Styles/ContainerPlacementPanel.xaml. That panel asks its DataContext for nine
    // things plus a PlacementTarget, and MainContentVM answers the same nine - which is exactly what
    // lets one piece of markup serve the item editor and this tab without a second copy.
    //
    // ARMOR AND WEAPONS SHOW THE VERY SAME ItemNodeVM INSTANCES the item tree holds, not new ones.
    // The user asked for these to be duplicated here, and duplicating the PRESENTATION is fine;
    // duplicating the STATE would not be. Two view models over one record would carry two dirty
    // flags and two ContainerStrings, and only one of them would ever be saved - while presets,
    // which write a placement through PresetFile.Container, would update the copy nobody is looking
    // at.
    public sealed class PlacementListVM : ViewModelBase, IMultiSelectTree
    {
        private readonly Func<IReadOnlyList<IPlaceable>> _source;
        private readonly Func<List<ContainerRecord>> _containers;
        private bool _loaded;

        public PlacementListVM(string title, Func<IReadOnlyList<IPlaceable>> source, Func<List<ContainerRecord>> containers,
                               Func<IReadOnlyList<string>>? loadOrder = null)
        {
            Title = title;
            _source = source;
            _containers = containers;

            _tree = new PluginTree<PlaceableRowVM>(
                r => r.Key,
                (r, search) => r.Display.Contains(search, StringComparison.OrdinalIgnoreCase)
                               || r.Key.Contains(search, StringComparison.OrdinalIgnoreCase),
                loadOrder);

            // Both of these act on PlacementTarget rather than on the selected record, because with
            // several records picked the target is a TEMPLATE and not a record at all.
            ToggleContainerForSelectedItemCommand = new RelayCommand<string>(key =>
            {
                var target = PlacementTarget;
                if (target == null || string.IsNullOrEmpty(key)) return;

                target.ContainerSelection.ToggleContainer(key);

                // Not committed while a template is being built: every tick would otherwise be a
                // write to all picked records, with no way to assemble a placement first and no way
                // back out of a misclick. ApplyToPickedCommand is the one write.
                if (!IsBulk) target.CommitPlacement();

                var vm = AllContainerVMs.FirstOrDefault(c => c.ContainerKey == key);
                if (vm != null)
                    vm.IsSelected = target.ContainerSelection.SelectedContainers
                        .Any(sc => sc.ContainerKey == key);
            });

            ClearContainerSelectionCommand = new RelayCommand(() =>
            {
                var target = PlacementTarget;
                if (target == null) return;

                target.ContainerSelection.Clear();
                if (!IsBulk) target.CommitPlacement();
                RefreshContainerFlags();
            });

            ToggleExpertContainersCommand = new RelayCommand(() => ShowExpertContainers = !ShowExpertContainers);

            ApplyToPickedCommand = new RelayCommand(async () => await ApplyToPickedAsync());

            ClearPickCommand = new RelayCommand(() => ClearMultiSelection());

            _pick = new MultiSelectState<PlaceableRowVM>(
                () => _tree.RowsInScreenOrder(),
                (row, picked) => row.IsPicked = picked);

            _pick.Picked.CollectionChanged += (_, _) => OnPickChanged();
        }

        public string Title { get; }

        // --- The list of things that can be placed ---

        public ObservableCollection<IPlaceable> Rows { get; } = new();

        public void EnsureLoaded()
        {
            if (_loaded) return;
            _loaded = true;
            Reload();
        }

        public void Invalidate()
        {
            _loaded = false;
            Rows.Clear();
            AllContainerVMs.Clear();
            RebuildTree();
            SelectedPlaceable = null;
        }

        public void Reload()
        {
            _pick.Clear();

            Rows.Clear();
            foreach (var row in _source())
                Rows.Add(row);

            AllContainerVMs.Clear();
            foreach (var c in _containers())
                AllContainerVMs.Add(new ContainerEntryVM(c));

            // The template holds ContainerEntryVMs built from the list above, so it cannot outlive a
            // reload of it.
            _bulkTarget = null;

            RebuildTree();

            OnPropertyChanged(nameof(FilteredContainers));
            OnPropertyChanged(nameof(RowCountText));
        }

        public string RowCountText => Rows.Count == 1 ? "1 record" : $"{Rows.Count} records";

        private string _rowSearch = "";
        public string RowSearch
        {
            get => _rowSearch;
            set
            {
                if (!SetProperty(ref _rowSearch, value ?? "")) return;

                _tree.Filter(_rowSearch);
                OnPropertyChanged(nameof(FilteredTree));
            }
        }

        // --- Plugin, and the records under it ---
        //
        // The same tree the containers/lists pane shows, with this tab's idea of a row and of a
        // match - see PluginTree. Built in the constructor, which is where the load order arrives.
        private readonly PluginTree<PlaceableRowVM> _tree;

        public IEnumerable<PluginGroupNodeVM> FilteredTree => _tree.Nodes;

        // Built over row wrappers, not over the records: the pick has to live on something this tab
        // owns - see PlaceableRowVM. They are created once per reload and reused by the filtered
        // tree, so a row keeps its pick while the search narrows around it.
        private readonly List<PlaceableRowVM> _rowVMs = new();

        private void RebuildTree()
        {
            _rowVMs.Clear();
            foreach (var row in Rows)
                _rowVMs.Add(new PlaceableRowVM(row));

            _tree.Rebuild(_rowVMs);
            OnPropertyChanged(nameof(FilteredTree));
        }

        // --- Picking several records at once ---
        //
        // Ctrl/Shift in the tree, the same gesture the item tree answers to. The arithmetic is in
        // MultiSelectState; what belongs here is what a pick MEANS on this tab: one record edits its
        // own placement, several records share a template that is written to all of them at once.
        private readonly MultiSelectState<PlaceableRowVM> _pick;

        public ObservableCollection<PlaceableRowVM> PickedRows => _pick.Picked;

        public void HandleRowClick(PlaceableRowVM clicked, bool ctrl, bool shift)
        {
            _pick.Handle(clicked, ctrl, shift);

            // One picked row still drives the ordinary single-record panel, exactly as a plain click
            // always did. At two or more it is the template's turn.
            SelectedPlaceable = PickedRows.Count == 1 ? PickedRows[0].Row : null;
        }

        // --- IMultiSelectTree: what the tree in the view asks of this ---

        public bool IsRow(object candidate) => candidate is PlaceableRowVM;

        public void HandleRowClick(object row, bool ctrl, bool shift)
        {
            if (row is PlaceableRowVM clicked) HandleRowClick(clicked, ctrl, shift);
        }

        // A plugin row or an arrow key. A leftover pick would otherwise keep the bulk banner and the
        // template up while the tree shows one record.
        public void SelectSingle(object row)
        {
            ClearMultiSelection();

            if (row is PlaceableRowVM clicked) HandleRowClick(clicked, ctrl: false, shift: false);
        }

        public void ClearMultiSelection()
        {
            _pick.Clear();
            SelectedPlaceable = null;
        }

        private void OnPickChanged()
        {
            // "Placed 2 of 2 picked" must not still be standing over a different pick.
            BulkStatus = "";

            OnPropertyChanged(nameof(PickedRows));
            OnPropertyChanged(nameof(IsBulk));
            OnPropertyChanged(nameof(PickCountText));
            OnPropertyChanged(nameof(PlacementTarget));
            OnPropertyChanged(nameof(HasSelection));
            OnPropertyChanged(nameof(HasNamedTarget));
            OnPropertyChanged(nameof(TargetName));
            OnPropertyChanged(nameof(TargetKey));
            OnPropertyChanged(nameof(HasNoSelection));
            RefreshContainerFlags();
        }

        public bool IsBulk => PickedRows.Count > 1;

        public string PickCountText => $"{PickedRows.Count} records picked";

        private string _bulkStatus = "";
        public string BulkStatus
        {
            get => _bulkStatus;
            private set => SetProperty(ref _bulkStatus, value);
        }

        public RelayCommand ApplyToPickedCommand { get; }
        public RelayCommand ClearPickCommand { get; }

        // ADDITIVE, like every other bulk apply in this tool: the template is added to what each
        // record already has rather than replacing it. The one exception is a container's LEVELS,
        // where the template wins - the same "old gets replaced by new" rule
        // MultiSelectDetailVM.ApplyContainersAsync and PresetApplyService.ApplyContainer follow, and
        // for the same reason: assigning a container and then setting its levels in bulk has to
        // reach a record that already had the container, or nothing happens at all.
        internal async Task ApplyToPickedAsync()
        {
            var template = BulkTarget.ContainerSelection.SelectedContainers.ToList();
            if (template.Count == 0)
            {
                BulkStatus = "Pick at least one container first.";
                return;
            }

            var rows = PickedRows.Select(r => r.Row).ToList();
            int changed = 0;

            foreach (var row in rows)
            {
                bool touched = false;

                foreach (var templateRow in template)
                {
                    var target = row.ContainerSelection.SelectedContainers
                        .FirstOrDefault(c => c.ContainerKey == templateRow.ContainerKey);

                    if (target == null)
                    {
                        row.ContainerSelection.ToggleContainer(templateRow.ContainerKey);
                        target = row.ContainerSelection.SelectedContainers
                            .FirstOrDefault(c => c.ContainerKey == templateRow.ContainerKey);
                        touched = true;
                    }

                    if (target == null) continue;
                    if (templateRow.LVLiEntries.Count == 0) continue;

                    var placements = templateRow.LVLiEntries.ToDictionary(
                        l => l.Key, l => new LvliPlacement(l.Level, l.Count));

                    // ApplyPlacements zeroes every list the template does not name, so the before/
                    // after comparison is what keeps re-applying the same template from reporting
                    // every record as changed and saving them all again. The count is part of it: a
                    // template that only changes the amount is still a change.
                    var before = Describe(target);
                    target.ApplyPlacements(placements);
                    if (Describe(target) != before) touched = true;
                }

                if (!touched) continue;

                await row.CommitPlacementAsync();
                changed++;
            }

            BulkStatus = changed == 0
                ? "No change - every picked record already had these containers."
                : $"Placed {changed} of {rows.Count} picked records.";
        }

        private static string Describe(ContainerEntryVM entry) =>
            string.Join(";", entry.LVLiEntries.Select(l => $"{l.Key},{l.Level},{l.Count}"));

        private IPlaceable? _selectedPlaceable;
        public IPlaceable? SelectedPlaceable
        {
            get => _selectedPlaceable;
            set
            {
                if (!SetProperty(ref _selectedPlaceable, value)) return;

                RefreshContainerFlags();
                OnPropertyChanged(nameof(PlacementTarget));
                OnPropertyChanged(nameof(HasSelection));
                OnPropertyChanged(nameof(HasNamedTarget));
                OnPropertyChanged(nameof(TargetName));
                OnPropertyChanged(nameof(TargetKey));
                OnPropertyChanged(nameof(HasNoSelection));
            }
        }

        public bool HasSelection => PlacementTarget != null;

        // What is being placed, named on the right. The tree used to carry the key under every row;
        // it is read once, about the record in hand, and until now the panel never said which record
        // that was at all. Not shown for a bulk pick - the banner above it already counts them.
        public bool HasNamedTarget => !IsBulk && _selectedPlaceable != null;

        public string TargetName => _selectedPlaceable?.Display ?? "";

        public string TargetKey => _selectedPlaceable?.Key ?? "";

        // A second bool rather than an inverting converter: the project has one visibility converter
        // and adding a second mechanism for one label is how a codebase grows two of everything.
        public bool HasNoSelection => PlacementTarget == null;

        // --- What the shared panel asks for ---
        //
        // ONE PANEL FOR BOTH, which is the whole reason this works: with several records picked the
        // panel is handed a template instead of a record, and everything it does - the catalog, the
        // LVLi level rows, the leveled-list window - keeps working unchanged because a template
        // answers the same three members. The alternative was a second copy of
        // Styles/ContainerPlacementPanel.xaml, and the six copied condition templates in this
        // codebase are the standing reminder of how that ends.
        public IPlaceable? PlacementTarget => IsBulk ? BulkTarget : _selectedPlaceable;

        private BulkPlacementTemplateVM? _bulkTarget;
        private BulkPlacementTemplateVM BulkTarget =>
            _bulkTarget ??= new BulkPlacementTemplateVM(_containers());

        // Nothing to say here yet. The item editor computes it per item from the scanned placements;
        // repeating that lookup is a separate job, and an empty line is honest where a wrong one
        // would not be.
        public string ExistingPlacementSummary => "";

        public bool HasChangedLists => false;
        public string ChangedListsButtonText => "";
        public ICommand ShowChangedListsCommand => null;

        public ObservableCollection<ContainerEntryVM> AllContainerVMs { get; } = new();

        private bool _showExpertContainers = true;
        public bool ShowExpertContainers
        {
            get => _showExpertContainers;
            set { if (SetProperty(ref _showExpertContainers, value)) OnPropertyChanged(nameof(FilteredContainers)); }
        }

        private string _containerSearchText = "";
        public string ContainerSearchText
        {
            get => _containerSearchText;
            set { if (SetProperty(ref _containerSearchText, value ?? "")) OnPropertyChanged(nameof(FilteredContainers)); }
        }

        public IEnumerable<ContainerEntryVM> FilteredContainers =>
            AllContainerVMs.Where(c =>
                string.IsNullOrWhiteSpace(ContainerSearchText)
                || c.Name.Contains(ContainerSearchText, StringComparison.OrdinalIgnoreCase));

        public RelayCommand<string> ToggleContainerForSelectedItemCommand { get; }
        public RelayCommand ClearContainerSelectionCommand { get; }
        public RelayCommand ToggleExpertContainersCommand { get; }

        // The left list's ticks have to follow whatever the panel is editing, or it would show the
        // previous record's placement as though it were this one's. Reads PlacementTarget and not the
        // selected record, so the ticks show the TEMPLATE while several records are picked.
        private void RefreshContainerFlags()
        {
            var selected = PlacementTarget?.ContainerSelection.SelectedContainers
                .Select(sc => sc.ContainerKey)
                .ToHashSet(StringComparer.OrdinalIgnoreCase) ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var vm in AllContainerVMs)
                vm.IsSelected = selected.Contains(vm.ContainerKey);
        }
    }

    // What the placement panel needs of the thing being placed. Implemented by ItemNodeVM (armor and
    // weapons, the same instances the item tree holds) and by WorldItemNodeVM (books, scrolls, misc,
    // soul gems, ammo, food, ingredients, keys).
    public interface IPlaceable
    {
        string Key { get; }
        string Display { get; }
        ContainerSelectionVM ContainerSelection { get; }

        // Write the selection back. Each kind knows its own route: an item goes through the save
        // pipeline it already has, a world item straight to its one shadow column.
        void CommitPlacement();

        // THE SAME WRITE, BUT AWAITED, and every caller that reads the result back must use this one.
        //
        // An item's route is the tracked-field pipeline, which debounces through one shared
        // debouncer: fifty CommitPlacement() calls in a loop cancel each other and only the last
        // record is ever saved, and a single one has not landed yet when the caller looks. Both
        // caught us out - the bulk apply first, then the Container tab taking a placement back out,
        // where the pane re-read the database before the save arrived and showed the placement it
        // had just removed. MainContentVM.PersistFieldAsync exists for exactly this. A world item
        // writes straight through and has nothing to wait for.
        Task CommitPlacementAsync();

        // The placement as it is stored, and the way to replace it wholesale.
        //
        // NOT ContainerSelection.BuildString() ROUND-TRIPPED, which is what this used to do: loading
        // a string into a selection drops any container the item's catalogue does not know, so a
        // round trip quietly rewrites placements nobody touched. The string is the record; the
        // selection is a view of it. ContainerPlacementEditor decides what the new string is, and
        // this writes exactly that.
        string PlacementString { get; }

        Task SetPlacementAsync(string containerString);
    }

    // The placement being built for several records at once.
    //
    // AN IPlaceable THAT PLACES NOTHING, on purpose. It exists so the shared container panel can be
    // handed a template exactly where it would otherwise be handed a record - same ContainerSelection,
    // same LVLi rows, same leveled-list window. What it must NOT have is a route to the database:
    // writing is PlacementListVM.ApplyToPickedCommand's job, once, for every picked record. Hence
    // both Commit members do nothing at all rather than throwing - the panel calls them on gestures
    // this tab routes elsewhere, and a template that crashes the tab when the panel does its normal
    // work would be worse than one that quietly stays a template.
    //
    // It also does not subscribe ContainerSelection.LevelChanged, which is what every real placeable
    // does to persist a slider: here a level belongs to the template until Apply carries it over.
    public sealed class BulkPlacementTemplateVM : IPlaceable
    {
        public BulkPlacementTemplateVM(List<ContainerRecord> allContainers)
        {
            ContainerSelection = new ContainerSelectionVM(allContainers);
            ContainerSelection.OwnerInfo = () => (Key, Display);
        }

        public string Key => "";
        public string Display => "the picked records";

        public ContainerSelectionVM ContainerSelection { get; }

        public void CommitPlacement() { }
        public Task CommitPlacementAsync() => Task.CompletedTask;

        public string PlacementString => ContainerSelection.BuildString();
        public Task SetPlacementAsync(string containerString)
        {
            ContainerSelection.LoadFromString(containerString ?? "{}");
            return Task.CompletedTask;
        }
    }

    // One row of a placement tree.
    //
    // A WRAPPER, AND IT HAS TO BE. The Armor and Weapons tabs show the item tree's own ItemNodeVM
    // instances, and ItemNodeVM.IsSelected is the ITEM TAB's multi-selection - the one
    // MainContentVM.SelectedItems and the bulk editor are built on. Picking rows here would light
    // them up over in the item tree, and MainContentVM.ClearMultiSelection would wipe this tab's
    // selection out from under it. So the pick lives on a row this tab owns, and the record keeps
    // its own.
    public sealed class PlaceableRowVM : ViewModelBase
    {
        public PlaceableRowVM(IPlaceable row)
        {
            Row = row;
        }

        public IPlaceable Row { get; }

        public string Key => Row.Key;
        public string Display => Row.Display;

        private bool _isPicked;
        public bool IsPicked
        {
            get => _isPicked;
            set => SetProperty(ref _isPicked, value);
        }
    }
}
