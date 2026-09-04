using SkyrimCraftingTool.Model;
using SkyrimCraftingTool.Services;
using SkyrimCraftingTool.Services.SavePipline;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;

namespace SkyrimCraftingTool.ViewModel
{
    public class EnchantmentMenuVM : ViewModelBase
    {
        private readonly ItemDBHandler _handler;
        private readonly IKeywordService _keywordService;
        private readonly IEnchantmentService _enchantmentService;
        private readonly ISaveRequestService _saveRequestService;
        private readonly IImportExportService _importExportService;

        // Shared autosave debouncer - only holds ONE pending action. Flushed on app shutdown.
        private readonly Debouncer _saveDebouncer = new();

        public System.Threading.Tasks.Task FlushPendingSavesAsync() => _saveDebouncer.FlushAsync();

        // Change tracking / Reset for the selected enchantment's own fields (Name/Cost), its Effects,
        // and its Worn Restriction Keywords. Snapshot of the pristine values, refreshed whenever
        // SelectedEnchantment changes - same pattern as ItemNodeVM's Item-Detail tracking. Effects and
        // WornRestrictionKeywords revert via the lazy _Original snapshot tables (see
        // Model/ItemDBHandler.cs's COBJ_Conditions_Original schema comment for the pattern).
        private bool _hasEnchantmentSnapshot;
        private string _originalEnchantmentName;
        private float _originalEnchantmentCost;
        private List<EnchantmentEffectRecord> _originalEffects = new();
        private List<string> _originalWornRestrictionKeywords = new();

        public ObservableCollection<EnchantmentEffectViewModel> EffectVMs { get; } = new();

        public ObservableCollection<EnchantmentTreeNode> TreeItems { get; } = new();
        public ObservableCollection<EnchantmentRecord> Enchantments { get; } = new();

        private EnchantmentRecord _selectedEnchantment;
        private List<PluginInfo> _activePlugins;

        // Key -> EditorID for every scanned enchantment, so the read-only "Base:" line in the detail
        // view can show the base enchantment's name instead of a raw Plugin|FormID. Rebuilt in
        // BuildEnchantmentTree.
        private Dictionary<string, string> _enchantNameByKey = new(StringComparer.OrdinalIgnoreCase);

        // Guards against UpdateKeywordSelection's own bulk IsSelected writes (when switching the
        // selected enchantment) being mistaken for user edits and triggering a save.
        private bool _isUpdatingKeywordSelection;


        // MagicEffects loaded once
        public List<MagicEffectsRecords> AllMagicEffects { get; private set; } = new();

        public EnchantmentRecord SelectedEnchantment
        {
            get => _selectedEnchantment;
            set
            {
                var previous = _selectedEnchantment;

                if (SetProperty(ref _selectedEnchantment, value))
                {
                    if (previous != null)
                        previous.FieldChanged -= OnEnchantmentFieldChanged;
                    if (_selectedEnchantment != null)
                        _selectedEnchantment.FieldChanged += OnEnchantmentFieldChanged;

                    UpdateKeywordSelection();
                    OnPropertyChanged(nameof(KeywordItems));
                    OnPropertyChanged(nameof(CanEditWornRestrictions));
                    OnPropertyChanged(nameof(SelectedWornRestrictionListChoice));
                    OnPropertyChanged(nameof(CurrentWornRestrictionListLabel));
                    OnPropertyChanged(nameof(HasBaseEnchantment));
                    OnPropertyChanged(nameof(CurrentBaseEnchantmentLabel));

                    foreach (var vm in EffectVMs)
                        vm.PropertyChanged -= OnEffectPropertyChanged;
                    EffectVMs.Clear();

                    if (_selectedEnchantment != null)
                    {
                        foreach (var eff in _selectedEnchantment.Effects)
                        {
                            var effectVm = new EnchantmentEffectViewModel(eff, AllMagicEffects);
                            effectVm.PropertyChanged += OnEffectPropertyChanged;
                            EffectVMs.Add(effectVm);
                        }
                    }

                    // Must run after EffectVMs is rebuilt above - IsEnchantmentEffectsChanged compares
                    // EffectVMs against the freshly selected enchantment's original effects, so
                    // snapshotting first would compare the PREVIOUS enchantment's leftover EffectVMs
                    // against the new one's original, permanently showing a false "changed" state.
                    RefreshEnchantmentSnapshot();
                }
            }
        }

        // Constructor
        public EnchantmentMenuVM(
            ItemDBHandler handler,
            IKeywordService keywordService,
            List<PluginInfo> activePlugins,
            IEnchantmentService enchantmentService,
            ICacheManager cacheManager,
            IImportExportService importExportService)
        {
            _handler = handler;
            _keywordService = keywordService;
            _enchantmentService = enchantmentService;
            _importExportService = importExportService;
            _activePlugins = activePlugins;

            _saveRequestService = new SaveRequestService(new ISaveHandler[]
            {
                new EnchantmentSaveHandler(enchantmentService, cacheManager),
            });

            EnchantementCollapseAllCommand = new RelayCommand(() => EnchantementExpandAll(false));
            ExportEnchantmentsCommand = new RelayCommand(async () => await ExportEnchantmentsAsync());
            ImportEnchantmentsCommand = new RelayCommand(async () => await ImportEnchantmentsAsync());

            // GlobalKeywords is shared app-wide, so subscribe to both the collection (rebuilt on
            // each scan via KeywordService.InitializeFrom) and each current item, keeping the two
            // subscriptions in sync as items are added/removed.
            foreach (var kw in _keywordService.GlobalKeywords)
                kw.PropertyChanged += OnGlobalKeywordPropertyChanged;
            _keywordService.GlobalKeywords.CollectionChanged += OnGlobalKeywordsCollectionChanged;

            // At construction time (in MainWindowVM's ctor) no scan has run yet, so the DB is empty
            // or doesn't exist — this initial build is expected to produce an empty tree. Call
            // RefreshData(activePlugins) again once real data exists (see MainWindowVM, which wires
            // MainContentVM.DataLoaded to this).
            RefreshData(activePlugins);
        }

        // Reloads MagicEffects + the enchantment tree from the DB and updates the plugin list used
        // for tree ordering. Must run on the UI thread — it mutates ObservableCollections bound to
        // the view. Call this whenever the underlying DB may have changed (initial load, rescan).
        public void RefreshData(List<PluginInfo> activePlugins)
        {
            _activePlugins = activePlugins ?? new List<PluginInfo>();

            AllMagicEffects = _handler.SearchByType("MagicEffect")
                .Cast<MagicEffectsRecords>()
                .OrderBy(m => m.Name)
                .ToList();

            // Clears the selection (and, via its setter, unsubscribes/cleans up EffectVMs) so we
            // don't keep pointing at EnchantmentRecord instances a rescan/import may have replaced —
            // but remember which one so it can be re-selected against the fresh records below.
            var previouslySelectedKey = _selectedEnchantment?.Key;
            SelectedEnchantment = null;

            BuildEnchantmentTree();
            RecomputeEditedEnchantmentCount();
            RefreshWornRestrictionListChoices();

            // Re-select the same enchantment (now a fresh record instance) so an import or rescan is
            // reflected in the detail panel straight away instead of blanking it — otherwise the user
            // has to hunt for and re-click the row to see that anything happened.
            if (!string.IsNullOrEmpty(previouslySelectedKey))
            {
                var leaf = FindEnchantmentLeaf(previouslySelectedKey);
                if (leaf?.Enchantment != null)
                    SelectedEnchantment = leaf.Enchantment;
            }

            // BuildEnchantmentTree publishes the UNFILTERED tree, but "Only edited" / "Only base" /
            // the search box stay checked in the UI — without this they'd still look active while
            // showing everything after a rescan or import. No-op when no filter is set (the
            // background filter early-returns TreeItems in that case).
            ApplyEnchantmentFilterDebounced(_enchantmentTreeSearchText);
        }

        private EnchantmentTreeNode FindEnchantmentLeaf(string key)
        {
            foreach (var root in TreeItems)
            {
                var found = Search(root, key);
                if (found != null) return found;
            }
            return null;

            static EnchantmentTreeNode Search(EnchantmentTreeNode node, string key)
            {
                if (node.Enchantment != null &&
                    string.Equals(node.Enchantment.Key, key, StringComparison.OrdinalIgnoreCase))
                    return node;
                foreach (var child in node.Children)
                {
                    var f = Search(child, key);
                    if (f != null) return f;
                }
                return null;
            }
        }

        // --- Worn-restriction list (FLST) picker ---
        // The tool never creates a new FLST, only attaches an enchantment to one that already
        // exists in the scanned load order — so the picker only ever offers real, known lists.

        public sealed class WornRestrictionListChoice
        {
            public string Key { get; }   // "" = none
            public string Label { get; }
            public WornRestrictionListChoice(string key, string label) { Key = key; Label = label; }
            public override string ToString() => Label;
        }

        public ObservableCollection<WornRestrictionListChoice> WornRestrictionListChoices { get; } = new();

        private static readonly WornRestrictionListChoice NoneChoice = new("", "(none)");

        // FLST Plugin|FormID -> EditorID, from the formid.db FormLists name table. Rebuilt in
        // RefreshWornRestrictionListChoices; used for both the picker labels and the "FLST: …" info
        // line so the user sees a real list name, not a raw FormID.
        private Dictionary<string, string> _flstNameByKey = new(StringComparer.OrdinalIgnoreCase);

        // Human-facing FLST label: the EditorID when we have it, the raw Plugin|FormID only as a
        // last resort (no rescan yet, or the record genuinely has no EditorID). The key is a
        // debugging detail, not something to put in front of the user.
        private string FlstDisplay(string listKey)
            => _flstNameByKey.TryGetValue(listKey, out var n) && !string.IsNullOrWhiteSpace(n)
                ? n
                : listKey;

        private void RefreshWornRestrictionListChoices()
        {
            var nameByKey = _keywordService.GlobalKeywords
                .GroupBy(k => k.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().Name, StringComparer.OrdinalIgnoreCase);

            _flstNameByKey = _enchantmentService.GetFormListNamesByKey();

            var currentKey = _selectedEnchantment?.WornRestrictionListKey ?? "";

            WornRestrictionListChoices.Clear();
            WornRestrictionListChoices.Add(NoneChoice);

            foreach (var (listKey, memberKeys, isUserEdited) in _enchantmentService.GetKnownWornRestrictionLists()
                         .OrderBy(t => FlstDisplay(t.ListKey), StringComparer.OrdinalIgnoreCase))
            {
                bool isCurrent = string.Equals(listKey, currentKey, StringComparison.OrdinalIgnoreCase);

                var resolved = memberKeys
                    .Select(k => nameByKey.GetValueOrDefault(k))
                    .Where(n => !string.IsNullOrEmpty(n))
                    .ToList();

                // A worn-restriction FLST's members are keywords. E3 scans every FLST in the load
                // order, so without this the picker fills up with perk/spell/misc lists whose
                // "(key, key, key)" preview is meaningless here. Keep a list only if its members
                // mostly resolve as keywords — but never hide the one this enchantment already uses,
                // nor one the user has hand-curated (which may legitimately be empty by now).
                if (!isCurrent && !isUserEdited && (memberKeys.Count == 0 || resolved.Count * 2 < memberKeys.Count))
                    continue;

                string label;
                if (memberKeys.Count == 0)
                {
                    // Only reachable for a list the user emptied on purpose (see the guard above).
                    label = $"{FlstDisplay(listKey)}  (empty)";
                }
                else
                {
                    var shown = (resolved.Count > 0 ? resolved : memberKeys).Take(3).ToList();
                    var suffix = memberKeys.Count > shown.Count ? ", …" : "";
                    label = $"{FlstDisplay(listKey)}  ({memberKeys.Count}: {string.Join(", ", shown)}{suffix})";
                }
                WornRestrictionListChoices.Add(new WornRestrictionListChoice(listKey, label));
            }

            OnPropertyChanged(nameof(CurrentWornRestrictionListLabel));
            OnPropertyChanged(nameof(SelectedWornRestrictionListChoice));
        }

        // Read-only "derived from" indicator. The selected enchantment inherits from a base ENCH
        // (magnitude/duration tier variant) — not editable, shown as a tag only.
        public bool HasBaseEnchantment => _selectedEnchantment?.IsDerived == true;

        public string CurrentBaseEnchantmentLabel
        {
            get
            {
                if (_selectedEnchantment?.IsDerived != true) return "";
                var baseKey = _selectedEnchantment.BaseEnchantmentKey;
                return _enchantNameByKey.TryGetValue(baseKey, out var name) && !string.IsNullOrWhiteSpace(name)
                    ? $"Base: {name}"
                    : $"Base: {baseKey}";
            }
        }

        // Info label shown next to the picker regardless of whether editing is possible.
        public string CurrentWornRestrictionListLabel =>
            _selectedEnchantment == null ? ""
            : KeyFactory.IsUnsetKey(_selectedEnchantment.WornRestrictionListKey) ? "FLST: (none)"
            : $"FLST: {FlstDisplay(_selectedEnchantment.WornRestrictionListKey)}";

        // --- E3.5: the keyword panel edits a SHARED list, not this enchantment's private keywords ---

        private bool _wornRestrictionListEdited;
        private int _wornRestrictionListUsageCount;

        // "Used by N enchantments — changes affect all of them." Empty when no list is attached.
        public string WornRestrictionListUsageLabel =>
            _selectedEnchantment == null || KeyFactory.IsUnsetKey(_selectedEnchantment.WornRestrictionListKey)
                ? ""
                : _wornRestrictionListUsageCount == 1
                    ? "Used by 1 enchantment."
                    : $"Used by {_wornRestrictionListUsageCount} enchantments — changes affect all of them.";

        // Drives the list-scoped "Reset list" button (distinct from the enchantment's own "Reset
        // Changes", which no longer touches list content — E3.5).
        public bool CanResetWornRestrictionList =>
            _selectedEnchantment != null
            && !KeyFactory.IsUnsetKey(_selectedEnchantment.WornRestrictionListKey)
            && _wornRestrictionListEdited;

        private void RefreshWornRestrictionListState()
        {
            var key = _selectedEnchantment?.WornRestrictionListKey;
            if (string.IsNullOrEmpty(key) || KeyFactory.IsUnsetKey(key))
            {
                _wornRestrictionListEdited = false;
                _wornRestrictionListUsageCount = 0;
            }
            else
            {
                _wornRestrictionListEdited = _enchantmentService.IsWornRestrictionListEdited(key);
                _wornRestrictionListUsageCount = _enchantmentService.CountEnchantmentsUsingWornRestrictionList(key);
            }
            OnPropertyChanged(nameof(WornRestrictionListUsageLabel));
            OnPropertyChanged(nameof(CanResetWornRestrictionList));
        }

        // Reverts ONLY the attached list's keyword content to the pristine scanned set — leaves this
        // (and every other) enchantment's own fields alone. See ItemDBHandler.ResetWornRestrictionKeywords.
        public ICommand ResetWornRestrictionListCommand => new RelayCommand(() =>
        {
            var ench = _selectedEnchantment;
            if (ench == null || KeyFactory.IsUnsetKey(ench.WornRestrictionListKey)) return;

            _enchantmentService.ResetWornRestrictionKeywords(ench.WornRestrictionListKey);

            ench.WornRestrictionKeywords = new ObservableCollection<string>(
                _enchantmentService.GetWornRestrictionKeywordsForList(ench.WornRestrictionListKey));
            UpdateKeywordSelection();
            OnPropertyChanged(nameof(KeywordItems));
            RefreshEnchantmentSnapshot();   // recomputes _originalWornRestrictionKeywords + list state
            RefreshWornRestrictionListChoices();   // member count / preview changed (already on the UI thread)
        });

        public WornRestrictionListChoice SelectedWornRestrictionListChoice
        {
            get
            {
                if (_selectedEnchantment == null) return NoneChoice;
                var key = _selectedEnchantment.WornRestrictionListKey ?? "";
                return WornRestrictionListChoices.FirstOrDefault(c =>
                           string.Equals(c.Key, key, StringComparison.OrdinalIgnoreCase))
                       // The enchantment's current list isn't in the picker (e.g. a keyword-less
                       // FLST, which GetKnownWornRestrictionLists doesn't return) - show it anyway,
                       // by resolved name where possible, rather than swapping to "(none)".
                       ?? (string.IsNullOrEmpty(key) ? NoneChoice : new WornRestrictionListChoice(key, FlstDisplay(key)));
            }
            set
            {
                var ench = _selectedEnchantment;
                if (ench == null || value == null) return;

                var newKey = value.Key ?? "";
                if (string.Equals(ench.WornRestrictionListKey ?? "", newKey, StringComparison.OrdinalIgnoreCase))
                    return;

                ench.WornRestrictionListKey = newKey;
                _enchantmentService.UpdateEnchantmentWornRestrictionListKey(ench.Key, newKey);
                MarkSelectedEnchantmentEdited();

                // Reflect the newly-attached list's actual current keyword membership.
                ench.WornRestrictionKeywords = new ObservableCollection<string>(
                    KeyFactory.IsUnsetKey(newKey)
                        ? new List<string>()
                        : _enchantmentService.GetWornRestrictionKeywordsForList(newKey));
                UpdateKeywordSelection();
                OnPropertyChanged(nameof(KeywordItems));

                RefreshEnchantmentSnapshot();
                OnPropertyChanged(nameof(SelectedWornRestrictionListChoice));
                OnPropertyChanged(nameof(CurrentWornRestrictionListLabel));
                OnPropertyChanged(nameof(CanEditWornRestrictions));
            }
        }

        // --- Change tracking / Reset (Name + Cost + Effects + Worn Restriction Keywords) ---

        private static string SerializeEffect(EnchantmentEffectRecord e) =>
            $"{e.MagicEffectKey}|{e.Magnitude}|{e.Duration}|{e.Area}";

        // Order-insensitive: EnchantmentEffects has PRIMARY KEY(EnchantmentKey, MagicEffectKey), so
        // there's no meaningful ordering between rows to preserve.
        private static bool StringListsEqual(List<string> a, List<string> b)
        {
            if (a.Count != b.Count) return false;
            return a.OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(b.OrderBy(x => x, StringComparer.Ordinal));
        }

        private void RefreshEnchantmentSnapshot()
        {
            var ench = _selectedEnchantment;
            var original = ench != null ? _enchantmentService.GetOriginalEnchantment(ench.Key) : null;

            _hasEnchantmentSnapshot = original != null;
            _originalEnchantmentName = original?.Name;
            _originalEnchantmentCost = original?.EnchantmentCost ?? 0f;

            _originalEffects = ench != null
                ? _enchantmentService.GetOriginalEnchantmentEffects(ench.Key)
                : new List<EnchantmentEffectRecord>();

            _originalWornRestrictionKeywords = !string.IsNullOrEmpty(ench?.WornRestrictionListKey)
                ? _enchantmentService.GetOriginalWornRestrictionKeywords(ench.WornRestrictionListKey)
                : new List<string>();

            // Pristine scanned list assignment (the base WornRestrictionListKey column, never
            // touched by the picker — only the IsEditedWornRestrictionListKey shadow is).
            _originalWornRestrictionListKey = original?.WornRestrictionListKey ?? "";

            RefreshWornRestrictionListState();

            OnPropertyChanged(nameof(IsEnchantmentNameChanged));
            OnPropertyChanged(nameof(IsEnchantmentCostChanged));
            OnPropertyChanged(nameof(IsEnchantmentEffectsChanged));
            OnPropertyChanged(nameof(IsWornRestrictionKeywordsChanged));
            OnPropertyChanged(nameof(IsWornRestrictionListAssignmentChanged));
            OnPropertyChanged(nameof(HasEnchantmentChanges));
        }

        private string _originalWornRestrictionListKey = "";

        // Whether this enchantment has been re-pointed at a DIFFERENT worn-restriction list via the
        // picker (the IsEditedWornRestrictionListKey shadow). This IS the enchantment's own edit, so
        // it counts toward HasEnchantmentChanges and "Reset Changes" reverts it.
        public bool IsWornRestrictionListAssignmentChanged =>
            _hasEnchantmentSnapshot && _selectedEnchantment != null &&
            !string.Equals(_selectedEnchantment.WornRestrictionListKey ?? "", _originalWornRestrictionListKey ?? "",
                           StringComparison.OrdinalIgnoreCase);

        public bool IsEnchantmentNameChanged =>
            _hasEnchantmentSnapshot && _selectedEnchantment != null && _selectedEnchantment.Name != _originalEnchantmentName;

        public bool IsEnchantmentCostChanged =>
            _hasEnchantmentSnapshot && _selectedEnchantment != null &&
            System.Math.Abs(_selectedEnchantment.EnchantmentCost - _originalEnchantmentCost) > 0.0001f;

        public bool IsEnchantmentEffectsChanged =>
            _hasEnchantmentSnapshot && !StringListsEqual(
                EffectVMs.Select(vm => SerializeEffect(vm.Model)).ToList(),
                _originalEffects.Select(SerializeEffect).ToList());

        // Whether the ATTACHED list's content differs from its pristine scanned set. Drives the amber
        // border on the "Selected Keywords" box — but NOT HasEnchantmentChanges (E3.5: a shared list
        // isn't this enchantment's own edit; it has its own "Reset list" affordance).
        public bool IsWornRestrictionKeywordsChanged =>
            _hasEnchantmentSnapshot && _selectedEnchantment != null && !StringListsEqual(
                (_selectedEnchantment.WornRestrictionKeywords ?? new ObservableCollection<string>()).ToList(),
                _originalWornRestrictionKeywords);

        public bool HasEnchantmentChanges =>
            IsEnchantmentNameChanged || IsEnchantmentCostChanged || IsEnchantmentEffectsChanged
            || IsWornRestrictionListAssignmentChanged;

        // Reverts the selected enchantment's Name/Cost/Effects + its list *assignment* to the
        // pristine plugin-scanned values by clearing the DB's shadow state for each (not just pushing
        // the old values back through the normal edit pipeline, which would leave the *Edited flag
        // set with the shadow value merely matching the original - see
        // ItemNodeVM/MainContentVM.ResetItemEdits for the same fix on the Armor/Weapon side).
        // E3.5: this NO LONGER resets the attached list's *content* - a shared FLST isn't owned by
        // one enchantment, so that's the list-scoped "Reset list" button (ResetWornRestrictionListCommand).
        // FieldChanged/PropertyChanged are briefly unsubscribed so applying the reverted values
        // doesn't immediately re-save them as a fresh edit.
        public ICommand ResetEnchantmentCommand => new RelayCommand(() =>
        {
            var ench = _selectedEnchantment;
            if (ench == null || !_hasEnchantmentSnapshot) return;

            _enchantmentService.ResetEnchantmentEdits(ench.Key);
            var original = _enchantmentService.GetOriginalEnchantment(ench.Key);
            if (original == null) return;

            ench.FieldChanged -= OnEnchantmentFieldChanged;
            ench.Name = original.Name;
            ench.EnchantmentCost = original.EnchantmentCost;
            ench.WornRestrictionListKey = original.WornRestrictionListKey;
            ench.FieldChanged += OnEnchantmentFieldChanged;

            _enchantmentService.ResetEnchantmentEffects(ench.Key);
            var restoredEffects = _enchantmentService.GetOriginalEnchantmentEffects(ench.Key);
            ench.Effects = new ObservableCollection<EnchantmentEffectRecord>(restoredEffects);

            foreach (var vm in EffectVMs)
                vm.PropertyChanged -= OnEffectPropertyChanged;
            EffectVMs.Clear();
            foreach (var eff in restoredEffects)
            {
                var effectVm = new EnchantmentEffectViewModel(eff, AllMagicEffects);
                effectVm.PropertyChanged += OnEffectPropertyChanged;
                EffectVMs.Add(effectVm);
            }

            // Reload the keyword collection for wherever the enchantment now effectively points -
            // its own pristine list if it was never reassigned, or nothing if reassignment itself
            // is what got reverted just above. The list's *content* is untouched here.
            ench.WornRestrictionKeywords = new ObservableCollection<string>(
                KeyFactory.IsUnsetKey(ench.WornRestrictionListKey)
                    ? new List<string>()
                    : _enchantmentService.GetWornRestrictionKeywordsForList(ench.WornRestrictionListKey));
            UpdateKeywordSelection();
            OnPropertyChanged(nameof(KeywordItems));
            OnPropertyChanged(nameof(CanEditWornRestrictions));
            OnPropertyChanged(nameof(SelectedWornRestrictionListChoice));
            OnPropertyChanged(nameof(CurrentWornRestrictionListLabel));

            // Reset cleared every DB edit flag for this enchantment — drop its tree badge too.
            if (ench.IsEdited)
            {
                ench.IsEdited = false;
                if (EditedEnchantmentCount > 0) EditedEnchantmentCount--;
            }

            RefreshEnchantmentSnapshot();
        });

        // --- Autosave wiring ---

        private void OnEnchantmentFieldChanged(string fieldName)
        {
            var ench = _selectedEnchantment;
            if (ench == null) return;

            MarkSelectedEnchantmentEdited();
            OnPropertyChanged(nameof(IsEnchantmentNameChanged));
            OnPropertyChanged(nameof(IsEnchantmentCostChanged));
            OnPropertyChanged(nameof(HasEnchantmentChanges));

            _saveDebouncer.DebounceAsync(350, async ct =>
            {
                var request = new SaveRequest(null, fieldName) { Enchantment = ench };
                await _saveRequestService.SaveAsync(request);
            });
        }

        private void OnEffectPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(EnchantmentEffectViewModel.Magnitude)
                && e.PropertyName != nameof(EnchantmentEffectViewModel.Duration)
                && e.PropertyName != nameof(EnchantmentEffectViewModel.Area))
                return;

            var ench = _selectedEnchantment;
            if (ench == null) return;

            MarkSelectedEnchantmentEdited();
            OnPropertyChanged(nameof(IsEnchantmentEffectsChanged));
            OnPropertyChanged(nameof(HasEnchantmentChanges));

            _saveDebouncer.DebounceAsync(350, async ct =>
            {
                var request = new SaveRequest(null, "Effects")
                {
                    Enchantment = ench,
                    Effects = EffectVMs.ToList()
                };
                await _saveRequestService.SaveAsync(request);
            });
        }

        private void OnGlobalKeywordsCollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.OldItems != null)
                foreach (KeywordSelectionVM kw in e.OldItems)
                    kw.PropertyChanged -= OnGlobalKeywordPropertyChanged;

            if (e.NewItems != null)
                foreach (KeywordSelectionVM kw in e.NewItems)
                    kw.PropertyChanged += OnGlobalKeywordPropertyChanged;
        }

        private void OnGlobalKeywordPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(KeywordSelectionVM.IsSelected)) return;
            if (_isUpdatingKeywordSelection) return;

            var ench = _selectedEnchantment;
            if (ench == null) return;

            // Worn-restriction keywords are stored per FLST. An FLST-less enchantment (key "" or
            // "Null|000000", shared by ~1100 records) has nothing to key a save on - editing worn
            // restrictions for it isn't supported. The UI disables the keyword panel in that case
            // (CanEditWornRestrictions); this is the belt-and-braces guard.
            if (KeyFactory.IsUnsetKey(ench.WornRestrictionListKey)) return;

            var selectedKeys = _keywordService.GlobalKeywords
                .Where(k => k.IsSelected)
                .Select(k => k.Key)
                .ToList();

            ench.WornRestrictionKeywords = new ObservableCollection<string>(selectedKeys);

            // E3: editing an FLST's contents marks the LIST (WornRestrictionListState), not the
            // enchantment(s) pointing at it — so no MarkSelectedEnchantmentEdited() here. The detail
            // view still shows it as a resettable change via IsWornRestrictionKeywordsChanged.
            _wornRestrictionListEdited = true;   // optimistic — the debounced save below sets it in the DB
            OnPropertyChanged(nameof(IsWornRestrictionKeywordsChanged));
            OnPropertyChanged(nameof(HasEnchantmentChanges));
            OnPropertyChanged(nameof(CanResetWornRestrictionList));

            _saveDebouncer.DebounceAsync(350, async ct =>
            {
                var request = new SaveRequest(null, "WornRestrictionKeywords")
                {
                    Enchantment = ench,
                    SelectedWornRestrictionKeywords = selectedKeys
                };
                await _saveRequestService.SaveAsync(request);

                // The list's member count / preview in the picker is now stale. Rebuild it — but on
                // the UI thread: Debouncer runs its action on a thread-pool thread and
                // WornRestrictionListChoices is bound to the ComboBox. Hanging this off the same
                // 350ms debounce (rather than every keyword click) keeps the dropdown from churning.
                System.Windows.Application.Current?.Dispatcher.Invoke(RefreshWornRestrictionListChoices);
            });
        }

        private void EnchantementExpandAll(bool expand)
        {
            foreach (var p in TreeItems)
            {
                ExpandNodeRecursive(p, expand);
            }
            ApplyEnchantmentFilterDebounced(_enchantmentTreeSearchText);
        }

        private void ExpandNodeRecursive(EnchantmentTreeNode node, bool expand)
        {
            node.IsExpanded = expand;

            foreach (var child in node.Children)
                ExpandNodeRecursive(child, expand);
        }


        private string _enchantmentTreeSearchText = string.Empty;
        public string EnchantmentTreeSearchText
        {
            get => _enchantmentTreeSearchText;
            set
            {
                if (SetProperty(ref _enchantmentTreeSearchText, value))
                    ApplyEnchantmentFilterDebounced(value);
            }
        }

        // --- Edited state (mirrors MainContentVM: badge + "N edited" + "only edited" filter) ---

        private bool _showOnlyEditedEnchantments;
        public bool ShowOnlyEditedEnchantments
        {
            get => _showOnlyEditedEnchantments;
            set
            {
                if (SetProperty(ref _showOnlyEditedEnchantments, value))
                    ApplyEnchantmentFilterDebounced(_enchantmentTreeSearchText);
            }
        }

        // "Only base enchantments" — hides derived (tier-variant) leaves that have a BaseEnchantment,
        // the clutter-reduction ask. Read-only classification, orthogonal to the edited filter.
        private bool _showOnlyBaseEnchantments;
        public bool ShowOnlyBaseEnchantments
        {
            get => _showOnlyBaseEnchantments;
            set
            {
                if (SetProperty(ref _showOnlyBaseEnchantments, value))
                    ApplyEnchantmentFilterDebounced(_enchantmentTreeSearchText);
            }
        }

        private int _editedEnchantmentCount;
        public int EditedEnchantmentCount
        {
            get => _editedEnchantmentCount;
            private set => SetProperty(ref _editedEnchantmentCount, value);
        }

        // Worn-restriction keywords are stored per FLST. An enchantment with no FLST has no list to
        // attach keywords to, so the panel is disabled for it.
        public bool CanEditWornRestrictions =>
            _selectedEnchantment != null && !KeyFactory.IsUnsetKey(_selectedEnchantment.WornRestrictionListKey);

        public int PluginCount => EnchantementFilteredTree.Count;

        // Walk the (unfiltered) tree once and count leaves whose record is edited.
        private void RecomputeEditedEnchantmentCount()
        {
            int n = 0;
            foreach (var plugin in TreeItems)
                CountEdited(plugin, ref n);
            EditedEnchantmentCount = n;

            static void CountEdited(EnchantmentTreeNode node, ref int n)
            {
                if (node.Enchantment is { IsEdited: true }) n++;
                foreach (var c in node.Children) CountEdited(c, ref n);
            }
        }

        // Called from every enchantment edit entry point (fields / effects / keywords).
        private void MarkSelectedEnchantmentEdited()
        {
            if (_selectedEnchantment == null || _selectedEnchantment.IsEdited) return;
            _selectedEnchantment.IsEdited = true;
            EditedEnchantmentCount++;
        }

        private readonly Debouncer _enchantmentDebouncer = new();
        private readonly BackgroundFilterRunner<string, List<EnchantmentTreeNode>> _enchantmentFilterRunner = new();

        public ObservableCollection<EnchantmentTreeNode> EnchantementFilteredTree { get; } = new();

        private void ApplyEnchantmentFilterDebounced(string text)
        {
            _enchantmentDebouncer.Debounce(120, _ =>
            {
                _enchantmentFilterRunner.Run(
                    text,
                    (search, token) => FilterEnchantmentTreeOnBackground(search, token),
                    result => UpdateEnchantmentFilteredTree(result)
                );
            });
        }

        private List<EnchantmentTreeNode> FilterEnchantmentTreeOnBackground(string search, CancellationToken token)
        {
            bool onlyEdited = _showOnlyEditedEnchantments;
            bool onlyBase = _showOnlyBaseEnchantments;
            bool searching = !string.IsNullOrWhiteSpace(search);

            if (!searching && !onlyEdited && !onlyBase)
                return TreeItems.ToList();

            var lower = searching ? search.ToLowerInvariant() : "";

            var result = new List<EnchantmentTreeNode>();
            foreach (var pluginNode in TreeItems)
            {
                token.ThrowIfCancellationRequested();

                var filtered = FilterPluginNode(pluginNode, lower, onlyEdited, onlyBase);
                if (filtered != null)
                    result.Add(filtered);
            }

            return result;
        }


        private EnchantmentTreeNode FilterPluginNode(EnchantmentTreeNode root, string search, bool onlyEdited, bool onlyBase)
        {
            bool searching = search.Length > 0;

            var newRoot = new EnchantmentTreeNode
            {
                DisplayName = root.DisplayName,
                Enchantment = root.Enchantment,
                IsExpanded = root.IsExpanded
            };

            // Leaf: gate on edited / base-only state first, then on the search text.
            if (root.Enchantment != null)
            {
                if (onlyEdited && !root.Enchantment.IsEdited)
                    return null;
                if (onlyBase && root.Enchantment.IsDerived)
                    return null;
                if (!searching || EnchantmentMatches(root.Enchantment, search))
                {
                    if (searching) newRoot.IsExpanded = true;
                    return newRoot;
                }
                return null;
            }

            // Folder / plugin node.
            foreach (var child in root.Children)
            {
                var filteredChild = FilterPluginNode(child, search, onlyEdited, onlyBase);
                if (filteredChild != null)
                    newRoot.Children.Add(filteredChild);
            }

            if (newRoot.Children.Any())
                return newRoot;

            // Plugin-name match with no matching children still shows the (empty) node while searching.
            if (searching && !onlyEdited && !onlyBase && root.DisplayName.ToLowerInvariant().Contains(search))
                return newRoot;

            return null;
        }

        private static bool EnchantmentMatches(EnchantmentRecord e, string lowerSearch) =>
            (e.EditorID?.ToLowerInvariant().Contains(lowerSearch) ?? false)
            || (e.Name?.ToLowerInvariant().Contains(lowerSearch) ?? false)
            || (e.Key?.ToLowerInvariant().Contains(lowerSearch) ?? false);

        private void UpdateEnchantmentFilteredTree(List<EnchantmentTreeNode> nodes)
        {
            EnchantementFilteredTree.Clear();
            foreach (var n in nodes)
                EnchantementFilteredTree.Add(n);

            OnPropertyChanged(nameof(PluginCount));
        }


        // --- Keyword UI state ---
        private bool _showAllKeywords;
        public bool ShowAllKeywords
        {
            get => _showAllKeywords;
            set
            {
                if (SetProperty(ref _showAllKeywords, value))
                    OnPropertyChanged(nameof(KeywordItems));
            }
        }

        private string _currentSearch = string.Empty;
        public string CurrentSearch
        {
            get => _currentSearch;
            set
            {
                if (SetProperty(ref _currentSearch, value))
                    OnPropertyChanged(nameof(KeywordItems));
            }
        }

        public IEnumerable<KeywordSelectionVM> KeywordItems
        {
            get
            {
                if (SelectedEnchantment == null)
                    return Enumerable.Empty<KeywordSelectionVM>();

                var category = EnchantmentCategoryHelper.Classify(SelectedEnchantment);

                IEnumerable<KeywordSelectionVM> baseList;

                if (ShowAllKeywords)
                    baseList = _keywordService.GlobalKeywords;
                else
                    baseList = _keywordService.FilterByEnchantmentCategory(category);

                if (!string.IsNullOrWhiteSpace(CurrentSearch))
                    baseList = baseList.Where(k =>
                        k.Name.Contains(CurrentSearch, StringComparison.OrdinalIgnoreCase));

                return baseList;
            }
        }


        private void UpdateKeywordSelection()
        {
            var selectedKeys = SelectedEnchantment?.WornRestrictionKeywords?.ToHashSet()
                               ?? new HashSet<string>();

            _isUpdatingKeywordSelection = true;
            try
            {
                foreach (var kw in _keywordService.GlobalKeywords)
                    kw.IsSelected = selectedKeys.Contains(kw.Key);
            }
            finally
            {
                _isUpdatingKeywordSelection = false;
            }
        }

        // --- Build Tree ---
        public void BuildEnchantmentTree()
        {
            TreeItems.Clear();

            var enchantments = _handler.GetAllEnchantments();

            _enchantNameByKey = enchantments
                .GroupBy(e => e.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    g => g.Key,
                    g => string.IsNullOrWhiteSpace(g.First().EditorID) ? g.First().Name : g.First().EditorID,
                    StringComparer.OrdinalIgnoreCase);

            var grouped = enchantments
             .GroupBy(e => e.Plugin)
             .OrderBy(g =>
                 _activePlugins.FindIndex(p =>
                     p.FileName.Equals(g.Key, StringComparison.OrdinalIgnoreCase)
                 )
             );


            foreach (var pluginGroup in grouped)
            {
                var pluginNode = new EnchantmentTreeNode
                {
                    DisplayName = pluginGroup.Key
                };

                var weaponNode = new EnchantmentTreeNode { DisplayName = "Weapon Enchantments" };
                var armorNode = new EnchantmentTreeNode { DisplayName = "Armor Enchantments" };
                var otherNode = new EnchantmentTreeNode { DisplayName = "Other" };

                foreach (var ench in pluginGroup.OrderBy(e => e.Name))
                {
                    var node = new EnchantmentTreeNode
                    {
                        DisplayName = ench.EditorID,
                        Enchantment = ench
                    };

                    switch (EnchantmentCategoryHelper.Classify(ench))
                    {
                        case EnchantmentCategory.Weapon:
                            weaponNode.Children.Add(node);
                            break;

                        case EnchantmentCategory.Armor:
                            armorNode.Children.Add(node);
                            break;

                        default:
                            otherNode.Children.Add(node);
                            break;
                    }
                }

                if (weaponNode.Children.Any()) pluginNode.Children.Add(weaponNode);
                if (armorNode.Children.Any()) pluginNode.Children.Add(armorNode);
                if (otherNode.Children.Any()) pluginNode.Children.Add(otherNode);

                TreeItems.Add(pluginNode);
            }

            // ONCE, after the loop - not per plugin. UpdateEnchantmentFilteredTree clears and
            // re-adds the ObservableCollection the TreeView is bound to, so calling it inside the
            // loop produced O(plugins²) CollectionChanged notifications (~45k at 300 plugins) for
            // the exact same end result.
            UpdateEnchantmentFilteredTree(TreeItems.ToList());
        }

        public RelayCommand EnchantementCollapseAllCommand { get; }
        public RelayCommand ExportEnchantmentsCommand { get; }
        public RelayCommand ImportEnchantmentsCommand { get; }

        // Enchantment edits are independent of item edits, so the Enchantments tab gets its own
        // Export / Import. Scoped to the two enchant-side units: "Enchantments" (a record's own
        // fields/effects) and "WornRestrictionList" (E3 — an FLST's edited contents).
        private static bool IsEnchantSideUnit(EditedItemDto i)
            => i.Table == "Enchantments" || i.Table == "WornRestrictionList";

        private async Task ExportEnchantmentsAsync()
        {
            await FlushPendingSavesAsync();

            List<EditedItemDto> items;
            try
            {
                items = _importExportService.GetEditedItems(ExportScope.All)
                    .Where(IsEnchantSideUnit)
                    .ToList();
            }
            catch (Exception ex)
            {
                AppLogger.LogError("ExportEnchantmentsAsync (GetEditedItems) failed", ex);
                System.Windows.MessageBox.Show($"Export failed:{Environment.NewLine}{ex.Message}",
                    "Error", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
                return;
            }

            if (items.Count == 0)
            {
                System.Windows.MessageBox.Show("No edited enchantments - nothing to export.",
                    "Export", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
                return;
            }

            var (count, root) = ImportExportFlow.ExportItems(items);
            System.Windows.MessageBox.Show(
                $"{count} enchantment(s) exported to{Environment.NewLine}{root}",
                "Export Successful", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
        }

        private async Task ImportEnchantmentsAsync()
        {
            // Before anything else: a pending debounced save would otherwise land ~350ms after the
            // import and overwrite the freshly imported values. Same as ExportEnchantmentsAsync.
            await FlushPendingSavesAsync();

            var items = ImportExportFlow.ReadAllExportedItems(IsEnchantSideUnit);
            if (items.Count == 0)
            {
                System.Windows.MessageBox.Show(
                    $"No enchantment export files found under{Environment.NewLine}{ExportFileStore.ExportsRoot}",
                    "Import", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
                return;
            }

            ImportResult? result;
            try
            {
                result = ImportExportFlow.RunImport(_importExportService, items);
            }
            catch (Exception ex)
            {
                AppLogger.LogError("ImportEnchantmentsAsync (RunImport) failed", ex);
                System.Windows.MessageBox.Show($"Import failed:{Environment.NewLine}{ex.Message}",
                    "Error", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
                return;
            }

            if (result == null)
                return; // user cancelled the conflict dialog

            try
            {
                RefreshData(_activePlugins);
            }
            catch (Exception ex)
            {
                AppLogger.LogError("ImportEnchantmentsAsync (RefreshData) failed", ex);
            }

            System.Windows.MessageBox.Show(ImportExportFlow.SummaryText(result), "Import Complete",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
        }
    }
}
