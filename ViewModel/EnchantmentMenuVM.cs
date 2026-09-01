using SkyrimCraftingTool.Model;
using SkyrimCraftingTool.Services;
using SkyrimCraftingTool.Services.SavePipline;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Windows.Input;

namespace SkyrimCraftingTool.ViewModel
{
    public class EnchantmentMenuVM : ViewModelBase
    {
        private readonly ItemDBHandler _handler;
        private readonly IKeywordService _keywordService;
        private readonly IEnchantmentService _enchantmentService;
        private readonly ISaveRequestService _saveRequestService;

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
            ICacheManager cacheManager)
        {
            _handler = handler;
            _keywordService = keywordService;
            _enchantmentService = enchantmentService;
            _activePlugins = activePlugins;

            _saveRequestService = new SaveRequestService(new ISaveHandler[]
            {
                new EnchantmentSaveHandler(enchantmentService, cacheManager),
            });

            EnchantementCollapseAllCommand = new RelayCommand(() => EnchantementExpandAll(false));

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
            // don't keep pointing at EnchantmentRecord instances a rescan may have replaced.
            SelectedEnchantment = null;

            BuildEnchantmentTree();
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

            OnPropertyChanged(nameof(IsEnchantmentNameChanged));
            OnPropertyChanged(nameof(IsEnchantmentCostChanged));
            OnPropertyChanged(nameof(IsEnchantmentEffectsChanged));
            OnPropertyChanged(nameof(IsWornRestrictionKeywordsChanged));
            OnPropertyChanged(nameof(HasEnchantmentChanges));
        }

        public bool IsEnchantmentNameChanged =>
            _hasEnchantmentSnapshot && _selectedEnchantment != null && _selectedEnchantment.Name != _originalEnchantmentName;

        public bool IsEnchantmentCostChanged =>
            _hasEnchantmentSnapshot && _selectedEnchantment != null &&
            System.Math.Abs(_selectedEnchantment.EnchantmentCost - _originalEnchantmentCost) > 0.0001f;

        public bool IsEnchantmentEffectsChanged =>
            _hasEnchantmentSnapshot && !StringListsEqual(
                EffectVMs.Select(vm => SerializeEffect(vm.Model)).ToList(),
                _originalEffects.Select(SerializeEffect).ToList());

        public bool IsWornRestrictionKeywordsChanged =>
            _hasEnchantmentSnapshot && _selectedEnchantment != null && !StringListsEqual(
                (_selectedEnchantment.WornRestrictionKeywords ?? new ObservableCollection<string>()).ToList(),
                _originalWornRestrictionKeywords);

        public bool HasEnchantmentChanges =>
            IsEnchantmentNameChanged || IsEnchantmentCostChanged || IsEnchantmentEffectsChanged || IsWornRestrictionKeywordsChanged;

        // Reverts the selected enchantment's Name/Cost/Effects/WornRestrictionKeywords to the
        // pristine plugin-scanned values by clearing the DB's shadow state for each (not just pushing
        // the old values back through the normal edit pipeline, which would leave the *Edited flag
        // set with the shadow value merely matching the original - see
        // ItemNodeVM/MainContentVM.ResetItemEdits for the same fix on the Armor/Weapon side).
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

            if (!string.IsNullOrEmpty(ench.WornRestrictionListKey))
            {
                _enchantmentService.ResetWornRestrictionKeywords(ench.WornRestrictionListKey);
                var restoredKeywords = _enchantmentService.GetOriginalWornRestrictionKeywords(ench.WornRestrictionListKey);
                ench.WornRestrictionKeywords = new ObservableCollection<string>(restoredKeywords);
                UpdateKeywordSelection();
                OnPropertyChanged(nameof(KeywordItems));
            }

            RefreshEnchantmentSnapshot();
        });

        // --- Autosave wiring ---

        private void OnEnchantmentFieldChanged(string fieldName)
        {
            var ench = _selectedEnchantment;
            if (ench == null) return;

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

            var selectedKeys = _keywordService.GlobalKeywords
                .Where(k => k.IsSelected)
                .Select(k => k.Key)
                .ToList();

            ench.WornRestrictionKeywords = new ObservableCollection<string>(selectedKeys);

            OnPropertyChanged(nameof(IsWornRestrictionKeywordsChanged));
            OnPropertyChanged(nameof(HasEnchantmentChanges));

            _saveDebouncer.DebounceAsync(350, async ct =>
            {
                var request = new SaveRequest(null, "WornRestrictionKeywords")
                {
                    Enchantment = ench,
                    SelectedWornRestrictionKeywords = selectedKeys
                };
                await _saveRequestService.SaveAsync(request);
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
            if (string.IsNullOrWhiteSpace(search))
                return TreeItems.ToList();

            search = search.ToLowerInvariant();

            var result = new List<EnchantmentTreeNode>();

            foreach (var pluginNode in TreeItems)
            {
                token.ThrowIfCancellationRequested();

                var filtered = FilterPluginNode(pluginNode, search);
                if (filtered != null)
                    result.Add(filtered);
            }

            return result;
        }


        private EnchantmentTreeNode FilterPluginNode(EnchantmentTreeNode root, string search)
        {
            // Treffer im Plugin-Namen?
            bool rootMatch = root.DisplayName.ToLowerInvariant().Contains(search);

            var newRoot = new EnchantmentTreeNode
            {
                DisplayName = root.DisplayName,
                Enchantment = root.Enchantment,
                IsExpanded = root.IsExpanded
            };

            foreach (var child in root.Children)
            {
                var filteredChild = FilterPluginNode(child, search);
                if (filteredChild != null)
                    newRoot.Children.Add(filteredChild);
            }

            // Match on its own name, or a match among its children?
            if (rootMatch || newRoot.Children.Any())
            {
                newRoot.IsExpanded = root.IsExpanded;
                return newRoot;
            }

            // Treffer im Item? (EditorID, Name oder Key inkl. Plugin-Prefix)
            if (root.Enchantment != null && EnchantmentMatches(root.Enchantment, search))
            {
                newRoot.IsExpanded = true;
                return newRoot;
            }

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
                UpdateEnchantmentFilteredTree(TreeItems.ToList());

            }
        }

        public RelayCommand EnchantementCollapseAllCommand { get; }
    }
}
