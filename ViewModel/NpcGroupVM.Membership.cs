using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using SkyrimCraftingTool.Model;
using SkyrimCraftingTool.Services;

namespace SkyrimCraftingTool.ViewModel
{
    // The half of the group editor that FILLS a group (G4, docs/NPC-Gruppen-Plan.md sections 4 and 6).
    //
    // Everything under section 4 was there from the start - the predicate, the hand corrections, the
    // store that writes both, the resolver that turns them into members, the rule builder that turns
    // them into a patch. What was missing was the way in: "New" produced a group with no predicate and
    // no override, and no control on the screen could give it either. A group nobody can fill is the
    // same failure NpcViewBindingTests was written for - finished everywhere except where it can be
    // reached.
    //
    // THE TWO WAYS IN ARE KEPT APART BECAUSE THE MODEL KEEPS THEM APART, and for the same reason
    // (section 4): a re-seed rewrites the predicate and must not be able to throw a hand correction
    // away. So the filters below write group.Predicates and nothing else, the search box writes
    // group.Overrides and nothing else, and no control on this screen writes a member list - there is
    // none to write, membership is resolved on every read.
    public sealed partial class NpcGroupVM
    {
        // ---- the filters -------------------------------------------------------------------------

        public ObservableCollection<NpcGroupClauseRowVM> Clauses { get; } = new();

        private RelayCommand? _addClauseCommand;
        public ICommand AddClauseCommand => _addClauseCommand ??= new RelayCommand(AddClause);

        public bool HasClauses => Clauses.Count > 0;

        private void RebuildClauses(NpcGroup? group)
        {
            foreach (var row in Clauses) row.Changed -= OnClauseChanged;
            Clauses.Clear();

            if (group != null)
            {
                foreach (var clause in group.Predicates)
                    Clauses.Add(new NpcGroupClauseRowVM(
                        clause.Axis, clause.Mode, clause.Value, clause.Origin,
                        ChoicesFor, ClauseValuesElsewhere, RemoveClause));
            }

            foreach (var row in Clauses) row.Changed += OnClauseChanged;
            OnPropertyChanged(nameof(HasClauses));
        }

        public void AddClause()
        {
            if (_selectedGroup?.Group == null) return;

            // Class, because it is the axis the pre-fill leans on for everything the EditorID scheme
            // does not cover (section 7) - and because every NPC has exactly one, so the first pick can
            // never produce an empty group by accident.
            var row = new NpcGroupClauseRowVM(
                NpcGroupAxis.Class, NpcGroupMode.Include, "", "", ChoicesFor, ClauseValuesElsewhere, RemoveClause);
            row.Changed += OnClauseChanged;
            Clauses.Add(row);

            // The new row's pick list already has to hide what the rows above it hold.
            foreach (var other in Clauses) other.RefreshPeers();

            OnPropertyChanged(nameof(HasClauses));
        }

        private void RemoveClause(NpcGroupClauseRowVM row)
        {
            if (row == null) return;

            row.Changed -= OnClauseChanged;
            Clauses.Remove(row);
            OnPropertyChanged(nameof(HasClauses));

            OnClauseChanged(row);
        }

        // A HALF-TYPED CLAUSE IS NOT WRITTEN TO THE GROUP. The resolver reads an empty value as a
        // warning and resolves the whole predicate to nothing (NpcGroupResolver.Resolve, "a clause this
        // resolver cannot evaluate resolves the PREDICATE to nothing"), which is right for a stored
        // group and wrong for one somebody is in the middle of building: pressing "Add filter" would
        // empty the group until the value box is filled in. So the row exists on screen, and the group
        // gets the clause the moment it means something.
        private void OnClauseChanged(NpcGroupClauseRowVM row)
        {
            // Every row's pick list and conflict mark depends on what the OTHER rows hold, so one change
            // makes all of them stale. Done before the write, so a row that was just freed up offers its
            // value again straight away.
            foreach (var other in Clauses) other.RefreshPeers();

            var group = _selectedGroup?.Group;
            if (group == null) return;

            group.Predicates.Clear();

            var written = new HashSet<(string, string, string)>();

            foreach (var clause in Clauses.Where(c => !c.IsIncomplete))
            {
                // An exact duplicate - same axis, same mode, same value - is a no-op the pick list no
                // longer offers, but a group loaded from the database may still carry one. Written once
                // rather than twice: two identical clauses resolve to the same members and produce the
                // same filter term twice in the rule.
                if (!written.Add((clause.Axis.ToLowerInvariant(), clause.Mode.ToLowerInvariant(),
                        clause.Value.ToLowerInvariant())))
                    continue;

                group.Predicates.Add(new NpcGroupPredicate
                {
                    Axis = clause.Axis,
                    Mode = clause.Mode,
                    Value = clause.Value,

                    // Carried through, or one edit anywhere in the list would wipe the mark off every
                    // line the auto-check wrote and its undo would go with them.
                    Origin = clause.Origin,
                });
            }

            Persist(group);
            RefreshMembership();
        }

        // What the OTHER filter rows already hold on this axis. Two things are read off it: the pick list
        // stops offering a value that is taken, and a row whose value is taken says so.
        //
        // PER AXIS AND NOT PER ROW: several clauses on one axis are the normal case, not a mistake - it
        // is how "either of these two classes" is written, and how the pre-fill keeps twelve tier
        // EditorIDs out of a class group. What cannot be repeated is the VALUE.
        private IReadOnlyList<(string Mode, string Value)> ClauseValuesElsewhere(
            NpcGroupClauseRowVM row, string axis)
            => Clauses
                .Where(c => !ReferenceEquals(c, row)
                            && !c.IsIncomplete
                            && string.Equals(c.Axis, axis, StringComparison.OrdinalIgnoreCase))
                .Select(c => (c.Mode, c.Value))
                .ToList();

        // ---- the value catalogues ------------------------------------------------------------------

        // Built once per axis from the loaded NPCs, and held: an editable ComboBox compares its
        // SelectedItem BY REFERENCE, so a getter that rebuilt the list on every call would hand WPF a
        // fresh object every time and the box would never show a selection.
        private readonly Dictionary<string, IReadOnlyList<NpcGroupChoice>> _choices =
            new(StringComparer.OrdinalIgnoreCase);

        // WITH THE MEMBER COUNT IN THE LABEL, and sorted by it. The catalogues are long - 1.458
        // factions, 335 races - and "EncClassBanditMelee (574)" is the difference between picking the
        // class that covers the load order and picking the one that covers four NPCs. It is also the one
        // number that says whether an axis value is worth a group at all.
        private IReadOnlyList<NpcGroupChoice> ChoicesFor(string axis)
        {
            axis = (axis ?? "").Trim();
            if (_choices.TryGetValue(axis, out var cached)) return cached;

            var built = BuildChoices(axis);
            _choices[axis] = built;
            return built;
        }

        private IReadOnlyList<NpcGroupChoice> BuildChoices(string axis)
        {
            switch ((axis ?? "").ToLowerInvariant())
            {
                case NpcGroupAxis.Class:
                    return Counted(_npcs.Select(n => n.ClassKey), k => _labels.Class(k));

                case NpcGroupAxis.Race:
                    return Counted(_npcs.Select(n => n.RaceKey), k => _labels.Race(k));

                case NpcGroupAxis.Faction:
                    // DISTINCT PER NPC: an NPC listed twice in the same faction would otherwise be
                    // counted twice, and the number beside a faction has to mean "how many NPCs this
                    // reaches".
                    return Counted(
                        _npcs.SelectMany(n => n.Factions
                            .Select(f => f.FactionKey)
                            .Where(k => !string.IsNullOrWhiteSpace(k))
                            .Distinct(StringComparer.OrdinalIgnoreCase)),
                        k => _labels.Faction(k));

                case NpcGroupAxis.Mod:
                    return Counted(_npcs.Select(n => PluginOf(n.Key)), k => k);

                case NpcGroupAxis.Flag:
                    return NpcFlagNames.All
                        .Select(name => new NpcGroupChoice(name, name, _npcs.Count(n => HasFlag(n, name))))
                        .OrderByDescending(c => c.Count)
                        .ThenBy(c => c.Token, StringComparer.OrdinalIgnoreCase)
                        .ToList();

                case NpcGroupAxis.Gender:
                    int female = _npcs.Count(n => ((NpcFlag)n.Flags).HasFlag(NpcFlag.Female));
                    return new List<NpcGroupChoice>
                    {
                        new("male", "male", _npcs.Count - female),
                        new("female", "female", female),
                    };

                // EditorID is a substring, not a key - there is no catalogue to pick from, and the row
                // shows a text box instead. See NpcGroupClauseRowVM.IsFreeText.
                default:
                    return Array.Empty<NpcGroupChoice>();
            }
        }

        private static List<NpcGroupChoice> Counted(IEnumerable<string> keys, Func<string, string> label)
            => keys
                .Where(k => !string.IsNullOrWhiteSpace(k))
                .GroupBy(k => k, StringComparer.OrdinalIgnoreCase)
                .Select(g => new NpcGroupChoice(g.Key, label(g.Key), g.Count()))
                .OrderByDescending(c => c.Count)
                .ThenBy(c => c.Label, StringComparer.OrdinalIgnoreCase)
                .ToList();

        // The one flag that is not a bit. Same reason as NpcGroupResolver.HasFlag: the PcLevelMult bit is
        // set on 0 of 6.642 NPCs while 654 of them use a player-level multiplier, so counting the bit
        // here would offer a filter reading "pclevelmult (0)" beside a group of 654.
        private static bool HasFlag(NpcRecord npc, string token)
        {
            if (!NpcFlagNames.TryParse(token, out var flag)) return false;
            if (flag == NpcFlag.PcLevelMult) return npc.UsesPcLevelMult;
            return ((NpcFlag)npc.Flags).HasFlag(flag);
        }

        private static string PluginOf(string? key)
        {
            if (string.IsNullOrWhiteSpace(key)) return "";
            int bar = key.IndexOf('|');
            return bar < 0 ? key.Trim() : key.Substring(0, bar).Trim();
        }

        // ---- by hand: the search box -----------------------------------------------------------------

        public ObservableCollection<NpcSearchRowVM> SearchResults { get; } = new();

        private string _memberSearchText = "";
        public string MemberSearchText
        {
            get => _memberSearchText;
            set
            {
                if (!SetProperty(ref _memberSearchText, value ?? "")) return;
                RebuildSearch();
            }
        }

        private string _memberSearchSummary = "";
        public string MemberSearchSummary
        {
            get => _memberSearchSummary;
            private set => SetProperty(ref _memberSearchSummary, value);
        }

        public bool HasSearchResults => SearchResults.Count > 0;

        // Two characters, not one: a single letter matches most of the load order, and the list would be
        // 50 arbitrary rows out of four thousand rather than an answer.
        private const int MinimumSearchLength = 2;
        private const int SearchResultLimit = 50;

        private void ClearMemberSearch()
        {
            _memberSearchText = "";
            OnPropertyChanged(nameof(MemberSearchText));
            RebuildSearch();
        }

        private void RebuildSearch()
        {
            SearchResults.Clear();

            var needle = _memberSearchText.Trim();

            if (_selectedGroup?.Group == null || needle.Length < MinimumSearchLength)
            {
                MemberSearchSummary = _selectedGroup?.Group == null
                    ? ""
                    : $"Type at least {MinimumSearchLength} characters to find an NPC by name, EditorID or FormID.";

                OnPropertyChanged(nameof(HasSearchResults));
                return;
            }

            var hits = _npcs
                .Where(n => Contains(n.EditorID, needle) || Contains(n.Name, needle) || Contains(n.Key, needle))
                .OrderBy(n => n.EditorID, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var memberKeys = new HashSet<string>(_currentMembers.Select(m => m.Key), StringComparer.OrdinalIgnoreCase);

            foreach (var npc in hits.Take(SearchResultLimit))
                SearchResults.Add(new NpcSearchRowVM(npc, _labels, memberKeys.Contains(npc.Key), AddNpc));

            MemberSearchSummary = hits.Count > SearchResultLimit
                ? $"{hits.Count} NPCs match - the first {SearchResultLimit} are listed."
                : $"{hits.Count} NPC(s) match.";

            OnPropertyChanged(nameof(HasSearchResults));
        }

        private static bool Contains(string? haystack, string needle)
            => (haystack ?? "").Contains(needle, StringComparison.OrdinalIgnoreCase);

        // ---- by hand: add and remove -------------------------------------------------------------------

        // ADDING AN NPC THE FILTER ALREADY REACHES WRITES NOTHING. The override would be a lie about the
        // group - "by hand +1" on a member the rule produces on its own - and it would outlive the day
        // the filter changes and stop meaning what it said. What it does do is drop a remove-override,
        // which is what "add" means for an NPC somebody had taken out by hand.
        public void AddNpc(NpcSearchRowVM row)
        {
            var group = _selectedGroup?.Group;
            if (group == null || row == null || string.IsNullOrWhiteSpace(row.Key)) return;

            bool hadOverride = group.Overrides.RemoveAll(o => SameKey(o.NpcKey, row.Key)) > 0;

            if (!PredicateMembers(group).Contains(row.Key))
            {
                group.Overrides.Add(new NpcGroupMemberOverride
                {
                    NpcKey = row.Key,
                    Mode = NpcGroupOverrideMode.Add,
                });
            }
            else if (!hadOverride)
            {
                // A member by rule already, and nothing to take back - so nothing to save either.
                return;
            }

            Persist(group);
            RefreshMembership();
        }

        // The mirror of AddNpc: an NPC the filter does not reach was there by hand, so dropping the
        // add-override is the whole of it. One the filter DOES reach needs a remove-override to stay out
        // - and that is the correction section 4 holds in its own table so a re-seed cannot lose it.
        public void RemoveMember(NpcMemberRowVM? row)
        {
            var group = _selectedGroup?.Group;
            if (group == null || row == null || string.IsNullOrWhiteSpace(row.Key)) return;

            group.Overrides.RemoveAll(o => SameKey(o.NpcKey, row.Key));

            if (PredicateMembers(group).Contains(row.Key))
            {
                group.Overrides.Add(new NpcGroupMemberOverride
                {
                    NpcKey = row.Key,
                    Mode = NpcGroupOverrideMode.Remove,
                });
            }

            Persist(group);
            RefreshMembership();
        }

        // What the filter alone reaches, with every hand correction taken back out. Both directions above
        // need it, because which override to write depends on whether the rule already answers the
        // question - and the group object cannot say, it only holds the two halves.
        private HashSet<string> PredicateMembers(NpcGroup group)
        {
            var probe = new NpcGroup();
            probe.Predicates.AddRange(group.Predicates);

            return new HashSet<string>(
                _resolver.Resolve(probe).Members.Select(m => m.Key), StringComparer.OrdinalIgnoreCase);
        }

        private static bool SameKey(string? a, string? b)
            => string.Equals((a ?? "").Trim(), (b ?? "").Trim(), StringComparison.OrdinalIgnoreCase);

        // ---- by hand: what was corrected -----------------------------------------------------------------

        // The corrections listed BY NAME and undoable one at a time. "by hand  +2 / -1" in the definition
        // says something was done; this says what - and it is the only place a remove-override is visible
        // at all, since a removed NPC is by definition not in the member list underneath.
        public ObservableCollection<NpcOverrideRowVM> HandCorrections { get; } = new();

        public bool HasHandCorrections => HandCorrections.Count > 0;

        private void RebuildHandCorrections(NpcGroup? group)
        {
            HandCorrections.Clear();

            if (group != null)
            {
                foreach (var over in group.Overrides
                             .OrderBy(o => o.Mode, StringComparer.OrdinalIgnoreCase)
                             .ThenBy(o => o.NpcKey, StringComparer.OrdinalIgnoreCase))
                {
                    var record = _npcs.FirstOrDefault(n => SameKey(n.Key, over.NpcKey));
                    HandCorrections.Add(new NpcOverrideRowVM(over, record, _labels, UndoOverride));
                }
            }

            OnPropertyChanged(nameof(HasHandCorrections));
        }

        private void UndoOverride(NpcOverrideRowVM row)
        {
            var group = _selectedGroup?.Group;
            if (group == null || row == null) return;

            if (group.Overrides.RemoveAll(o => SameKey(o.NpcKey, row.Key)) == 0) return;

            Persist(group);
            RefreshMembership();
        }

        // ---- the refresh after an edit --------------------------------------------------------------------

        // Not RebuildGroups: that one rebuilds every row and resets the selection to the top, which would
        // throw the user out of the group they are editing on every keystroke. Only the two counts that can
        // have moved are touched - this group's, and the one row that is not a group.
        private void RefreshMembership()
        {
            if (_selectedGroup?.Group != null)
            {
                var resolution = _resolver.Resolve(_selectedGroup.Group);
                Remember(_selectedGroup.Group, new NpcGroupResolutionKeys(resolution.Members.Select(m => m.Key)));
                _selectedGroup.MemberCount = resolution.Count;
            }

            // One resolution, then a set union over what was already resolved. The other groups did not
            // change, and re-resolving all of them on every keystroke is what made this the slow path.
            RecomputeCoverage();

            RefreshDetailBody();
            RebuildSearch();

            OnPropertyChanged(nameof(GroupSummary));
        }
    }

    // One value an axis can be filtered on, with how many NPCs carry it. Token is what goes into the
    // predicate and into the patch string; Label is what the box shows.
    public sealed class NpcGroupChoice
    {
        public NpcGroupChoice(string token, string label, int count = -1)
        {
            Token = token ?? "";
            Count = count;

            // A count of -1 means "this is not a catalogue entry" - the axis and mode lists use the same
            // type and would otherwise read "Class  (0)".
            Label = count < 0 ? label ?? "" : $"{label}  ({count})";
        }

        public string Token { get; }
        public string Label { get; }
        public int Count { get; }

        public override string ToString() => Label;
    }

    // One clause of the definition, made editable: axis, mode, value.
    //
    // The value half is two controls, not one, because the seven axes are not one kind of thing. Six of
    // them take a RECORD KEY or a fixed token and get a filtering picker over what the load order
    // actually contains; EditorID takes a SUBSTRING, where there is nothing to pick from and a picker
    // would only get in the way of typing "EncBandit03".
    public sealed class NpcGroupClauseRowVM : ViewModelBase
    {
        private readonly Func<string, IReadOnlyList<NpcGroupChoice>> _choices;
        private readonly Func<NpcGroupClauseRowVM, string, IReadOnlyList<(string Mode, string Value)>> _elsewhere;

        public NpcGroupClauseRowVM(
            string axis,
            string mode,
            string value,
            string origin,
            Func<string, IReadOnlyList<NpcGroupChoice>> choices,
            Func<NpcGroupClauseRowVM, string, IReadOnlyList<(string Mode, string Value)>> elsewhere,
            Action<NpcGroupClauseRowVM> remove)
        {
            _choices = choices;
            _elsewhere = elsewhere;
            _axis = Known(axis) ? axis.Trim().ToLowerInvariant() : NpcGroupAxis.Class;
            _mode = KnownMode(mode) ? mode.Trim() : NpcGroupMode.Include;
            _value = (value ?? "").Trim();
            _origin = (origin ?? "").Trim();
            _valueFilter = CurrentLabel;

            RemoveCommand = new RelayCommand(() => remove(this));
        }

        // Who wrote this line. It goes back into the group on every rewrite, so the auto-check can still
        // find its own work after the list has been edited around it.
        private string _origin;
        public string Origin => _origin;

        public bool IsAutoExcluded
            => string.Equals(_origin, NpcGroupPredicateOrigin.AutoExclude, StringComparison.OrdinalIgnoreCase);

        // EDITING A LINE MAKES IT YOURS. Otherwise "remove what the auto-check added" would quietly take
        // away a line somebody had since changed into something they meant.
        private void NowMine()
        {
            if (_origin.Length == 0) return;

            _origin = "";
            OnPropertyChanged(nameof(Origin));
            OnPropertyChanged(nameof(IsAutoExcluded));
        }

        public event Action<NpcGroupClauseRowVM>? Changed;

        // The row carries its own remove button rather than the parent carrying a command with the row as
        // its parameter: an ancestor binding out of an ItemsControl is the kind of thing that fails
        // silently, and this screen already has a test that exists because of exactly that.
        public ICommand RemoveCommand { get; }

        // ---- axis ----

        public static IReadOnlyList<NpcGroupChoice> AxisChoices { get; } = new List<NpcGroupChoice>
        {
            new(NpcGroupAxis.Class, "Class"),
            new(NpcGroupAxis.Race, "Race"),
            new(NpcGroupAxis.Faction, "Faction"),
            new(NpcGroupAxis.EditorId, "EditorID"),
            new(NpcGroupAxis.Mod, "Mod"),
            new(NpcGroupAxis.Flag, "Flag"),
            new(NpcGroupAxis.Gender, "Gender"),
        };

        public IReadOnlyList<NpcGroupChoice> Axes => AxisChoices;

        private string _axis;
        public string Axis
        {
            get => _axis;
            set
            {
                var next = (value ?? "").Trim().ToLowerInvariant();
                if (!Known(next) || string.Equals(next, _axis, StringComparison.Ordinal)) return;

                _axis = next;

                // THE VALUE GOES WITH THE AXIS. A class key left behind on the race axis matches nothing,
                // and a clause that matches nothing empties the whole group - see NpcGroupResolver.Resolve.
                // Clearing it puts the row back into the "half typed" state, where it is not written to the
                // group at all.
                _value = "";
                _valueFilter = "";

                OnPropertyChanged();
                OnPropertyChanged(nameof(Value));
                OnPropertyChanged(nameof(ValueFilter));
                OnPropertyChanged(nameof(ValueOptions));
                OnPropertyChanged(nameof(SelectedValue));
                OnPropertyChanged(nameof(IsFreeText));
                OnPropertyChanged(nameof(IsPicker));
                OnPropertyChanged(nameof(IsIncomplete));
                OnPropertyChanged(nameof(Modes));
                OnPropertyChanged(nameof(Mode));

                NowMine();
                Changed?.Invoke(this);
            }
        }

        private static bool Known(string? axis)
            => axis != null && NpcGroupAxis.All.Contains(axis.Trim(), StringComparer.OrdinalIgnoreCase);

        // ---- mode ----

        // The words change with the axis because the operation does: filterByEditorIdContains is a
        // substring test, and "EditorID is EncBandit03" would be a plain lie about what the patch does.
        // Same wording the read-only summary uses (NpcGroupVM.ModeLabel), so the two halves of the screen
        // cannot drift apart.
        public IReadOnlyList<NpcGroupChoice> Modes
        {
            get
            {
                bool contains = string.Equals(_axis, NpcGroupAxis.EditorId, StringComparison.OrdinalIgnoreCase);

                return new List<NpcGroupChoice>
                {
                    new(NpcGroupMode.Include, contains ? "contains" : "is"),
                    new(NpcGroupMode.IncludeOr, contains ? "contains any of" : "is any of"),
                    new(NpcGroupMode.Exclude, contains ? "does not contain" : "is not"),
                };
            }
        }

        private string _mode;
        public string Mode
        {
            get => _mode;
            set
            {
                var next = (value ?? "").Trim();
                if (!KnownMode(next) || string.Equals(next, _mode, StringComparison.Ordinal)) return;

                _mode = next;
                OnPropertyChanged();
                NowMine();
                Changed?.Invoke(this);
            }
        }

        private static bool KnownMode(string? mode)
            => string.Equals(mode, NpcGroupMode.Include, StringComparison.OrdinalIgnoreCase)
               || string.Equals(mode, NpcGroupMode.IncludeOr, StringComparison.OrdinalIgnoreCase)
               || string.Equals(mode, NpcGroupMode.Exclude, StringComparison.OrdinalIgnoreCase);

        // ---- value ----

        public bool IsFreeText => string.Equals(_axis, NpcGroupAxis.EditorId, StringComparison.OrdinalIgnoreCase);
        public bool IsPicker => !IsFreeText;

        private string _value;
        public string Value
        {
            get => _value;
            set
            {
                var next = (value ?? "").Trim();
                if (string.Equals(next, _value, StringComparison.Ordinal)) return;

                _value = next;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsIncomplete));
                NowMine();
                Changed?.Invoke(this);
            }
        }

        // Nothing picked yet. The row stays on screen; the group does not get the clause until this is
        // false - see NpcGroupVM.OnClauseChanged.
        public bool IsIncomplete => string.IsNullOrWhiteSpace(_value);

        private IReadOnlyList<NpcGroupChoice> Catalogue => _choices(_axis) ?? Array.Empty<NpcGroupChoice>();

        private string CurrentLabel
            => Catalogue.FirstOrDefault(c => string.Equals(c.Token, _value, StringComparison.OrdinalIgnoreCase))?.Label
               ?? _value;

        private string _valueFilter;
        public string ValueFilter
        {
            get => _valueFilter;
            set
            {
                if (!SetProperty(ref _valueFilter, value ?? "")) return;
                OnPropertyChanged(nameof(ValueOptions));
            }
        }

        // What the other rows already hold on this axis.
        private IReadOnlyList<(string Mode, string Value)> Elsewhere
            => _elsewhere?.Invoke(this, _axis) ?? Array.Empty<(string, string)>();

        // THE VALUES ALREADY PLACED ARE NOT OFFERED AGAIN. Both ways of repeating one are mistakes and
        // neither announces itself:
        //
        //   * the same value twice in the same mode is a no-op - two identical filter terms, the same
        //     members, and one line of the definition that means nothing;
        //   * the same value in two modes is a CONTRADICTION. Include and exclude on one value resolve to
        //     an empty group (NpcGroupResolver.Matches tests the exclusions last), and nothing on the
        //     screen would say why the count fell to zero.
        //
        // The row's OWN value stays in the list, or the box could not show its own selection.
        public IEnumerable<NpcGroupChoice> ValueOptions
        {
            get
            {
                var taken = new HashSet<string>(
                    Elsewhere.Select(e => e.Value), StringComparer.OrdinalIgnoreCase);

                return Catalogue
                    .Where(c => !taken.Contains(c.Token)
                                || string.Equals(c.Token, _value, StringComparison.OrdinalIgnoreCase))
                    .Where(c => string.IsNullOrWhiteSpace(_valueFilter)
                                || c.Label.Contains(_valueFilter, StringComparison.OrdinalIgnoreCase)
                                || c.Token.Contains(_valueFilter, StringComparison.OrdinalIgnoreCase));
            }
        }

        // Hiding a value in the pick list prevents the mistake; this REPORTS one that is already there -
        // typed into the EditorID box, which has no list to hide anything from, or loaded from a group
        // that was written before the list did.
        public string ConflictText
        {
            get
            {
                if (IsIncomplete) return "";

                var clash = Elsewhere.FirstOrDefault(e =>
                    string.Equals(e.Value, _value, StringComparison.OrdinalIgnoreCase));

                if (clash.Value == null) return "";

                return string.Equals(clash.Mode, _mode, StringComparison.OrdinalIgnoreCase)
                    ? "already on another line - this one changes nothing"
                    : "another line has the opposite on the same value - together they match nobody";
            }
        }

        public bool HasConflict => ConflictText.Length > 0;

        // Another row changed, so what this one may offer and whether it clashes have both moved.
        public void RefreshPeers()
        {
            OnPropertyChanged(nameof(ValueOptions));
            OnPropertyChanged(nameof(ConflictText));
            OnPropertyChanged(nameof(HasConflict));
        }

        public NpcGroupChoice? SelectedValue
        {
            get => Catalogue.FirstOrDefault(c =>
                string.Equals(c.Token, _value, StringComparison.OrdinalIgnoreCase));
            set
            {
                // null arrives when the filter hides the selected row, which happens on every keystroke.
                // The same trap NpcLinkPickerVM documents: taking it at face value would clear the clause
                // mid-word, and an empty clause is a group that resolves to nothing.
                if (value == null) return;

                // AND THE SAME VALUE IS NOT A CHANGE - the guard Axis, Mode and Value all have, and the
                // one this setter was missing. Without it the screen killed the process outright:
                //
                //   pick a value -> _valueFilter becomes that value's own label -> ValueOptions filters
                //   by it and collapses to one entry -> the box's ItemsSource changes under WPF, which
                //   re-applies the selection -> this setter runs AGAIN with the same choice -> Changed
                //   -> RefreshPeers raises ValueOptions -> ItemsSource changes -> ...
                //
                // Synchronous all the way down, so it is recursion, not a loop: StackOverflowException,
                // which the CLR answers by tearing the process down WITHOUT running a single one of
                // App.xaml.cs's three handlers. No dialog, no line in error.log - the tool simply
                // vanished. Reported on the gender and flag axes, where the collapsed list is shortest.
                if (string.Equals(value.Token, _value, StringComparison.OrdinalIgnoreCase)) return;

                _value = value.Token;
                _valueFilter = value.Label;

                OnPropertyChanged();
                OnPropertyChanged(nameof(Value));
                OnPropertyChanged(nameof(ValueFilter));
                OnPropertyChanged(nameof(IsIncomplete));

                NowMine();
                Changed?.Invoke(this);
            }
        }
    }

    // One search hit. It says whether the NPC is ALREADY in the group, because the alternative is a
    // button that looks like it does something and writes nothing - the group already reaches this NPC
    // by rule (see NpcGroupVM.AddNpc).
    public sealed class NpcSearchRowVM
    {
        public NpcSearchRowVM(NpcRecord npc, INpcLabels labels, bool alreadyMember, Action<NpcSearchRowVM> add)
        {
            Key = npc.Key;
            EditorID = npc.EditorID;
            Name = string.IsNullOrWhiteSpace(npc.Name) ? npc.EditorID : npc.Name;
            ClassName = labels.Class(npc.ClassKey);
            AlreadyMember = alreadyMember;

            AddCommand = new RelayCommand(() => add(this));
        }

        public string Key { get; }
        public string EditorID { get; }
        public string Name { get; }
        public string ClassName { get; }
        public bool AlreadyMember { get; }
        public bool CanAdd => !AlreadyMember;

        public string AddText => AlreadyMember ? "in group" : "Add";

        public ICommand AddCommand { get; }
    }

    // One hand correction, with the button that takes it back.
    public sealed class NpcOverrideRowVM
    {
        public NpcOverrideRowVM(
            NpcGroupMemberOverride over, NpcRecord? record, INpcLabels labels, Action<NpcOverrideRowVM> undo)
        {
            Key = over.NpcKey;
            IsRemoval = string.Equals(over.Mode, NpcGroupOverrideMode.Remove, StringComparison.OrdinalIgnoreCase);
            Mark = IsRemoval ? "-" : "+";

            // A correction may name an NPC the current load order no longer has - a removed mod, or a
            // group carried over from somewhere else. It is SAID rather than dropped, for the same reason
            // the resolver warns about it: the alternative is a member count that shrank with no reason
            // given.
            Name = record != null
                ? (string.IsNullOrWhiteSpace(record.Name) ? record.EditorID : $"{record.EditorID} - {record.Name}")
                : $"{over.NpcKey}  (not in the scanned data)";

            ClassName = record != null ? labels.Class(record.ClassKey) : "";
            IsMissing = record == null;

            UndoCommand = new RelayCommand(() => undo(this));
        }

        public string Key { get; }
        public string Mark { get; }
        public string Name { get; }
        public string ClassName { get; }
        public bool IsRemoval { get; }
        public bool IsMissing { get; }

        public ICommand UndoCommand { get; }
    }
}
