using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using SkyrimCraftingTool.Model;
using SkyrimCraftingTool.Services;

namespace SkyrimCraftingTool.ViewModel
{
    // Perks on the group (docs/NPC-Gruppen-Plan.md section 6, the "Perks 3 gemeinsam + [ ]" row of the
    // sketch).
    //
    // The back end was finished and unreachable: NpcGroupList has carried perks, spells, items,
    // factions and keywords since G2, NpcGroupStore reads and writes them, and
    // NpcGroupRuleBuilder.AppendLists turns them into perksToAdd. There was no control to make one.
    //
    // WHAT MAKES THIS ROW WORTH LOOKING AT IS THE SAME THING AS THE VALUES TABLE: what the members
    // ALREADY have. "Adds Juggernaut - 171 of 177 members already have it" is a line that saves the
    // edit; without the count it reads as a change and is not one. The values table earned this screen
    // its keep with the "is" column, and a perk list without one would be the same screen half built.
    //
    // ONLY ADDING. SkyPatcher has perksToAdd and no perksToRemove (docs/NPC_Patcher.txt), so a "remove"
    // entry could be stored and could never reach the game. Rather than offer it and drop it later, the
    // list offers what can be patched - and the rule builder now says so out loud if an entry from
    // somewhere else ever asks for a removal.
    public sealed partial class NpcGroupVM
    {
        public const string PerkKind = "perk";

        public ObservableCollection<NpcGroupPerkRowVM> Perks { get; } = new();

        public bool HasPerks => Perks.Count > 0;

        private void RebuildPerks(NpcGroup? group, List<NpcRecord> members)
        {
            Perks.Clear();

            if (group != null)
            {
                foreach (var entry in group.Lists
                             .Where(l => string.Equals(l.Kind, PerkKind, StringComparison.OrdinalIgnoreCase))
                             .OrderBy(l => _labels.Perk(l.TargetKey), StringComparer.OrdinalIgnoreCase))
                {
                    Perks.Add(new NpcGroupPerkRowVM(
                        entry, _labels.Perk(entry.TargetKey), CountCarrying(members, entry.TargetKey),
                        members.Count, RemovePerk));
                }
            }

            RebuildCommonPerks(members);
            RefreshPerkChoices();

            OnPropertyChanged(nameof(HasPerks));
        }

        // How many of the members carry this perk already, read off the effective list rather than the
        // scanned one: an edit made on the NPC tab counts, because that is what the record will hold.
        private static int CountCarrying(List<NpcRecord> members, string perkKey)
            => string.IsNullOrWhiteSpace(perkKey)
                ? 0
                : members.Count(m => (m.Perks ?? new List<NpcPerkRecord>())
                    .Any(p => string.Equals(p.PerkKey, perkKey, StringComparison.OrdinalIgnoreCase)));

        // ---- what the members already share -----------------------------------------------------

        // The sketch's "Perks 3 gemeinsam". A perk every member already carries is the group's own
        // baseline, and it is the thing worth knowing before adding one: a combat group whose members
        // all have Armsman already does not need it added.
        public ObservableCollection<string> CommonPerks { get; } = new();

        public bool HasCommonPerks => CommonPerks.Count > 0;

        private string _commonPerkSummary = "";
        public string CommonPerkSummary
        {
            get => _commonPerkSummary;
            private set => SetProperty(ref _commonPerkSummary, value);
        }

        // A cap, because the answer is a hint and not a list to read: the Dremora class group has 1.215
        // members and they share their whole perk set.
        private const int CommonPerkLimit = 12;

        private void RebuildCommonPerks(List<NpcRecord> members)
        {
            CommonPerks.Clear();

            if (members.Count == 0)
            {
                CommonPerkSummary = "";
                OnPropertyChanged(nameof(HasCommonPerks));
                return;
            }

            // Intersection over the members. Starting from the first member's list rather than from the
            // catalogue: the catalogue has thousands of perks and all but a handful would be discarded
            // on the first member anyway.
            var shared = new HashSet<string>(
                (members[0].Perks ?? new List<NpcPerkRecord>()).Select(p => p.PerkKey),
                StringComparer.OrdinalIgnoreCase);

            foreach (var member in members.Skip(1))
            {
                if (shared.Count == 0) break;

                shared.IntersectWith((member.Perks ?? new List<NpcPerkRecord>()).Select(p => p.PerkKey));
            }

            var named = shared
                .Select(k => _labels.Perk(k))
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var name in named.Take(CommonPerkLimit)) CommonPerks.Add(name);
            if (named.Count > CommonPerkLimit)
                CommonPerks.Add($"... and {named.Count - CommonPerkLimit} more");

            CommonPerkSummary = named.Count == 0
                ? "No perk is on every member."
                : $"{named.Count} perk(s) on every member already:";

            OnPropertyChanged(nameof(HasCommonPerks));
        }

        // ---- the picker ---------------------------------------------------------------------------

        private string _perkSearchText = "";
        public string PerkSearchText
        {
            get => _perkSearchText;
            set
            {
                if (!SetProperty(ref _perkSearchText, value ?? "")) return;
                OnPropertyChanged(nameof(PerkChoices));
            }
        }

        // The catalogue minus what the group already adds - an entry twice would be one perksToAdd
        // twice, and the primary key on NpcGroupList would collapse them on the next save anyway.
        public IEnumerable<FormIDRecord> PerkChoices
        {
            get
            {
                var taken = new HashSet<string>(
                    Perks.Select(p => p.Key), StringComparer.OrdinalIgnoreCase);

                return _labels.PerkChoices()
                    .Where(p => !taken.Contains(p.Key))
                    .Where(p => string.IsNullOrWhiteSpace(_perkSearchText)
                                || (p.Name ?? "").Contains(_perkSearchText, StringComparison.OrdinalIgnoreCase));
            }
        }

        private FormIDRecord? _selectedPerkToAdd;
        public FormIDRecord? SelectedPerkToAdd
        {
            get => _selectedPerkToAdd;
            set
            {
                // null arrives when the filter hides the selected row, which happens on every keystroke.
                // Same trap as every other filtering box on this screen.
                if (value == null) return;
                SetProperty(ref _selectedPerkToAdd, value);
            }
        }

        private RelayCommand? _addPerkCommand;
        public ICommand AddPerkCommand => _addPerkCommand ??= new RelayCommand(AddPerk);

        public void AddPerk()
        {
            var group = _selectedGroup?.Group;
            if (group == null || _selectedPerkToAdd == null) return;

            var key = _selectedPerkToAdd.Key;
            if (string.IsNullOrWhiteSpace(key)) return;

            if (group.Lists.Any(l => string.Equals(l.Kind, PerkKind, StringComparison.OrdinalIgnoreCase)
                                     && string.Equals(l.TargetKey, key, StringComparison.OrdinalIgnoreCase)))
                return;

            group.Lists.Add(new NpcGroupList
            {
                Kind = PerkKind,
                TargetKey = key,
                Mode = NpcGroupListMode.Add,
            });

            Persist(group);

            // Membership does not change - a perk does not move an NPC in or out of the group - so only
            // the parts that read the group are rebuilt, and the rule preview among them.
            RefreshPerks();
        }

        private void RemovePerk(NpcGroupPerkRowVM row)
        {
            var group = _selectedGroup?.Group;
            if (group == null || row == null) return;

            int gone = group.Lists.RemoveAll(l =>
                string.Equals(l.Kind, PerkKind, StringComparison.OrdinalIgnoreCase)
                && string.Equals(l.TargetKey, row.Key, StringComparison.OrdinalIgnoreCase));

            if (gone == 0) return;

            Persist(group);
            RefreshPerks();
        }

        // Cheaper than RefreshDetailBody and enough: the members are the same NPCs, so the spread, the
        // overlap and the drift all still hold. Only what the group SAYS has changed.
        private void RefreshPerks()
        {
            var group = _selectedGroup?.Group;

            RebuildPerks(group, _currentMembers);
            RebuildDefinition(group);
            RebuildPreview(group, _currentMembers);
        }

        private void RefreshPerkChoices()
        {
            _selectedPerkToAdd = null;
            OnPropertyChanged(nameof(SelectedPerkToAdd));
            OnPropertyChanged(nameof(PerkChoices));
        }
    }

    // One perk the group adds.
    public sealed class NpcGroupPerkRowVM
    {
        public NpcGroupPerkRowVM(
            NpcGroupList entry, string name, int carrying, int members, Action<NpcGroupPerkRowVM> remove)
        {
            Key = entry.TargetKey;
            Name = name;
            Carrying = carrying;
            Members = members;

            // THE COLUMN THAT MAKES THE ROW WORTH READING, the same idea as the "is" column in the
            // values table: an edit that changes nothing has to look different from one that does.
            Have = members == 0
                ? ""
                : carrying == 0
                    ? "none has it"
                    : carrying >= members
                        ? "every member already has it"
                        : $"{carrying} of {members} already have it";

            RemoveCommand = new RelayCommand(() => remove(this));
        }

        public string Key { get; }
        public string Name { get; }
        public int Carrying { get; }
        public int Members { get; }
        public string Have { get; }

        // A perk every member already carries is a rule line that reaches the game and does nothing.
        // Not an error - the group may be meant to hold the baseline - so it is marked, not refused.
        public bool IsRedundant => Members > 0 && Carrying >= Members;

        public ICommand RemoveCommand { get; }
    }
}
