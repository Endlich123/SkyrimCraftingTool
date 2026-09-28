using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using SkyrimCraftingTool.Model;
using SkyrimCraftingTool.Services;

namespace SkyrimCraftingTool.ViewModel
{
    // Where two groups reach the same NPC (docs/NPC-Gruppen-Plan.md sections 3.0.3 and 6).
    //
    // GROUPS ARE ALLOWED TO OVERLAP, and the seeder itself produces overlaps on purpose - it excludes
    // the tier EditorIDs from the class groups precisely because it would otherwise. What is not
    // allowed is overlapping WITHOUT KNOWING: SkyPatcher applies both rules, and for a field both of
    // them write, one of the two wins by file name (section 10.4) with nothing on screen to say which.
    // An NPC then quietly gets the other group's numbers.
    //
    // So this is not a validator and it refuses nothing. It says who shares whom, and it says it
    // louder when the two groups write the same field - which is the only case where the overlap
    // costs anything.
    //
    // IT IS ALSO THE REASON THE SUMMARY LINE CHANGED. It used to add the member counts up, so a load
    // order with 4.726 memberships over 4.561 distinct NPCs read "4.726 of 6.642 covered, 2.081
    // without a group" - two numbers that cannot both be true, and the 165 double counted were exactly
    // the overlap nobody could see.
    public sealed partial class NpcGroupVM
    {
        // Every group's resolved member keys, kept from the pass that built the list. Resolution is the
        // expensive part of this screen (6.642 NPCs against up to 15 clauses, per group), and coverage,
        // the ungrouped row and the overlap below are three questions about the same answer.
        private readonly Dictionary<NpcGroup, HashSet<string>> _resolvedKeys = new();

        // The union over the active groups. What "covered" means, as opposed to the sum of the counts.
        private HashSet<string> _covered = new(StringComparer.OrdinalIgnoreCase);

        private void Remember(NpcGroup group, NpcGroupResolutionKeys keys)
            => _resolvedKeys[group] = keys.Keys;

        // A set union over what is already resolved, not a re-resolve. Editing one group therefore costs
        // one resolution, not forty-two.
        private void RecomputeCoverage()
        {
            var covered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var row in Groups)
            {
                if (row.Group == null || !row.Group.Active) continue;
                if (!_resolvedKeys.TryGetValue(row.Group, out var keys)) continue;

                foreach (var key in keys) covered.Add(key);
            }

            _covered = covered;

            var ungrouped = Groups.FirstOrDefault(g => g.Group == null);
            if (ungrouped != null) ungrouped.MemberCount = Math.Max(0, _npcs.Count - covered.Count);
        }

        // The NPCs no active group reaches, read off the same union.
        private List<NpcRecord> UngroupedMembers()
            => _npcs.Where(n => !_covered.Contains(n.Key)).ToList();

        // ---- what the selected group shares --------------------------------------------------------

        public ObservableCollection<NpcGroupOverlapRowVM> Overlaps { get; } = new();

        public bool HasOverlap => Overlaps.Count > 0;

        // The block stays while there is a report to read. Without this, a successful auto-check would
        // remove the last overlap, hide the block, and take its own result off the screen with it - the
        // button would look like it had done nothing at the exact moment it worked.
        //
        // And it stays while there is something to undo, which is the case that outlives the report: a
        // group whose overlap is gone BECAUSE the button worked has no overlap to show and still needs
        // the way back.
        public bool ShowOverlapBlock => HasOverlap || HasAutoExcludeReport || HasAutoExcluded;

        // An overlap with no shared field is information; an overlap WITH one is a decision somebody has
        // to make. The view colours the block by this, and nothing else on the screen can say it: each
        // group's own rule preview is correct on its own, and they only collide in the game.
        public bool HasValueCollision => Overlaps.Any(o => o.HasCollision);

        private string _overlapHeadline = "";
        public string OverlapHeadline
        {
            get => _overlapHeadline;
            private set => SetProperty(ref _overlapHeadline, value);
        }

        private void RebuildOverlap(NpcGroup? group, List<NpcRecord> members)
        {
            // Here rather than in each of the three places that change a predicate: this runs after every
            // one of them, and the undo button's label carries a count.
            RefreshAutoExcluded();

            Overlaps.Clear();

            if (group == null || members.Count == 0)
            {
                OverlapHeadline = "";
                OnPropertyChanged(nameof(HasOverlap));
                OnPropertyChanged(nameof(ShowOverlapBlock));
                OnPropertyChanged(nameof(HasValueCollision));
                return;
            }

            var mine = new HashSet<string>(members.Select(m => m.Key), StringComparer.OrdinalIgnoreCase);
            var sharedAnywhere = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var rows = new List<NpcGroupOverlapRowVM>();

            foreach (var row in Groups)
            {
                var other = row.Group;
                if (other == null || ReferenceEquals(other, group) || !other.Active) continue;
                if (!_resolvedKeys.TryGetValue(other, out var keys)) continue;

                var shared = keys.Where(mine.Contains).ToList();
                if (shared.Count == 0) continue;

                foreach (var key in shared) sharedAnywhere.Add(key);

                rows.Add(new NpcGroupOverlapRowVM(
                    other, shared.Count, mine.Count, CollidingFields(group, other), Select));
            }

            foreach (var row in rows.OrderByDescending(r => r.Shared).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase))
                Overlaps.Add(row);

            OverlapHeadline = Overlaps.Count == 0
                ? ""
                : $"{sharedAnywhere.Count} of these {mine.Count} NPCs are also in " +
                  $"{Overlaps.Count} other group{(Overlaps.Count == 1 ? "" : "s")}.";

            OnPropertyChanged(nameof(HasOverlap));
            OnPropertyChanged(nameof(ShowOverlapBlock));
            OnPropertyChanged(nameof(HasValueCollision));
        }

        // The fields BOTH groups write. Only these cost anything: SkyPatcher runs both rules, and for a
        // field only one of them can be the last word. An overlap where one group sets values and the
        // other does not is harmless, and calling it a problem would make the real ones easy to ignore.
        private static IReadOnlyList<string> CollidingFields(NpcGroup a, NpcGroup b)
        {
            var mine = a.Values
                .Where(v => !string.IsNullOrWhiteSpace(v.Low))
                .Select(v => v.Field)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            return b.Values
                .Where(v => !string.IsNullOrWhiteSpace(v.Low) && mine.Contains(v.Field))
                .Select(v => v.Field)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private void Select(NpcGroup group)
        {
            var row = Groups.FirstOrDefault(g => ReferenceEquals(g.Group, group));
            if (row != null) SelectedGroup = row;
        }

        // ---- the auto-check -----------------------------------------------------------------------

        private RelayCommand? _autoExcludeCommand;
        public ICommand AutoExcludeCommand => _autoExcludeCommand ??= new RelayCommand(AutoExclude);

        public ObservableCollection<string> AutoExcludeNotes { get; } = new();
        public ObservableCollection<string> AutoExcludeProblems { get; } = new();

        private string _autoExcludeReport = "";
        public string AutoExcludeReport
        {
            get => _autoExcludeReport;
            private set => SetProperty(ref _autoExcludeReport, value);
        }

        public bool HasAutoExcludeReport => AutoExcludeReport.Length > 0;

        // ON THIS GROUP ONLY. Two overlapping groups can be separated from either side, and which one
        // keeps the NPCs is a decision, not a calculation - so it is made by choosing the group to press
        // it on. Rewriting every group at once would make that choice silently, forty-two times.
        //
        // What it writes is visible and undoable: the lines appear in the filter editor above and can be
        // taken out one at a time.
        public void AutoExclude()
        {
            var group = _selectedGroup?.Group;
            if (group == null) return;

            // ITS OWN PREVIOUS ANSWER GOES FIRST. Pressing it twice must recompute, not accumulate: the
            // other groups may have moved since, and a line that separated them yesterday can be exactly
            // the line that costs members today. Only lines still marked as its own are dropped - one the
            // user has since edited belongs to them (NpcGroupClauseRowVM.NowMine).
            int replaced = group.Predicates.RemoveAll(IsAutoExcluded);

            var plan = new NpcGroupDeduplicator(_npcs).Plan(
                group, Groups.Where(g => g.Group != null).Select(g => g.Group!));

            foreach (var clause in plan.Added) group.Predicates.Add(clause);

            if (plan.AnythingToDo || replaced > 0)
            {
                Persist(group);
                RefreshMembership();

                // The filter editor is rebuilt only on a selection change, so the new lines would not be
                // on screen until the user clicked away and back - and a button whose result is invisible
                // reads as a button that did nothing.
                RebuildClauses(group);
            }

            AutoExcludeReport = replaced > 0
                ? $"{plan.Headline} ({replaced} earlier line(s) from this button were recomputed.)"
                : plan.Headline;

            AutoExcludeNotes.Clear();
            foreach (var note in plan.Notes) AutoExcludeNotes.Add(note);

            AutoExcludeProblems.Clear();
            foreach (var problem in plan.Problems) AutoExcludeProblems.Add(problem);

            OnPropertyChanged(nameof(HasAutoExcludeReport));
            OnPropertyChanged(nameof(ShowOverlapBlock));
        }

        // ---- taking it back ---------------------------------------------------------------------

        private static bool IsAutoExcluded(NpcGroupPredicate clause)
            => string.Equals(clause.Origin, NpcGroupPredicateOrigin.AutoExclude, StringComparison.OrdinalIgnoreCase);

        // Survives leaving the group and coming back, because the mark is a column and not a field on
        // this screen. Every line is still removable one at a time with its own X - this is the one
        // press that undoes the one press.
        public int AutoExcludedCount
            => _selectedGroup?.Group?.Predicates.Count(IsAutoExcluded) ?? 0;

        public bool HasAutoExcluded => AutoExcludedCount > 0;

        public string RemoveAutoExcludedText
            => $"Remove the {AutoExcludedCount} line(s) this button added";

        private RelayCommand? _removeAutoExcludedCommand;
        public ICommand RemoveAutoExcludedCommand
            => _removeAutoExcludedCommand ??= new RelayCommand(RemoveAutoExcluded);

        public void RemoveAutoExcluded()
        {
            var group = _selectedGroup?.Group;
            if (group == null) return;

            int gone = group.Predicates.RemoveAll(IsAutoExcluded);
            if (gone == 0) return;

            Persist(group);
            RefreshMembership();
            RebuildClauses(group);

            AutoExcludeReport = $"{gone} filter line(s) removed - the NPCs they left to other groups are " +
                                "back in this one.";

            AutoExcludeNotes.Clear();
            AutoExcludeProblems.Clear();

            OnPropertyChanged(nameof(HasAutoExcludeReport));
            OnPropertyChanged(nameof(ShowOverlapBlock));
        }

        private void RefreshAutoExcluded()
        {
            OnPropertyChanged(nameof(AutoExcludedCount));
            OnPropertyChanged(nameof(HasAutoExcluded));
            OnPropertyChanged(nameof(RemoveAutoExcludedText));
            OnPropertyChanged(nameof(ShowOverlapBlock));
        }

        private void ClearAutoExcludeReport()
        {
            AutoExcludeReport = "";
            AutoExcludeNotes.Clear();
            AutoExcludeProblems.Clear();
            OnPropertyChanged(nameof(HasAutoExcludeReport));
            OnPropertyChanged(nameof(ShowOverlapBlock));
        }
    }

    // A resolved group's member keys. A named type rather than a bare HashSet so the two places that
    // fill the cache cannot swap the arguments.
    public readonly struct NpcGroupResolutionKeys
    {
        public NpcGroupResolutionKeys(IEnumerable<string> keys)
            => Keys = new HashSet<string>(keys, StringComparer.OrdinalIgnoreCase);

        public HashSet<string> Keys { get; }
    }

    // One other group that reaches some of the same NPCs.
    public sealed class NpcGroupOverlapRowVM
    {
        public NpcGroupOverlapRowVM(
            NpcGroup other, int shared, int mine, IReadOnlyList<string> collidingFields, Action<NpcGroup> select)
        {
            Name = other.Name;
            Shared = shared;

            // Against THIS group, not against the other one: the question the screen is answering is
            // "how much of what I am editing is somebody else's too".
            Share = mine <= 0 ? "" : $"{100.0 * shared / mine:0.#} % of this group";

            HasCollision = collidingFields.Count > 0;
            CollisionText = HasCollision
                ? $"both groups set {string.Join(", ", collidingFields)} - only one of the two reaches the game"
                : "";

            ShowCommand = new RelayCommand(() => select(other));
        }

        public string Name { get; }
        public int Shared { get; }
        public string Share { get; }
        public bool HasCollision { get; }
        public string CollisionText { get; }

        public ICommand ShowCommand { get; }
    }
}
