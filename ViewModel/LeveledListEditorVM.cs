using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Windows.Input;
using SkyrimCraftingTool.Model;
using SkyrimCraftingTool.Services;

namespace SkyrimCraftingTool.ViewModel
{
    // An item this window is placing. One of them from the item editor, all of the selection from
    // the multi-select - the list treats them alike, and so does the arithmetic.
    public sealed record PlacementSubject(string Key, string Name);

    // One container that rolls this list, with the chance of the item coming out of one restock of
    // it. ChanceText is pre-formatted because the sorting needs the number and the row needs the
    // text, and formatting in a converter would put the same rule in two places.
    //
    // IsDirect and Via say HOW the container gets here: it either holds this very list, or it holds
    // another one that leads to it. Worth showing, because it is the difference between "this chest
    // is stocked from your list" and "this chest is stocked from a list that happens to contain
    // yours somewhere below" - and with 120 containers reaching one list, most are the latter.
    public sealed record ContainerReachVM(
        string Name, double Chance, string ChanceText, bool IsDirect, string Via)
    {
        public string Tag => IsDirect ? "container" : "list";

        public string ViaText => IsDirect ? "holds this list" : "via " + Via;

        public string Tooltip => IsDirect
            ? "This container holds this leveled list itself."
            : $"This container holds {Via}, which leads to this list.";
    }

    // One entry of the flags dropdown. A closed list of five, because SkyPatcher can only express
    // those five - see CalcFlagMode.
    public sealed record CalcFlagOption(CalcFlagMode Mode, string Label)
    {
        public override string ToString() => Label;
    }

    // The detail window behind a leveled list in the Container section.
    //
    // THREE things live here, and keeping them apart is most of the design:
    //   * what the LIST is - its contents, and its chance and flags. Editable since 2026-09-15, and
    //     that is a different kind of edit from the rest of this tool: chanceNone and the calc flags
    //     belong to the whole list, so changing them changes the odds for every mod feeding it.
    //   * what THIS ITEM gets - level and amount. A placement the patch adds, and nobody else's.
    //   * what any of it MEANS - the calculator. It recomputes on every change, and that is the
    //     whole point: the user asked for the number to be here rather than in the dry-run, because
    //     "by the time it is in the dry-run it is too late".
    public sealed class LeveledListEditorVM : ViewModelBase
    {
        // Null when the window was opened for a NESTED list. The entry being edited belongs to the
        // list the user came from; a nested list has no placement of this item to edit, and offering
        // one would let them set a level on a list they never selected. So that half is hidden
        // rather than disabled - a disabled editor invites the question "why can't I use this".
        private readonly LVLiEntryVM? _entry;

        private readonly OddsModel _odds;
        private readonly string? _dbPath;

        public bool CanPlace => _entry != null;

        public string ListKey { get; }
        // Everything this placement is for. One item from the single-item editor, all of them from
        // the multi-select - the window is the same either way, because the list does not care how
        // many items arrive at once and neither does the arithmetic: each one is another slot in
        // the draw.
        public IReadOnlyList<PlacementSubject> Subjects { get; }

        public bool IsMultiItem => Subjects.Count > 1;

        // The representative the calculator reports on. Every subject is added at the same level and
        // amount, so they all have the same chance - except one that is already in the list, which
        // addOnce leaves alone. Picking a subject that will actually be added keeps the headline
        // number true for the items the user is placing.
        private PlacementSubject? Representative =>
            Subjects.FirstOrDefault(s => !_scanned.Any(
                e => string.Equals(e.Reference, s.Key, StringComparison.OrdinalIgnoreCase)))
            ?? Subjects.FirstOrDefault();

        public string ItemKey => Representative?.Key ?? "";

        public bool HasSubjects => Subjects.Count > 0;

        // A preset slot places into lists without being an item: it has no form key, so there is
        // nothing to compute a chance for and nothing that can be "already in this list" - but the
        // placement itself is real and needs a heading. Without this the box was headed by a blank
        // line and claimed "Not in this list yet" about nothing at all.
        private readonly string _subjectLabel;

        // What the placement box is headed with: one name, how many items, or who the owner is when
        // the owner is not an item.
        public string ItemName => IsMultiItem
            ? $"{Subjects.Count} selected items"
            : Subjects.FirstOrDefault()?.Name ?? _subjectLabel;

        public string ListName { get; }

        // What the scan found, kept as the reference point for "has the user changed anything".
        public int ScannedChanceNone { get; }
        public string ScannedFlags { get; }

        // The scanned contents, untouched. Contents below is this plus what the patch will add, and
        // the two have to stay separable: "already in the game" and "you asked for it" are opposite
        // statements, and AlreadyInList must only ever answer the first one.
        private readonly IReadOnlyList<LeveledListEntryInfo> _scanned;

        private readonly IReadOnlyList<PlacementLookup.PlannedPlacement> _plannedByOthers;
        private readonly IReadOnlyList<PlacementLookup.PlannedPlacement> _plannedBySubjects;

        public ObservableCollection<LeveledListEntryInfo> Contents { get; } = new();

        public ICommand ResetListPropertiesCommand { get; }

        public LeveledListEditorVM(LVLiEntryVM entry, string itemKey, string itemName)
            : this(entry.Key, entry.LVLiName, new[] { new PlacementSubject(itemKey, itemName) }, entry, null) { }

        // The multi-select: one placement row standing for every selected item. The row carries the
        // level and the amount, the subjects are who gets them.
        public LeveledListEditorVM(LVLiEntryVM entry, IReadOnlyList<PlacementSubject> subjects)
            : this(entry.Key, entry.LVLiName, subjects, entry, null) { }

        // Read-only view of a list nobody is placing into - used when following a nested list.
        public LeveledListEditorVM(string listKey, string fallbackName)
            : this(listKey, fallbackName, Array.Empty<PlacementSubject>(), null, null) { }

        // Test seam: the same window against a prepared database.
        internal LeveledListEditorVM(string listKey, string fallbackName, string itemKey, string itemName,
                                     LVLiEntryVM? entry, string? dbPath)
            : this(listKey, fallbackName,
                   string.IsNullOrWhiteSpace(itemKey)
                       ? Array.Empty<PlacementSubject>()
                       : new[] { new PlacementSubject(itemKey, itemName) },
                   entry, dbPath, itemName) { }

        internal LeveledListEditorVM(string listKey, string fallbackName,
                                     IReadOnlyList<PlacementSubject> subjects,
                                     LVLiEntryVM? entry, string? dbPath, string subjectLabel = "")
        {
            _entry = entry;
            _subjectLabel = subjectLabel ?? "";
            _dbPath = dbPath;
            ListKey = listKey;

            Subjects = (subjects ?? Array.Empty<PlacementSubject>())
                .Where(s => s != null && !string.IsNullOrWhiteSpace(s.Key))
                .ToList();

            var info = PlacementLookup.Read(ListKey, dbPath);

            ListName = string.IsNullOrWhiteSpace(info?.EditorId) ? fallbackName : info!.EditorId;
            ScannedChanceNone = info?.ChanceNone ?? 0;
            ScannedFlags = info?.Flags ?? "";

            _scanned = info?.Entries ?? Array.Empty<LeveledListEntryInfo>();

            // What every OTHER item is lined up to put into this list. Read once - those settings
            // cannot change while this window is open - and kept apart from the subjects of this
            // window, whose placement is live and comes from the editor below.
            var subjectKeys = new HashSet<string>(Subjects.Select(s => s.Key), StringComparer.OrdinalIgnoreCase);
            var planned = PlacementLookup.PlannedFor(ListKey, dbPath);

            _plannedByOthers = planned.Where(p => !subjectKeys.Contains(p.ItemKey)).ToList();

            // The subjects' OWN saved placements, which only matter in the multi-select: there the
            // row is a template that has not been applied yet, so an item may already be lined up
            // for this list from its own editor. In the single-item editor the row IS the item's
            // live state, and a stale saved value would contradict the checkbox in front of the user.
            _plannedBySubjects = IsMultiItem
                ? planned.Where(p => subjectKeys.Contains(p.ItemKey)).ToList()
                : new List<PlacementLookup.PlannedPlacement>();

            // Loaded once. The math then runs against this snapshot, which is what lets the numbers
            // follow a dragged slider: the widest tree in the load order costs 69 ms to read and
            // 2 ms to compute.
            _odds = OddsModel.Load(ListKey, dbPath);

            FlagOptions = new[]
            {
                new CalcFlagOption(CalcFlagMode.ForLevelAndEachItem, LeveledListFlags.Describe(CalcFlagMode.ForLevelAndEachItem)),
                new CalcFlagOption(CalcFlagMode.ForLevel, LeveledListFlags.Describe(CalcFlagMode.ForLevel)),
                new CalcFlagOption(CalcFlagMode.EachItem, LeveledListFlags.Describe(CalcFlagMode.EachItem)),
                new CalcFlagOption(CalcFlagMode.UseAll, LeveledListFlags.Describe(CalcFlagMode.UseAll)),
                new CalcFlagOption(CalcFlagMode.None, LeveledListFlags.Describe(CalcFlagMode.None)),
            };

            // The editor starts at what the list already is, from the stored edit if there is one.
            var stored = LeveledListEditStore.Read(ListKey, dbPath);
            _chanceNone = stored?.ChanceNone ?? ScannedChanceNone;

            // Read, not offered. One lookup for the one list being shown - the picker that would have
            // needed all 1,567 of them is gone.
            //
            // A global whose own plugin has left still counts as governing: the entry is only there
            // to explain why the stored number is not what the game uses, and "no name" is a better
            // answer than pretending the list has a fixed chance.
            ScannedGlobalKey = info?.GlobalKey ?? "";
            _governingGlobal = string.IsNullOrWhiteSpace(ScannedGlobalKey)
                ? null
                : GlobalsLookup.Read(ScannedGlobalKey, dbPath) ?? new GlobalInfo(ScannedGlobalKey, "", null);

            var storedMode = stored?.Flags ?? CalcFlagMode.Unchanged;
            var startMode = storedMode == CalcFlagMode.Unchanged
                ? LeveledListFlags.FromRecord(ScannedFlags)
                : storedMode;
            _selectedFlagOption = FlagOptions.FirstOrDefault(o => o.Mode == startMode) ?? FlagOptions[0];

            _playerLevel = Math.Clamp(AppPrefs.GetInt("odds.playerLevel", 20), 1, 100);

            ResetListPropertiesCommand = new RelayCommand(ResetListProperties);

            RebuildContents();
        }

        // --- What the list is (editable since 2026-09-15) ---

        private int _chanceNone;
        public int ChanceNone
        {
            get => _chanceNone;
            set
            {
                var clamped = Math.Clamp(value, 0, 100);
                if (_chanceNone == clamped) return;

                _chanceNone = clamped;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ChanceNoneDouble));
                OnPropertyChanged(nameof(ChanceSummary));
                SaveListProperties();
                ListPropertiesChanged();
            }
        }

        // Slider-facing view of the same value. WPF sliders speak double, and routing them through
        // the int property keeps the clamping and the save in one place.
        public double ChanceNoneDouble
        {
            get => ChanceNone;
            set => ChanceNone = (int)Math.Round(value);
        }

        // --- The chance from a global variable: SHOWN, never edited ---
        //
        // A leveled list can take its chance-none from a GLOB instead of from a fixed number, and 223
        // lists in the load order do. The stored number is then dead weight - parked at 100 in 209 of
        // those cases - and the game reads the global, which quests and scripts move during play:
        // PCIllusionAdept sits at 100 until the player reaches Illusion 50, which is how spell tomes
        // of a tier appear in shops only once you are good enough.
        //
        // POINTING A LIST AT A DIFFERENT GLOBAL WAS BUILT AND THEN TAKEN BACK OUT (2026-09-16, the
        // user's call). It works, but it is a footgun with a narrow use: choosing one couples a list
        // to unrelated game state, the value shown is only a starting value, and SkyPatcher can set a
        // global but never remove one - so the edit is a one-way door. No user has asked for it.
        //
        // What stays is the reading, and it has to: without it the calculator reports "this list
        // never gives anything" for every gated list in the load order.
        public string ScannedGlobalKey { get; }

        private readonly GlobalInfo? _governingGlobal;

        public bool HasGoverningGlobal => _governingGlobal != null;

        public string GlobalSummary
        {
            get
            {
                if (_governingGlobal == null) return "";

                var value = _governingGlobal.Value.HasValue
                    ? $"currently {_governingGlobal.ValueText}%"
                    : "current value unknown";

                return $"This list takes its chance from the global {_governingGlobal.Display} ({value}), " +
                       "so the number above is what the record stores but not what the game uses - and a " +
                       "quest can change it while you play.";
            }
        }

        public IReadOnlyList<CalcFlagOption> FlagOptions { get; }

        private CalcFlagOption _selectedFlagOption;
        public CalcFlagOption SelectedFlagOption
        {
            get => _selectedFlagOption;
            set
            {
                if (value == null || _selectedFlagOption == value) return;

                _selectedFlagOption = value;
                OnPropertyChanged();
                SaveListProperties();
                ListPropertiesChanged();
            }
        }

        // An edit exists only where the value DIFFERS from the scan. Selecting the state a list
        // already has is not an edit, and writing a rule for it would make this tool the owner of a
        // property nobody changed - the next mod to touch that list would then be fighting us.
        public int? ChanceNoneEdit => _chanceNone == ScannedChanceNone ? null : _chanceNone;

        public CalcFlagMode FlagModeEdit =>
            _selectedFlagOption.Mode == LeveledListFlags.FromRecord(ScannedFlags)
                ? CalcFlagMode.Unchanged
                : _selectedFlagOption.Mode;

        public bool HasListEdit => ChanceNoneEdit.HasValue || FlagModeEdit != CalcFlagMode.Unchanged;

        public string ListEditSummary => !HasListEdit
            ? "Unchanged - this list behaves exactly as your load order has it."
            : "Changed by you: " + string.Join(", ", Changes()) +
              $". This applies to all {Contents.Count} entries of this list, including other mods'.";

        private IEnumerable<string> Changes()
        {
            if (ChanceNoneEdit.HasValue)
                yield return $"chance of nothing {ScannedChanceNone}% -> {ChanceNoneEdit}%";

            if (FlagModeEdit != CalcFlagMode.Unchanged)
                yield return $"calculation {LeveledListFlags.Describe(LeveledListFlags.FromRecord(ScannedFlags))} " +
                             $"-> {LeveledListFlags.Describe(FlagModeEdit)}";
        }

        // SpecialLoot has no SkyPatcher operation. Saying so beats letting someone switch the flags
        // on one of those 108 lists and find out from the game.
        public bool TouchesSpecialLoot =>
            LeveledListFlags.HasSpecialLoot(ScannedFlags) && FlagModeEdit != CalcFlagMode.Unchanged;

        public string SpecialLootWarning =>
            "This list also has the SpecialLoot flag, which SkyPatcher cannot set. Changing the " +
            "calculation here does not carry that flag over.";

        private void ResetListProperties()
        {
            _chanceNone = ScannedChanceNone;

            _selectedFlagOption = FlagOptions.FirstOrDefault(o => o.Mode == LeveledListFlags.FromRecord(ScannedFlags))
                                  ?? FlagOptions[0];

            OnPropertyChanged(nameof(ChanceNone));
            OnPropertyChanged(nameof(SelectedFlagOption));

            LeveledListEditStore.Delete(ListKey, _dbPath);
            ListPropertiesChanged();
        }

        private void SaveListProperties()
            => LeveledListEditStore.Save(new LeveledListEdit(ListKey, ChanceNoneEdit, FlagModeEdit), _dbPath);

        private void ListPropertiesChanged()
        {
            OnPropertyChanged(nameof(ChanceNoneEdit));
            OnPropertyChanged(nameof(FlagModeEdit));
            OnPropertyChanged(nameof(HasListEdit));
            OnPropertyChanged(nameof(ListEditSummary));
            OnPropertyChanged(nameof(TouchesSpecialLoot));
            OnPropertyChanged(nameof(GlobalSummary));
            Recalculate();
        }

        // --- What this item gets (editable) ---

        public bool IsPlaced
        {
            get => _entry?.IsSelected ?? false;
            set
            {
                if (_entry == null || _entry.IsSelected == value) return;
                _entry.IsSelected = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(Level));
                Recalculate();
            }
        }

        public int Level
        {
            get => _entry?.Level ?? 0;
            set
            {
                if (_entry == null || _entry.Level == value) return;
                _entry.Level = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsPlaced));
                Recalculate();
            }
        }

        public double LevelDouble
        {
            get => Level;
            set => Level = (int)value;
        }

        // In the multi-select nothing is written yet: this row is the template the bulk apply uses.
        // Saying so beats letting someone set a level here, close the window and wonder why the
        // items did not change.
        public string PlacementScopeNote => IsMultiItem
            ? $"Applies to all {Subjects.Count} selected items when you press \"Apply Containers\" in the multi-select editor."
            : "";

        public bool HasPlacementScopeNote => IsMultiItem;

        public int Count
        {
            get => _entry?.Count ?? 1;
            set
            {
                if (_entry == null || _entry.Count == value) return;
                _entry.Count = value;
                OnPropertyChanged();
                Recalculate();
            }
        }

        // --- The calculator ---

        private int _playerLevel;
        public int PlayerLevel
        {
            get => _playerLevel;
            set
            {
                var clamped = Math.Clamp(value, 1, 100);
                if (_playerLevel == clamped) return;

                _playerLevel = clamped;
                AppPrefs.SetInt("odds.playerLevel", clamped);
                OnPropertyChanged();
                Recalculate();
            }
        }

        public double PlayerLevelDouble
        {
            get => PlayerLevel;
            set => PlayerLevel = (int)value;
        }

        // Without an item there is nothing to compute a chance FOR - a nested list opened on its own
        // still shows its properties and contents, but the calculator has no subject.
        public bool CanCalculate => !string.IsNullOrWhiteSpace(ItemKey);

        private IReadOnlyDictionary<string, OddsOverride>? Overrides()
        {
            if (!HasListEdit) return null;

            return new Dictionary<string, OddsOverride>(StringComparer.OrdinalIgnoreCase)
            {
                [ListKey] = new(ChanceNoneEdit, FlagModeEdit),
            };
        }

        // Everything the patch would add to this list: what other items are lined up for, plus this
        // item's own placement as the editor currently has it. All of it, because they compete -
        // ten planned additions make each one rarer, and a calculator that ignored the other nine
        // would quietly promise too much.
        private IReadOnlyCollection<PendingPlacement> Pending()
        {
            var pending = _plannedByOthers
                .Select(p => new PendingPlacement(ListKey, p.ItemKey, p.Level, p.Count))
                .ToList();

            // Every selected item, not just the representative: from the multi-select this row places
            // all of them at once, and each one is a slot in the same draw. Counting one would make
            // the number look better the more items the user selects, which is backwards.
            if (CanPlace && IsPlaced)
                foreach (var subject in Subjects)
                    pending.Add(new PendingPlacement(ListKey, subject.Key, Level, Count));
            else
                foreach (var own in _plannedBySubjects)
                    pending.Add(new PendingPlacement(ListKey, own.ItemKey, own.Level, own.Count));

            return pending;
        }

        // The list as it will be: scanned entries first, then the rows the patch appends. Rebuilt
        // rather than patched in place - it changes on the checkbox, the level and the amount, and
        // three separate update paths is how a view and its model drift apart.
        private void RebuildContents()
        {
            Contents.Clear();

            foreach (var entry in _scanned)
                Contents.Add(entry);

            int ordinal = _scanned.Count;

            foreach (var planned in _plannedByOthers)
            {
                // addOnce: an item the list already has is not added again, so it must not appear
                // twice here either.
                if (_scanned.Any(e => string.Equals(e.Reference, planned.ItemKey, StringComparison.OrdinalIgnoreCase)))
                    continue;

                Contents.Add(new LeveledListEntryInfo(
                    ordinal++, planned.ItemKey, planned.Name, planned.Level, planned.Count,
                    IsList: false, IsPlanned: true));
            }

            // One row per selected item. From the multi-select this is where "I am adding twelve
            // things to this list" becomes visible as twelve rows - which is also exactly what the
            // odds below are working from.
            var subjectRows = CanPlace && IsPlaced
                ? Subjects.Select(s => (s.Key, s.Name, Level, Count))
                : _plannedBySubjects.Select(p => (p.ItemKey, p.Name, p.Level, p.Count));

            foreach (var (key, name, level, count) in subjectRows)
            {
                if (_scanned.Any(e => string.Equals(e.Reference, key, StringComparison.OrdinalIgnoreCase)))
                    continue;

                Contents.Add(new LeveledListEntryInfo(
                    ordinal++, key, string.IsNullOrWhiteSpace(name) ? key : name,
                    level, count, IsList: false, IsPlanned: true));
            }

            OnPropertyChanged(nameof(EntryCountSummary));
            OnPropertyChanged(nameof(ListEditSummary));
        }

        private OddsResult Before() => LeveledListOdds.Chance(_odds, ListKey, ItemKey, PlayerLevel);

        private OddsResult After() => LeveledListOdds.Chance(
            _odds, ListKey, ItemKey, PlayerLevel, 1, Overrides(), Pending());

        // "As your load order has it" - no placement, no edits. The baseline everything is compared
        // against, and 0% is a perfectly normal answer here: the item is usually not in the list yet.
        public string ChanceBeforeText
        {
            get
            {
                var before = Before();

                return before.Chance <= 0
                    ? "Not in this list right now - 0% per roll."
                    : $"As it is now: {Percent(before.Chance)} per roll.";
            }
        }

        public string ChanceAfterText
        {
            get
            {
                var after = After();
                if (after.Chance <= 0)
                    return "With your settings: still nothing - check the level and the checkbox above.";

                var rolls = LeveledListOdds.RollsFor(after.Chance);

                // "Each of them", because every selected item is added at the same level and amount
                // and therefore has the same chance - they do not share one, they each get one.
                var lead = IsMultiItem ? "With your settings, each item: " : "With your settings: ";

                return lead + $"{Percent(after.Chance)} per roll" +
                       (double.IsInfinity(rolls) ? "." : $" - about every {rolls:n0} rolls.");
            }
        }

        public string ExpectedCountText
        {
            get
            {
                var after = After();

                return after.ExpectedCount <= 0
                    ? ""
                    : $"On average {after.ExpectedCount:n2} of them per roll.";
            }
        }

        // The honest footnote. A percentage per roll is exact; a percentage per playthrough would
        // need to know how often the game rolls this list, which depends on the cell, the restock
        // timer and whatever else is installed.
        public string CalculatorCaveat =>
            "Per roll of this list. How often the game rolls it is a different question - a merchant " +
            "chest restocks roughly every 48 in-game hours, a boss chest once.";

        public string LevelEffectHint => LeveledListFlags.HasAllLevels(FlagsInEffect())
            ? "Draws from every entry up to your level, so higher levels mean more competition."
            : "Draws only from the highest level band you have reached - entries below it drop out entirely.";

        private string FlagsInEffect()
        {
            // What the flags will BE after this edit, expressed the way the record writes them, so
            // the hint above describes the state the user is looking at rather than the scanned one.
            var mode = FlagModeEdit == CalcFlagMode.Unchanged
                ? LeveledListFlags.FromRecord(ScannedFlags)
                : FlagModeEdit;

            return mode switch
            {
                CalcFlagMode.ForLevel => "CalculateFromAllLevelsLessThanOrEqualPlayer",
                CalcFlagMode.ForLevelAndEachItem => "CalculateFromAllLevelsLessThanOrEqualPlayer, CalculateForEachItemInCount",
                CalcFlagMode.EachItem => "CalculateForEachItemInCount",
                CalcFlagMode.UseAll => "UseAll",
                _ => "",
            };
        }

        private static string Percent(double value)
        {
            var percent = value * 100;

            // Below a tenth of a per cent the useful digits are all behind the decimal point, and a
            // rounded "0.0%" would read as "never" for something that does happen.
            var text = percent >= 10 ? percent.ToString("n1", CultureInfo.CurrentCulture)
                     : percent >= 1 ? percent.ToString("n2", CultureInfo.CurrentCulture)
                     : percent.ToString("n3", CultureInfo.CurrentCulture);

            return text + "%";
        }

        private void Recalculate()
        {
            RebuildContents();

            OnPropertyChanged(nameof(ChanceBeforeText));
            OnPropertyChanged(nameof(ChanceAfterText));
            OnPropertyChanged(nameof(ExpectedCountText));
            OnPropertyChanged(nameof(LevelEffectHint));
            OnPropertyChanged(nameof(PlannedCount));
        }

        // --- Where this list is actually rolled ---
        //
        // A list is never rolled by itself. It is rolled because a chest, a merchant or a corpse
        // holds something that leads to it, sometimes six levels up - so "4% per roll" only becomes
        // an answer once you know who rolls it.
        //
        // Loaded on first expand, not on open: it walks every ancestor of the list and then loads an
        // odds model per container entry. Cheap for a leaf list, not cheap for one hanging under 120
        // containers, and most visits to this window never ask the question.

        public ObservableCollection<ContainerReachVM> ReachedBy { get; } = new();

        private bool _reachLoaded;
        private bool _isReachExpanded;
        public bool IsReachExpanded
        {
            get => _isReachExpanded;
            set
            {
                if (_isReachExpanded == value) return;
                _isReachExpanded = value;
                OnPropertyChanged();

                if (value) LoadReach();
            }
        }

        // How many containers reach this list AT ALL - which is a different question from how many
        // of them would hand out this item, and conflating the two was a real bug: a list opened
        // straight out of a container reported "nowhere" simply because the item was not placed yet.
        public int ReachCount { get; private set; }

        public string ReachSummary
        {
            get
            {
                if (!_reachLoaded)
                    return "Expand to work out which containers and merchants roll this list.";

                if (ReachCount == 0)
                    return "No container in your load order rolls this list - it is only reached " +
                           "through other lists, or not at all.";

                var lead = $"{ReachCount} container(s) roll this list, directly or through another list. ";

                return ReachedBy.Any(r => r.Chance > 0)
                    ? lead + "Chances are per restock of that container, with your settings applied."
                    : lead + "Your item does not come out of any of them yet - tick the placement above.";
            }
        }

        // Enough to see the shape of it. A list under 120 containers produces a wall of rows nobody
        // reads, and the ones that matter are the likeliest.
        private const int MaxReachRows = 25;

        public bool ReachTruncated { get; private set; }

        public string ReachTruncationNote => ReachTruncated
            ? $"Showing the {MaxReachRows} most likely. More containers reach this list."
            : "";

        private void LoadReach()
        {
            if (_reachLoaded) return;
            _reachLoaded = true;

            try
            {
                var reaches = PlacementLookup.ContainersReaching(ListKey, _dbPath);
                ReachCount = reaches.Count;

                // One model per entry list, shared across every container holding it: containers
                // overwhelmingly point at the same handful of lists, and a model load is the
                // expensive part.
                var models = new Dictionary<string, OddsModel>(StringComparer.OrdinalIgnoreCase);
                var rows = new List<ContainerReachVM>();

                foreach (var reach in reaches)
                {
                    double missAll = 1;

                    foreach (var (listKey, count) in reach.Entries)
                    {
                        if (!models.TryGetValue(listKey, out var model))
                            models[listKey] = model = OddsModel.Load(listKey, _dbPath);

                        var result = LeveledListOdds.Chance(
                            model, listKey, ItemKey, PlayerLevel, count, Overrides(), Pending());

                        // Several entries leading to the same list are independent chances at one
                        // restock, not alternatives.
                        missAll *= 1 - result.Chance;
                    }

                    // A container with a zero chance is kept, not dropped. It still answers the
                    // question in the heading - who rolls this list - and dropping it is how this
                    // panel came to claim "nowhere" about a list the user had just picked out of a
                    // container. A dash rather than "0%": nothing is coming out of it yet, and a
                    // percentage suggests a computation where there is simply no placement.
                    // Which way in: the container either holds this list, or holds another one that
                    // leads to it. Named, not just flagged - "via LItemBlacksmithArmor75" says where
                    // the chain actually enters, which is the next thing anyone asks.
                    bool direct = reach.Entries.Any(
                        e => string.Equals(e.ListKey, ListKey, StringComparison.OrdinalIgnoreCase));

                    var via = direct ? "" : string.Join(", ", reach.Entries
                        .Select(e => models.TryGetValue(e.ListKey, out var m) ? m.Get(e.ListKey)?.EditorId : null)
                        .Where(n => !string.IsNullOrWhiteSpace(n))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Take(2));

                    if (!direct && string.IsNullOrWhiteSpace(via))
                        via = "another list";

                    var chance = 1 - missAll;
                    rows.Add(new ContainerReachVM(
                        reach.Name, chance, chance > 0 ? Percent(chance) : "-", direct, via));
                }

                ReachTruncated = rows.Count > MaxReachRows;

                foreach (var row in rows.OrderByDescending(r => r.Chance).Take(MaxReachRows))
                    ReachedBy.Add(row);
            }
            catch (Exception ex)
            {
                AppLogger.LogError($"LeveledListEditorVM.LoadReach({ListKey})", ex);
            }

            OnPropertyChanged(nameof(ReachCount));
            OnPropertyChanged(nameof(ReachSummary));
            OnPropertyChanged(nameof(ReachTruncated));
            OnPropertyChanged(nameof(ReachTruncationNote));
        }

        // --- What the list already is, in words ---

        public string ChanceSummary => ChanceNone <= 0
            ? "Chance of nothing: 0% - the list always rolls something."
            : $"Chance of nothing: {ChanceNone}% - that often the list hands out nothing at all.";

        public int PlannedCount => Contents.Count(e => e.IsPlanned);

        public string EntryCountSummary
        {
            get
            {
                var scanned = _scanned.Count == 1
                    ? "1 entry in this list"
                    : $"{_scanned.Count} entries in this list";

                return PlannedCount == 0
                    ? scanned + "."
                    : $"{scanned}, plus {PlannedCount} your patch will add.";
            }
        }

        // Is the item ALREADY in this list, without the patch? Against the SCANNED entries only -
        // Contents also carries the rows the patch will add, and counting those would turn "you
        // placed it here" into "the game already has it", which is the one confusion this window
        // exists to prevent.
        public bool AlreadyInList =>
            _scanned.Any(e => string.Equals(e.Reference, ItemKey, StringComparison.OrdinalIgnoreCase));

        // The patch writes addOnceToLLs, which the patcher applies only when the item is not already
        // in the list (see docs/Leveled_List_Patcher.txt). So a placement on a list that already has
        // the item does nothing at all - worth saying plainly rather than letting someone set a level
        // and wonder why nothing changed in game.
        public string AlreadyInListSummary
        {
            get
            {
                int already = Subjects.Count(s => _scanned.Any(
                    e => string.Equals(e.Reference, s.Key, StringComparison.OrdinalIgnoreCase)));

                if (already == 0) return "Not in this list yet.";

                // From the multi-select this is the useful shape of the answer: the patch silently
                // skips the ones that are already there, so the count is what the user needs, not a
                // yes/no about a single item.
                if (IsMultiItem)
                    return $"{already} of {Subjects.Count} are already in this list. The patch skips those - " +
                           "an item is only added if the list does not already contain it.";

                return "Already in this list in your load order. A placement here would have no effect - " +
                       "the patch adds an item only if the list does not already contain it.";
            }
        }
    }
}
