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
                               Func<IReadOnlyList<string>>? loadOrder = null, Func<HashSet<string>>? editedPlacementKeys = null)
        {
            Title = title;
            _source = source;
            _containers = containers;
            _editedPlacementKeys = editedPlacementKeys ?? (() => new HashSet<string>(StringComparer.OrdinalIgnoreCase));

            ResetPlacementCommand = new RelayCommand(async () => await ResetPlacementAsync());

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
                if (!IsBulk) { target.CommitPlacement(); MarkEdited(target); }

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
                if (!IsBulk) { target.CommitPlacement(); MarkEdited(target); }
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

        // Whether EnsureLoaded would read anything - so the loading overlay (P-P1) only shows when it does.
        public bool NeedsLoad => !_loaded;

        public void EnsureLoaded()
        {
            if (_loaded) return;
            _loaded = true;
            Reload();
        }

        public void Invalidate()
        {
            _loaded = false;
            _existingPlacementCache.Clear();
            Rows.Clear();
            AllContainerVMs.Clear();
            RebuildTree();
            SelectedPlaceable = null;
        }

        public void Reload()
        {
            _pick.Clear();

            // Timed for Logs\perf.log (docs/Performance-Plan.md, P-P0): reading the records, and the
            // container rows the placement panel offers.
            var step = PerfLog.Measure($"{Title} tab: read the records");
            var source = _source();
            step.Detail = $"{source.Count} records";
            step.Dispose();

            Rows.Clear();
            foreach (var row in source)
                Rows.Add(row);

            // A reload follows a rescan, the one thing that changes where a record already sits.
            _existingPlacementCache.Clear();

            step = PerfLog.Measure($"{Title} tab: build container rows");
            AllContainerVMs.Clear();
            foreach (var c in _containers())
                AllContainerVMs.Add(new ContainerEntryVM(c));
            step.Detail = $"{AllContainerVMs.Count} containers";
            step.Dispose();

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

            RefreshEdited();

            _tree.Rebuild(_rowVMs);
            OnPropertyChanged(nameof(FilteredTree));
        }

        // --- Only edited, and the reset (user, 2026-10-17) ---
        //
        // "Edited" is read from the DATABASE, not from the rows: an armor or weapon only knows its
        // scanned placement once its details were opened in the item tab, so a flag on the view model
        // would miss every item nobody clicked this session. One query over three tables.
        private readonly Func<HashSet<string>> _editedPlacementKeys;
        private HashSet<string> _edited = new(StringComparer.OrdinalIgnoreCase);

        private void RefreshEdited()
        {
            _edited = _editedPlacementKeys() ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in _rowVMs) row.IsEdited = _edited.Contains(row.Key);
            OnPropertyChanged(nameof(EditedCountText));
            OnPropertyChanged(nameof(CanResetPlacement));
        }

        // A placement just made counts at once. The database would say so too, but for an armor or
        // weapon only after the item tab's debounced save - the marker should not wait for that.
        private void MarkEdited(IPlaceable placeable)
        {
            if (placeable == null || string.IsNullOrEmpty(placeable.Key)) return;
            _edited.Add(placeable.Key);
            foreach (var row in _rowVMs.Where(r => string.Equals(r.Key, placeable.Key, StringComparison.OrdinalIgnoreCase)))
                row.IsEdited = true;
            OnPropertyChanged(nameof(EditedCountText));
        }

        public string EditedCountText
        {
            get
            {
                int n = _rowVMs.Count(r => r.IsEdited);
                return n == 1 ? "1 edited" : $"{n} edited";
            }
        }

        private bool _onlyEdited;
        public bool OnlyEdited
        {
            get => _onlyEdited;
            set
            {
                if (!SetProperty(ref _onlyEdited, value)) return;
                if (value) RefreshEdited();
                _tree.RowFilter = value ? r => r.IsEdited : null;
                _tree.Filter(_rowSearch);
                OnPropertyChanged(nameof(FilteredTree));
            }
        }

        // The record in hand, or every picked one. NOT narrowed to the edited set: an armor's
        // placement is saved through the item tab's debounced pipeline, so a placement made a moment
        // ago may not be in the database yet. The reset flushes that first (MainContentVM), and the
        // database only touches a row whose placement IS the user's (ItemDBHandler.ResetPlacement).
        private List<IPlaceable> ResetTargets() =>
            (IsBulk ? PickedRows.Select(r => r.Row) : _selectedPlaceable == null ? Enumerable.Empty<IPlaceable>() : new[] { _selectedPlaceable })
                .ToList();

        public bool CanResetPlacement => IsBulk ? PickedRows.Count > 0 : _selectedPlaceable != null;

        public RelayCommand ResetPlacementCommand { get; }

        // Several records at once asks first: it throws away placements the user may have built one
        // by one, and there is no undo behind it.
        internal async Task ResetPlacementAsync(bool confirm = true)
        {
            var targets = ResetTargets();
            if (targets.Count == 0) return;

            if (confirm && targets.Count > 1)
            {
                var answer = System.Windows.MessageBox.Show(
                    $"Take the placements of {targets.Count} records back to what the plugins say?",
                    "Reset placements", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question);
                if (answer != System.Windows.MessageBoxResult.Yes) return;
            }

            foreach (var target in targets)
                await target.ResetPlacementAsync();

            RefreshEdited();
            RefreshContainerFlags();
            if (_onlyEdited)
            {
                // The reset records leave the "only edited" tree; it must not be rebuilt under a
                // pick that still holds them (the owners tab next door, 2026-10-17).
                ClearMultiSelection();
                _tree.Filter(_rowSearch);
                OnPropertyChanged(nameof(FilteredTree));
            }
            BulkStatus = targets.Count == 1 ? "Placement reset." : $"Reset {targets.Count} placements.";
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
            OnPropertyChanged(nameof(CanResetPlacement));
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
                MarkEdited(row);
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
                OnPropertyChanged(nameof(CanResetPlacement));
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

        // --- "Already in your load order" (read-only) ---
        //
        // The same answer the item editor gives (MainContentVM.UpdateAllContainerSelectionFlags), and
        // the same strict split from the user's own placement: a badge says the GAME has it there, a
        // tick says the PATCH will put it there. Worth more here than over there - this tab also
        // places books, scrolls and the rest, which sit in leveled lists far more often than armor.
        //
        // Cached per record for the same reason as in MainContentVM (~3 ms a lookup, and every
        // container toggle comes back through RefreshContainerFlags); dropped with every reload,
        // which is what a rescan ends in.
        private readonly Dictionary<string, IReadOnlyList<Placement>> _existingPlacementCache
            = new(StringComparer.OrdinalIgnoreCase);

        private string _existingPlacementSummary = "";
        public string ExistingPlacementSummary
        {
            get => _existingPlacementSummary;
            private set => SetProperty(ref _existingPlacementSummary, value);
        }

        // Nothing for a bulk pick: the template is not a record, and "already in the game" would have
        // to mean one of the picked records - any answer would be about the wrong one.
        private IReadOnlyList<Placement> ExistingPlacementsForTarget()
        {
            if (IsBulk || _selectedPlaceable == null || string.IsNullOrWhiteSpace(_selectedPlaceable.Key))
                return Array.Empty<Placement>();

            var key = _selectedPlaceable.Key;
            if (_existingPlacementCache.TryGetValue(key, out var cached))
                return cached;

            var found = PlacementLookup.WhereIs(key);
            _existingPlacementCache[key] = found;
            return found;
        }

        // --- Lists whose own properties the user changed ---
        //
        // The way back to them, as in the item editor. The set is cached in LeveledListEditStore, so
        // asking on every selection change costs nothing.
        public int ChangedListCount => LeveledListEditStore.EditedKeys().Count;

        public bool HasChangedLists => ChangedListCount > 0;

        public string ChangedListsButtonText => ChangedListCount == 1
            ? "1 changed list"
            : $"{ChangedListCount} changed lists";

        public ICommand ShowChangedListsCommand => new RelayCommand(() =>
        {
            View.ChangedListsWindow.Show(System.Windows.Application.Current?.MainWindow);
            RefreshChangedListCount();
        });

        private void RefreshChangedListCount()
        {
            OnPropertyChanged(nameof(ChangedListCount));
            OnPropertyChanged(nameof(HasChangedLists));
            OnPropertyChanged(nameof(ChangedListsButtonText));
        }

        public ObservableCollection<ContainerEntryVM> AllContainerVMs { get; } = new();

        // Starts in the standard view (merchants only), like the item tab. It used to start in expert
        // view, and the list below did not read the switch at all - so the button changed its label
        // and nothing else.
        private bool _showExpertContainers;
        public bool ShowExpertContainers
        {
            get => _showExpertContainers;
            set
            {
                if (!SetProperty(ref _showExpertContainers, value)) return;

                OnPropertyChanged(nameof(FilteredContainers));

                // The summary counts the hits the current list cannot show.
                RefreshContainerFlags();
            }
        }

        private string _containerSearchText = "";
        public string ContainerSearchText
        {
            get => _containerSearchText;
            set { if (SetProperty(ref _containerSearchText, value ?? "")) OnPropertyChanged(nameof(FilteredContainers)); }
        }

        public IEnumerable<ContainerEntryVM> FilteredContainers =>
            AllContainerVMs.Where(c =>
                ContainerEntryVM.IsShown(c, ShowExpertContainers)
                && (string.IsNullOrWhiteSpace(ContainerSearchText)
                    || c.Name.Contains(ContainerSearchText, StringComparison.OrdinalIgnoreCase)));

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

            var existing = ExistingPlacementsForTarget();
            var heldCounts = PlacementLookup.HeldPerContainer(existing);

            // The leveled-list half lands on the rows inside the selected containers - the only place
            // a list has a row at all. Set on the selection, not per row, so a container ticked later
            // gets its marks too.
            if (!IsBulk)
                _selectedPlaceable?.ContainerSelection.SetExistingListKeys(
                    existing.Where(p => p.Kind == PlacementKind.LeveledList).Select(p => p.Key));

            foreach (var vm in AllContainerVMs)
            {
                vm.IsSelected = selected.Contains(vm.ContainerKey);
                vm.SetExistingPlacement(heldCounts.TryGetValue(vm.ContainerKey, out var held) ? held : 0);
            }

            // Counted against the view, not the search: a hit the standard view hides gets the
            // "switch to Expert view" hint, the same sentence the item tab says.
            int visible = AllContainerVMs.Count(vm =>
                ContainerEntryVM.IsShown(vm, ShowExpertContainers) && heldCounts.ContainsKey(vm.ContainerKey));

            ExistingPlacementSummary = IsBulk || _selectedPlaceable == null
                ? ""
                : PlacementLookup.DescribeExisting(
                    heldCounts.Count,
                    existing.Count(p => p.Kind == PlacementKind.LeveledList),
                    Math.Max(heldCounts.Count - visible, 0));

            RefreshChangedListCount();
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

        // Which table the placement is stored in ("Armor", "Weapons", "WorldItem") - what "only
        // edited" and the reset on the Container/LeveledList tab need to reach the database row.
        // "" for something that is not a record (the bulk template).
        string PlacementTable { get; }

        // Back to what the plugins say, for the placement only - the record's other edits stay.
        Task ResetPlacementAsync();
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

        // A template is not a record: nothing stored, nothing to reset.
        public string PlacementTable => "";
        public Task ResetPlacementAsync() => Task.CompletedTask;
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

        // The placement is the user's - drives the marker in the tree and "only edited". Set by
        // PlacementListVM from the database, see RefreshEdited there.
        private bool _isEdited;
        public bool IsEdited
        {
            get => _isEdited;
            set => SetProperty(ref _isEdited, value);
        }

        private bool _isPicked;
        public bool IsPicked
        {
            get => _isPicked;
            set => SetProperty(ref _isPicked, value);
        }
    }
}
