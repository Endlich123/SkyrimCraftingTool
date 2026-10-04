using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using SkyrimCraftingTool.Model;
using SkyrimCraftingTool.Services;

namespace SkyrimCraftingTool.ViewModel
{
    // One sub-tab of the Container/LeveledList tab: a tree of owners on the left, the contents of the
    // selected one on the right, with rows the user can take out again.
    //
    // ONE VIEW MODEL FOR BOTH KINDS, parameterised by RemovalScope. Containers and leveled lists ask
    // exactly the same question here - what is in this thing, and what do I want gone - and the only
    // differences are the words on screen and which table the decision lands in. A second copy of
    // this file would be the six copied condition templates all over again.
    public sealed class OwnerContentsVM : ViewModelBase, IMultiSelectTree
    {
        private readonly RemovalScope _scope;
        private readonly string _dbPath;

        // How to reach the live view model of an item that has a placement here, if there is one.
        // Supplied by the owner (WorldContentVM) rather than looked up: this tab has no business
        // knowing where armor, weapons and world items are held. Null is allowed and means "write
        // the database row" - see UnPlace.
        private readonly Func<string, IPlaceable?>? _findPlaceable;

        public OwnerContentsVM(RemovalScope scope, string dbPath = null, Func<string, IPlaceable?>? findPlaceable = null,
                               Func<IReadOnlyList<string>>? loadOrder = null)
        {
            _scope = scope;
            _dbPath = dbPath;
            _findPlaceable = findPlaceable;

            // Built here rather than as a field initializer only because it needs the load order
            // handed in. First, because the pick below reads its rows.
            //
            // Matching the KEY and not only the name is what makes "Skyrim.esm" work as a search:
            // the key starts with the plugin, so typing a plugin name keeps everything it defines.
            _tree = new PluginTree<OwnerRowVM>(
                o => o.Key,
                (o, search) => o.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
                               || o.Key.Contains(search, StringComparison.OrdinalIgnoreCase),
                loadOrder);

            _pick = new MultiSelectState<OwnerRowVM>(
                () => _tree.RowsInScreenOrder(),
                (row, picked) => row.IsPicked = picked);

            _pick.Picked.CollectionChanged += (_, _) => OnPickChanged();

            // The two sections are a GROUPING over one collection, not two lists in a stack. The
            // grouping is put on the default view here rather than through a CollectionViewSource in
            // the XAML, because a CollectionViewSource declared in a DataTemplate's resources cannot
            // bind to the template's data item - resources are not part of the tree the DataContext
            // flows down. MultiSelectDetailVM exposes its views from the view model for the same
            // kind of reason.
            //
            // Encounter order decides the group order, which is why RebuildRows adds the in-section
            // first and there is no second sort to keep in step with it.
            System.Windows.Data.CollectionViewSource.GetDefaultView(Rows)
                .GroupDescriptions.Add(new System.Windows.Data.PropertyGroupDescription(nameof(IOwnerPaneRow.Section)));
        }

        // The tab header. Set by the owner rather than derived, so the two sub-tabs read the way the
        // rest of them do.
        public string Title => IsContainer ? "Containers" : "Leveled lists";

        public bool IsContainer => _scope == RemovalScope.Container;

        public string OwnerNoun => IsContainer ? "container" : "leveled list";

        // --- The tree ---

        public ObservableCollection<OwnerRowVM> Owners { get; } = new();

        private bool _loaded;

        // Loaded on first sight rather than at construction: the tab is built with the window and a
        // real load order has thousands of containers. Nothing here is worth reading before someone
        // actually opens it.
        public void EnsureLoaded()
        {
            if (_loaded) return;
            _loaded = true;
            Reload();
        }

        // Dropped rather than refreshed after a scan, same as the NPC tab: the rows are scanned data
        // and whatever this is holding is stale. The next visit rebuilds.
        // ON THE UI THREAD, because this one is called from somewhere else. It hangs off
        // MainContentVM.DataLoaded, which a rescan raises without going through the dispatcher - and
        // Rows carries a grouped CollectionView from the moment this view model is built, which
        // refuses changes from any thread but the one that made it. Before the grouping the bound
        // collections had the same affinity, but only once the tab had been opened; this is the
        // version that would also bite someone who never opened it.
        public void Invalidate()
        {
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher != null && !dispatcher.CheckAccess())
            {
                dispatcher.Invoke(Invalidate);
                return;
            }

            _loaded = false;
            _pick.Clear();
            Owners.Clear();
            Contents.Clear();
            History.Clear();
            RebuildTree();
            OnPropertyChanged(nameof(HasSelection));
            OnPropertyChanged(nameof(ContentsHeader));
            OnPropertyChanged(nameof(ContentsKey));
            OnPropertyChanged(nameof(ContentsCountText));
            OnPropertyChanged(nameof(HasContentsSubHeader));
        }

        public void Reload()
        {
            var previous = _selectedOwner?.Key;

            // The rows are about to be replaced, so the pick cannot survive as object references.
            _pick.Clear();

            Owners.Clear();
            var rows = IsContainer
                ? PlacementLookup.AllContainers(_dbPath)
                : PlacementLookup.AllLeveledLists(_dbPath);

            foreach (var row in rows)
                Owners.Add(new OwnerRowVM(row));

            RebuildTree();

            OnPropertyChanged(nameof(OwnerCountText));
            OnPropertyChanged(nameof(RemovalCountText));
            OnPropertyChanged(nameof(HasRemovals));

            // Keep the user where they were if the row survived the reload. Only the single pick is
            // restored: re-picking several would have to re-read all of them, and a reload happens
            // when the data underneath changed.
            if (!string.IsNullOrEmpty(previous))
                PickOnly(Owners.FirstOrDefault(
                    o => string.Equals(o.Key, previous, StringComparison.OrdinalIgnoreCase)));
        }

        public string OwnerCountText => Owners.Count == 1
            ? $"1 {OwnerNoun}"
            : $"{Owners.Count} {OwnerNoun}s";

        private string _search = "";
        public string Search
        {
            get => _search;
            set
            {
                if (!SetProperty(ref _search, value ?? "")) return;

                _tree.Filter(_search);
                OnPropertyChanged(nameof(FilteredTree));
            }
        }

        // --- Plugin, and the owners under it ---
        //
        // Everything about the tree is PluginTree's, which the placement tabs share. It is built in
        // the constructor, where the search and the load order are given to it.
        private readonly PluginTree<OwnerRowVM> _tree;

        public IEnumerable<PluginGroupNodeVM> FilteredTree => _tree.Nodes;

        private void RebuildTree()
        {
            _tree.Rebuild(Owners);
            OnPropertyChanged(nameof(FilteredTree));
        }

        // The single pick, for the header and for restoring the user's place after a reload. Set from
        // the pick and not the other way round: the contents are loaded from PickedOwners, which is
        // one owner in the ordinary case and several when the user asked for several.
        private OwnerRowVM? _selectedOwner;
        public OwnerRowVM? SelectedOwner
        {
            get => _selectedOwner;
            private set => SetProperty(ref _selectedOwner, value);
        }

        // One owner, picked from code - the reload's "put the user back where they were". Goes
        // through the pick so there is one path into LoadContents and no second way to get the
        // selection and the contents out of step.
        public void PickOnly(OwnerRowVM? owner)
        {
            if (owner == null)
            {
                _pick.Clear();
                return;
            }

            _pick.Handle(owner, ctrl: false, shift: false);
        }

        public bool HasSelection => PickedOwners.Count > 0;

        // --- Picking several owners at once ---
        //
        // Ctrl/Shift in the tree, the same gesture the item tree answers to; MultiSelectState holds
        // the arithmetic. What it MEANS here: the contents pane shows what is in all of them at once,
        // folded per object, and a removal reaches every picked owner that holds the object. That is
        // the whole point of it - "take this out of these eleven chests" was eleven visits before.
        private readonly MultiSelectState<OwnerRowVM> _pick;

        public ObservableCollection<OwnerRowVM> PickedOwners => _pick.Picked;

        public void HandleOwnerClick(OwnerRowVM clicked, bool ctrl, bool shift)
        {
            _pick.Handle(clicked, ctrl, shift);
        }

        // --- IMultiSelectTree: what the tree in the view asks of this ---

        public bool IsRow(object candidate) => candidate is OwnerRowVM;

        public void HandleRowClick(object row, bool ctrl, bool shift)
        {
            if (row is OwnerRowVM owner) HandleOwnerClick(owner, ctrl, shift);
        }

        // A plugin row or an arrow key: the pick belongs to the gesture before this one.
        public void SelectSingle(object row)
        {
            ClearMultiSelection();

            if (row is OwnerRowVM owner) PickOnly(owner);
        }

        public void ClearMultiSelection() => _pick.Clear();

        private void OnPickChanged()
        {
            SelectedOwner = PickedOwners.Count == 1 ? PickedOwners[0] : null;

            LoadContents();

            OnPropertyChanged(nameof(PickedOwners));
            OnPropertyChanged(nameof(IsMultiOwner));
            OnPropertyChanged(nameof(HasSelection));
            OnPropertyChanged(nameof(ContentsHeader));
            OnPropertyChanged(nameof(ContentsKey));
            OnPropertyChanged(nameof(ContentsCountText));
            OnPropertyChanged(nameof(HasContentsSubHeader));
        }

        public bool IsMultiOwner => PickedOwners.Count > 1;

        // --- One list, two sections ---
        //
        // The contents and the History used to be two boxes, and the two halves of one question kept
        // having to be read against each other: an entry the user removed sat struck through in the
        // first box, while an entry an override removed sat in the second. Both mean "this does not
        // come out of here", and they now sit in the same place.
        //
        // The two rules that make it one view rather than two lists stacked up:
        //
        //   Remove something that the LOAD ORDER put here  -> it moves down into the history.
        //   Remove something YOU put here                  -> it is simply gone; you never lost it.
        //   Put something back from the history            -> it moves up into the list.
        //
        // Which is also why there are two buttons and not four: "put back" covers undoing your own
        // removal and restoring what an override dropped, because from the list's point of view
        // those are the same thing happening.
        //
        // Rows holds both kinds - OwnedEntryVM for an object in the thing, LostEntryVM for one an
        // override dropped - and the view groups it by Section. Contents and History stay as the two
        // honest collections underneath; Rows is how they are shown.
        public ObservableCollection<object> Rows { get; } = new();

        public string SectionIn => IsContainer ? "In this container" : "In this list";

        public string SectionOut => "Not in it any more";

        public string SectionNote => IsContainer
            ? "The lower section is what is not in it: objects you took out, and what the leveled lists "
              + "hanging in it lost to an override. Tick and use the buttons to move rows between the two."
            : "The lower section is what is not in it: entries you took out, and entries an override "
              + "dropped. Tick and use the buttons to move rows between the two.";

        private void RebuildRows()
        {
            Rows.Clear();

            // In-section first, so the groups come out in this order - PropertyGroupDescription
            // groups in encounter order and there is no separate sort to keep in step.
            foreach (var row in Contents.Where(r => !r.IsRemoved))
            {
                row.Section = SectionIn;
                Rows.Add(row);
            }

            foreach (var row in Contents.Where(r => r.IsRemoved))
            {
                row.Section = SectionOut;
                Rows.Add(row);
            }

            // Only the ones not going back. A restored entry is already up in the list as "+ readded"
            // - having it in both sections at once is exactly the double reading this merge removes.
            foreach (var lost in History.Where(h => !h.IsRestored))
            {
                lost.Section = SectionOut;
                Rows.Add(lost);
            }

        }

        public ObservableCollection<OwnedEntryVM> Contents { get; } = new();

        public string ContentsHeader => PickedOwners.Count switch
        {
            0 => $"Pick a {OwnerNoun} to see what is in it. Ctrl or Shift picks several.",
            1 => PickedOwners[0].Name,
            var n => $"{n} {OwnerNoun}s picked - everything in them, folded per object",
        };

        // The key, and what the tree no longer spells out. It belongs here rather than on every row:
        // it is an identifier you read once, about the thing you are looking at - on the rows it was
        // a second line under each of 522 containers, which is what made the tree twice as tall as it
        // needed to be.
        //
        // KEPT APART FROM THE COUNT on purpose: the key is selected and copied into xEdit or a
        // SkyPatcher rule, and "Skyrim.esm|0C0001  ·  21 entries" is not something anyone can paste.
        public string ContentsKey => PickedOwners.Count == 1 ? PickedOwners[0].Key : "";

        public string ContentsCountText => PickedOwners.Count == 1 ? PickedOwners[0].CountText : "";

        public bool HasContentsSubHeader => PickedOwners.Count == 1;

        private void LoadContents()
        {
            Contents.Clear();

            var owners = PickedOwners.ToList();
            if (owners.Count == 0)
            {
                OnPropertyChanged(nameof(HasContents));

                // Nothing picked means no list, so no properties and no calculator. Skipping this
                // left the previous list's panel standing over an empty pane.
                RefreshListEditor(owners);
                return;
            }

            // Insertion order, not sorted: for one owner that is the order the query returned, which
            // is what this pane always showed. Objects only the later owners hold are appended.
            var order = new List<OwnedEntryVM>();
            var byReference = new Dictionary<string, OwnedEntryVM>(StringComparer.OrdinalIgnoreCase);

            foreach (var owner in owners)
            {
                var rows = IsContainer
                    ? PlacementLookup.ReadContainerContents(owner.Key, _dbPath)
                    : PlacementLookup.ReadListContents(owner.Key, _dbPath);

                foreach (var row in rows)
                {
                    if (byReference.TryGetValue(row.Reference, out var existing))
                    {
                        existing.FoldIn(row, owner.Key);
                        continue;
                    }

                    var vm = new OwnedEntryVM(row, owner.Key, hasLevel: !IsContainer);
                    WatchSelection(vm);
                    byReference[row.Reference] = vm;
                    order.Add(vm);
                }
            }

            foreach (var row in order)
            {
                row.SelectionSize = owners.Count;

                // The rows behind the fold, for one owner only - see OwnedEntryVM.ShowBreakdown.
                if (owners.Count == 1) row.ShowBreakdown();

                Contents.Add(row);
            }

            OnPropertyChanged(nameof(HasContents));

            // Before the planned rows, because a restored entry IS a planned row and the History is
            // where it is known.
            LoadHistory(owners);

            if (owners.Count == 1)
                AddWhatThePatchWillAdd(owners[0], byReference, order);

            OnPropertyChanged(nameof(HasContents));

            RebuildRows();
            RefreshListEditor(owners);
        }

        // What the patch will put in here, shown among the scanned rows.
        //
        // TWO LOOKUPS, BECAUSE A PLACEMENT IS TWO DIFFERENT THINGS. An item's ContainerString says
        // "{ContainerKey: {LVLiKey,Level; ...}}", and the sliders decide which: a raised slider
        // places the item into THOSE leveled lists, every slider at 0 places it into the container
        // itself. So a list asks PlannedFor and a container asks PlannedForContainer - the second
        // one did not exist, which is why items assigned to a chest never turned up in it.
        //
        // THE addOnce RULE, the same one the list window follows (LeveledListEditorVM.
        // RebuildContents): SkyPatcher does not add an object the thing already has, so a placement
        // onto something already in there is NOT a new row and must not be advertised as one. It
        // becomes nothing at all.
        private void AddWhatThePatchWillAdd(
            OwnerRowVM owner, Dictionary<string, OwnedEntryVM> byReference, List<OwnedEntryVM> order)
        {
            var planned = IsContainer
                ? PlacementLookup.PlannedForContainer(owner.Key, _dbPath)
                : PlacementLookup.PlannedFor(owner.Key, _dbPath);

            foreach (var row in planned)
            {
                // Already in there: addOnce will not add it again, so there is nothing to show.
                if (byReference.ContainsKey(row.ItemKey)) continue;

                var vm = new OwnedEntryVM(
                    new OwnedEntry(row.ItemKey, row.Name, 0, 0, IsList: false, IsRemoved: false),
                    owner.Key, hasLevel: !IsContainer);

                vm.SelectionSize = 1;
                vm.AddPlanned(new OwnedOccurrence(row.Level, row.Count, OwnedOrigin.Planned));

                WatchSelection(vm);

                byReference[row.ItemKey] = vm;
                Contents.Add(vm);
            }

            // Entries the user put back after an override dropped them. Marked apart from a plain
            // addition: same effect on the game, different thing to read months later.
            foreach (var lost in History.Where(h => h.IsRestored))
            {
                if (byReference.TryGetValue(lost.Reference, out var existing))
                {
                    // In the list AND restored: the restoration is redundant, so say nothing new.
                    if (existing.Breakdown.Any(r => r.IsScanned)) continue;

                    existing.AddPlanned(new OwnedOccurrence(lost.Level, lost.Count, OwnedOrigin.Readded));
                    continue;
                }

                // hasLevel even in a container's pane: a restored entry goes back into the LIST that
                // lost it, and that list hands it out at a level. The row says so, next to the
                // container's own entries which have none - which is the truth about both.
                var vm = new OwnedEntryVM(
                    new OwnedEntry(lost.Reference, lost.Name, 0, 0, lost.IsList, IsRemoved: false),
                    owner.Key, hasLevel: true);

                vm.SelectionSize = 1;
                vm.AddPlanned(new OwnedOccurrence(lost.Level, lost.Count, OwnedOrigin.Readded));

                WatchSelection(vm);

                byReference[lost.Reference] = vm;
                Contents.Add(vm);
            }
        }

        public bool HasContents => Contents.Count > 0;

        // --- What this list does, and what it means for one object in it ---
        //
        // THE WINDOW'S OWN PANEL, hosted here. A leveled list has properties that decide the odds
        // for every mod feeding it - the chance of nothing, and whether it calculates for the
        // player's level - and a calculator that says what those odds come to. All of it already
        // existed; what was missing is that someone reading a list in THIS pane never came through
        // an item and so never saw any of it.
        //
        // LeveledListEditorVM is built with the pane's picked list and, as its subject, the one
        // ticked row: the calculator answers a question about one object, so it needs one. The
        // read-only constructor (no LVLiEntryVM) is what keeps the window's placement box out of it -
        // nothing is being placed here.
        private LeveledListEditorVM? _listEditor;

        public LeveledListEditorVM? ListEditor => _listEditor;

        // Containers have none of this: a chest hands out what is in it, with no chance and no level.
        public bool HasListEditor => _listEditor != null;

        // COLLAPSED BY DEFAULT, because the contents are what this pane is for. Open, the properties
        // and the calculator take about as much height as the list below them - which on a tab whose
        // whole job is "what comes out of this" left room for a row and a half.
        //
        // Remembered, unlike the History expander: someone tuning a chance works through several
        // lists in a row, and re-opening it on every one of them is the kind of friction that gets a
        // panel called useless. The History is the opposite - it is read once and closed.
        private const string ExpandedKey = "world.listPanel.expanded";

        public bool IsListEditorExpanded
        {
            get => AppPrefs.GetBool(ExpandedKey, fallback: false);
            set
            {
                if (IsListEditorExpanded == value) return;

                AppPrefs.SetBool(ExpandedKey, value);
                OnPropertyChanged();
            }
        }

        // What the box says while it is closed, so it is worth leaving closed.
        public string ListEditorHeader => _listEditor?.HeaderLine ?? "";

        private void RefreshListEditor(List<OwnerRowVM> owners)
        {
            if (IsContainer || owners.Count != 1)
            {
                SetListEditor(null);
                return;
            }

            // One ticked row is a subject; none or several is not a question anyone could answer, so
            // the properties stay and the calculator says what it is waiting for.
            var ticked = Contents.Where(r => r.IsSelected).ToList();
            var subject = ticked.Count == 1 ? ticked[0] : null;

            // Rebuilt only when the question changes. Constructing one re-reads the list, and a tick
            // on an unrelated row is not a new question.
            if (_listEditor != null
                && string.Equals(_listEditor.ListKey, owners[0].Key, StringComparison.OrdinalIgnoreCase)
                && string.Equals(_listEditor.ItemKey ?? "", subject?.Reference ?? "", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            SetListEditor(new LeveledListEditorVM(
                owners[0].Key, owners[0].Name, subject?.Reference ?? "", subject?.Name ?? "", null, _dbPath));
        }

        private void SetListEditor(LeveledListEditorVM? editor)
        {
            _listEditor = editor;

            OnPropertyChanged(nameof(ListEditor));
            OnPropertyChanged(nameof(HasListEditor));
            OnPropertyChanged(nameof(ListEditorHeader));
        }

        // --- History: what the load order took out of this ---
        //
        // The same thing the leveled-list window has always called History, in the pane where
        // someone is actually looking at the contents: entries this used to hand out and does not
        // any more, with the plugin that dropped them. The rows are the window's own LostEntryVM
        // rendered through the shared Styles/LostEntryRowTemplate.xaml, so the two cannot drift.
        //
        // PUTTING ONE BACK IS DONE HERE TOO, over a selection rather than per row: one list measured
        // lost 47 entries, and that is almost always one decision rather than 47. The same store the
        // list window writes (LeveledListRestoreStore), so a restoration made in either place is the
        // same fact - and the row that is going back says so in both.
        //
        // The decision is recorded against the LIST, never against the container being looked at: a
        // chest cannot carry an entry back, only the list that lost it can. That is why the rows
        // know their own FromListKey.
        public ObservableCollection<LostEntryVM> History { get; } = new();

        public bool HasHistory => History.Count > 0;

        public string HistoryHeader => IsContainer
            ? $" (lost by the leveled lists in it: {History.Count})"
            : $" (items lost to overrides: {History.Count})";

        // Said in the pane because the obvious reading of this block is the wrong one.
        public string HistoryNote => IsContainer
            ? "A container keeps no history of its own - the scan records what it holds now, nothing more. "
              + "This is what the leveled lists hanging in it have lost. A plugin may have dropped those on "
              + "purpose; this says who, not why. Nothing here is written to your patch."
            : "A plugin may have removed these on purpose, or may simply have overwritten the list. "
              + "This says who, not why. Nothing here is written to your patch.";

        // One owner at a time, and the reason is the row itself: a lost entry is a fact about one
        // list, and folding the histories of several picked owners together would produce rows
        // nobody could place. The contents above fold because they are facts about an OBJECT.
        public bool IsHistoryOnePickOnly => PickedOwners.Count > 1;

        public string HistoryMultiNote => $"Pick a single {OwnerNoun} to see its history.";

        private void LoadHistory(List<OwnerRowVM> owners)
        {
            History.Clear();

            if (owners.Count == 1)
            {
                // A container has no override history of its own, so its History is the losses of
                // the lists it holds - one hop, never deeper. See PlacementLookup.ReadLostEntries.
                var listKeys = IsContainer
                    ? Contents.Where(r => r.IsList).Select(r => r.Reference).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
                    : new List<string> { owners[0].Key };

                foreach (var group in PlacementLookup.ReadLostEntries(listKeys, _dbPath))
                {
                    // Once per list rather than once per row: the store answers for a whole list.
                    //
                    // Re-wrapped in an explicitly case-insensitive set. The store already returns
                    // one, but behind IReadOnlyCollection<string> a Contains() goes through LINQ and
                    // only stays case-insensitive because that overload happens to delegate to
                    // ICollection<T>.Contains. References reach this tool in either casing (see the
                    // COLLATE NOCASE notes in both schemas), so this is not a detail to leave to an
                    // implementation coincidence.
                    var restored = new HashSet<string>(
                        LeveledListRestoreStore.RestoredKeys(group.ListKey, _dbPath),
                        StringComparer.OrdinalIgnoreCase);

                    foreach (var entry in group.Entries)
                    {
                        var row = new LostEntryVM(entry, restored.Contains(entry.Reference))
                        {
                            // The display name only where the rows come from several lists - the row
                            // template hides the column when it is empty. The KEY always, because
                            // putting an entry back is recorded against the list that lost it.
                            FromList = IsContainer ? group.ListName : "",
                            FromListKey = group.ListKey,
                        };

                        WatchSelection(row);
                        History.Add(row);
                    }
                }
            }

            OnPropertyChanged(nameof(HasHistory));
            OnPropertyChanged(nameof(HistoryHeader));
            OnPropertyChanged(nameof(IsHistoryOnePickOnly));
            OnPropertyChanged(nameof(HasSelectedRows));
        }

        // --- The two directions ---
        //
        // TWO COMMANDS, NOT FOUR, and that is the point of merging the two lists. "Take it out" and
        // "put it back" are the only two things anyone does here; which of the four stores they
        // touch follows from the kind of row that was ticked, not from a fourth button.
        //
        // Acts on the ticked rows rather than one at a time: pruning a chest is usually several
        // decisions in a row, and the restore flow in the list window set the precedent.

        // Out of the list, into the history - except for what the user put there themselves, which
        // is simply dropped. You cannot have lost something you added.
        public ICommand RemoveSelectedCommand => new RelayCommand(async () => await RemoveSelectedAsync());

        internal async Task RemoveSelectedAsync()
        {
            foreach (var row in Contents.Where(r => r.IsSelected && !r.IsRemoved).ToList())
            {
                // NOTHING THE LOAD ORDER PUT HERE: there is no removal to record, only the user's
                // own addition to undo. A removeFromLLs rule for something this same patch adds
                // would be the tool arguing with itself.
                if (row.IsReaddedOnly || row.IsPlacedOnly)
                {
                    // Put back after an override dropped it: the restoration goes, and the entry is
                    // a loss again in the lower section.
                    if (row.IsReaddedOnly) UndoRestore(row.Reference);

                    // Placed from an item's own tab: the placement lives in THAT item's
                    // ContainerString, so that is what gets edited.
                    else await UnPlace(row.Reference);

                    row.IsSelected = false;
                    continue;
                }

                // The owner comes off the ROW, not off the selection: a row folded across four
                // picked chests records four removals, and one that only sits in one of them records
                // one. Reading the selection here would mark the object as removed from owners that
                // never held it.
                foreach (var ownerKey in row.OwnersStillHolding)
                {
                    RemovalStore.Add(_scope, new RemovedEntry(ownerKey, row.Reference), _dbPath);
                    row.SetRemovedIn(ownerKey, true);
                }

                row.IsSelected = false;
            }

            AfterDecisionChanged();
        }

        // Back into the list, from either side of the history: a removal of the user's own is taken
        // back, an entry an override dropped is put back at the level and amount it last had.
        public ICommand PutBackSelectedCommand => new RelayCommand(() =>
        {
            // Not restricted to fully removed rows: a row marked in three of four owners is not
            // IsRemoved, and leaving those three standing would make the way back unreachable.
            foreach (var row in Contents.Where(r => r.IsSelected).ToList())
            {
                foreach (var ownerKey in row.OwnersMarkedForRemoval)
                {
                    RemovalStore.Remove(_scope, ownerKey, row.Reference, _dbPath);
                    row.SetRemovedIn(ownerKey, false);
                }

                row.IsSelected = false;
            }

            foreach (var lost in History.Where(h => h.IsSelected && !h.IsRestored).ToList())
            {
                // Level and Count are the values the entry had where it was last seen, so it goes
                // back as it was rather than at some default - that is why the lost table carries
                // them, and why Ambiguous exists for the rows where the versions disagreed.
                //
                // Recorded against the LIST that lost it, never against the container being looked
                // at: a chest cannot carry an entry back, only the list that dropped it can.
                LeveledListRestoreStore.Add(
                    new RestoredEntry(lost.FromListKey, lost.Reference, lost.Level, lost.Count), _dbPath);

                lost.IsRestored = true;
                lost.IsSelected = false;
            }

            AfterDecisionChanged();
        });

        // Take this owner back out of the item's placement.
        //
        // THROUGH THE ITEM'S OWN VIEW MODEL WHERE THERE IS ONE. An ARMO/WEAP row on these tabs is
        // the item tree's own ItemNodeVM - it carries the dirty state and the save pipeline for that
        // record, and writing the database row behind its back would let it save the old placement
        // back later. Only when nothing is loaded does the store write the row itself.
        //
        // Either way the string is transformed by the same ContainerPlacementEditor, which is where
        // the rule lives - including the trap that makes this more than a delete (see that file).
        // AWAITED, and that is the whole reason this is async. An item's placement is saved through
        // the tracked-field pipeline, which DEBOUNCES: the write had not landed yet when this pane
        // re-read the database straight afterwards, so the row the user had just removed came back
        // and the placement looked unremovable. SetPlacementAsync goes past the debouncer.
        private async Task UnPlace(string itemKey)
        {
            var owner = SelectedOwner;
            if (owner == null) return;

            var placeable = _findPlaceable?.Invoke(itemKey);

            if (placeable != null)
            {
                var updated = ContainerPlacementEditor.WithoutPlacement(
                    placeable.PlacementString, owner.Key, IsContainer);

                await placeable.SetPlacementAsync(updated);
                return;
            }

            PlannedPlacementStore.RemovePlacement(itemKey, owner.Key, IsContainer, _dbPath);
        }

        private void UndoRestore(string reference)
        {
            var lost = History.FirstOrDefault(
                h => string.Equals(h.Reference, reference, StringComparison.OrdinalIgnoreCase));

            if (lost == null) return;

            LeveledListRestoreStore.Remove(lost.FromListKey, lost.Reference, _dbPath);
            lost.IsRestored = false;
        }

        public bool HasSelectedRows =>
            Contents.Any(r => r.IsSelected) || History.Any(h => h.IsSelected);

        public string SelectionSummary
        {
            get
            {
                int outOf = Contents.Count(r => r.IsSelected && !r.IsRemoved);
                int backIn = Contents.Count(r => r.IsSelected && r.IsRemoved)
                             + History.Count(h => h.IsSelected && !h.IsRestored);

                if (outOf == 0 && backIn == 0) return "Tick rows and move them between the sections.";

                var parts = new List<string>();
                if (outOf > 0) parts.Add($"{outOf} that can be taken out");
                if (backIn > 0) parts.Add($"{backIn} that can go back in");

                return string.Join(", ", parts) + " selected.";
            }
        }

        // The commands and the summary both read the selection, and a tick is a property on a row -
        // so the rows are listened to rather than the view being asked to report. Subscribed on
        // every row this view model creates; rows are built fresh on each load, so the old ones go
        // with the collection they were in.
        private void WatchSelection(ViewModelBase row)
        {
            row.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(OwnedEntryVM.IsSelected))
                    SelectionChanged();
            };
        }

        public void SelectionChanged()
        {
            OnPropertyChanged(nameof(HasSelectedRows));
            OnPropertyChanged(nameof(SelectionSummary));

            // The calculator's subject is the ticked row, so a tick is a new question for it.
            RefreshListEditor(PickedOwners.ToList());
        }

        // Re-read rather than patched in place. A decision here changes which SECTION a row belongs
        // to, and - for a restoration - adds a row to the list that was not there before. Three
        // separate update paths for that is how a view and its model drift apart (the list window's
        // RebuildContents carries the same note).
        private void AfterDecisionChanged()
        {
            LoadContents();
            SelectionChanged();

            OnPropertyChanged(nameof(RemovalCountText));
            OnPropertyChanged(nameof(HasRemovals));
        }

        public bool HasRemovals => RemovalStore.Count(_scope, _dbPath) > 0;

        // The standing reminder, shown only once there is something to remind about. Counted fresh
        // rather than tracked: the number is read when it is displayed, and nothing else changes it.
        public string RemovalCountText
        {
            get
            {
                int n = RemovalStore.Count(_scope, _dbPath);
                if (n == 0) return "";

                return n == 1
                    ? $"1 object will be removed from a {OwnerNoun} when you generate the patch."
                    : $"{n} objects will be removed from {OwnerNoun}s when you generate the patch.";
            }
        }
    }

    // A row of the contents pane, in whichever of its two sections it belongs. Implemented by both
    // kinds of row the pane shows - an object in the thing (OwnedEntryVM) and an entry an override
    // dropped (LostEntryVM) - so the one grouped list can hold both and the grouping has a property
    // name to key on that a rename cannot silently break.
    public interface IOwnerPaneRow
    {
        string Section { get; }
    }

    public sealed class OwnerRowVM : ViewModelBase
    {
        public OwnerRowVM(OwnerSummary summary)
        {
            Key = summary.Key;
            Name = summary.Name;
            EntryCount = summary.EntryCount;
        }

        public string Key { get; }
        public string Name { get; }
        public int EntryCount { get; }

        public string CountText => EntryCount == 1 ? "1 entry" : $"{EntryCount} entries";

        // Part of the Ctrl/Shift selection in the tree. These rows belong to this tab alone, so
        // unlike the placement tabs there is nothing to wrap - the flag can live on the row itself.
        private bool _isPicked;
        public bool IsPicked
        {
            get => _isPicked;
            set => SetProperty(ref _isPicked, value);
        }
    }

    // One OBJECT in the contents, with every occurrence of it folded in - see PlacementLookup's
    // OwnedEntry for why folded and not per row.
    //
    // FOLDED ACROSS OWNERS TOO, now that several containers or lists can be picked at once. The same
    // object sitting in four of the picked chests is one row that knows it is in four of them, not
    // four rows: the decision the user makes here is about the object, which is the same reason the
    // occurrences inside one owner were folded in the first place. The removal itself is still
    // recorded per owner - RemovalStore is keyed (owner, reference) - so the row has to carry which
    // owners it came from, and which of them already hold the decision.
    public sealed class OwnedEntryVM : ViewModelBase, IOwnerPaneRow
    {
        private readonly OwnedEntry _entry;

        // owner key -> is this object already marked for removal in that owner. Case-insensitive
        // because a container key reaches this tool in either casing (see the COLLATE NOCASE note in
        // both databases).
        private readonly Dictionary<string, bool> _owners = new(StringComparer.OrdinalIgnoreCase);

        private int _occurrences;
        private int _totalCount;
        private bool _isSelected;

        public OwnedEntryVM(OwnedEntry entry, string ownerKey, bool hasLevel = false)
        {
            _entry = entry;
            _hasLevel = hasLevel;
            _occurrences = entry.Occurrences;
            _totalCount = entry.TotalCount;
            _owners[ownerKey] = entry.IsRemoved;
        }

        // --- The rows behind the fold ---
        //
        // Filled only while ONE owner is picked. Across several owners the rows would be a pile of
        // amounts from different chests with nothing to say which came from where - the same reason
        // the History is shown for one owner at a time. The aggregate stays either way.
        private readonly bool _hasLevel;

        public ObservableCollection<OwnedOccurrenceVM> Breakdown { get; } = new();

        public bool HasBreakdown => Breakdown.Count > 0;

        public void ShowBreakdown()
        {
            Breakdown.Clear();

            foreach (var row in _entry.Breakdown ?? Array.Empty<OwnedOccurrence>())
                Breakdown.Add(new OwnedOccurrenceVM(row, _hasLevel));

            AfterBreakdownChanged();
        }

        // What the patch will put here, shown among the scanned rows rather than in a box of its
        // own: the question this pane answers is "what comes out of this", and a placement the user
        // has already made is part of that answer. Marked, never silently mixed in.
        public void AddPlanned(OwnedOccurrence row)
        {
            Breakdown.Add(new OwnedOccurrenceVM(row, _hasLevel));
            AfterBreakdownChanged();
        }

        private void AfterBreakdownChanged()
        {
            OnPropertyChanged(nameof(HasBreakdown));
            OnPropertyChanged(nameof(HasPlannedRows));
            OnPropertyChanged(nameof(HasRemoveNote));
            OnPropertyChanged(nameof(RemoveNote));
            OnPropertyChanged(nameof(IsReaddedOnly));
            OnPropertyChanged(nameof(IsPlacedOnly));
        }

        public bool HasPlannedRows => Breakdown.Any(r => !r.IsScanned);

        // Nothing but a restoration the user made. Taking THIS out is undoing that decision, not
        // recording a removal - see RemoveSelectedCommand.
        public bool IsReaddedOnly =>
            Breakdown.Count > 0 && Breakdown.All(r => r.IsRestored);

        // Nothing but a placement the user made from an item's own tab. Taking THIS out means
        // editing that item's ContainerString, which is where the placement actually lives.
        public bool IsPlacedOnly =>
            Breakdown.Count > 0 && Breakdown.All(r => r.IsPlacement);

        // Which half of the pane this row is in. Set by the loader rather than computed, because the
        // wording differs between a container and a list and the row has no business knowing which
        // it is in. The view groups on it.
        public string Section { get; set; } = "";

        // EVERY ROW CAN BE TAKEN OUT. What that MEANS differs, and RemoveSelectedCommand reads it
        // off the row:
        //
        //   it is in the load order  -> a removal, recorded for the patch
        //   you put it back          -> the restoration is undone and it is a loss again
        //   you placed it here       -> the placement is taken out of the item that carries it
        //
        // The last two used to have no tick at all, on the reasoning that a row the patch adds has
        // no load-order entry to remove. True, and beside the point: there is something to undo in
        // every case, and the row is where the user is looking at it.
        public bool CanRemove => true;

        // What taking this row out will do, said on the row - "remove" meaning three things is only
        // safe if the row says which one.
        public bool HasRemoveNote => Breakdown.Count > 0 && !Breakdown.Any(r => r.IsScanned);

        public string RemoveNote
        {
            get
            {
                if (!HasRemoveNote) return "";

                return Breakdown.Any(r => r.IsRestored)
                    ? "Not in the load order - you put this back. Taking it out undoes that, and it is a loss again below."
                    : "Not in the load order - your patch adds it. Taking it out removes the placement from the item that carries it.";
            }
        }

        // The same object, found again in another picked owner.
        public void FoldIn(OwnedEntry entry, string ownerKey)
        {
            _owners[ownerKey] = entry.IsRemoved;
            _occurrences += entry.Occurrences;
            _totalCount += entry.TotalCount;

            OnPropertyChanged(nameof(Occurrences));
            OnPropertyChanged(nameof(TotalCount));
            OnPropertyChanged(nameof(OccurrenceText));
            OnPropertyChanged(nameof(CountText));
            OnPropertyChanged(nameof(HasOccurrenceNote));
            OnPropertyChanged(nameof(OccurrenceNote));
            OnPropertyChanged(nameof(OwnerCount));
            OnPropertyChanged(nameof(IsRemoved));
        }

        public string Reference => _entry.Reference;
        public string Name => _entry.Name;
        public bool IsList => _entry.IsList;
        public int Occurrences => _occurrences;
        public int TotalCount => _totalCount;

        // How many of the picked owners hold this object.
        public int OwnerCount => _owners.Count;

        // How many owners were picked in total, so the row can say "in 3 of 5". Set by the loader
        // rather than looked up: the row has no business knowing the view model.
        public int SelectionSize { get; set; } = 1;

        public IEnumerable<string> OwnersStillHolding =>
            _owners.Where(kv => !kv.Value).Select(kv => kv.Key).ToList();

        public IEnumerable<string> OwnersMarkedForRemoval =>
            _owners.Where(kv => kv.Value).Select(kv => kv.Key).ToList();

        public void SetRemovedIn(string ownerKey, bool removed)
        {
            if (!_owners.ContainsKey(ownerKey)) return;

            _owners[ownerKey] = removed;
            OnPropertyChanged(nameof(IsRemoved));
            OnPropertyChanged(nameof(OccurrenceNote));
        }

        // Struck through only once the decision covers every owner that holds it. A half-removed row
        // has to keep looking removable, or the second half could never be reached.
        public bool IsRemoved => _owners.Count > 0 && _owners.Values.All(v => v);

        // "x2" only where it is true. A row that says nothing is a row listed once, and that is the
        // common case - decorating every row with "x1" would bury the one that matters.
        public string OccurrenceText => _occurrences > 1 ? $"×{_occurrences}" : "";

        // A bool rather than a converter on the string: every other visibility switch in this tool
        // binds BoolToVis, and adding a second mechanism for one row is how a codebase ends up with
        // two of everything.
        public bool HasOccurrenceNote => _occurrences > 1 || SelectionSize > 1;

        // The one thing the user has to understand before ticking anything, said on the row that
        // needs it rather than in a banner nobody reads. With several owners picked it also has to
        // say how far the row reaches - "remove" on a row held by four of them is four removals.
        public string OccurrenceNote
        {
            get
            {
                var parts = new List<string>();

                if (SelectionSize > 1)
                    parts.Add($"In {OwnerCount} of the {SelectionSize} picked. Removing takes it out of all {OwnerCount}.");

                if (_occurrences > 1)
                    parts.Add($"Listed {_occurrences} times in total. Removing takes ALL of them - the patch "
                              + "removes the object, not one entry. Put one back by placing it from the item's own tab.");

                return string.Join(" ", parts);
            }
        }

        public string CountText => _totalCount > 1 ? $"count {_totalCount}" : "";

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected == value) return;
                _isSelected = value;
                OnPropertyChanged();
            }
        }
    }

    // One row behind a folded entry: what a leveled list hands out at which level, or how many of a
    // thing a chest holds.
    //
    // THE LEVEL IS WHY THIS EXISTS. The pane used to show "x3" for a sword a list hands out at
    // level 4, level 12 and level 30 - three different facts summed into one number. The
    // leveled-list window has always shown them as rows; this is the same row, in the pane where
    // the contents are being read.
    public sealed class OwnedOccurrenceVM
    {
        private readonly OwnedOccurrence _row;
        private readonly bool _hasLevel;

        public OwnedOccurrenceVM(OwnedOccurrence row, bool hasLevel)
        {
            _row = row;
            _hasLevel = hasLevel;
        }

        public int Level => _row.Level;
        public int Count => _row.Count;

        // A container has no levels, so it says what it has: an amount. Level 0 on a list entry is
        // "from the start" and reads better as that than as "Level 0".
        public string Text =>
            !_hasLevel ? $"x{Count}"
            : Level > 0 ? $"Level {Level}  x{Count}"
            : $"From the start  x{Count}";

        public bool IsScanned => _row.Origin == OwnedOrigin.Scanned;

        // THE TWO NAMES Styles/PlacementTagStyle.xaml BINDS. They are not arbitrary: the
        // leveled-list window's rows (LeveledListEntryInfo) have carried exactly these two for far
        // longer, and matching them is what lets one style put the same two words, colours and
        // tooltips on both - instead of each view spelling them out again, which is how the two
        // drifted apart in the first place.
        public bool IsPlanned => _row.Origin != OwnedOrigin.Scanned;
        public bool IsRestored => _row.Origin == OwnedOrigin.Readded;

        // A placement made from an item's own tab, as opposed to a restoration. Both are "planned";
        // they are undone in completely different places, so the row has to tell them apart.
        public bool IsPlacement => _row.Origin == OwnedOrigin.Planned;
    }
}
