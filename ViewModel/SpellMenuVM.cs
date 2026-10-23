using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using SkyrimCraftingTool.Model;
using SkyrimCraftingTool.Services;

namespace SkyrimCraftingTool.ViewModel
{
    // The Spells tab (docs/Spells-Plan.md, S-P2).
    //
    // SHOWS, DOES NOT EDIT. That is the phase boundary and it is worth keeping sharp: the records
    // are reachable, groupable and searchable here, and nothing in this file can write. The editing
    // half arrives in S-P3 with its shadow columns and autosave, against a tree that has already
    // been looked at.
    //
    // Built along EnchantmentMenuVM, deliberately. What is NOT carried over is as telling as what
    // is: there is no family view, because spells have no base spell to form tiers over, and no
    // worn-restriction panel, because nobody wears a spell.
    public sealed class SpellMenuVM : ViewModelBase, ITutorialHost
    {
        private readonly ISpellService _spells;
        private List<PluginInfo> _activePlugins;

        public SpellMenuVM(ISpellService spells, List<PluginInfo> activePlugins, IImportExportService? importExportService = null)
        {
            _spells = spells;
            _activePlugins = activePlugins ?? new List<PluginInfo>();

            CollapseAllCommand = new RelayCommand(() => ExpandAll(false));
            ExpandAllCommand = new RelayCommand(() => ExpandAll(true));

            // No flush: the spell editors commit synchronously on focus loss (see the note on the
            // save path below), so nothing is pending by the time a toolbar button is clicked.
            // The half-cost perk is a pick from the scanned perks, never free text (user, 2026-10-18).
            HalfCostPerkPicker = new SpellPerkPickerVM(() => _perkChoices, () => IsHalfCostPerkEdited);

            ImportExport = new TabImportExportVM(TabExportModules.Spells, importExportService,
                refreshAfterImport: () => { RefreshData(_activePlugins); return System.Threading.Tasks.Task.CompletedTask; });
        }

        public ICommand CollapseAllCommand { get; }
        public ICommand ExpandAllCommand { get; }

        // Export / Import of this tab (TabExportModules.Spells).
        public TabImportExportVM ImportExport { get; }

        public SpellPerkPickerVM HalfCostPerkPicker { get; }
        private List<PerkChoice> _perkChoices = new();

        // The whole tree, unfiltered. FilteredTree is what the TreeView binds to.
        public ObservableCollection<SpellTreeNode> TreeItems { get; } = new();
        public ObservableCollection<SpellTreeNode> FilteredTree { get; } = new();

        private List<SpellRecord> _allSpells = new();

        // Read on the first visit, not after every scan: 3.684 spells and 8.302 effect rows are a
        // cost nobody should pay for a tab they did not open (docs/Performance-Plan.md, same rule as
        // the NPC tab). A scan drops what is held; the next visit reads again.
        private bool _loaded;
        public bool NeedsLoad => !_loaded;

        public void EnsureLoaded(List<PluginInfo> activePlugins)
        {
            if (_loaded) return;
            RefreshData(activePlugins);
        }

        public void Invalidate() => _loaded = false;

        public void RefreshData(List<PluginInfo> activePlugins)
        {
            _loaded = true;
            _activePlugins = activePlugins ?? new List<PluginInfo>();

            // The picker and the per-field enabling both need it, and it only changes on a rescan.
            AllMagicEffects = _spells.GetAllMagicEffects();
            _perkChoices = _spells.GetPerkChoices() ?? new List<PerkChoice>();

            BuildTree();
        }

        public void BuildTree()
        {
            string previouslySelected = SelectedSpell?.Key;

            TreeItems.Clear();
            _allSpells = _spells.GetAllSpells();

            // Load order, not alphabetical - the item tree's order, see PluginTreeOrder.
            var rank = PluginTreeOrder.Ranker(_activePlugins);
            var grouped = _allSpells
                .GroupBy(s => s.Plugin, StringComparer.OrdinalIgnoreCase)
                .OrderBy(g => rank(g.Key))
                .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase);

            foreach (var pluginGroup in grouped)
            {
                var pluginNode = new SpellPluginNode { DisplayName = pluginGroup.Key, Menu = this };

                // Grouped the way the game's magic menu files them (docs/Spells-Plan.md): the five
                // schools for plain spells - by their costliest effect - then Powers, Shouts,
                // Abilities and the rest. Fixed order, so the tree does not reshuffle between plugins.
                foreach (var typeGroup in pluginGroup
                             .GroupBy(s => s.Category)
                             .OrderBy(g => SpellCategory.Order(g.Key))
                             .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
                {
                    var typeNode = new SpellTypeNode { DisplayName = typeGroup.Key };

                    foreach (var spell in typeGroup.OrderBy(s => s.Label, StringComparer.OrdinalIgnoreCase))
                        typeNode.Children.Add(new SpellLeafNode { DisplayName = spell.Label, Spell = spell });

                    if (typeNode.Children.Count > 0) pluginNode.Children.Add(typeNode);
                }

                if (pluginNode.Children.Count > 0) TreeItems.Add(pluginNode);
            }

            ApplyFilter();

            // Keep the record the user was looking at, if it is still there. A rescan that silently
            // moves the selection is a rescan that loses someone's place.
            if (!string.IsNullOrEmpty(previouslySelected))
            {
                var leaf = FindLeaf(previouslySelected);
                if (leaf != null) SelectedNode = leaf;
            }

            OnPropertyChanged(nameof(Summary));
        }

        // ===================================================================
        // selection
        // ===================================================================

        // Whatever row the tree has, at any level - the detail panel picks its template from this
        // object's TYPE, so it is deliberately untyped.
        private object _selectedNode;
        public object SelectedNode
        {
            get => _selectedNode;
            set
            {
                if (!SetProperty(ref _selectedNode, value)) return;

                // A folder row clears the panel rather than leaving the last record on screen, which
                // would describe something the tree no longer points at.
                SelectedSpell = (value as SpellLeafNode)?.Spell;
            }
        }

        private SpellRecord _selectedSpell;
        public SpellRecord SelectedSpell
        {
            get => _selectedSpell;
            private set
            {
                // The outgoing record must be let go of FIRST. Left subscribed, an edit made on a
                // record the tree no longer points at would still be saved - and saved against the
                // right key, which makes it invisible rather than wrong.
                if (_selectedSpell != null) _selectedSpell.FieldChanged -= OnSpellFieldChanged;

                if (!SetProperty(ref _selectedSpell, value))
                {
                    if (_selectedSpell != null) _selectedSpell.FieldChanged += OnSpellFieldChanged;
                    return;
                }

                if (_selectedSpell != null) _selectedSpell.FieldChanged += OnSpellFieldChanged;

                _originalSpell = _selectedSpell != null ? _spells.GetOriginalSpell(_selectedSpell.Key) : null;

                BuildEffectRows();
                RaiseSelectionProperties();
            }
        }

        public bool HasSelection => _selectedSpell != null;

        // ===================================================================
        // the record's own fields (S-P5)
        // ===================================================================

        // What the scan found for the selected spell, read once per selection. Drives the
        // changed-marking beside each field without a query per keystroke.
        private SpellRecord _originalSpell;

        // Which fields the user has moved. Each one has a SkyPatcher operation behind it - see
        // ISpellService.
        public bool IsNameEdited => Differs(_originalSpell?.Name, _selectedSpell?.Name);
        public bool IsCastTypeEdited => Differs(_originalSpell?.CastType, _selectedSpell?.CastType);
        public bool IsHalfCostPerkEdited => Differs(_originalSpell?.HalfCostPerkKey, _selectedSpell?.HalfCostPerkKey);

        public bool IsBaseCostEdited =>
            _originalSpell != null && _selectedSpell != null && _originalSpell.BaseCost != _selectedSpell.BaseCost;

        public bool IsChargeTimeEdited =>
            _originalSpell != null && _selectedSpell != null &&
            Math.Abs(_originalSpell.ChargeTime - _selectedSpell.ChargeTime) > 0.0001f;

        public bool IsFlagsEdited =>
            _originalSpell != null && _selectedSpell != null && _originalSpell.Flags != _selectedSpell.Flags;

        public bool SelectedFieldsEdited =>
            IsNameEdited || IsCastTypeEdited || IsHalfCostPerkEdited ||
            IsBaseCostEdited || IsChargeTimeEdited || IsFlagsEdited;

        private static bool Differs(string a, string b)
            => a != null && b != null && !string.Equals(a, b, StringComparison.Ordinal);

        // The four values SkyPatcher's castType operation accepts, spelled as it spells them.
        public IReadOnlyList<string> CastTypeChoices { get; } =
            new[] { "ConstantEffect", "FireAndForget", "Concentration", "Scroll" };

        // One switch per flag SkyPatcher can write. Rebuilt per selection rather than bound through
        // a converter, because a flag is a bit in a number and a checkbox is not.
        public ObservableCollection<SpellFlagVM> FlagVMs { get; } = new();

        // The bits this spell carries that nothing names, as text. Shown rather than hidden: they
        // are real, hundreds of records set them, and a window reporting "no flags" while three bits
        // are on would be wrong. Not editable - the patcher works by NAME and cannot write them.
        public string UnnamedFlagBits
        {
            get
            {
                var spell = _selectedSpell;
                if (spell == null) return "";

                var bits = SpellFlags.UnnamedBitsIn(spell.Flags).Select(b => $"0x{b:X}").ToList();
                return bits.Count == 0 ? "" : string.Join(", ", bits);
            }
        }

        public bool HasUnnamedFlagBits => UnnamedFlagBits.Length > 0;

        private bool _suppressFlagWrites;

        private void BuildFlagRows()
        {
            _suppressFlagWrites = true;
            try
            {
                foreach (var row in FlagVMs) row.Changed -= OnFlagToggled;
                FlagVMs.Clear();

                var spell = _selectedSpell;
                if (spell == null) return;

                foreach (var (patcherName, bit, label) in SpellFlags.Known)
                {
                    var row = new SpellFlagVM(label, bit, (spell.Flags & bit) != 0);
                    row.Changed += OnFlagToggled;
                    FlagVMs.Add(row);
                }
            }
            finally
            {
                _suppressFlagWrites = false;
            }
        }

        private void OnFlagToggled()
        {
            if (_suppressFlagWrites) return;

            var spell = _selectedSpell;
            if (spell == null) return;

            // Rebuilt from the switches, but the bits nothing names are CARRIED OVER rather than
            // rewritten from the checkboxes: they are not represented there, and a round trip
            // through the UI would quietly clear them.
            int flags = spell.Flags;
            foreach (var row in FlagVMs)
                flags = row.IsSet ? flags | row.Bit : flags & ~row.Bit;

            spell.Flags = flags;        // raises FieldChanged, which saves
        }

        private void OnSpellFieldChanged(string field)
        {
            var spell = _selectedSpell;
            if (spell == null) return;

            switch (field)
            {
                case nameof(SpellRecord.Name): _spells.UpdateSpellName(spell.Key, spell.Name); break;
                case nameof(SpellRecord.BaseCost): _spells.UpdateSpellBaseCost(spell.Key, spell.BaseCost); break;
                case nameof(SpellRecord.CastType): _spells.UpdateSpellCastType(spell.Key, spell.CastType); break;
                case nameof(SpellRecord.ChargeTime): _spells.UpdateSpellChargeTime(spell.Key, spell.ChargeTime); break;
                case nameof(SpellRecord.HalfCostPerkKey): _spells.UpdateSpellHalfCostPerk(spell.Key, spell.HalfCostPerkKey); break;
                case nameof(SpellRecord.Flags): _spells.UpdateSpellFlags(spell.Key, spell.Flags); break;
                case nameof(SpellRecord.Keywords): _spells.UpdateSpellKeywords(spell.Key, spell.Keywords); break;

                // EditorID has no SkyPatcher operation and belongs to the plugin that declared it.
                // Reached only on a tool-created record, which S-P6 will write from scratch.
                default: return;
            }

            spell.IsEdited = true;
            RaiseFieldFlags();
            RaiseTreeEditedFlags();
        }

        // ===================================================================
        // spells of the user's own (S-P6)
        // ===================================================================

        // True when the selected record is one the user created here. Drives the extra editors: the
        // fields SkyPatcher cannot write are reachable on a record the ESP builds from scratch.
        public bool IsUserCreatedSelected => _selectedSpell?.IsUserCreated == true;

        // The other half, as its own property rather than an inverting converter: a row shows ONE
        // of two controls, and binding both to the same flag through opposite converters is where
        // they end up disagreeing and both appear at once.
        public bool IsScannedSelected => _selectedSpell != null && _selectedSpell.IsOriginal;

        // The eight SPIT spell types, for the dropdown a tool-created record gets. Not offered on a
        // scanned one: there is no operation for it, and the tree is grouped by it.
        public IReadOnlyList<string> SpellTypeChoices { get; } =
            new[] { "Spell", "Ability", "Power", "LesserPower", "Voice", "Disease", "Poison", "Addiction" };

        public IReadOnlyList<string> TargetTypeChoices { get; } =
            new[] { "Self", "Touch", "Aimed", "TargetActor", "TargetLocation" };

        public ICommand NewSpellCommand => new RelayCommand(() =>
        {
            SpellRecord created;
            try
            {
                created = _spells.CreateSpell("", "");
            }
            catch (Exception ex)
            {
                // Said out loud. Without this a failed create is indistinguishable from a button
                // that does nothing - the tree simply rebuilds unchanged.
                AppLogger.LogError("Creating a spell failed", ex);
                System.Windows.MessageBox.Show(
                    $"The spell could not be created:{Environment.NewLine}{ex.Message}",
                    "New spell", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
                return;
            }

            // The tree is rebuilt from the database; afterwards the new record is the instance
            // everything uses, not the one that was returned.
            BuildTree();
            NavigateTo(created.Key);

            IssueHub.Current.Report(new AppIssue(
                AppIssueSeverity.Info,
                $"{created.EditorID} created under {KeyFactory.UserPluginName}. It reaches the game " +
                "through the generated ESP — give it at least one effect, or it will not be written.",
                Category: "spell"));
        });

        // Takes the tree to a record: clears a filter that would hide it, opens every branch above
        // it, selects it, and points the detail panel at the node the TREE is showing.
        //
        // NOT JUST "SelectedNode = leaf", which is what this was and why a freshly created spell
        // looked like it had not been added at all. Its branch is a new root at the very bottom of
        // a hundred-plugin tree, collapsed, with nothing selected - so the record was there and
        // invisible.
        private void NavigateTo(string key)
        {
            var target = FindLeaf(key);
            if (target?.Spell == null)
            {
                IssueHub.Current.Report(new AppIssue(
                    AppIssueSeverity.Warning,
                    $"Spell {key} is not in the tree — nothing to jump to.",
                    Category: "navigation"));
                return;
            }

            // A filter that hides the target would land the jump on an invisible row. Clearing it is
            // the honest way out: the user asked to be taken THERE, not somewhere near.
            var path = new List<SpellTreeNode>();
            if (!TryFindPath(FilteredTree, target.Spell, path))
            {
                _searchText = "";
                _onlyEdited = false;
                OnPropertyChanged(nameof(SearchText));
                OnPropertyChanged(nameof(OnlyEdited));
                ApplyFilter();

                path.Clear();
                if (!TryFindPath(FilteredTree, target.Spell, path)) return;
            }

            // Deselect the previous row in the MODEL. Leaving it to the control works only while a
            // TreeView is attached to these nodes, and after a rebuild it briefly is not - two rows
            // would then carry IsSelected and the control would take whichever it realised first.
            ClearSelection(FilteredTree);

            // Ancestors first: a TreeViewItem whose parent is collapsed was never realised, and an
            // unrealised container cannot take the selection.
            for (int i = 0; i < path.Count - 1; i++)
                path[i].IsExpanded = true;

            path[^1].IsSelected = true;

            // path[^1], not `target`: after a filter run the tree holds a COPY carrying the same
            // record, and the panel has to point at the node the tree is actually showing.
            SelectedNode = path[^1];
        }

        private static bool TryFindPath(IEnumerable<SpellTreeNode> nodes, SpellRecord record, List<SpellTreeNode> path)
        {
            foreach (var node in nodes)
            {
                path.Add(node);

                if (node is SpellLeafNode leaf && ReferenceEquals(leaf.Spell, record)) return true;
                if (TryFindPath(node.Children, record, path)) return true;

                path.RemoveAt(path.Count - 1);
            }
            return false;
        }

        private static void ClearSelection(IEnumerable<SpellTreeNode> nodes)
        {
            foreach (var node in nodes)
            {
                node.IsSelected = false;
                ClearSelection(node.Children);
            }
        }

        public bool CanDeleteSelected => IsUserCreatedSelected;

        public ICommand DeleteSpellCommand => new RelayCommand(() =>
        {
            var spell = _selectedSpell;
            if (spell == null || !spell.IsUserCreated) return;

            if (!_spells.DeleteSpell(spell.Key)) return;

            SelectedNode = null;
            BuildTree();
        });

        // The fields that have no SkyPatcher operation, written straight to the base column.
        //
        // SEPARATE FROM THE SHADOW PATH ON PURPOSE. A shadow column means "the scan owns the base
        // value and this is the user's override"; here the user owns the record outright, so there
        // is nothing to shadow and a shadow would just be a second copy to keep in step.
        public string UserSpellType
        {
            get => _selectedSpell?.SpellType ?? "";
            set => WriteUserField("SpellType", value, v => _selectedSpell.SpellType = v);
        }

        public string UserTargetType
        {
            get => _selectedSpell?.TargetType ?? "";
            set => WriteUserField("TargetType", value, v => _selectedSpell.TargetType = v);
        }

        public string UserDescription
        {
            get => _selectedSpell?.Description ?? "";
            set => WriteUserField("Description", value, v => _selectedSpell.Description = v);
        }

        public string UserEditorId
        {
            get => _selectedSpell?.EditorID ?? "";
            set
            {
                var spell = _selectedSpell;
                if (spell == null || !spell.IsUserCreated || spell.EditorID == value) return;

                // Never blank: the EditorID is what the tree shows and what xEdit will list.
                if (string.IsNullOrWhiteSpace(value)) return;

                spell.EditorID = value;
                _spells.UpdateUserSpellField(spell.Key, "EditorID", value);

                spell.IsEdited = true;
                OnPropertyChanged(nameof(UserEditorId));
                RaiseTreeEditedFlags();
            }
        }

        private void WriteUserField(string column, string value, Action<string> apply)
        {
            var spell = _selectedSpell;
            if (spell == null || !spell.IsUserCreated) return;

            apply(value ?? "");
            _spells.UpdateUserSpellField(spell.Key, column, value ?? "");

            spell.IsEdited = true;
            OnPropertyChanged(nameof(UserSpellType));
            OnPropertyChanged(nameof(UserTargetType));
            OnPropertyChanged(nameof(UserDescription));
            RaiseTreeEditedFlags();
        }

        // Puts every edited FIELD back. Not the effects: they have their own snapshot and their own
        // button, so one reset cannot throw away the other's work.
        public ICommand ResetFieldsCommand => new RelayCommand(() =>
        {
            var spell = _selectedSpell;
            if (spell == null || _originalSpell == null) return;

            _spells.ResetSpellEdits(spell.Key);

            // Unsubscribed around the revert: every assignment below raises FieldChanged, and the
            // autosave would write each one straight back as a fresh edit - the reset undoing
            // itself, field by field.
            spell.FieldChanged -= OnSpellFieldChanged;
            try
            {
                spell.Name = _originalSpell.Name;
                spell.CastType = _originalSpell.CastType;
                spell.BaseCost = _originalSpell.BaseCost;
                spell.ChargeTime = _originalSpell.ChargeTime;
                spell.HalfCostPerkKey = _originalSpell.HalfCostPerkKey;
                spell.Flags = _originalSpell.Flags;
                spell.Keywords = _originalSpell.Keywords;
            }
            finally
            {
                spell.FieldChanged += OnSpellFieldChanged;
            }

            // The record stays marked while its EFFECTS are still the user's - added or removed
            // effects included (SelectedEffectsEdited).
            spell.IsEdited = SelectedEffectsEdited;

            BuildFlagRows();
            RaiseSelectionProperties();
            RaiseTreeEditedFlags();
        });

        private void RaiseSelectionProperties()
        {
            HalfCostPerkPicker.Follow(_selectedSpell);
            BuildFlagRows();

            OnPropertyChanged(nameof(HasSelection));
            OnPropertyChanged(nameof(SelectedFlagNames));
            OnPropertyChanged(nameof(UnnamedFlagBits));
            OnPropertyChanged(nameof(HasUnnamedFlagBits));

            OnPropertyChanged(nameof(IsUserCreatedSelected));
            OnPropertyChanged(nameof(IsScannedSelected));
            OnPropertyChanged(nameof(CanDeleteSelected));
            OnPropertyChanged(nameof(UserEditorId));
            OnPropertyChanged(nameof(UserSpellType));
            OnPropertyChanged(nameof(UserTargetType));
            OnPropertyChanged(nameof(UserDescription));

            RaiseFieldFlags();
        }

        private void RaiseFieldFlags()
        {
            OnPropertyChanged(nameof(IsNameEdited));
            OnPropertyChanged(nameof(IsCastTypeEdited));
            OnPropertyChanged(nameof(IsHalfCostPerkEdited));
            OnPropertyChanged(nameof(IsBaseCostEdited));
            OnPropertyChanged(nameof(IsChargeTimeEdited));
            OnPropertyChanged(nameof(IsFlagsEdited));
            OnPropertyChanged(nameof(SelectedFieldsEdited));
            OnPropertyChanged(nameof(SelectedFlagNames));
            OnPropertyChanged(nameof(UnnamedFlagBits));
            OnPropertyChanged(nameof(HasUnnamedFlagBits));
        }

        // ===================================================================
        // effects (S-P3)
        // ===================================================================

        public ObservableCollection<SpellEffectVM> EffectVMs { get; } = new();

        public bool HasEffects => EffectVMs.Count > 0;

        // The scanned magic effects, for the "add effect" picker and for knowing which of
        // magnitude / duration / area each effect actually has.
        public List<MagicEffectsRecords> AllMagicEffects { get; private set; } = new();

        private string _magicEffectSearchText = "";
        public string MagicEffectSearchText
        {
            get => _magicEffectSearchText;
            set
            {
                if (!SetProperty(ref _magicEffectSearchText, value ?? "")) return;
                OnPropertyChanged(nameof(FilteredMagicEffects));
            }
        }

        private MagicEffectsRecords _selectedMagicEffectToAdd;
        public MagicEffectsRecords SelectedMagicEffectToAdd
        {
            get => _selectedMagicEffectToAdd;
            set
            {
                if (!SetProperty(ref _selectedMagicEffectToAdd, value)) return;
                OnPropertyChanged(nameof(CanAddEffect));
            }
        }

        // Shared with the Enchantments tab rather than written twice: the rule is the same on both
        // sides - one record carries a given magic effect at most once, because the table's primary
        // key is (record, effect) - and a second copy of that rule is a second place to get it
        // wrong. The filter matches the LABEL first, which is what the row shows and what picking
        // one puts into the box.
        public IEnumerable<MagicEffectsRecords> FilteredMagicEffects =>
            EnchantmentMenuVM.AddableMagicEffects(
                AllMagicEffects, EffectVMs.Select(vm => vm.MagicEffectKey), MagicEffectSearchText);

        public bool CanAddEffect => _selectedSpell != null && _selectedMagicEffectToAdd != null;

        public ICommand AddEffectCommand => new RelayCommand(() =>
        {
            var spell = _selectedSpell;
            var pick = _selectedMagicEffectToAdd;
            if (spell == null || pick == null) return;

            // Magnitude, duration and area start at 0. The patch writes the new entry as
            // mgefsToAdd, and Reset takes it away again.
            spell.Effects.Add(new SpellEffectRecord
            {
                SpellKey = spell.Key,
                MagicEffectKey = pick.Key,
                EditorID = pick.EditorID ?? "",
                Name = pick.Name ?? "",
            });

            _spells.SaveSpellEffects(spell.Key, spell.Effects.ToList());
            spell.IsEdited = true;

            BuildEffectRows();

            // Cleared so the next add starts from an empty box rather than from the row just used -
            // which cannot be added again anyway, since the picker now hides it.
            SelectedMagicEffectToAdd = null;
            MagicEffectSearchText = "";

            RaiseTreeEditedFlags();
        });

        public ICommand RemoveEffectCommand => new RelayCommand<SpellEffectVM>(row =>
        {
            var spell = _selectedSpell;
            if (spell == null || row == null) return;

            var target = spell.Effects.FirstOrDefault(e =>
                string.Equals(e.MagicEffectKey, row.MagicEffectKey, StringComparison.OrdinalIgnoreCase));
            if (target == null) return;

            spell.Effects.Remove(target);

            _spells.SaveSpellEffects(spell.Key, spell.Effects.ToList());
            spell.IsEdited = true;

            BuildEffectRows();
            RaiseTreeEditedFlags();
        });

        // Anything on the selected spell's effects that is not what the scan found: a changed number,
        // OR an effect added or removed. The second half was missing (2026-10-17) - a spell whose
        // only change was an added or removed effect showed no "Reset effects" button at all.
        public bool SelectedEffectsEdited =>
            EffectVMs.Any(e => e.IsEdited)
            || !_originalEffectKeys.SetEquals(EffectVMs.Select(e => e.MagicEffectKey));

        // The effects the scan found, by key - set by BuildEffectRows.
        private HashSet<string> _originalEffectKeys = new(StringComparer.OrdinalIgnoreCase);


        private void BuildEffectRows()
        {
            foreach (var row in EffectVMs) row.Changed -= OnEffectChanged;
            EffectVMs.Clear();
            _originalEffectKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var spell = _selectedSpell;
            if (spell != null)
            {
                // The pristine numbers, read once per selection rather than per keystroke. On a
                // spell that has never been edited this returns the LIVE rows, which is the point:
                // they are the pristine ones until something is written.
                var original = _spells.GetOriginalSpellEffects(spell.Key)
                    .ToDictionary(e => e.MagicEffectKey, StringComparer.OrdinalIgnoreCase);
                _originalEffectKeys = new HashSet<string>(original.Keys, StringComparer.OrdinalIgnoreCase);

                foreach (var effect in spell.Effects)
                {
                    original.TryGetValue(effect.MagicEffectKey, out var pristine);

                    var row = new SpellEffectVM(effect, pristine);

                    // Which of the three numbers this effect even has comes from the catalogue, not
                    // from the row: a Soul Trap has no magnitude, a Fortify no area.
                    row.AttachCatalogue(AllMagicEffects.FirstOrDefault(m =>
                        string.Equals(m.Key, effect.MagicEffectKey, StringComparison.OrdinalIgnoreCase)));

                    row.Changed += OnEffectChanged;
                    EffectVMs.Add(row);
                }
            }

            OnPropertyChanged(nameof(HasEffects));
            OnPropertyChanged(nameof(SelectedEffectsEdited));
            OnPropertyChanged(nameof(FilteredMagicEffects));
            OnPropertyChanged(nameof(CanAddEffect));
        }

        // Saves the whole list, because that is the unit the database writes.
        //
        // SYNCHRONOUS. The enchantment tab debounces through a save pipeline, which is the right
        // answer where a keystroke in a text box fires per character; here the editors commit on
        // focus loss, so a write is a deliberate act and one per act is not a load.
        private void OnEffectChanged()
        {
            var spell = _selectedSpell;
            if (spell == null) return;

            _spells.SaveSpellEffects(spell.Key, spell.Effects.ToList());

            // The record is the user's now. Set here rather than read back from the database so the
            // tree badge appears as the number is typed rather than after the next reload.
            spell.IsEdited = true;

            OnPropertyChanged(nameof(SelectedEffectsEdited));
            RaiseTreeEditedFlags();
        }

        // Puts the selected spell's effects back to the scan.
        public ICommand ResetEffectsCommand => new RelayCommand(() =>
        {
            var spell = _selectedSpell;
            if (spell == null) return;

            _spells.ResetSpellEffects(spell.Key);

            // The LIST comes back from the database, not just the numbers: an effect added here is
            // gone again and one removed here is back. Reverting the rows in place - as this did -
            // only put numbers back, and left an added effect on screen that the database no
            // longer had. Rebuilding the rows also needs no "silent" revert: new rows announce
            // nothing.
            var restored = _spells.GetOriginalSpellEffects(spell.Key);
            spell.Effects.Clear();
            foreach (var effect in restored) spell.Effects.Add(effect);
            BuildEffectRows();

            // Still edited if its FIELDS are the user's.
            spell.IsEdited = SelectedFieldsEdited;

            OnPropertyChanged(nameof(SelectedEffectsEdited));
            RaiseTreeEditedFlags();
        });

        // Tells every tree row to ask again whether something below it is edited. The badge is a
        // computed property, so it needs telling - nothing else notices.
        private void RaiseTreeEditedFlags()
        {
            foreach (var root in TreeItems) Walk(root);
            foreach (var root in FilteredTree) Walk(root);

            static void Walk(SpellTreeNode node)
            {
                node.RaiseHasEditedDescendant();
                foreach (var child in node.Children) Walk(child);
            }
        }

        // The flags of the selected spell, by name, with the unnamed bits shown as bits.
        //
        // NOT THROUGH AN ENUM, and this is the one place the reason is visible to the user: three
        // bits that 415 spells in a real load order actually set have no name in Mutagen's enum at
        // all. Printed as "0x40000" they are at least present; parsed through an enum they would
        // simply not be there.
        public string SelectedFlagNames
        {
            get
            {
                var spell = _selectedSpell;
                if (spell == null || spell.Flags == 0) return "none";

                var parts = new List<string>();
                for (int bit = 0; bit < 32; bit++)
                {
                    int mask = 1 << bit;
                    if ((spell.Flags & mask) == 0) continue;
                    parts.Add(SpellFlags.Name(mask) ?? $"0x{mask:X}");
                }
                return string.Join(", ", parts);
            }
        }

        private SpellLeafNode FindLeaf(string key)
        {
            foreach (var root in TreeItems)
            {
                var found = Search(root);
                if (found != null) return found;
            }
            return null;

            SpellLeafNode Search(SpellTreeNode node)
            {
                if (node is SpellLeafNode leaf &&
                    string.Equals(leaf.Spell?.Key, key, StringComparison.OrdinalIgnoreCase))
                    return leaf;

                foreach (var child in node.Children)
                {
                    var hit = Search(child);
                    if (hit != null) return hit;
                }
                return null;
            }
        }

        // --- Guided tour (Tutorials/spells.json) ---

        public bool PrepareTutorialStep(string action) => action switch
        {
            "spells.selectExample" => SelectTutorialExample(),
            _ => false,
        };

        // The tour walks through a scanned plain spell - the case most edits are about - and one with a
        // half-cost perk, so that row has something to show. One already open that fits is kept.
        // Vanilla first: the reader is likelier to know Flames than a mod's spell.
        private bool SelectTutorialExample()
        {
            static bool Plain(SpellRecord s) => s.IsOriginal && s.SpellType == "Spell";

            if (_selectedSpell != null && Plain(_selectedSpell)) return true;

            var candidates = _allSpells.Where(Plain).ToList();
            var example = candidates.FirstOrDefault(s => !string.IsNullOrEmpty(s.HalfCostPerkKey) && s.Plugin.Equals("Skyrim.esm", StringComparison.OrdinalIgnoreCase))
                          ?? candidates.FirstOrDefault(s => !string.IsNullOrEmpty(s.HalfCostPerkKey))
                          ?? candidates.FirstOrDefault();
            if (example == null) return false;

            NavigateTo(example.Key);
            return _selectedSpell?.Key == example.Key;
        }

        // ===================================================================
        // filter
        // ===================================================================

        private string _searchText = "";
        public string SearchText
        {
            get => _searchText;
            set { if (SetProperty(ref _searchText, value)) ApplyFilter(); }
        }

        private bool _onlyEdited;
        public bool OnlyEdited
        {
            get => _onlyEdited;
            set { if (SetProperty(ref _onlyEdited, value)) ApplyFilter(); }
        }

        public string Summary
        {
            get
            {
                int spells = _allSpells.Count;
                int effects = _allSpells.Sum(s => s.Effects.Count);
                int incomplete = _allSpells.Count(s => s.HasDroppedEffectRows);

                string text = $"{spells:n0} spell(s), {effects:n0} effect(s)";

                // Said in the summary rather than only on the record: it is a property of the data
                // set, and someone comparing this tab against xEdit deserves to know up front that a
                // few records are shown short.
                if (incomplete > 0)
                    text += $" · {incomplete:n0} name the same effect more than once and are shown incompletely";

                return text;
            }
        }

        private void ApplyFilter()
        {
            FilteredTree.Clear();

            bool hasSearch = !string.IsNullOrWhiteSpace(_searchText);
            if (!hasSearch && !_onlyEdited)
            {
                foreach (var node in TreeItems) FilteredTree.Add(node);
                return;
            }

            string needle = _searchText?.Trim() ?? "";

            foreach (var root in TreeItems)
            {
                var kept = Keep(root);
                if (kept != null) FilteredTree.Add(kept);
            }

            SpellTreeNode Keep(SpellTreeNode node)
            {
                if (node is SpellLeafNode leaf)
                {
                    var spell = leaf.Spell;
                    if (spell == null) return null;
                    if (_onlyEdited && !spell.IsEdited) return null;

                    if (hasSearch && !Matches(spell, needle)) return null;

                    return leaf.CloneWithoutChildren();
                }

                // A folder survives only if something under it did. Copies, never the originals:
                // the TreeView would otherwise be handed the same node twice and the unfiltered
                // tree would lose its children to the filtered one.
                var copy = node.CloneWithoutChildren();
                foreach (var child in node.Children)
                {
                    var keptChild = Keep(child);
                    if (keptChild != null) copy.Children.Add(keptChild);
                }

                return copy.Children.Count > 0 ? copy : null;
            }
        }

        // EditorID, display name and the raw key, because all three are things people paste in.
        private static bool Matches(SpellRecord spell, string needle)
            => spell.EditorID.Contains(needle, StringComparison.OrdinalIgnoreCase)
            || spell.Name.Contains(needle, StringComparison.OrdinalIgnoreCase)
            || spell.Key.Contains(needle, StringComparison.OrdinalIgnoreCase);

        private void ExpandAll(bool expanded)
        {
            foreach (var root in FilteredTree) Walk(root);

            void Walk(SpellTreeNode node)
            {
                // Leaves have nothing to open, and expanding them would only make the tree taller.
                if (node is not SpellLeafNode) node.IsExpanded = expanded;
                foreach (var child in node.Children) Walk(child);
            }
        }
    }
}
