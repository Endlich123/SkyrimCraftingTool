using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using SkyrimCraftingTool.Model;
using SkyrimCraftingTool.Services;
using SkyrimCraftingTool.Services.PatchGen;

namespace SkyrimCraftingTool.ViewModel
{
    // The group editor (G4, docs/NPC-Gruppen-Plan.md section 6).
    //
    // Three properties carry this screen, and all three are about making a bad group VISIBLE rather
    // than asking the user to know in advance:
    //
    //   * THE "IS" COLUMN SHOWS THE SPREAD. "Health 222 - 238" is a group worth editing, "Health
    //     35 - 497" is four tiers in one bucket - and nobody has to decide which beforehand.
    //   * THE RULE PREVIEW IS ALWAYS VISIBLE, including what the rule cannot carry.
    //   * "(without a group)" IS ITSELF A ROW WITH A NUMBER. Nothing hides.
    //
    // The numbers behind the spread come from NpcStatResolver, not from the records: 61,4 % of NPCs
    // take their stats from a template, and a spread over those stored fields would be a spread over
    // numbers the game never reads.
    // The other half of this class is in NpcGroupVM.Membership.cs: the filter editor and the by-hand
    // member picking, which is what turns a new empty group into a group with NPCs in it.
    public sealed partial class NpcGroupVM : ViewModelBase
    {
        private readonly IItemService? _items;
        private readonly NpcGroupStore? _store;

        private List<NpcRecord> _npcs = new();
        private INpcLabels _labels = NpcLabels.Empty;
        private NpcGroupResolver _resolver = new(Array.Empty<NpcRecord>());
        private NpcStatResolver _stats = new(Array.Empty<NpcRecord>());
        private bool _loaded;

        // Null for both is the test case: the view model is exercised without a database behind it,
        // the same way NpcNodeVM can be built from a record alone.
        public NpcGroupVM(IItemService? items = null, NpcGroupStore? store = null)
        {
            _items = items;
            _store = store;

            SeedCommand = new RelayCommand(Seed);
            NewGroupCommand = new RelayCommand(NewGroup);
            DeleteGroupCommand = new RelayCommand(DeleteGroup, () => SelectedGroup?.Group != null);
            ToggleMembersCommand = new RelayCommand(() => ShowMembers = !ShowMembers);
            ConfirmCommand = new RelayCommand(Confirm, () => SelectedGroup?.Group != null);
        }

        public ObservableCollection<NpcGroupRowVM> Groups { get; } = new();

        public ObservableCollection<NpcGroupValueRowVM> Values { get; } = new();

        // The eighteen skills, in their own block below the four attributes (G7). A second collection
        // rather than a marker on the rows, so the view can put a real separator between the two and
        // give the skills a simpler row: a skill takes one number, so it needs no kind selector.
        public ObservableCollection<NpcGroupValueRowVM> SkillValues { get; } = new();

        public ObservableCollection<string> DefinitionLines { get; } = new();

        public ObservableCollection<NpcMemberRowVM> Members { get; } = new();

        public ObservableCollection<string> RuleProblems { get; } = new();

        public ICommand SeedCommand { get; }
        public ICommand NewGroupCommand { get; }
        public ICommand DeleteGroupCommand { get; }
        public ICommand ToggleMembersCommand { get; }
        public ICommand ConfirmCommand { get; }

        // ---- loading ---------------------------------------------------------------------------

        // First visit reads the NPC tables; later visits are free. Same contract as the NPC tab, and
        // for the same reason: this is the biggest block in the database and a session may never open
        // the tab.
        public void EnsureLoaded()
        {
            if (_loaded || _items == null) return;
            _loaded = true;

            Load(_items.GetAllNpcs(), _items.GetNpcLabels(), _store?.LoadAll(), _items.GetLeveledNpcs());
        }

        // A scan rewrites the NPC tables, so whatever is on screen is stale. Dropped rather than
        // re-read - the user is usually not on this tab when a scan finishes.
        public void Invalidate()
        {
            _loaded = false;
            Load(new List<NpcRecord>(), NpcLabels.Empty, new List<NpcGroup>());
        }

        public void Load(
            IEnumerable<NpcRecord>? npcs,
            INpcLabels? labels = null,
            IEnumerable<NpcGroup>? groups = null,
            IReadOnlyDictionary<string, List<string>>? leveledNpcs = null)
        {
            _npcs = npcs?.ToList() ?? new List<NpcRecord>();
            _labels = labels ?? NpcLabels.Empty;
            _resolver = new NpcGroupResolver(_npcs);

            // The filter editor's pick lists are counted off these records, so they are as stale as the
            // records they were built from.
            _choices.Clear();

            // The levelled character lists, so an NPC drawn from one shows the range of what it can
            // be instead of a shrug (G8). Null in a test and on a database scanned before those tables
            // existed - then case D stays unresolved, which is what it was.
            _stats = new NpcStatResolver(_npcs, leveledNpcs);

            RebuildGroups(groups?.ToList() ?? new List<NpcGroup>());
        }

        public int NpcCount => _npcs.Count;
        public bool HasData => _npcs.Count > 0;

        private void RebuildGroups(List<NpcGroup> groups)
        {
            Groups.Clear();

            // The rescan diff of section 8, computed once for the whole list: a group is not just its
            // member count, it is its member count AGAINST the one somebody confirmed. Without this a
            // group that grew by 17 NPCs after a mod install looks exactly like one that did not.
            var snapshots = _store?.LoadSnapshots() ?? new Dictionary<long, List<string>>();

            _resolvedKeys.Clear();

            foreach (var group in groups.OrderBy(g => g.SortOrder).ThenBy(g => g.Id))
            {
                snapshots.TryGetValue(group.Id, out var snapshot);
                var resolution = _resolver.Resolve(group);

                // Held, because coverage, "(without a group)" and the overlap block are three questions
                // about this one answer - and resolving is the expensive part of this screen.
                Remember(group, new NpcGroupResolutionKeys(resolution.Members.Select(m => m.Key)));

                Groups.Add(new NpcGroupRowVM(
                    group,
                    resolution.Count,
                    NpcGroupDiffService.Compare(group, resolution.Members, snapshot)));
            }

            // "(without a group)" is a row like any other, with its number in the same column. It is
            // the check on the whole screen: a seeder that covered 40 % would otherwise look the same
            // as one that covered 90 %. RecomputeCoverage fills in its number.
            Groups.Add(NpcGroupRowVM.Ungrouped(0));
            RecomputeCoverage();

            SelectedGroup = Groups.FirstOrDefault();
            OnPropertyChanged(nameof(NpcCount));
            OnPropertyChanged(nameof(HasData));
            OnPropertyChanged(nameof(GroupSummary));
        }

        public string GroupSummary
        {
            get
            {
                int real = Groups.Count(g => g.Group != null);

                // DISTINCT NPCs, not the sum of the member counts. Groups are allowed to overlap - the
                // seeder makes overlapping ones on purpose - and adding the counts up produced a line
                // that could not be true: measured on the real load order, 4.726 memberships over 4.561
                // NPCs read "4.726 of 6.642 covered, 2.081 without a group", which is 6.807 NPCs in a
                // database that holds 6.642. The 165 too many WERE the overlap, and nothing said so.
                int covered = _covered.Count;
                int sum = Groups.Where(g => g.Group is { Active: true }).Sum(g => g.MemberCount);

                var line = $"{real} groups, {covered} of {_npcs.Count} NPCs covered, " +
                           $"{Math.Max(0, _npcs.Count - covered)} without a group";

                return sum > covered
                    ? line + $" - {sum - covered} memberships are shared between groups"
                    : line;
            }
        }

        // ---- selection -------------------------------------------------------------------------

        private NpcGroupRowVM? _selectedGroup;
        public NpcGroupRowVM? SelectedGroup
        {
            get => _selectedGroup;
            set
            {
                if (!SetProperty(ref _selectedGroup, value)) return;

                ShowMembers = false;
                RefreshDetail();
            }
        }

        public bool HasSelection => _selectedGroup?.Group != null;
        public bool IsUngroupedSelected => _selectedGroup != null && _selectedGroup.Group == null;

        public string GroupName
        {
            get => _selectedGroup?.Group?.Name ?? _selectedGroup?.Name ?? "";
            set
            {
                var group = _selectedGroup?.Group;
                if (group == null || string.Equals(group.Name, value, StringComparison.Ordinal)) return;

                group.Name = value ?? "";
                _selectedGroup!.Refresh();
                Persist(group);
                OnPropertyChanged();
            }
        }

        public int MemberCount => _selectedGroup?.MemberCount ?? 0;

        private bool _showMembers;
        public bool ShowMembers
        {
            get => _showMembers;
            set
            {
                if (!SetProperty(ref _showMembers, value)) return;

                RebuildMembers();
                OnPropertyChanged(nameof(MembersButtonText));
            }
        }

        public string MembersButtonText => ShowMembers ? "Hide list" : "Show list";

        private string _rulePreview = "";
        public string RulePreview
        {
            get => _rulePreview;
            private set => SetProperty(ref _rulePreview, value);
        }

        public bool HasRuleProblems => RuleProblems.Count > 0;

        // A different group was selected. The two controls that hold half-finished input - the filter
        // rows and the search box - are rebuilt HERE and nowhere else: doing it on every membership
        // change would pull the row out from under the combo box that just changed it.
        private void RefreshDetail()
        {
            RebuildClauses(_selectedGroup?.Group);
            ClearMemberSearch();
            ClearAutoExcludeReport();

            RefreshDetailBody();
        }

        // Everything that is derived from the group and has to follow an edit to it.
        private void RefreshDetailBody()
        {
            var group = _selectedGroup?.Group;

            // The ungrouped half comes out of the coverage union rather than a second pass over every
            // group: it is the same answer, and resolving forty-two groups again to get it was the most
            // expensive thing this screen did.
            var members = group != null
                ? _resolver.Resolve(group).Members.ToList()
                : _selectedGroup != null
                    ? UngroupedMembers()
                    : new List<NpcRecord>();

            _currentMembers = members;

            RebuildDefinition(group);
            RebuildValues(group, members);
            RebuildPreview(group, members);
            RebuildMembers();
            RebuildHandCorrections(group);
            RebuildPerks(group, members);
            RebuildOverlap(group, members);
            RefreshDrift();

            OnPropertyChanged(nameof(GroupName));
            OnPropertyChanged(nameof(MemberCount));
            OnPropertyChanged(nameof(HasSelection));
            OnPropertyChanged(nameof(IsUngroupedSelected));
            OnPropertyChanged(nameof(ConfirmText));
        }

        private List<NpcRecord> _currentMembers = new();

        // ---- the definition block --------------------------------------------------------------

        private void RebuildDefinition(NpcGroup? group)
        {
            DefinitionLines.Clear();
            if (group == null) return;

            foreach (var axis in group.Predicates
                         .Where(p => !string.Equals(p.Axis, NpcGroupAxis.Flag, StringComparison.OrdinalIgnoreCase))
                         .GroupBy(p => (Axis: (p.Axis ?? "").ToLowerInvariant(), Mode: p.Mode ?? "")))
            {
                string values = string.Join(", ", axis.Select(p => Describe(axis.Key.Axis, p.Value)));
                DefinitionLines.Add($"{AxisLabel(axis.Key.Axis)}  {ModeLabel(axis.Key.Axis, axis.Key.Mode)} {values}");
            }

            // The three default exclusions on one line rather than three: they are on nearly every
            // group, and three lines of boilerplate would push the part that differs off the top.
            var excludedFlags = group.Predicates
                .Where(p => string.Equals(p.Axis, NpcGroupAxis.Flag, StringComparison.OrdinalIgnoreCase)
                            && string.Equals(p.Mode, NpcGroupMode.Exclude, StringComparison.OrdinalIgnoreCase))
                .Select(p => p.Value)
                .ToList();

            if (excludedFlags.Count > 0)
                DefinitionLines.Add($"without  {string.Join(", ", excludedFlags)}");

            var includedFlags = group.Predicates
                .Where(p => string.Equals(p.Axis, NpcGroupAxis.Flag, StringComparison.OrdinalIgnoreCase)
                            && !string.Equals(p.Mode, NpcGroupMode.Exclude, StringComparison.OrdinalIgnoreCase))
                .Select(p => p.Value)
                .ToList();

            if (includedFlags.Count > 0)
                DefinitionLines.Add($"only  {string.Join(", ", includedFlags)}");

            int added = group.Overrides.Count(o =>
                !string.Equals(o.Mode, NpcGroupOverrideMode.Remove, StringComparison.OrdinalIgnoreCase));
            int removed = group.Overrides.Count - added;

            if (added > 0 || removed > 0)
                DefinitionLines.Add($"by hand  +{added} / -{removed}");

            int perks = group.Lists.Count(l =>
                string.Equals(l.Kind, PerkKind, StringComparison.OrdinalIgnoreCase));

            if (perks > 0)
                DefinitionLines.Add($"adds  {perks} perk{(perks == 1 ? "" : "s")}");
        }

        private string Describe(string axis, string value) => axis switch
        {
            NpcGroupAxis.Class => _labels.Class(value),
            NpcGroupAxis.Race => _labels.Race(value),
            NpcGroupAxis.Faction => _labels.Faction(value),
            NpcGroupAxis.EditorId => $"\"{value}\"",
            _ => value,
        };

        private static string AxisLabel(string axis) => axis switch
        {
            NpcGroupAxis.Class => "Class",
            NpcGroupAxis.Race => "Race",
            NpcGroupAxis.Faction => "Faction",
            NpcGroupAxis.EditorId => "EditorID",
            NpcGroupAxis.Mod => "Mod",
            NpcGroupAxis.Gender => "Gender",
            NpcGroupAxis.Flag => "Flag",
            _ => axis,
        };

        private static string ModeLabel(string axis, string mode)
        {
            bool contains = string.Equals(axis, NpcGroupAxis.EditorId, StringComparison.OrdinalIgnoreCase);

            if (string.Equals(mode, NpcGroupMode.Exclude, StringComparison.OrdinalIgnoreCase))
                return contains ? "does not contain" : "not";

            if (string.Equals(mode, NpcGroupMode.IncludeOr, StringComparison.OrdinalIgnoreCase))
                return contains ? "contains any of" : "any of";

            return contains ? "contains" : "";
        }

        // ---- the "is" / "should" table ---------------------------------------------------------

        private void RebuildValues(NpcGroup? group, List<NpcRecord> members)
        {
            foreach (var row in Values) row.Changed -= OnValueChanged;
            foreach (var row in SkillValues) row.Changed -= OnValueChanged;
            Values.Clear();
            SkillValues.Clear();

            if (group == null) return;

            foreach (var (field, label, read) in StatFields)
                Values.Add(BuildRow(group, members, field, label, read));

            // ALL EIGHTEEN SKILLS, always, in their own block below the attributes. The first version
            // added them one at a time through a picker to keep the table short; the user asked for the
            // full list instead, and with a separator between the two blocks it reads fine - every row
            // is one line, the pane scrolls, and a skill with no value simply shows its spread.
            //
            // Ordered by what a skill does to an NPC, not by the skill menu: the weapon and armour
            // skills decide a fight, so they are the ones at the top where they get seen.
            foreach (var skill in NpcSkillNames.ByEffect)
            {
                SkillValues.Add(BuildRow(group, members, skill, NpcSkillNames.Label(skill),
                    view => view.Skill(skill)));
            }

            foreach (var row in Values) row.Changed += OnValueChanged;
            foreach (var row in SkillValues) row.Changed += OnValueChanged;
        }

        // The four fields the group editor writes. Level first because it is the one lever that was
        // measured to actually move an NPC (docs/TODO.md, Prio 8: "level= wirkt, Zahlen wirken kaum").
        private static readonly (string Field, string Label, Func<NpcStatView, int?> Read)[] StatFields =
        {
            ("level", "Level", v => v.Level),
            ("health", "Health", v => v.Health),
            ("magicka", "Magicka", v => v.Magicka),
            ("stamina", "Stamina", v => v.Stamina),
        };

        private NpcGroupValueRowVM BuildRow(
            NpcGroup group, List<NpcRecord> members, string field, string label, Func<NpcStatView, int?> read)
        {
            var stored = group.Values.FirstOrDefault(v =>
                string.Equals(v.Field, field, StringComparison.OrdinalIgnoreCase));

            // A skill takes a single number and nothing else, so its row offers no span, multiplier or
            // per-level kind - changeSkills has no spelling for those, and a box that accepts a value
            // the patch then refuses to write is worse than no box.
            bool singleValueOnly = NpcSkillNames.All.Contains(field, StringComparer.OrdinalIgnoreCase);

            return new NpcGroupValueRowVM(field, label, _stats.Spread(members, read), stored, singleValueOnly);
        }

        private void OnValueChanged(NpcGroupValueRowVM row)
        {
            var group = _selectedGroup?.Group;
            if (group == null) return;

            group.Values.RemoveAll(v => string.Equals(v.Field, row.Field, StringComparison.OrdinalIgnoreCase));
            var value = row.ToRecord();
            if (value != null) group.Values.Add(value);

            RebuildPreview(group, _currentMembers);
            Persist(group);
        }

        // ---- the rule preview ------------------------------------------------------------------

        private void RebuildPreview(NpcGroup? group, List<NpcRecord> members)
        {
            RuleProblems.Clear();

            if (group == null)
            {
                RulePreview = "";
                OnPropertyChanged(nameof(HasRuleProblems));
                return;
            }

            // The whole NPC set, not just the members: the builder compares what the group means
            // against what its filter would reach, and names the difference (G5). It also carries the
            // resolver's own warnings, so a group the resolver cannot evaluate does not show a clean
            // preview beside a member count of zero.
            // All groups, because a scaling group has to exclude the other scaling groups by name - see
            // NpcGroupRuleBuilder.WithScalingCounterExclusions.
            var preview = NpcGroupRuleBuilder.Build(group, _npcs, _stats,
                Groups.Where(g => g.Group != null).Select(g => g.Group!).ToList());

            RulePreview = preview.Text;
            foreach (var problem in preview.Problems) RuleProblems.Add(problem);

            // Checked over every group, not just this one: SkyPatcher keeps a single calcLevelMin/Max
            // pair for the whole patch, so two groups that scale to different bounds silently share the
            // last pair. A per-group preview cannot see that, and it is exactly the kind of thing the
            // preview exists to catch.
            foreach (var collision in NpcGroupRuleBuilder.ScalingCollisions(
                         Groups.Where(g => g.Group != null).Select(g => g.Group!)))
            {
                RuleProblems.Add(collision);
            }

            OnPropertyChanged(nameof(HasRuleProblems));
        }

        // ---- the member list -------------------------------------------------------------------

        private void RebuildMembers()
        {
            Members.Clear();
            if (!ShowMembers) return;

            foreach (var npc in _currentMembers
                         .OrderBy(n => n.EditorID, StringComparer.OrdinalIgnoreCase)
                         .Take(MemberListLimit))
            {
                // No remove button on "(without a group)": there is no group to take an NPC out of, and
                // the list is the one place the ungrouped rest can be read at all.
                Members.Add(new NpcMemberRowVM(npc, _stats.Resolve(npc), _labels,
                    _selectedGroup?.Group != null ? RemoveMember : null));
            }

            OnPropertyChanged(nameof(MemberListTruncated));
        }

        // The two biggest class groups the seeder produces hold 1.215 and 734 NPCs. The list is
        // virtualised, but a cap keeps the first paint honest on a group nobody wants to read to the
        // end anyway - and the spread above is what that group is actually judged by.
        private const int MemberListLimit = 500;

        public bool MemberListTruncated => ShowMembers && _currentMembers.Count > MemberListLimit;

        // ---- commands --------------------------------------------------------------------------

        // Section 7's pre-fill. ADDITIVE ON PURPOSE: a group already present under the same seed is
        // left exactly as it is, including a name or a value the user changed. Refreshing an existing
        // auto group's predicate after a scan is the rescan's job (G6), where the change can be shown
        // before it is applied - doing it here would quietly rewrite groups somebody had already
        // edited.
        public void Seed()
        {
            if (_npcs.Count == 0) return;

            var existing = Groups.Where(g => g.Group != null).Select(g => g.Group!).ToList();
            var known = new HashSet<string>(
                existing.Select(g => g.Seed ?? "").Where(s => s.Length > 0), StringComparer.OrdinalIgnoreCase);

            var seeded = new NpcGroupSeeder(_npcs, _labels).Seed();
            var added = seeded.Groups.Where(g => !known.Contains(g.Seed)).ToList();

            foreach (var group in added)
            {
                group.SortOrder = existing.Count + added.IndexOf(group);
                Persist(group);
            }

            LastSeedReport = added.Count == 0
                ? $"Nothing added - all {seeded.GroupCount} seed groups are already here."
                : $"Added {added.Count} of {seeded.GroupCount} seed groups " +
                  $"({seeded.TierGroups} by EditorID, {seeded.ClassGroups} by class); " +
                  $"{seeded.Ungrouped.Count} NPCs still without a group.";

            RebuildGroups(existing.Concat(added).ToList());
            OnPropertyChanged(nameof(LastSeedReport));
        }

        public string LastSeedReport { get; private set; } = "";

        public void NewGroup()
        {
            var group = new NpcGroup
            {
                Name = "New group",
                Origin = NpcGroupOrigin.User,
                SortOrder = Groups.Count(g => g.Group != null),
            };

            Persist(group);

            var groups = Groups.Where(g => g.Group != null).Select(g => g.Group!).Append(group).ToList();
            RebuildGroups(groups);

            SelectedGroup = Groups.FirstOrDefault(g => g.Group == group);
        }

        public void DeleteGroup()
        {
            var group = _selectedGroup?.Group;
            if (group == null) return;

            if (group.Id > 0) _store?.Delete(group.Id);

            RebuildGroups(Groups
                .Where(g => g.Group != null && g.Group != group)
                .Select(g => g.Group!)
                .ToList());
        }

        // Section 8: the snapshot is what the user has CONFIRMED this group contains, and the rescan diff
        // reports every difference from it by name. Until then the group counts as unchecked - which is
        // why this is a button and not something that happens on every resolve. A snapshot that refreshed
        // itself would agree with the new membership every time and the diff would never report anything.
        public void Confirm()
        {
            var group = _selectedGroup?.Group;
            if (group == null) return;

            // A group that was never saved has no Id to hang a snapshot on. Saving first rather than
            // refusing: from where the user stands they pressed a button on a group that exists.
            if (group.Id <= 0) Persist(group);
            if (group.Id <= 0) return;

            var keys = _currentMembers.Select(m => m.Key).ToList();
            _store?.SaveSnapshot(group.Id, keys);

            _selectedGroup!.Drift = NpcGroupDiffService.Compare(group, _currentMembers, keys);
            RefreshDrift();
        }

        public string ConfirmText
            => _selectedGroup?.Confirmed == true ? "Confirmed" : "Confirm these members";

        // ---- the rescan diff (section 8) -------------------------------------------------------

        public ObservableCollection<string> DriftAddedNames { get; } = new();

        private string _driftHeadline = "";
        public string DriftHeadline
        {
            get => _driftHeadline;
            private set => SetProperty(ref _driftHeadline, value);
        }

        public bool HasDrift => _selectedGroup?.Drift?.NeedsAttention ?? false;

        private void RefreshDrift()
        {
            DriftAddedNames.Clear();

            var drift = _selectedGroup?.Drift;

            if (drift == null || !drift.NeedsAttention)
            {
                DriftHeadline = "";
            }
            else
            {
                DriftHeadline = drift.Headline;

                // The names, because that is what makes the change judgeable: "17 new" says something
                // moved, "17 new, all EncBandit03*" says whether it was meant.
                foreach (var name in drift.AddedNames) DriftAddedNames.Add(name);

                if (drift.AddedNamesNotShown > 0)
                    DriftAddedNames.Add($"... and {drift.AddedNamesNotShown} more");

                foreach (var gone in drift.RemovedKeys.Take(10))
                    DriftAddedNames.Add($"gone: {gone}");
            }

            OnPropertyChanged(nameof(HasDrift));
            OnPropertyChanged(nameof(ConfirmText));
        }

        private void Persist(NpcGroup group)
        {
            if (_store == null) return;
            _store.Save(group);
        }
    }

    // One row in the group list: the name and the number, which is the whole left-hand column of
    // section 6's sketch.
    public sealed class NpcGroupRowVM : ViewModelBase
    {
        public NpcGroupRowVM(NpcGroup group, int memberCount, NpcGroupDrift? drift = null)
        {
            Group = group;
            MemberCount = memberCount;
            Drift = drift;
        }

        private NpcGroupRowVM(string name, int memberCount)
        {
            Group = null;
            Name = name;
            MemberCount = memberCount;
        }

        // The row that is not a group. It carries a number like the others so nothing can hide behind
        // "the rest".
        public static NpcGroupRowVM Ungrouped(int count) => new("(without a group)", count);

        public NpcGroup? Group { get; }

        private string _name = "";
        public string Name
        {
            get => Group?.Name ?? _name;
            private set => _name = value;
        }

        // Settable, because an edit to the group's own definition moves it. Rebuilding the whole list
        // instead would reset the selection to the top and throw the user out of the group they are
        // editing - see NpcGroupVM.RefreshMembership.
        private int _memberCount;
        public int MemberCount
        {
            get => _memberCount;
            set => SetProperty(ref _memberCount, value);
        }

        public bool IsAuto => string.Equals(Group?.Origin, NpcGroupOrigin.Auto, StringComparison.OrdinalIgnoreCase);

        // How this group's membership stands against the one that was last confirmed (section 8). Null
        // for the "(without a group)" row, which nobody confirms.
        private NpcGroupDrift? _drift;
        public NpcGroupDrift? Drift
        {
            get => _drift;
            set
            {
                _drift = value;
                OnPropertyChanged(nameof(Drift));
                OnPropertyChanged(nameof(NeedsAttention));
                OnPropertyChanged(nameof(DriftMark));
                OnPropertyChanged(nameof(Confirmed));
            }
        }

        public bool Confirmed => Drift is { HasSnapshot: true, Changed: false };

        // Either nobody has checked this group, or it has moved since they did. The list marks it, so a
        // group that quietly grew after a mod install is visible without opening it.
        public bool NeedsAttention => Drift?.NeedsAttention ?? false;

        // A mark rather than a colour alone: "!" for a group that changed, "?" for one nobody has ever
        // confirmed. Both mean "look at me", and they mean different things.
        public string DriftMark => Drift == null || !Drift.NeedsAttention
            ? ""
            : Drift.HasSnapshot ? "!" : "?";

        public void Refresh() => OnPropertyChanged(nameof(Name));
    }

    // One line of the values table: what the members HAVE on the left, what they should get on the
    // right. The left half is the reason this screen works - see NpcGroupVM.
    public sealed class NpcGroupValueRowVM : ViewModelBase
    {
        private readonly NpcStatSpread _spread;

        public NpcGroupValueRowVM(
            string field, string label, NpcStatSpread spread, NpcGroupValue? stored, bool singleValueOnly = false)
        {
            Field = field;
            Label = label;
            _spread = spread;

            Kinds = singleValueOnly
                ? new[] { NpcGroupValueKind.Direct }
                : new[]
                {
                    NpcGroupValueKind.Direct, NpcGroupValueKind.Span,
                    NpcGroupValueKind.Mult, NpcGroupValueKind.Calc,
                };

            _kind = singleValueOnly ? NpcGroupValueKind.Direct : stored?.Kind ?? NpcGroupValueKind.Direct;
            _low = stored?.Low ?? "";
            _high = stored?.High ?? "";
        }

        public event Action<NpcGroupValueRowVM>? Changed;

        public string Field { get; }
        public string Label { get; }

        // The "is" column: a single number for a homogeneous group, a range for a wide one, and the
        // count of members whose value does not resolve at all.
        public string IsText => _spread.Text;

        // True when the range is wide enough that the members are not really one thing. The view
        // colours the row by this, which is what turns "Health 35 - 497" from a number into a warning.
        //
        // RELATIVE, NOT ABSOLUTE, and measured: an absolute threshold cannot work across levels. The
        // seeded tier groups run from "Health 35" at level 1 to "1000 - 1400" at level 30-45, so a
        // fixed 50 called Bandit 06 (441 - 497, a 13 % spread) too coarse and would have called any
        // high-level group coarse no matter how tight it was. A quarter of the low end puts
        // Bandit 03 (222 - 238) and Bandit 06 on the right side and still flags Bandit 03's magicka
        // (25 - 173, the magic-user variants) - which IS heterogeneous, and worth seeing.
        public bool IsWide
        {
            get
            {
                if (!_spread.Low.HasValue || !_spread.High.HasValue) return false;

                int range = _spread.High.Value - _spread.Low.Value;
                return range > Math.Max(MinimumWideRange, _spread.Low.Value / 4);
            }
        }

        // A floor, so a group sitting at single digits does not read as "too coarse" over a difference
        // of two points.
        private const int MinimumWideRange = 10;

        public IReadOnlyList<string> Kinds { get; }

        private string _kind;
        public string Kind
        {
            get => _kind;
            set
            {
                if (!SetProperty(ref _kind, value ?? NpcGroupValueKind.Direct)) return;
                OnPropertyChanged(nameof(WantsHigh));
                Changed?.Invoke(this);
            }
        }

        // Only a span takes two numbers. The other three read one, and a second box would invite a
        // value that is then silently dropped.
        public bool WantsHigh => string.Equals(Kind, NpcGroupValueKind.Span, StringComparison.OrdinalIgnoreCase);

        private string _low;
        public string Low
        {
            get => _low;
            set { if (SetProperty(ref _low, value ?? "")) Changed?.Invoke(this); }
        }

        private string _high;
        public string High
        {
            get => _high;
            set { if (SetProperty(ref _high, value ?? "")) Changed?.Invoke(this); }
        }

        public bool HasValue => !string.IsNullOrWhiteSpace(_low);

        // Null when the row is empty, which is how a value is REMOVED from the group: there is no
        // separate clear button, because an empty box already says it.
        public NpcGroupValue? ToRecord()
            => HasValue
                ? new NpcGroupValue { Field = Field, Kind = Kind, Low = _low.Trim(), High = _high.Trim() }
                : null;
    }

    // One NPC in the member list, with the numbers it actually has - resolved through the template
    // chain, so an inherited value shows where it came from instead of showing the dead field.
    public sealed class NpcMemberRowVM
    {
        public NpcMemberRowVM(
            NpcRecord npc, NpcStatView stats, INpcLabels labels, Action<NpcMemberRowVM>? remove = null)
        {
            Key = npc.Key;
            EditorID = npc.EditorID;
            Name = string.IsNullOrWhiteSpace(npc.Name) ? npc.EditorID : npc.Name;
            ClassName = labels.Class(npc.ClassKey);
            RaceName = labels.Race(npc.RaceKey);

            Level = npc.UsesPcLevelMult ? $"x{npc.LevelMult:0.##}" : Range(stats, v => v.Level);

            Health = Range(stats, v => v.Health);
            Magicka = Range(stats, v => v.Magicka);
            Stamina = Range(stats, v => v.Stamina);

            Source = stats.Source switch
            {
                NpcStatSource.Own => "own",
                NpcStatSource.Inherited => $"from {labels.Npc(stats.SourceKey)}",
                NpcStatSource.Candidates => $"{stats.Candidates.Count} from {labels.LeveledNpc(stats.SourceKey)}",
                _ => stats.Reason,
            };

            // Null in a test that builds the row from a record alone. The button is only offered where
            // there is a group to take the NPC out of.
            RemoveCommand = remove == null ? null : new RelayCommand(() => remove(this));
        }

        // The NPC this row stands for, which is what a hand correction is written against
        // (NpcGroupMemberOverride.NpcKey).
        public string Key { get; } = "";

        // Taking one NPC out of a group is not the same as taking it out of the filter: what this writes
        // is a remove-override, held in its own table so a re-seed cannot drop it (section 4).
        public ICommand? RemoveCommand { get; }

        public bool CanRemove => RemoveCommand != null;

        // One number, or the range across the candidates when the NPC is drawn from a levelled
        // character list (G8): the game picks one of them at spawn, so a single number would be a
        // guess and a dash would be less than what is known.
        private static string Range(NpcStatView stats, Func<NpcStatView, int?> field)
        {
            int? low = null, high = null;

            foreach (var leaf in stats.Leaves)
            {
                var value = field(leaf);
                if (value == null) continue;

                if (low == null || value < low) low = value;
                if (high == null || value > high) high = value;
            }

            if (low == null) return "-";
            return low == high ? low.ToString()! : $"{low} - {high}";
        }

        public string EditorID { get; }
        public string Name { get; }
        public string ClassName { get; }
        public string RaceName { get; }
        public string Level { get; }
        public string Health { get; }
        public string Magicka { get; }
        public string Stamina { get; }
        public string Source { get; }
    }
}
