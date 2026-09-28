using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using System;
using SkyrimCraftingTool.Model;
using SkyrimCraftingTool.Services;

namespace SkyrimCraftingTool.ViewModel
{
    // The NPC tab. Prio 8 / N-P1 — see docs/NPC-Plan.md.
    //
    // The tree is Plugin -> Class -> NPC, and the factions are deliberately NOT a level in it: a
    // faction does not belong to a plugin (BanditFaction is defined in Skyrim.esm but reaches 613
    // NPCs across several), so nesting it under one made the node count differ from what a rule on
    // it would patch - 405 shown against 613 patched. Plugin -> Class gives every NPC exactly one
    // home: measured on the real load order that is 14 plugin nodes, 324 class nodes and 6.580
    // leaves with zero duplicates, against 1.226 + 3.293 nodes and 16.213 leaves the other way
    // round. See NPC-Plan.md §8.
    //
    // Factions had a view of their own until 2026-09-17 and are now a filter on this tree (§12) -
    // the lever for a bulk edit turned out to be the template, not the faction.
    //
    // This view model also backs the Templates tab: a template is an NPC record, so both tabs share
    // one instance and one editor rather than keeping two copies of the same records (§12).
    public class NpcMenuVM : ViewModelBase
    {
        private readonly Services.IItemService? _items;
        private readonly Services.NpcEditStore? _edits;

        // A function rather than the list itself: the keyword catalogue is loaded by the item tab,
        // and on the first NPC visit it may or may not be ready yet.
        private readonly Func<List<FormIDRecord>>? _keywords;
        private List<NpcTreeNodeVM> _allRoots = new();
        private bool _loaded;

        public NpcMenuVM(Services.IItemService? items = null, Services.NpcEditStore? edits = null,
                         Func<List<FormIDRecord>>? keywords = null)
        {
            _items = items;
            _edits = edits;
            _keywords = keywords;

            // Reset is per NPC and asks first: it drops every edit on that record at once, and there
            // is no undo behind it.
            ResetNpcCommand = new RelayCommand(() =>
            {
                var npc = SelectedNpc;
                if (npc == null || !npc.HasAnyEdit) return;

                var answer = System.Windows.MessageBox.Show(
                    $"Discard every change to '{npc.DisplayName}' and go back to what the plugins say?",
                    "Reset NPC", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question);
                if (answer != System.Windows.MessageBoxResult.Yes) return;

                _edits?.ResetNpc(npc.Key);
                npc.ResetToScanned();

                ApplyFilter();
                OnPropertyChanged(nameof(EditedCount));
            });

            // "(none)" is only offered where the patcher can actually clear the field.
            static List<FormIDRecord> WithNone(List<FormIDRecord> list) =>
                new[] { new FormIDRecord { Key = "", Name = "(none)" } }.Concat(list).ToList();

            ClassPicker = new NpcLinkPickerVM("Class", () => AllClasses,
                n => n?.ClassKey ?? "", (n, k) => n.ClassKey = k, n => n?.IsClassChanged ?? false);
            RacePicker = new NpcLinkPickerVM("Race", () => AllRaces,
                n => n?.RaceKey ?? "", (n, k) => n.RaceKey = k, n => n?.IsRaceChanged ?? false);
            VoicePicker = new NpcLinkPickerVM("Voice", () => AllVoiceTypes,
                n => n?.VoiceKey ?? "", (n, k) => n.VoiceKey = k, n => n?.IsVoiceChanged ?? false);
            OutfitPicker = new NpcLinkPickerVM("Outfit", () => AllOutfits,
                n => n?.DefaultOutfitKey ?? "", (n, k) => n.DefaultOutfitKey = k, n => n?.IsDefaultOutfitChanged ?? false);
            SleepOutfitPicker = new NpcLinkPickerVM("Sleep outfit", () => AllOutfits,
                n => n?.SleepOutfitKey ?? "", (n, k) => n.SleepOutfitKey = k, n => n?.IsSleepOutfitChanged ?? false);
            DeathItemPicker = new NpcLinkPickerVM("Death item", () => WithNone(AllLeveledItems),
                n => n?.DeathItemKey ?? "", (n, k) => n.DeathItemKey = k, n => n?.IsDeathItemChanged ?? false,
                clearable: true);
            SkinPicker = new NpcLinkPickerVM("Skin", () => WithNone(AllArmors),
                n => n?.SkinKey ?? "", (n, k) => n.SkinKey = k, n => n?.IsSkinChanged ?? false,
                clearable: true);

            // Both clearable: an ESP override can simply write a null link, which is the one thing
            // it can do that no documented operation offers.
            CombatStylePicker = new NpcLinkPickerVM("Combat style", () => WithNone(AllCombatStyles),
                n => n?.CombatStyleKey ?? "", (n, k) => n.CombatStyleKey = k,
                n => n?.IsCombatStyleChanged ?? false, clearable: true);
            CrimeFactionPicker = new NpcLinkPickerVM("Crime faction", () => WithNone(AllFactions),
                n => n?.CrimeFactionKey ?? "", (n, k) => n.CrimeFactionKey = k,
                n => n?.IsCrimeFactionChanged ?? false, clearable: true);

            BulkSetCommand = new RelayCommand(() =>
            {
                if (!double.TryParse(BulkValue, System.Globalization.NumberStyles.Any,
                                     System.Globalization.CultureInfo.CurrentCulture, out double value)
                    && !double.TryParse(BulkValue, System.Globalization.NumberStyles.Any,
                                        System.Globalization.CultureInfo.InvariantCulture, out value))
                {
                    BulkStatus = $"'{BulkValue}' is not a number.";
                    return;
                }

                ApplyToSelection($"Set {BulkField} to {BulkValue}", npc =>
                {
                    switch (BulkField)
                    {
                        case "Health": npc.Health = (int)value; break;
                        case "Magicka": npc.Magicka = (int)value; break;
                        case "Stamina": npc.Stamina = (int)value; break;
                        case "Level": npc.Level = (int)value; break;
                        case "CalcMinLevel": npc.CalcMinLevel = (int)value; break;
                        case "CalcMaxLevel": npc.CalcMaxLevel = (int)value; break;
                        case "Weight": npc.Weight = (float)value; break;
                        case "Height": npc.Height = (float)value; break;
                    }
                });
            });

            // The list additions. Whichever picker currently holds something is what gets added -
            // one button rather than four, because the pickers sit next to each other and only one
            // of them is ever the answer.
            BulkAddCommand = new RelayCommand(() =>
            {
                if (SelectedKeywordToAdd != null)
                {
                    var key = SelectedKeywordToAdd.Key;
                    ApplyToSelection($"Add keyword {SelectedKeywordToAdd.Name}", npc =>
                    {
                        if (!npc.Keywords.Any(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase)))
                            npc.ToggleKeyword(key);
                    });
                    return;
                }

                if (SelectedFactionToAdd != null)
                {
                    var key = SelectedFactionToAdd.Key;
                    ApplyToSelection($"Add faction {SelectedFactionToAdd.Name}", npc => npc.AddFaction(key));
                    return;
                }

                if (SelectedSpellToAdd != null)
                {
                    var choice = SelectedSpellToAdd;
                    ApplyToSelection($"Add {choice.Name}", npc => npc.AddSpell(choice.Key, choice.Kind));
                    return;
                }

                if (SelectedPerkToAdd != null)
                {
                    var key = SelectedPerkToAdd.Key;
                    ApplyToSelection($"Add perk {SelectedPerkToAdd.Name}", npc => npc.AddPerk(key));
                    return;
                }

                if (SelectedItemToAdd != null)
                {
                    var key = SelectedItemToAdd.Key;
                    int count = Math.Max(1, ItemCountToAdd);
                    ApplyToSelection($"Add {count}x {SelectedItemToAdd.Name}", npc => npc.AddItem(key, count));
                    return;
                }

                BulkStatus = "Pick a keyword, faction, spell, perk or item first.";
            });

            AddSpellCommand = new RelayCommand(() =>
            {
                if (SelectedNpc == null || SelectedSpellToAdd == null) return;

                SelectedNpc.AddSpell(SelectedSpellToAdd.Key, SelectedSpellToAdd.Kind);
                AfterListEdit();
            });

            RemoveSpellCommand = new RelayCommand<NpcSpellRowVM>(row =>
            {
                if (SelectedNpc == null || row == null) return;
                SelectedNpc.RemoveSpell(row);
                AfterListEdit();
            });

            AddPerkCommand = new RelayCommand(() =>
            {
                if (SelectedNpc == null || SelectedPerkToAdd == null) return;

                SelectedNpc.AddPerk(SelectedPerkToAdd.Key);
                AfterListEdit();
            });

            RemovePerkCommand = new RelayCommand<NpcPerkRowVM>(row =>
            {
                if (SelectedNpc == null || row == null) return;
                SelectedNpc.RemovePerk(row);
                AfterListEdit();
            });

            AddItemCommand = new RelayCommand(() =>
            {
                if (SelectedNpc == null || SelectedItemToAdd == null) return;

                SelectedNpc.AddItem(SelectedItemToAdd.Key, Math.Max(1, ItemCountToAdd));
                AfterListEdit();
            });

            RemoveItemCommand = new RelayCommand<NpcItemRowVM>(row =>
            {
                if (SelectedNpc == null || row == null) return;
                SelectedNpc.RemoveItem(row);
                AfterListEdit();
            });

            AddFactionCommand = new RelayCommand(() =>
            {
                if (SelectedNpc == null || SelectedFactionToAdd == null) return;

                SelectedNpc.AddFaction(SelectedFactionToAdd.Key);
                AfterListEdit();
            });

            RemoveFactionCommand = new RelayCommand<NpcFactionRowVM>(row =>
            {
                if (SelectedNpc == null || row == null) return;

                SelectedNpc.RemoveFaction(row);
                AfterListEdit();
            });

            AddKeywordCommand = new RelayCommand(() =>
            {
                if (SelectedNpc == null || SelectedKeywordToAdd == null) return;

                // Toggle, not add: picking one the NPC already has would otherwise remove it, which
                // is not what an "Add" button should ever do.
                if (SelectedNpc.Keywords.Any(k =>
                        string.Equals(k, SelectedKeywordToAdd.Key, StringComparison.OrdinalIgnoreCase)))
                {
                    return;
                }

                SelectedNpc.ToggleKeyword(SelectedKeywordToAdd.Key);
                AfterListEdit();
            });

            ToggleKeywordCommand = new RelayCommand<string>(key =>
            {
                if (SelectedNpc == null || string.IsNullOrWhiteSpace(key)) return;

                SelectedNpc.ToggleKeyword(key);
                AfterListEdit();
            });

            CollapseAllCommand = new RelayCommand(() =>
            {
                foreach (var root in FilteredTree) root.CollapseAll();
            });
        }

        // --------------------
        // The two list pickers (N-P3)
        // --------------------
        //
        // The catalogues live here rather than on each NPC: 1.458 factions and the whole keyword list
        // would otherwise be held 6.580 times over. The commands are here for the same reason the
        // reset is - they act on the selected NPC, and the rows in the detail pane reach them
        // through the view's DataContext.

        public List<FormIDRecord> AllFactions { get; private set; } = new();
        public List<FormIDRecord> AllKeywords { get; private set; } = new();

        private string _factionSearchText = "";
        public string FactionSearchText
        {
            get => _factionSearchText;
            set
            {
                if (SetProperty(ref _factionSearchText, value ?? ""))
                    OnPropertyChanged(nameof(FilteredFactions));
            }
        }

        public IEnumerable<FormIDRecord> FilteredFactions =>
            AllFactions.Where(f => string.IsNullOrWhiteSpace(FactionSearchText)
                                || f.Name.Contains(FactionSearchText, StringComparison.OrdinalIgnoreCase));

        private FormIDRecord? _selectedFactionToAdd;
        public FormIDRecord? SelectedFactionToAdd
        {
            get => _selectedFactionToAdd;
            // null arrives when the filter hides the selection - the same trap the enchantment
            // picker has, and the same answer: ignore it rather than clearing the choice mid-type.
            set { if (value != null) SetProperty(ref _selectedFactionToAdd, value); }
        }

        private string _keywordSearchText = "";
        public string KeywordSearchText
        {
            get => _keywordSearchText;
            set
            {
                if (SetProperty(ref _keywordSearchText, value ?? ""))
                    OnPropertyChanged(nameof(FilteredKeywords));
            }
        }

        public IEnumerable<FormIDRecord> FilteredKeywords =>
            AllKeywords.Where(k => string.IsNullOrWhiteSpace(KeywordSearchText)
                                || k.Name.Contains(KeywordSearchText, StringComparison.OrdinalIgnoreCase));

        private FormIDRecord? _selectedKeywordToAdd;
        public FormIDRecord? SelectedKeywordToAdd
        {
            get => _selectedKeywordToAdd;
            set { if (value != null) SetProperty(ref _selectedKeywordToAdd, value); }
        }

        // The selected NPC's keywords as chips - key plus a readable name, which the NPC itself
        // cannot produce because it only holds keys.
        public IEnumerable<FormIDRecord> SelectedNpcKeywords =>
            (SelectedNpc?.Keywords ?? new List<string>())
                .Select(k => new FormIDRecord
                {
                    Key = k,
                    Name = AllKeywords.FirstOrDefault(c =>
                        string.Equals(c.Key, k, StringComparison.OrdinalIgnoreCase))?.Name ?? k,
                })
                .OrderBy(k => k.Name, StringComparer.OrdinalIgnoreCase);

        // --------------------
        // The link pickers (N-P4)
        // --------------------
        //
        // Built once and pointed at whichever NPC is selected, so the catalogues are held once
        // rather than 6.580 times. Only deathItem and skin are clearable: those are the two
        // SkyPatcher documents "null" for.

        public NpcLinkPickerVM ClassPicker { get; }
        public NpcLinkPickerVM RacePicker { get; }
        public NpcLinkPickerVM VoicePicker { get; }
        public NpcLinkPickerVM OutfitPicker { get; }
        public NpcLinkPickerVM SleepOutfitPicker { get; }
        public NpcLinkPickerVM DeathItemPicker { get; }
        public NpcLinkPickerVM SkinPicker { get; }

        // N-P6: no rule can carry these two, so picking one writes an ESP override of the record.
        public NpcLinkPickerVM CombatStylePicker { get; }
        public NpcLinkPickerVM CrimeFactionPicker { get; }

        private IEnumerable<NpcLinkPickerVM> Pickers
        {
            get
            {
                yield return ClassPicker;
                yield return RacePicker;
                yield return VoicePicker;
                yield return OutfitPicker;
                yield return SleepOutfitPicker;
                yield return DeathItemPicker;
                yield return SkinPicker;
                yield return CombatStylePicker;
                yield return CrimeFactionPicker;
            }
        }

        public List<FormIDRecord> AllClasses { get; private set; } = new();
        public List<FormIDRecord> AllRaces { get; private set; } = new();
        public List<FormIDRecord> AllVoiceTypes { get; private set; } = new();
        public List<FormIDRecord> AllOutfits { get; private set; } = new();
        public List<FormIDRecord> AllLeveledItems { get; private set; } = new();
        public List<FormIDRecord> AllArmors { get; private set; } = new();
        public List<FormIDRecord> AllCombatStyles { get; private set; } = new();

        // The AI token lists, straight from the vocabulary so the screen can only offer what the
        // patch string accepts.
        public IReadOnlyList<string> AggressionTokens => NpcAiTokens.Aggression;
        public IReadOnlyList<string> ConfidenceTokens => NpcAiTokens.Confidence;
        public IReadOnlyList<string> AssistanceTokens => NpcAiTokens.Assistance;
        public IReadOnlyList<string> MoralityTokens => NpcAiTokens.Morality;
        public IReadOnlyList<string> MoodTokens => NpcAiTokens.Mood;

        // --------------------
        // The pickers for spells, perks and inventory (N-P3)
        // --------------------
        //
        // Spells need a kind alongside the key: the three catalogues become one list to pick from,
        // and each entry remembers which of SkyPatcher's three operations will carry it.
        public sealed class SpellChoice
        {
            public string Key { get; init; } = "";
            public string Name { get; init; } = "";
            public string Kind { get; init; } = "";
        }

        public List<SpellChoice> AllSpellChoices { get; private set; } = new();
        public List<FormIDRecord> AllPerkChoices { get; private set; } = new();
        public List<FormIDRecord> AllItemChoices { get; private set; } = new();

        private string _spellSearchText = "";
        public string SpellSearchText
        {
            get => _spellSearchText;
            set { if (SetProperty(ref _spellSearchText, value ?? "")) OnPropertyChanged(nameof(FilteredSpells)); }
        }

        public IEnumerable<SpellChoice> FilteredSpells =>
            AllSpellChoices.Where(s => string.IsNullOrWhiteSpace(SpellSearchText)
                                    || s.Name.Contains(SpellSearchText, StringComparison.OrdinalIgnoreCase));

        private SpellChoice? _selectedSpellToAdd;
        public SpellChoice? SelectedSpellToAdd
        {
            get => _selectedSpellToAdd;
            set { if (value != null) SetProperty(ref _selectedSpellToAdd, value); }
        }

        private string _perkSearchText = "";
        public string PerkSearchText
        {
            get => _perkSearchText;
            set { if (SetProperty(ref _perkSearchText, value ?? "")) OnPropertyChanged(nameof(FilteredPerks)); }
        }

        public IEnumerable<FormIDRecord> FilteredPerks =>
            AllPerkChoices.Where(p => string.IsNullOrWhiteSpace(PerkSearchText)
                                   || p.Name.Contains(PerkSearchText, StringComparison.OrdinalIgnoreCase));

        private FormIDRecord? _selectedPerkToAdd;
        public FormIDRecord? SelectedPerkToAdd
        {
            get => _selectedPerkToAdd;
            set { if (value != null) SetProperty(ref _selectedPerkToAdd, value); }
        }

        private string _itemSearchText = "";
        public string ItemSearchText
        {
            get => _itemSearchText;
            set { if (SetProperty(ref _itemSearchText, value ?? "")) OnPropertyChanged(nameof(FilteredItems)); }
        }

        public IEnumerable<FormIDRecord> FilteredItems =>
            AllItemChoices.Where(i => string.IsNullOrWhiteSpace(ItemSearchText)
                                   || i.Name.Contains(ItemSearchText, StringComparison.OrdinalIgnoreCase));

        private FormIDRecord? _selectedItemToAdd;
        public FormIDRecord? SelectedItemToAdd
        {
            get => _selectedItemToAdd;
            set { if (value != null) SetProperty(ref _selectedItemToAdd, value); }
        }

        // How many of the picked item to give. One is the ordinary answer and the box starts there.
        private int _itemCountToAdd = 1;
        public int ItemCountToAdd
        {
            get => _itemCountToAdd;
            set => SetProperty(ref _itemCountToAdd, value);
        }

        public ICommand AddSpellCommand { get; }
        public ICommand RemoveSpellCommand { get; }
        public ICommand AddPerkCommand { get; }
        public ICommand RemovePerkCommand { get; }
        public ICommand AddItemCommand { get; }
        public ICommand RemoveItemCommand { get; }

        public ICommand AddFactionCommand { get; }
        public ICommand RemoveFactionCommand { get; }
        public ICommand AddKeywordCommand { get; }
        public ICommand ToggleKeywordCommand { get; }

        private void AfterListEdit()
        {
            OnPropertyChanged(nameof(SelectedNpcKeywords));
            OnPropertyChanged(nameof(EditedCount));
            ApplyFilter();
        }

        // --------------------
        // Multi-selection (N-P5)
        // --------------------
        //
        // The whole point of the tab: not editing 6.580 NPCs one at a time. Selecting several and
        // setting one value writes the same edit to each of them through the ordinary store, and the
        // generator then merges the identical rules into one filterByNpcs line (NPC-Plan.md §10).
        //
        // Each NPC keeps its own edit rather than the selection being a thing in its own right: an
        // NPC edited in a bulk pass can then be corrected on its own, reset on its own, and shows
        // its own badge - none of which would be true of a selection-shaped edit.

        public ObservableCollection<NpcNodeVM> SelectedNpcs { get; } = new();

        public bool IsMultiSelectActive => SelectedNpcs.Count > 1;

        public string MultiSelectSummary => $"{SelectedNpcs.Count} NPCs selected";

        // Called by the view on a Ctrl-click. A plain click still selects one, the way it did.
        public void ToggleSelection(NpcNodeVM npc)
        {
            if (npc == null) return;

            if (SelectedNpcs.Contains(npc)) SelectedNpcs.Remove(npc);
            else SelectedNpcs.Add(npc);

            // The bulk panel takes the place of the detail pane, so the record that was open has to
            // let go - otherwise both are on screen and it is not clear which one an edit reaches.
            // Dropping back to one selected NPC opens that one instead.
            SelectedNpc = SelectedNpcs.Count == 1 ? SelectedNpcs[0] : null;

            OnPropertyChanged(nameof(IsMultiSelectActive));
            OnPropertyChanged(nameof(MultiSelectSummary));
        }

        // Selecting a whole branch is the natural way to reach "every bandit": one click on the
        // class node instead of 389 Ctrl-clicks.
        public void SelectBranch(NpcTreeNodeVM branch)
        {
            if (branch == null) return;

            SelectedNpcs.Clear();
            foreach (var npc in Leaves(branch)) SelectedNpcs.Add(npc);

            SelectedNpc = null;
            OnPropertyChanged(nameof(IsMultiSelectActive));
            OnPropertyChanged(nameof(MultiSelectSummary));
        }

        private static IEnumerable<NpcNodeVM> Leaves(NpcTreeNodeVM node) =>
            node.Npcs.Concat(node.Children.SelectMany(Leaves));

        public void ClearSelection()
        {
            SelectedNpcs.Clear();
            OnPropertyChanged(nameof(IsMultiSelectActive));
            OnPropertyChanged(nameof(MultiSelectSummary));
        }

        // What a bulk pass can set. Deliberately the single-value fields and the list ADDITIONS
        // only: "give every bandit this spell" is a sentence, "set every bandit's spell list to
        // exactly this" is a way to wipe out what made them different from each other.
        public ICommand BulkSetCommand { get; }
        public ICommand BulkAddCommand { get; }

        private string _bulkField = "Health";
        public string BulkField
        {
            get => _bulkField;
            set => SetProperty(ref _bulkField, value ?? "Health");
        }

        public IReadOnlyList<string> BulkFields { get; } = new[]
        {
            "Health", "Magicka", "Stamina", "Level", "CalcMinLevel", "CalcMaxLevel",
            "Weight", "Height",
        };

        private string _bulkValue = "";
        public string BulkValue
        {
            get => _bulkValue;
            set => SetProperty(ref _bulkValue, value ?? "");
        }

        private string _bulkStatus = "";
        public string BulkStatus
        {
            get => _bulkStatus;
            private set => SetProperty(ref _bulkStatus, value);
        }

        private void ApplyToSelection(string what, Action<NpcNodeVM> apply)
        {
            var targets = SelectedNpcs.ToList();
            if (targets.Count == 0) return;

            // Asked first, because there is no undo for a bulk pass beyond resetting each NPC - and
            // the count is the only warning that says how big this is.
            var answer = System.Windows.MessageBox.Show(
                $"{what} on all {targets.Count} selected NPCs?",
                "Bulk edit", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question);
            if (answer != System.Windows.MessageBoxResult.Yes) return;

            foreach (var npc in targets) apply(npc);

            BulkStatus = $"{what} on {targets.Count} NPCs.";
            ApplyFilter();
            OnPropertyChanged(nameof(EditedCount));
        }

        public ObservableCollection<NpcTreeNodeVM> FilteredTree { get; } = new();

        public ICommand CollapseAllCommand { get; }
        public ICommand ResetNpcCommand { get; }

        // Reads the NPC tables the first time the tab is actually opened, and not before: they are
        // the biggest block in the database and a session may never look at them. A rescan calls
        // Invalidate() below, so the next visit re-reads.
        public void EnsureLoaded()
        {
            if (_loaded || _items == null) return;
            _loaded = true;

            var labels = _items.GetNpcLabels();

            // The two catalogues the list pickers offer. Factions come from the NPC catalogue built
            // by the scan; keywords come from the same list the item editor uses, so both tabs
            // always offer exactly the same set.
            AllFactions = labels.Factions
                .Select(kv => new FormIDRecord { Key = kv.Key, Name = kv.Value })
                .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            AllKeywords = _keywords?.Invoke()?
                .OrderBy(k => k.Name, StringComparer.OrdinalIgnoreCase)
                .ToList() ?? new List<FormIDRecord>();

            List<FormIDRecord> Catalogue(Dictionary<string, string> from) =>
                from.Select(kv => new FormIDRecord { Key = kv.Key, Name = kv.Value })
                    .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();

            AllClasses = Catalogue(labels.Classes);
            AllRaces = Catalogue(labels.Races);
            AllVoiceTypes = Catalogue(labels.VoiceTypes);
            AllOutfits = Catalogue(labels.Outfits);
            AllLeveledItems = Catalogue(labels.LeveledItems);
            AllArmors = Catalogue(labels.Armors);
            AllCombatStyles = Catalogue(labels.CombatStyles);

            // The three catalogues become one spell list, each entry remembering which operation
            // will carry it - that is the whole reason the scan records a kind.
            AllSpellChoices =
                labels.Spells.Select(kv => new SpellChoice { Key = kv.Key, Name = kv.Value, Kind = "spell" })
                .Concat(labels.Shouts.Select(kv => new SpellChoice { Key = kv.Key, Name = kv.Value, Kind = "shout" }))
                .Concat(labels.LeveledSpells.Select(kv => new SpellChoice { Key = kv.Key, Name = kv.Value, Kind = "levspell" }))
                .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            AllPerkChoices = Catalogue(labels.Perks);

            // An inventory line can point at anything. Armour, weapons and leveled lists are what
            // this database knows by name; anything else would show as its key, which is not a
            // picker anyone can use, so it is not offered.
            AllItemChoices = Catalogue(labels.Armors)
                .Concat(Catalogue(labels.Weapons))
                .Concat(Catalogue(labels.LeveledItems))
                .OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            Load(_items.GetAllNpcs().Select(r => new NpcNodeVM(r, labels)));
        }

        // Called after a scan. Does not re-read on the spot - the user may not be on this tab, and
        // reading 6.580 records to throw them away again would just make every scan slower.
        public void Invalidate()
        {
            _loaded = false;
            _allRoots = new List<NpcTreeNodeVM>();
            NpcCount = 0;
            SelectedNpc = null;

            ApplyFilter();
            OnPropertyChanged(nameof(HasData));
            OnPropertyChanged(nameof(PluginCount));
        }

        // Hands the tab a fresh set of NPCs. Rebuilds the tree and drops the selection, because the
        // selected NPC may not exist any more after a rescan.
        public void Load(IEnumerable<NpcNodeVM> npcs)
        {
            var list = npcs?.ToList() ?? new List<NpcNodeVM>();

            // Every NPC saves through this tab rather than holding a service of its own: the view
            // models are built by the thousand and most are never touched.
            foreach (var npc in list)
            {
                npc.FieldChanged = (n, column, value) => _edits?.SaveField(n.Key, column, value);
                npc.SkillChanged = (n, skill, value) => _edits?.SaveSkill(n.Key, skill, value);
                npc.FactionsChanged = (n, factions) => _edits?.SaveFactions(n.Key, factions);
                npc.KeywordsChanged = (n, keywords) =>
                    _edits?.SaveField(n.Key, "Keywords", string.Join(",", keywords));
                npc.SpellsChanged = (n, spells) => _edits?.SaveSpells(n.Key, spells);
                npc.PerksChanged = (n, perks) => _edits?.SavePerks(n.Key, perks);
                npc.ItemsChanged = (n, items) => _edits?.SaveItems(n.Key, items);
            }

            _allRoots = NpcTreeNodeVM.Build(list);
            BuildTemplateIndex(list);

            NpcCount = _allRoots.Sum(r => r.NpcCount);
            SelectedNpc = null;

            ApplyFilter();
            OnPropertyChanged(nameof(HasData));
            OnPropertyChanged(nameof(PluginCount));
        }

        private string _searchText = "";
        public string SearchText
        {
            get => _searchText;
            set
            {
                if (SetProperty(ref _searchText, value ?? ""))
                    ApplyFilter();
            }
        }

        private bool _showOnlyEdited;
        public bool ShowOnlyEdited
        {
            get => _showOnlyEdited;
            set
            {
                if (SetProperty(ref _showOnlyEdited, value))
                    ApplyFilter();
            }
        }

        // --------------------
        // Templates
        // --------------------
        //
        // WHY THEY LIVE HERE AND NOT IN A VIEW OF THEIR OWN (docs/NPC-Plan.md §12): a template IS an
        // NPC record. 1.073 of the 1.372 templates in the load order are ordinary NPCs this tab
        // already holds, so the Templates tab shows the same view models over the same editor - an
        // edit made in one tab is the edit the other one shows, with no second copy to drift.
        //
        // The first cut of this was an inheritance TREE, which the user could not use: what is
        // wanted is all of them, listed and editable, plus a way to get from an NPC to the record
        // that actually decides its values.
        private readonly Dictionary<string, NpcNodeVM> _byKey = new(StringComparer.OrdinalIgnoreCase);

        // Every NPC that something inherits from, biggest first - the number is why anyone opens
        // one: EncBandit00Template feeds 502 NPCs, most templates feed one or two.
        public ObservableCollection<NpcNodeVM> Templates { get; } = new();

        private List<NpcNodeVM> _allTemplates = new();

        private void BuildTemplateIndex(List<NpcNodeVM> all)
        {
            _byKey.Clear();
            foreach (var npc in all) _byKey[npc.Key] = npc;

            // Direct heirs only. A chain (3.073 NPCs sit two or more hops from their source) still
            // resolves one link at a time through the jump below, which is how someone actually
            // walks it - counting the whole subtree here would promise that editing this record
            // moves NPCs that inherit from something further down instead.
            var heirs = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            // Targets that are not NPC records at all - levelled character lists, 299 of them in the
            // real load order. Collected in the same pass rather than in a property, because a
            // property would walk every NPC again each time the summary line is read.
            var unscanned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var npc in all)
            {
                if (string.IsNullOrWhiteSpace(npc.TemplateKey)) continue;
                if (string.Equals(npc.TemplateKey, npc.Key, StringComparison.OrdinalIgnoreCase)) continue;

                heirs[npc.TemplateKey] = heirs.TryGetValue(npc.TemplateKey, out var n) ? n + 1 : 1;

                if (!_byKey.ContainsKey(npc.TemplateKey)) unscanned.Add(npc.TemplateKey);
            }

            UnscannedTemplateCount = unscanned.Count;

            foreach (var npc in all)
                npc.HeirCount = heirs.TryGetValue(npc.Key, out var count) ? count : 0;

            _allTemplates = all
                .Where(n => n.HeirCount > 0)
                .OrderByDescending(n => n.HeirCount)
                .ThenBy(n => n.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            ApplyTemplateFilter();
            OnPropertyChanged(nameof(TemplateCount));
        }

        public int TemplateCount => _allTemplates.Count;

        // Targets that are not NPC records at all - levelled character lists, 299 of them in the
        // real load order. They cannot be listed or edited here, and saying how many there are
        // beats letting someone wonder why a template they know is missing.
        // Counted once when the list is built, not on every read: this walked all 6.580 NPCs, and
        // TemplateSummary asked for it twice per keystroke in the filter box.
        public int UnscannedTemplateCount { get; private set; }

        private string _templateSearchFilter = "";
        public string TemplateSearchFilter
        {
            get => _templateSearchFilter;
            set { if (SetProperty(ref _templateSearchFilter, value ?? "")) ApplyTemplateFilter(); }
        }

        private void ApplyTemplateFilter()
        {
            Templates.Clear();

            foreach (var npc in _allTemplates)
            {
                if (!string.IsNullOrWhiteSpace(TemplateSearchFilter)
                    && !npc.DisplayName.Contains(TemplateSearchFilter, StringComparison.OrdinalIgnoreCase)
                    && !npc.EditorID.Contains(TemplateSearchFilter, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                Templates.Add(npc);
            }

            OnPropertyChanged(nameof(VisibleTemplateCount));
            OnPropertyChanged(nameof(TemplateSummary));
        }

        public int VisibleTemplateCount => Templates.Count;

        public string TemplateSummary =>
            TemplateCount == 0
                ? "No template data. This list is built from the NPC tables - run a rescan to fill them."
                : $"{VisibleTemplateCount} of {TemplateCount} templates"
                + (UnscannedTemplateCount > 0
                    ? $" · {UnscannedTemplateCount} more targets are levelled lists, which are not records this tool reads"
                    : "");

        // Raised when the template link is followed, so the shell can bring the Templates tab up.
        // Selecting alone was not enough: the editor changed while the NPC tree on the left went on
        // showing the record that was open before, which reads as "the button did nothing".
        public event Action? TemplateOpened;

        // THE LINK THE USER ASKED FOR: from an NPC to the record that actually decides its values.
        // Selects the template AND moves to the tab that lists templates - for 4.039 NPCs the record
        // that matters is not the one they opened.
        public ICommand OpenTemplateCommand => new RelayCommand(() =>
        {
            var npc = SelectedNpc;
            if (npc == null || !npc.HasTemplate) return;
            if (!_byKey.TryGetValue(npc.TemplateKey, out var template)) return;

            SelectedNpc = template;

            // The template list has its own filter, and a template hidden by it would be selected
            // but invisible. Cleared rather than left: landing on a list that does not show what
            // was just opened is the same complaint as not moving at all.
            if (!Templates.Contains(template)) TemplateSearchFilter = "";

            TemplateOpened?.Invoke();
        });

        // False when the template is a levelled list: there is no record to jump to, and a button
        // that does nothing is worse than no button.
        public bool CanOpenTemplate =>
            SelectedNpc != null
            && SelectedNpc.HasTemplate
            && _byKey.ContainsKey(SelectedNpc.TemplateKey);

        // --------------------
        // The faction lens
        // --------------------
        //
        // What is left of the faction view (docs/NPC-Plan.md §12). It was its own tab until the
        // numbers showed the faction is the wrong lever for a bulk edit: 4.039 of 6.580 NPCs take
        // their stats from a template, so a changeStats rule on BanditFaction reached 613 NPCs and
        // moved 110. What the view was actually good for is "show me who hangs together", and that
        // is a filter on the NPC tree, not a second tree.
        private string _factionFilterKey = "";
        public string FactionFilterKey
        {
            get => _factionFilterKey;
            set
            {
                if (!SetProperty(ref _factionFilterKey, value ?? "")) return;

                ApplyFilter();
                OnPropertyChanged(nameof(HasFactionFilter));
                OnPropertyChanged(nameof(FactionFilterNote));
            }
        }

        public bool HasFactionFilter => !string.IsNullOrEmpty(FactionFilterKey);

        // Says what the number means, because it is not what a faction rule would reach: membership
        // is runtime state, and 855 NPCs inherit their factions from a template and carry no row of
        // their own. The count is what this database saw, not a promise about the game.
        public string FactionFilterNote =>
            !HasFactionFilter
                ? ""
                : $"{VisibleCount} NPCs in this faction, as of the last scan. NPCs that inherit "
                + "their factions from a template are not counted - a filterByFactions rule would "
                + "still catch them, because the game decides membership at runtime.";

        public ICommand ClearFactionFilterCommand => new RelayCommand(() => FactionFilterKey = "");

        private void ApplyFilter()
        {
            FilteredTree.Clear();

            foreach (var root in _allRoots)
            {
                var filtered = root.Filter(SearchText, parentMatches: false,
                                           onlyEdited: ShowOnlyEdited, factionKey: FactionFilterKey);
                if (filtered != null) FilteredTree.Add(filtered);
            }

            OnPropertyChanged(nameof(VisibleCount));
            OnPropertyChanged(nameof(FactionFilterNote));
        }

        private NpcNodeVM? _selectedNpc;
        public NpcNodeVM? SelectedNpc
        {
            get => _selectedNpc;
            set
            {
                if (SetProperty(ref _selectedNpc, value))
                {
                    OnPropertyChanged(nameof(HasSelection));
                    OnPropertyChanged(nameof(SelectedNpcKeywords));
                    OnPropertyChanged(nameof(CanOpenTemplate));

                    foreach (var picker in Pickers) picker.Follow(value);
                }
            }
        }

        public bool HasSelection => _selectedNpc != null;

        public int NpcCount { get; private set; }

        public int PluginCount => _allRoots.Count;

        // What the filter left standing, so a search that finds nothing says so with a number rather
        // than an empty box.
        public int VisibleCount => FilteredTree.Sum(r => r.NpcCount);

        // 24 ms over 6.580 NPCs, because "edited" is a field-by-field comparison against the scan
        // rather than a flag (see NpcNodeVM.IsEdited). Left uncached on purpose: it is read when the
        // tab loads and when a record is reset, both of which already cost far more than this, and a
        // cache would need invalidating from every setter in the editor. The branch badges are the
        // ones rendered constantly, and those short-circuit - 0,38 ms across 5.118 NPCs, measured.
        public int EditedCount => _allRoots.Sum(CountEdited);

        private static int CountEdited(NpcTreeNodeVM node) =>
            node.Npcs.Count(n => n.IsEdited) + node.Children.Sum(CountEdited);

        // "Is there anything to show at all" - drives the empty state. Separate from HasSelection so
        // "no data scanned" and "data, but nothing picked" stay two different messages on screen.
        public bool HasData => _allRoots.Count > 0;

        public string EmptyStateText =>
            "No NPC data yet.\n\n" +
            "The NPC tables exist, but the scan does not fill them yet - that is the next step " +
            "(see docs/NPC-Plan.md, N-P1).";
    }
}
