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
        // ONE DEBOUNCER PER FIELD, not one for the whole view model.
        //
        // A Debouncer keeps a single pending action, so changing two DIFFERENT fields inside the
        // 350 ms window meant the first one was cancelled and never written - its own class comment
        // says as much. With Name and Cost that rarely bit, because people type one box at a time.
        // With the ENIT fields it bit immediately: tick "No auto-calc", then set Charge Time and
        // Amount, and only Amount reached the database. Measured exactly that before the split.
        //
        // Keyed by field name, so a burst within one field still coalesces to a single write.
        private readonly Dictionary<string, Debouncer> _saveDebouncers = new(StringComparer.Ordinal);

        private Debouncer DebouncerFor(string fieldName)
        {
            lock (_saveDebouncers)
            {
                if (!_saveDebouncers.TryGetValue(fieldName, out var d))
                    _saveDebouncers[fieldName] = d = new Debouncer();
                return d;
            }
        }

        public async System.Threading.Tasks.Task FlushPendingSavesAsync()
        {
            Debouncer[] all;
            lock (_saveDebouncers) all = _saveDebouncers.Values.ToArray();

            foreach (var d in all)
                await d.FlushAsync();
        }

        // Change tracking / Reset for the selected enchantment's own fields (Name/Cost), its Effects,
        // and its Worn Restriction Keywords. Snapshot of the pristine values, refreshed whenever
        // SelectedEnchantment changes - same pattern as ItemNodeVM's Item-Detail tracking. Effects and
        // WornRestrictionKeywords revert via the lazy _Original snapshot tables (see
        // Model/ItemDBHandler.cs's COBJ_Conditions_Original schema comment for the pattern).
        private bool _hasEnchantmentSnapshot;
        private string _originalEnchantmentName;
        private float _originalEnchantmentCost;
        private int _originalFlags;
        private float _originalChargeTime;
        private int _originalEnchantmentAmount;
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
                    OnPropertyChanged(nameof(ShowWornRestrictions));
                    OnPropertyChanged(nameof(SelectedWornRestrictionListChoice));
                    OnPropertyChanged(nameof(CurrentWornRestrictionListLabel));
                    OnPropertyChanged(nameof(HasBaseEnchantment));
                    OnPropertyChanged(nameof(CurrentBaseEnchantmentLabel));
                    OnPropertyChanged(nameof(CanOpenBaseEnchantment));
                    OnPropertyChanged(nameof(CanAddEffect));
                    OnPropertyChanged(nameof(HasFamilyChildren));
                    OnPropertyChanged(nameof(ApplyToFamilyLabel));
                    OnPropertyChanged(nameof(ShowLineageRow));
                    OnPropertyChanged(nameof(IsUserCreatedSelected));
                    OnPropertyChanged(nameof(VariantsLabel));
                    OnPropertyChanged(nameof(FamilyChildLinks));
                    IsVariantListOpen = false;

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
            SelectedNode = null;

            BuildEnchantmentTree();
            RecomputeEditedEnchantmentCount();
            RefreshWornRestrictionListChoices();

            // Re-select the same enchantment (now a fresh record instance) so an import or rescan is
            // reflected in the detail panel straight away instead of blanking it — otherwise the user
            // has to hunt for and re-click the row to see that anything happened.
            //
            // Through SelectedNode, not SelectedEnchantment: the detail panel is a ContentPresenter
            // over the NODE now. Setting only the record would leave the view model pointing at an
            // enchantment while the panel showed nothing - which is exactly what "blanking it" above
            // was written to prevent. The nodes here are the ones the tree holds: BuildEnchantmentTree
            // ends in UpdateEnchantmentFilteredTree(TreeItems), so at this moment both lists carry
            // the same instances, before any filter makes copies of them.
            if (!string.IsNullOrEmpty(previouslySelectedKey))
            {
                var leaf = FindEnchantmentLeaf(previouslySelectedKey);
                if (leaf != null)
                    SelectedNode = leaf;
            }

            // BuildEnchantmentTree publishes the UNFILTERED tree, but "Only edited" / "Only base" /
            // the search box stay checked in the UI — without this they'd still look active while
            // showing everything after a rescan or import. No-op when no filter is set (the
            // background filter early-returns TreeItems in that case).
            ApplyEnchantmentFilterDebounced(_enchantmentTreeSearchText);
        }

        // What the tree has selected, whatever level it sits on - the ContentPresenter on the right
        // picks its DataTemplate from this object's type, the way MainContentVM.SelectedNode does.
        // Deliberately untyped: a plugin row and an enchantment row are different types by design.
        //
        // SelectedEnchantment stays the single source of truth for the editor itself (snapshot,
        // EffectVMs, every command), so it is derived here rather than bound to in parallel.
        private object _selectedNode;
        public object SelectedNode
        {
            get => _selectedNode;
            set
            {
                if (!SetProperty(ref _selectedNode, value)) return;

                // A folder row clears the editor instead of leaving the last record on screen -
                // otherwise the panel would describe something the tree no longer points at.
                SelectedEnchantment = (value as EnchantmentLeafNode)?.Enchantment;
            }
        }

        private EnchantmentLeafNode FindEnchantmentLeaf(string key)
        {
            foreach (var root in TreeItems)
            {
                var found = Search(root, key);
                if (found != null) return found;
            }
            return null;

            static EnchantmentLeafNode Search(EnchantmentTreeNode node, string key)
            {
                if (node is EnchantmentLeafNode leaf &&
                    string.Equals(leaf.Enchantment.Key, key, StringComparison.OrdinalIgnoreCase))
                    return leaf;
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

        // --- Sprungmarke: vom abgeleiteten Enchantment zu seinem Basis-Record (Prio 9) ---

        // Offered only when the base record is actually in the tree. A derived enchantment whose
        // base sits in a plugin that has left the load order keeps its "Base: <key>" line - the
        // scanned value stays true - but there is nothing to jump to, so the button greys out
        // instead of doing nothing on click.
        public bool CanOpenBaseEnchantment =>
            _selectedEnchantment?.IsDerived == true
            && FindEnchantmentLeaf(_selectedEnchantment.BaseEnchantmentKey) != null;

        public ICommand OpenBaseEnchantmentCommand => new RelayCommand(() =>
        {
            var ench = _selectedEnchantment;
            if (ench?.IsDerived != true) return;

            NavigateToEnchantment(ench.BaseEnchantmentKey);
        });

        // --- Eigene Enchantments anlegen und löschen ---

        public bool IsUserCreatedSelected => _selectedEnchantment?.IsUserCreated == true;

        // The two closed vocabularies. Offering them as lists rather than free text is what makes a
        // wrong value impossible - the "<Unknown: 0>" enchant type this tool used to write came from
        // nobody choosing at all. CastType matches SkyPatcher's four documented castType= values.
        public IReadOnlyList<string> CastTypeChoices { get; } =
            new[] { "ConstantEffect", "FireAndForget", "Concentration", "Scroll" };

        public IReadOnlyList<string> EnchantTypeChoices { get; } =
            new[] { "Enchantment", "StaffEnchantment" };

        // Whether the worn-restriction panel is worth showing at all.
        //
        // Measured across the load order: of 1018 enchantments exactly 81 carry a worn-restriction
        // list, and every one of them is ConstantEffect. No weapon and no staff enchantment uses the
        // field - 0 of 937. So on those it is a permanently empty panel.
        //
        // But the category is a guess (cast type, which the engine does not enforce), and hiding a
        // field hides DATA, which nobody reports as missing - they just assume the tool cannot do it.
        // So an assigned list always wins over the category: the panel can be hidden only where
        // there is provably nothing to lose.
        public bool ShowWornRestrictions => ShouldShowWornRestrictions(_selectedEnchantment);

        // Static so the rule can be tested without standing a whole view model up behind it.
        internal static bool ShouldShowWornRestrictions(EnchantmentRecord ench)
        {
            if (ench == null) return false;

            // An assigned list beats the category, always. This is the line that makes hiding safe.
            if (!KeyFactory.IsUnsetKey(ench.WornRestrictionListKey)) return true;

            return EnchantmentCategoryHelper.Classify(ench) == EnchantmentCategory.Armor;
        }

        // Legt einen leeren Record an und wählt ihn aus. Ohne CastType/TargetType: die entscheidet
        // der erste Effekt (1.893 von 1.895 Effektzeilen stimmen mit ihrem Enchantment überein).
        public ICommand NewEnchantmentCommand => new RelayCommand(() =>
        {
            EnchantmentRecord created;
            try
            {
                created = _enchantmentService.CreateEnchantment("", "New enchantment");
            }
            catch (Exception ex)
            {
                AppLogger.LogError("Creating an enchantment failed", ex);
                System.Windows.MessageBox.Show(
                    $"The enchantment could not be created:{Environment.NewLine}{ex.Message}",
                    "New enchantment", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
                return;
            }

            // Der Baum wird aus der Datenbank gebaut; danach ist der neue Record die Instanz, die
            // überall benutzt wird - nicht die zurückgegebene.
            BuildEnchantmentTree();
            RefreshWornRestrictionListChoices();
            UpdateEnchantmentFilteredTree(TreeItems.ToList());

            NavigateToEnchantment(created.Key);

            IssueHub.Current.Report(new AppIssue(
                AppIssueSeverity.Info,
                $"{created.EditorID} created. It reaches the game through the generated ESP — " +
                "give it at least one effect, then assign it to an item.",
                Category: "enchantment"));
        });

        public ICommand DeleteEnchantmentCommand => new RelayCommand(() =>
        {
            var ench = _selectedEnchantment;
            if (ench?.IsUserCreated != true) return;

            DeleteUserEnchantment(ench,
                $"Delete '{ench.EditorID}' for good? It is yours alone — no plugin defines it, so " +
                "this cannot be undone by a rescan.",
                "Delete enchantment");
        });

        // "Back to how it was created" for a record no plugin defines: the shadow columns go, the
        // effects go, and the panel is rebuilt from what the database now actually holds. Going
        // through RefreshData rather than restoring field by field is deliberate - there is no
        // original row to restore FROM, so the database is the only truth left.
        private void ResetUserEnchantmentToCreatedState(EnchantmentRecord ench)
        {
            var answer = System.Windows.MessageBox.Show(
                $"Reset '{ench.EditorID}' to the empty enchantment it started as? Its effects and edits go," +
                $" the record itself stays." +
                $"{Environment.NewLine}{Environment.NewLine}To remove it entirely, use Delete instead.",
                "Reset enchantment", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning);
            if (answer != System.Windows.MessageBoxResult.Yes) return;

            var key = ench.Key;
            _enchantmentService.ResetEnchantmentEdits(key);
            _enchantmentService.ResetEnchantmentEffects(key);

            RefreshData(_activePlugins);
            NavigateToEnchantment(key);
        }

        // Shared by the Delete button and by the plugin panel's Delete, which is the same operation
        // reached from the other direction.
        private void DeleteUserEnchantment(EnchantmentRecord ench, string question, string title)
        {
            if (ench?.IsUserCreated != true) return;

            var answer = System.Windows.MessageBox.Show(
                question, title, System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Warning);
            if (answer != System.Windows.MessageBoxResult.Yes) return;

            bool gone;
            try
            {
                gone = _enchantmentService.DeleteEnchantment(ench.Key);
            }
            catch (Exception ex)
            {
                AppLogger.LogError($"Deleting enchantment {ench.Key} failed", ex);
                System.Windows.MessageBox.Show(
                    $"The enchantment could not be deleted:{Environment.NewLine}{ex.Message}",
                    "Delete enchantment", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
                return;
            }

            if (!gone) return;

            // Items that still point at it would now carry a dead reference. Saying so beats letting
            // the patch discover it later.
            IssueHub.Current.Report(new AppIssue(
                AppIssueSeverity.Info,
                $"{ench.EditorID} deleted. Any item you assigned it to now points at nothing — " +
                "check those items before generating a patch.",
                Category: "enchantment"));

            SelectedNode = null;
            BuildEnchantmentTree();
            RecomputeEditedEnchantmentCount();
            UpdateEnchantmentFilteredTree(TreeItems.ToList());
        }

        // --- Der Weg nach unten: zu den Stufen-Varianten ---
        //
        // Der Gegenrichtung von "Open base". Nicht überflüssig neben der Suche: von 68 Familien
        // teilen nur 39 den Namensstamm ihrer Basis - bei 29 führt kein Suchbegriff zur Familie.
        // `EnchRobesAlterationBase` heißt seine Kinder `EnchRobesCollegeAlteration*`, und
        // `EnchArmorArticulation01` hat `DA03ClavicusMaskEnch` als Kind. Dass die Maske von Clavicus
        // Vile zur Articulation-Familie gehört, steht nirgends außer in der Verknüpfung.

        public string VariantsLabel => $"Variants ({FamilyChildCount})  ▾";

        // Beide Knöpfe sitzen in einer Zeile, jeder mit eigener Sichtbarkeit - 15 Records sind
        // Basis UND Kind und zeigen deshalb beide.
        public bool ShowLineageRow => HasBaseEnchantment || HasFamilyChildren;

        private bool _isVariantListOpen;
        public bool IsVariantListOpen
        {
            get => _isVariantListOpen;
            set => SetProperty(ref _isVariantListOpen, value);
        }

        public sealed class EnchantmentLinkVM
        {
            public string Key { get; init; } = "";
            public string Label { get; init; } = "";
        }

        public IReadOnlyList<EnchantmentLinkVM> FamilyChildLinks =>
            CurrentChildren
                .OrderBy(c => c.EditorID, StringComparer.OrdinalIgnoreCase)
                .Select(c => new EnchantmentLinkVM { Key = c.Key, Label = VariantLabel(c) })
                .ToList();

        // "EnchArmorFortifyBlock04 — 30". The value is the point: the tiers ARE a ladder, so the
        // number is what tells them apart - the EditorID alone would make picking the right one a
        // guess. Magnitude where there is one, duration otherwise (SoulTrap and friends carry their
        // ladder in the seconds).
        internal static string VariantLabel(EnchantmentRecord variant)
        {
            var name = string.IsNullOrWhiteSpace(variant.EditorID) ? variant.Key : variant.EditorID;

            var first = variant.Effects?.FirstOrDefault();
            if (first == null) return name;

            if (first.Magnitude != 0)
                return $"{name}  —  {first.Magnitude.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)}";
            if (first.Duration != 0) return $"{name}  —  {first.Duration}s";

            return name;
        }

        public ICommand OpenVariantCommand => new RelayCommand<string>(key =>
        {
            if (string.IsNullOrEmpty(key)) return;

            IsVariantListOpen = false;
            NavigateToEnchantment(key);
        });

        // Shows an enchantment: in the detail panel AND in the tree. Written as its own step rather
        // than folded into the command, because the same move is what every further jump target of
        // Prio 9 needs.
        //
        // Three things have to line up, and the filtered tree is why: it is a COPY, rebuilt whenever
        // the filter runs. The target has to survive the filter, its ancestors have to be expanded
        // (an unrealised TreeViewItem cannot be selected), and the node that carries IsSelected has
        // to be the one currently IN that copy - not the original it was cloned from.
        private void NavigateToEnchantment(string key)
        {
            var target = FindEnchantmentLeaf(key);
            if (target?.Enchantment == null)
            {
                IssueHub.Current.Report(new AppIssue(
                    AppIssueSeverity.Warning,
                    $"Base enchantment {key} is not in the scanned load order - nothing to jump to.",
                    Category: "navigation"));
                return;
            }

            // A filter that hides the target would land the jump on an invisible row. Clearing it is
            // the honest way out: the user asked to be taken there, not to be taken somewhere near.
            if (!TryFindPath(EnchantementFilteredTree, target.Enchantment, new List<EnchantmentTreeNode>()))
                ClearEnchantmentFiltersAndRebuild();

            SelectedEnchantment = target.Enchantment;
            TrySelectInTree(EnchantementFilteredTree, target.Enchantment);
        }

        private void ClearEnchantmentFiltersAndRebuild()
        {
            _enchantmentTreeSearchText = "";
            _showOnlyEditedEnchantments = false;
            _showOnlyBaseEnchantments = false;
            OnPropertyChanged(nameof(EnchantmentTreeSearchText));
            OnPropertyChanged(nameof(ShowOnlyEditedEnchantments));
            OnPropertyChanged(nameof(ShowOnlyBaseEnchantments));

            // Rebuilt here and now, not through the debouncer: the selection below has to happen on
            // the tree that is on screen, and a debounced rebuild would replace it 120 ms later -
            // taking the selection with it. With no filter set this is exactly what the background
            // filter would have produced (see FilterEnchantmentTreeOnBackground's early return).
            UpdateEnchantmentFilteredTree(TreeItems.ToList());
        }

        // Selects the node carrying this record and expands everything above it. Static and taking
        // the roots so it can be tested without a view model behind it.
        internal static bool TrySelectInTree(IEnumerable<EnchantmentTreeNode> roots, EnchantmentRecord record)
        {
            var path = new List<EnchantmentTreeNode>();
            if (!TryFindPath(roots, record, path)) return false;

            // Ancestors first - a TreeViewItem whose parent is collapsed was never realised, and an
            // unrealised container cannot take the selection.
            for (int i = 0; i < path.Count - 1; i++)
                path[i].IsExpanded = true;

            // The TreeView clears the previously selected row on its own, and the two-way binding
            // carries that back into its node - so nothing has to be deselected here.
            path[^1].IsSelected = true;
            return true;
        }

        internal static bool TryFindPath(IEnumerable<EnchantmentTreeNode> nodes, EnchantmentRecord record,
                                         List<EnchantmentTreeNode> path)
        {
            foreach (var node in nodes)
            {
                path.Add(node);

                if (node is EnchantmentLeafNode leaf && ReferenceEquals(leaf.Enchantment, record)) return true;
                if (TryFindPath(node.Children, record, path)) return true;

                path.RemoveAt(path.Count - 1);
            }
            return false;
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
                // Clearing the list can take the panel away; assigning one always brings it back.
                OnPropertyChanged(nameof(ShowWornRestrictions));
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
            _originalFlags = original?.Flags ?? 0;
            _originalChargeTime = original?.ChargeTime ?? 0f;
            _originalEnchantmentAmount = original?.EnchantmentAmount ?? 0;

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
            OnPropertyChanged(nameof(IsEnchantmentFlagsChanged));
            OnPropertyChanged(nameof(IsNoAutoCalcChanged));
            OnPropertyChanged(nameof(IsExtendDurationOnRecastChanged));
            OnPropertyChanged(nameof(IsEnchantmentChargeTimeChanged));
            OnPropertyChanged(nameof(IsEnchantmentAmountChanged));
            OnPropertyChanged(nameof(IsEnchantmentEffectsChanged));
            OnPropertyChanged(nameof(IsWornRestrictionKeywordsChanged));
            OnPropertyChanged(nameof(HasWornRestrictionChanges));
            OnPropertyChanged(nameof(IsWornRestrictionListAssignmentChanged));
            OnPropertyChanged(nameof(HasWornRestrictionChanges));
            OnPropertyChanged(nameof(HasEnchantmentChanges));

            // The effect list decides what the "add effect" picker may still offer - a selection
            // switch and a reset both rebuild it.
            OnPropertyChanged(nameof(FilteredMagicEffects));
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

        public bool IsEnchantmentFlagsChanged =>
            _hasEnchantmentSnapshot && _selectedEnchantment != null
            && _selectedEnchantment.Flags != _originalFlags;

        // Per BIT, not per field. Flags is one int in the database, but the user sees two check
        // boxes - and marking both because one of them moved points at the wrong one half the time.
        // Each box answers for its own bit; IsEnchantmentFlagsChanged above stays the field-level
        // answer that HasEnchantmentChanges and the reset path need.
        // Static so the rule can be tested without standing up the view model, the same seam
        // ShouldShowWornRestrictions uses. Masking both sides matters: comparing the whole int would
        // light up BOTH boxes as soon as either bit moved, and comparing the raw bit values would
        // miss that 0x04 and 0x00 are "the same bit, different state".
        internal static bool FlagBitDiffers(int current, int original, int bit)
            => (current & bit) != (original & bit);

        private bool FlagBitChanged(int bit) =>
            _hasEnchantmentSnapshot && _selectedEnchantment != null
            && FlagBitDiffers(_selectedEnchantment.Flags, _originalFlags, bit);

        public bool IsNoAutoCalcChanged => FlagBitChanged(EnchantmentRecord.FlagNoAutoCalc);

        public bool IsExtendDurationOnRecastChanged => FlagBitChanged(EnchantmentRecord.FlagExtendDurationOnRecast);

        public bool IsEnchantmentChargeTimeChanged =>
            _hasEnchantmentSnapshot && _selectedEnchantment != null
            && System.Math.Abs(_selectedEnchantment.ChargeTime - _originalChargeTime) > 0.0001f;

        public bool IsEnchantmentAmountChanged =>
            _hasEnchantmentSnapshot && _selectedEnchantment != null
            && _selectedEnchantment.EnchantmentAmount != _originalEnchantmentAmount;

        public bool HasEnchantmentChanges =>
            IsEnchantmentNameChanged || IsEnchantmentCostChanged || IsEnchantmentEffectsChanged
            || IsWornRestrictionListAssignmentChanged
            || IsEnchantmentFlagsChanged || IsEnchantmentChargeTimeChanged || IsEnchantmentAmountChanged;

        // The dot on the collapsed Worn Restrictions header. Both kinds of change count, even though
        // they are not the same thing to the rest of the view model: re-pointing the enchantment at
        // another list is its own edit, editing that list's keywords changes a SHARED record. From
        // "did I touch anything in there" they are the same question, and a collapsed section that
        // hides an edit is exactly what the header dot exists to prevent.
        public bool HasWornRestrictionChanges =>
            IsWornRestrictionListAssignmentChanged || IsWornRestrictionKeywordsChanged;

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

            // Reset never deletes - Delete is its own button, right next to this one. For a record
            // the user created that means "back to how it was created": no effects, the name it was
            // given, cost 0. The bug worth naming is what used to happen instead: the wipe below ran
            // first and GetOriginalEnchantment then returned null for it, so the restore bailed out
            // halfway and the panel kept showing values the database no longer held.
            if (ench.IsUserCreated)
            {
                ResetUserEnchantmentToCreatedState(ench);
                return;
            }

            _enchantmentService.ResetEnchantmentEdits(ench.Key);
            var original = _enchantmentService.GetOriginalEnchantment(ench.Key);
            if (original == null) return;

            ench.FieldChanged -= OnEnchantmentFieldChanged;
            ench.Name = original.Name;
            ench.EnchantmentCost = original.EnchantmentCost;
            ench.WornRestrictionListKey = original.WornRestrictionListKey;

            // The rest of ENIT. ResetEnchantmentEdits above already cleared their shadow columns, so
            // the database is right either way - but without these three the panel kept showing the
            // edited numbers until the user selected something else and came back. Everything the
            // shadow columns cover has to be restored here, or the two drift apart.
            ench.Flags = original.Flags;
            ench.ChargeTime = original.ChargeTime;
            ench.EnchantmentAmount = original.EnchantmentAmount;

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

            // The tree on screen is a filtered COPY, rebuilt only when the filter runs — nothing in
            // it recomputes itself. Both filters can be invalidated by this reset: "only edited"
            // because the row just stopped being edited, and the search text because the reverted
            // Name is part of what it matches on. Without this the reset row stays listed (and
            // selectable) until the next keystroke or rescan. Same rule as
            // MainContentVM.NotifyItemEditedStateChanged on the item side.
            if (_showOnlyEditedEnchantments || !string.IsNullOrWhiteSpace(_enchantmentTreeSearchText))
                ApplyEnchantmentFilterDebounced(_enchantmentTreeSearchText);
        });

        // --- Effekte hinzufügen / entfernen ---
        //
        // Braucht keine ESP: mgefsToAdd/mgefsToRemove gibt der EnchantmentRuleBuilder längst aus, und
        // das Zurücksetzen steht auch schon — EnchantmentEffects_Original hält den gescannten Stand,
        // das EffectsEdited-Flag am Eltern-Record schützt ihn vor dem nächsten Scan. Ein entfernter
        // Block ist damit nach dem Reset wieder da, ein hinzugefügter wieder weg. Hier fehlte nur die
        // Bedienung.

        private string _magicEffectSearchText = "";

        // True only while a pick is being put back into the box. See SelectedMagicEffectToAdd.
        private bool _isSyncingMagicEffectPicker;

        public string MagicEffectSearchText
        {
            get => _magicEffectSearchText;
            set
            {
                // Only the user typing moves the search text on its own. The ComboBox also writes
                // here - the Text binding is TwoWay - including the empty string it produces by
                // itself while its ItemsSource is being swapped.
                if (_isSyncingMagicEffectPicker) return;

                if (SetProperty(ref _magicEffectSearchText, value ?? ""))
                    OnPropertyChanged(nameof(FilteredMagicEffects));
            }
        }

        private void SetMagicEffectSearchText(string text)
        {
            if (SetProperty(ref _magicEffectSearchText, text ?? "", nameof(MagicEffectSearchText)))
                OnPropertyChanged(nameof(FilteredMagicEffects));
        }

        private MagicEffectsRecords _selectedMagicEffectToAdd;
        public MagicEffectsRecords SelectedMagicEffectToAdd
        {
            get => _selectedMagicEffectToAdd;
            set
            {
                // Same trap as the item's enchantment picker: an editable ComboBox whose filtered
                // rows no longer contain the selection reports null. Taken at face value the Add
                // button would go dead the moment the user types.
                if (value == null) return;
                if (!SetProperty(ref _selectedMagicEffectToAdd, value)) return;

                // THE BUG THIS EXISTS FOR, reported as "it is selected, but the line shows nothing":
                // picking a row makes the ComboBox write the row's text back through the Text
                // binding, which re-runs the filter, which hands the control a new ItemsSource -
                // whereupon it drops its own SelectedItem and clears its text. The view model still
                // held the pick (the null above is ignored), so Add worked while the box looked
                // empty. Writing the label here, past the guard, and re-announcing both properties
                // is what puts the row back on screen.
                _isSyncingMagicEffectPicker = true;
                try
                {
                    SetMagicEffectSearchText(value.Label);
                }
                finally
                {
                    _isSyncingMagicEffectPicker = false;
                    OnPropertyChanged(nameof(MagicEffectSearchText));
                    OnPropertyChanged(nameof(SelectedMagicEffectToAdd));
                }

                OnPropertyChanged(nameof(CanAddEffect));
            }
        }

        // Nach dem Hinzufügen steht die Box auf einem Effekt, den die Auswahl nicht mehr anbietet
        // (er ist jetzt am Enchantment) - die Liste wäre leer und der Text ein Eintrag, den es dort
        // nicht mehr gibt. Also zurück auf Anfang für den nächsten.
        private void ClearMagicEffectPicker()
        {
            _selectedMagicEffectToAdd = null;

            _isSyncingMagicEffectPicker = true;
            try { SetMagicEffectSearchText(""); }
            finally
            {
                _isSyncingMagicEffectPicker = false;
                OnPropertyChanged(nameof(MagicEffectSearchText));
                OnPropertyChanged(nameof(SelectedMagicEffectToAdd));
            }

            OnPropertyChanged(nameof(CanAddEffect));
        }

        public IEnumerable<MagicEffectsRecords> FilteredMagicEffects =>
            AddableMagicEffects(AllMagicEffects, EffectVMs.Select(vm => vm.Model.MagicEffectKey), MagicEffectSearchText);

        // What may still be added, and why anything is excluded at all: EnchantmentEffects has
        // PRIMARY KEY(EnchantmentKey, MagicEffectKey), so one enchantment can carry a given effect
        // exactly once. Vanilla records do occasionally list the same MGEF twice with different
        // values - that shape simply cannot be stored here (see ByMgef in EnchantmentRuleBuilder for
        // the same limitation on the emitting side), so the picker hides what is already in the list
        // rather than letting the save silently drop it.
        internal static IEnumerable<MagicEffectsRecords> AddableMagicEffects(
            IEnumerable<MagicEffectsRecords> catalogue, IEnumerable<string> alreadyUsedKeys, string search)
        {
            var used = new HashSet<string>(
                (alreadyUsedKeys ?? Enumerable.Empty<string>()).Where(k => !string.IsNullOrEmpty(k)),
                StringComparer.OrdinalIgnoreCase);

            var hits = (catalogue ?? Enumerable.Empty<MagicEffectsRecords>())
                .Where(m => m != null && !used.Contains(m.Key));

            if (!string.IsNullOrWhiteSpace(search))
                hits = hits.Where(m =>
                    // The LABEL first, and that is not a nicety: what a row shows is
                    // "EditorID | Name", and picking one puts exactly that string into the box.
                    // Matching only the halves meant the search for a whole label found NOTHING -
                    // the list emptied, the ComboBox had no row left to hold, and it dropped its
                    // selection and cleared the line. Reported as "it is selected, but the line
                    // shows nothing".
                    m.Label.Contains(search, StringComparison.OrdinalIgnoreCase)
                    || (m.EditorID ?? "").Contains(search, StringComparison.OrdinalIgnoreCase)
                    || (m.Name ?? "").Contains(search, StringComparison.OrdinalIgnoreCase)
                    || (m.Key ?? "").Contains(search, StringComparison.OrdinalIgnoreCase));

            return hits.ToList();
        }

        public bool CanAddEffect => _selectedEnchantment != null && _selectedMagicEffectToAdd != null;

        public ICommand AddEffectCommand => new RelayCommand(async () =>
        {
            var ench = _selectedEnchantment;
            var mgef = _selectedMagicEffectToAdd;
            if (ench == null || mgef == null) return;
            if (EffectVMs.Any(vm => string.Equals(vm.Model.MagicEffectKey, mgef.Key, StringComparison.OrdinalIgnoreCase)))
                return;

            var record = new EnchantmentEffectRecord
            {
                EnchantmentKey = ench.Key,
                MagicEffectKey = mgef.Key,
                EditorID = mgef.EditorID ?? "",
                Name = mgef.Name ?? "",
            };

            ench.Effects.Add(record);

            var effectVm = new EnchantmentEffectViewModel(record, AllMagicEffects);
            effectVm.PropertyChanged += OnEffectPropertyChanged;
            EffectVMs.Add(effectVm);

            // Was ein neu angelegtes Enchantment IST, entscheidet sein erster Effekt - nicht eine
            // Frage an den Anwender. Gemessen an der echten Ladereihenfolge stimmen 1.893 von 1.895
            // Effektzeilen im CastType mit ihrem Enchantment überein und 1.894 von 1.895 im
            // TargetType. ConstantEffect/Self ist die Rüstungsform, FireAndForget/Touch die einer
            // Waffe; davon hängt auch ab, in welchem Zweig des Baums der Record landet.
            bool adoptedType = ench.IsUserCreated && string.IsNullOrEmpty(ench.CastType) && EffectVMs.Count == 1;
            if (adoptedType)
            {
                ench.CastType = mgef.CastType ?? "";
                ench.TargetType = mgef.TargetType ?? "";

                _enchantmentService.UpdateEnchantmentCastType(ench.Key, ench.CastType);
                _enchantmentService.UpdateEnchantmentTargetType(ench.Key, ench.TargetType);

                IssueHub.Current.Report(new AppIssue(
                    AppIssueSeverity.Info,
                    $"{ench.EditorID} is now {ench.CastType}/{ench.TargetType} — taken from its first effect, " +
                    "and has moved to the matching branch of the tree.",
                    Category: "enchantment"));
            }

            ClearMagicEffectPicker();

            await PersistEffectsAsync("added");

            // Der Zweig, in dem ein Enchantment hängt, wird aus seinem CastType bestimmt und beim
            // Bauen des Baums festgelegt - ändert sich der Typ, hängt der Knoten am falschen Ast.
            // Ein frisch angelegter Record hat noch gar keinen und liegt deshalb unter "Other"; mit
            // seinem ersten Effekt gehört er zu Rüstung oder Waffe. Also neu bauen und den Record
            // wieder aufsuchen - BuildEnchantmentTree liest aus der Datenbank, die Instanz danach
            // ist eine andere, weshalb der Sprung über den Schlüssel geht und nicht über das Objekt.
            if (adoptedType)
            {
                var key = ench.Key;

                BuildEnchantmentTree();
                UpdateEnchantmentFilteredTree(TreeItems.ToList());
                NavigateToEnchantment(key);
            }
        });

        public ICommand RemoveEffectCommand => new RelayCommand<EnchantmentEffectViewModel>(async effectVm =>
        {
            var ench = _selectedEnchantment;
            if (ench == null || effectVm == null) return;

            effectVm.PropertyChanged -= OnEffectPropertyChanged;
            EffectVMs.Remove(effectVm);

            var record = ench.Effects.FirstOrDefault(e =>
                string.Equals(e.MagicEffectKey, effectVm.Model.MagicEffectKey, StringComparison.OrdinalIgnoreCase));
            if (record != null) ench.Effects.Remove(record);

            await PersistEffectsAsync("removed");
        });

        // Written straight through instead of through the debouncer. Adding or removing a block is a
        // structural change, and this view model shares ONE debouncer for every autosave - a pending
        // Name save would be dropped in favour of this one (see Debouncer's class comment). The same
        // reason MainContentVM persists its bulk paths directly.
        private async Task PersistEffectsAsync(string what)
        {
            var ench = _selectedEnchantment;
            if (ench == null) return;

            MarkSelectedEnchantmentEdited();

            OnPropertyChanged(nameof(FilteredMagicEffects));
            OnPropertyChanged(nameof(IsEnchantmentEffectsChanged));
            OnPropertyChanged(nameof(HasEnchantmentChanges));

            var request = new SaveRequest(null, "Effects")
            {
                Enchantment = ench,
                Effects = EffectVMs.ToList(),
            };

            try
            {
                await _saveRequestService.SaveAsync(request);
            }
            catch (Exception ex)
            {
                AppLogger.LogError($"Saving enchantment effects failed ({what}, key={ench.Key})", ex);
                IssueHub.Current.Report(new AppIssue(
                    AppIssueSeverity.Error,
                    $"The effect could not be {what} - see Logs\\error.log.",
                    Category: "enchantment"));
            }
        }

        // --- Die Effektliste auf die Stufen-Varianten übertragen ---

        private IReadOnlyList<EnchantmentRecord> CurrentChildren =>
            _selectedEnchantment == null
                ? Array.Empty<EnchantmentRecord>()
                : EnchantmentFamily.ChildrenOf(_selectedEnchantment, AllEnchantmentRecords());

        public int FamilyChildCount => CurrentChildren.Count;

        public bool HasFamilyChildren => FamilyChildCount > 0;

        public string ApplyToFamilyLabel =>
            FamilyChildCount == 1 ? "Apply to 1 variant…" : $"Apply to {FamilyChildCount} variants…";

        // Adds what this enchantment carries and the variant does not, and removes what the variant
        // carries and this one no longer does. The numbers are SCALED, not copied — see
        // Services/EnchantmentFamily for what the load order says about tier ladders — and every
        // proposal is shown before anything is written.
        public ICommand ApplyToFamilyCommand => new RelayCommand(async () =>
        {
            var parent = _selectedEnchantment;
            if (parent == null) return;

            var children = CurrentChildren;
            if (children.Count == 0) return;

            // The panel's live effect list is the truth here, not the record's - the user may have
            // added a block seconds ago.
            parent.Effects = new ObservableCollection<EnchantmentEffectRecord>(EffectVMs.Select(vm => vm.Model));

            var byKey = AllMagicEffects.ToDictionary(m => m.Key, StringComparer.OrdinalIgnoreCase);
            var plan = EnchantmentFamily.Plan(
                parent, children,
                mgef => !byKey.TryGetValue(mgef, out var m) || m.HasMagnitude,
                mgef => byKey.TryGetValue(mgef, out var m) ? m.Label : mgef);

            if (plan.Count == 0)
            {
                System.Windows.MessageBox.Show(
                    "Every variant already carries exactly these effects — nothing to do.",
                    "Apply to variants", System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Information);
                return;
            }

            var accepted = View.EnchantmentFamilyWindow.Show(
                System.Windows.Application.Current?.MainWindow, parent, plan, children.Count);

            if (accepted == null || accepted.Count == 0) return;

            await ApplyFamilyChangesAsync(accepted);
        });

        private async Task ApplyFamilyChangesAsync(IReadOnlyList<FamilyChangeRowVM> rows)
        {
            int written = 0;

            // One save per variant, not per row: SaveEnchantmentEffects replaces a record's whole
            // effect list (and takes its pristine snapshot on the first edit), so every change to the
            // same variant has to be in the list that gets written.
            foreach (var group in rows.GroupBy(r => r.Change.Child))
            {
                var child = group.Key;

                var effects = EnchantmentFamily.ApplyTo(
                    child.Key,
                    child.Effects,
                    group.Select(r => (r.Change, r.Magnitude, r.Duration, r.Area)),
                    mgef => (EffectEditorId(mgef), EffectName(mgef)));

                try
                {
                    _enchantmentService.SaveEnchantmentEffects(child.Key, effects);
                }
                catch (Exception ex)
                {
                    AppLogger.LogError($"Applying effects to variant {child.Key} failed", ex);
                    IssueHub.Current.Report(new AppIssue(
                        AppIssueSeverity.Error,
                        $"{child.EditorID}: the effects could not be written — see Logs\\error.log.",
                        Category: "enchantment"));
                    continue;
                }

                child.Effects = new ObservableCollection<EnchantmentEffectRecord>(effects);

                if (!child.IsEdited)
                {
                    child.IsEdited = true;
                    EditedEnchantmentCount++;
                }

                written++;
            }

            // The variants now differ from their scan, so the "only edited" tree has to be rebuilt.
            ApplyEnchantmentFilterDebounced(_enchantmentTreeSearchText);

            IssueHub.Current.Report(new AppIssue(
                AppIssueSeverity.Info,
                $"{rows.Count} effect change(s) written to {written} variant(s).",
                Category: "enchantment"));

            await Task.CompletedTask;
        }

        private string EffectEditorId(string mgefKey) =>
            AllMagicEffects.FirstOrDefault(m => string.Equals(m.Key, mgefKey, StringComparison.OrdinalIgnoreCase))?.EditorID ?? "";

        private string EffectName(string mgefKey) =>
            AllMagicEffects.FirstOrDefault(m => string.Equals(m.Key, mgefKey, StringComparison.OrdinalIgnoreCase))?.Name ?? "";

        // Every scanned enchantment, from the unfiltered tree - the same records the detail panel
        // edits, so a change made here is visible everywhere at once.
        private List<EnchantmentRecord> AllEnchantmentRecords()
        {
            var all = new List<EnchantmentRecord>();
            foreach (var root in TreeItems)
                Collect(root, all);
            return all;

            static void Collect(EnchantmentTreeNode node, List<EnchantmentRecord> into)
            {
                if (node is EnchantmentLeafNode leaf) into.Add(leaf.Enchantment);
                foreach (var child in node.Children) Collect(child, into);
            }
        }

        // --- Autosave wiring ---

        private void OnEnchantmentFieldChanged(string fieldName)
        {
            var ench = _selectedEnchantment;
            if (ench == null) return;

            MarkSelectedEnchantmentEdited();
            OnPropertyChanged(nameof(IsEnchantmentNameChanged));
            OnPropertyChanged(nameof(IsEnchantmentCostChanged));
            OnPropertyChanged(nameof(IsEnchantmentFlagsChanged));
            OnPropertyChanged(nameof(IsNoAutoCalcChanged));
            OnPropertyChanged(nameof(IsExtendDurationOnRecastChanged));
            OnPropertyChanged(nameof(IsEnchantmentChargeTimeChanged));
            OnPropertyChanged(nameof(IsEnchantmentAmountChanged));
            OnPropertyChanged(nameof(HasEnchantmentChanges));

            // The category is derived from EnchantType and CastType, and the worn-restriction panel
            // hangs off the category - so switching either has to re-ask.
            if (fieldName is nameof(EnchantmentRecord.EnchantType) or nameof(EnchantmentRecord.CastType))
                OnPropertyChanged(nameof(ShowWornRestrictions));

            DebouncerFor(fieldName).DebounceAsync(350, async ct =>
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

            DebouncerFor("Effects").DebounceAsync(350, async ct =>
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
            OnPropertyChanged(nameof(HasWornRestrictionChanges));
            OnPropertyChanged(nameof(HasEnchantmentChanges));
            OnPropertyChanged(nameof(CanResetWornRestrictionList));

            DebouncerFor("WornRestrictionKeywords").DebounceAsync(350, async ct =>
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
        // The plugin and category dots hang off this. Raising them here rather than at each call
        // site is deliberate: the count moves in both directions - up when a record is marked, down
        // when one is reset - and the reset path is exactly the one that was missing its nudge.
        // Anything that changes the count changes whether an ancestor still has an edited record
        // under it, so there is no call site that may skip this.
        public int EditedEnchantmentCount
        {
            get => _editedEnchantmentCount;
            private set
            {
                if (SetProperty(ref _editedEnchantmentCount, value))
                    RaiseTreeEditedFlags();
            }
        }

        // Worn-restriction keywords are stored per FLST. An enchantment with no FLST has no list to
        // attach keywords to, so the panel is disabled for it.
        public bool CanEditWornRestrictions =>
            _selectedEnchantment != null && !KeyFactory.IsUnsetKey(_selectedEnchantment.WornRestrictionListKey);

        public int PluginCount => EnchantementFilteredTree.Count;

        // Walk the (unfiltered) tree once and count leaves whose record is edited.
        // Both trees, and for the same reason as MainContentVM.RaiseTreeEditedFlags: the filter hands
        // the TreeView copies of the folder nodes, which share the records but not the notifications.
        private void RaiseTreeEditedFlags()
        {
            foreach (var root in TreeItems) Raise(root);
            foreach (var root in EnchantementFilteredTree) Raise(root);

            static void Raise(EnchantmentTreeNode node)
            {
                node.RaiseHasEditedDescendant();
                foreach (var c in node.Children) Raise(c);
            }
        }

        private void RecomputeEditedEnchantmentCount()
        {
            int n = 0;
            foreach (var plugin in TreeItems)
                CountEdited(plugin, ref n);
            EditedEnchantmentCount = n;

            // Explicitly, not only through the setter: a rebuild can replace WHICH records are
            // edited while the total stays the same, and then SetProperty raises nothing.
            RaiseTreeEditedFlags();

            static void CountEdited(EnchantmentTreeNode node, ref int n)
            {
                if (node is EnchantmentLeafNode { Enchantment.IsEdited: true }) n++;
                foreach (var c in node.Children) CountEdited(c, ref n);
            }
        }

        // Called from every enchantment edit entry point (fields / effects / keywords).
        private void MarkSelectedEnchantmentEdited()
        {
            if (_selectedEnchantment == null || _selectedEnchantment.IsEdited) return;
            _selectedEnchantment.IsEdited = true;
            EditedEnchantmentCount++;   // its setter raises the ancestors' dots
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

            // Type-preserving: a plugin has to come back a plugin, or the templates that pick by
            // DataType would dress it as something else.
            var newRoot = root.CloneWithoutChildren();

            // Leaf: gate on edited / base-only state first, then on the search text.
            if (root is EnchantmentLeafNode leaf)
            {
                if (onlyEdited && !leaf.Enchantment.IsEdited)
                    return null;
                if (onlyBase && leaf.Enchantment.IsDerived)
                    return null;
                if (!searching || EnchantmentMatches(leaf.Enchantment, search))
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
                var pluginNode = new EnchantmentPluginNode
                {
                    DisplayName = pluginGroup.Key,
                    Menu = this,
                };

                var weaponNode = new EnchantmentCategoryNode { DisplayName = "Weapon Enchantments" };
                var armorNode = new EnchantmentCategoryNode { DisplayName = "Armor Enchantments" };
                var staffNode = new EnchantmentCategoryNode { DisplayName = "Staff Enchantments" };
                var otherNode = new EnchantmentCategoryNode { DisplayName = "Other" };

                foreach (var ench in pluginGroup.OrderBy(e => e.Name))
                {
                    var node = new EnchantmentLeafNode
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

                        case EnchantmentCategory.Staff:
                            staffNode.Children.Add(node);
                            break;

                        default:
                            otherNode.Children.Add(node);
                            break;
                    }
                }

                // Empty categories stay out of the tree, which is also what keeps "Other" from
                // showing up as an always-empty branch once every record has been rescanned.
                if (weaponNode.Children.Any()) pluginNode.Children.Add(weaponNode);
                if (armorNode.Children.Any()) pluginNode.Children.Add(armorNode);
                if (staffNode.Children.Any()) pluginNode.Children.Add(staffNode);
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

        // --- The same three actions, scoped to one plugin (the plugin detail panel) ---
        //
        // Scope is the WHOLE plugin, independent of the tree filter, exactly like the item side's
        // PluginNodeVM: the tree node the panel hangs off may be a filtered COPY, so the work is
        // driven off the plugin NAME and the unfiltered TreeItems, never off the node's children.

        private static bool BelongsTo(EditedItemDto i, string plugin)
            => (i.Key ?? "").StartsWith(plugin + "|", StringComparison.OrdinalIgnoreCase);

        // Both halves get reset - Delete is a separate button - but they land somewhere different,
        // and the confirmation has to say which:
        //   scanned (Original = 1)      -> the values its plugin defines come back underneath
        //   user-created (Original = 0) -> the empty enchantment it was created as; nothing is
        //                                  underneath it, so that is as far back as reset goes
        // Split out of the command so the counts can be tested; the command itself is wrapped in
        // MessageBox calls and cannot be.
        internal static (List<EnchantmentRecord> Scanned, List<EnchantmentRecord> Own) SplitForReset(
            IEnumerable<EnchantmentRecord> edited)
        {
            var all = (edited ?? Enumerable.Empty<EnchantmentRecord>()).Where(e => e != null).ToList();
            return (all.Where(e => !e.IsUserCreated).ToList(),
                    all.Where(e => e.IsUserCreated).ToList());
        }

        internal async Task ExportPluginEnchantmentsAsync(string plugin)
        {
            await FlushPendingSavesAsync();

            List<EditedItemDto> items;
            try
            {
                items = _importExportService.GetEditedItems(ExportScope.Plugin, plugin)
                    .Where(IsEnchantSideUnit)
                    .ToList();
            }
            catch (Exception ex)
            {
                AppLogger.LogError($"ExportPluginEnchantmentsAsync ({plugin}) failed", ex);
                System.Windows.MessageBox.Show($"Export failed:{Environment.NewLine}{ex.Message}",
                    "Error", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
                return;
            }

            if (items.Count == 0)
            {
                System.Windows.MessageBox.Show($"'{plugin}' has no edited enchantments to export.",
                    "Export", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
                return;
            }

            var (count, root) = ImportExportFlow.ExportItems(items);
            System.Windows.MessageBox.Show(
                $"{count} enchantment(s) from '{plugin}' exported to{Environment.NewLine}{root}",
                "Export Successful", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
        }

        internal async Task ImportPluginEnchantmentsAsync(string plugin)
        {
            await FlushPendingSavesAsync();

            var items = ImportExportFlow.ReadAllExportedItems(
                i => IsEnchantSideUnit(i) && BelongsTo(i, plugin));

            if (items.Count == 0)
            {
                System.Windows.MessageBox.Show(
                    $"No exported enchantments for '{plugin}' found under{Environment.NewLine}{ExportFileStore.ExportsRoot}",
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
                AppLogger.LogError($"ImportPluginEnchantmentsAsync ({plugin}) failed", ex);
                System.Windows.MessageBox.Show($"Import failed:{Environment.NewLine}{ex.Message}",
                    "Error", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
                return;
            }

            if (result == null) return; // user cancelled the conflict dialog

            try { RefreshData(_activePlugins); }
            catch (Exception ex) { AppLogger.LogError($"ImportPluginEnchantmentsAsync ({plugin}) RefreshData failed", ex); }

            System.Windows.MessageBox.Show(ImportExportFlow.SummaryText(result), "Import Complete",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
        }

        // Reverts every edited enchantment of one plugin. Unlike ResetEnchantmentCommand this does
        // NOT restore the live objects field by field - it clears the shadow columns and then
        // rebuilds from the database. Replaying that delicate in-place restoration across hundreds
        // of records is exactly where it would go wrong; the rebuild is the same path a rescan takes.
        internal async Task ResetPluginEnchantmentsAsync(string plugin)
        {
            // A pending debounced save has to land BEFORE the edited set is read and the shadow
            // columns are cleared - otherwise it fires afterwards and re-marks a just-reset record
            // as edited. Same rule as PluginNodeVM.ResetPluginCommand.
            await FlushPendingSavesAsync();

            var edited = EnchantmentsOf(plugin).Where(e => e.IsEdited).ToList();

            if (edited.Count == 0)
            {
                System.Windows.MessageBox.Show($"'{plugin}' has no edited enchantments to reset.",
                    "Reset plugin", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
                return;
            }

            // Reset does not delete - Delete is its own button on this panel. Records the user
            // created are reset to the empty state they were created in, the same as the single
            // record's Reset does; removing them is the Delete button's job.
            var (scanned, own) = SplitForReset(edited);

            var question =
                $"Revert ALL edits on {edited.Count} enchantment(s) in '{plugin}' - fields and effects?" +
                $"{Environment.NewLine}{Environment.NewLine}{scanned.Count} scanned record(s) go back to the state their plugin defines.";

            if (own.Count > 0)
                question += $" {own.Count} you created yourself go back to the empty enchantment they started as - " +
                            "they are NOT deleted; use Delete for that.";

            question += $"{Environment.NewLine}{Environment.NewLine}This covers the whole plugin, regardless of any active tree filter, and cannot be undone." +
                        $"{Environment.NewLine}{Environment.NewLine}Worn-restriction LIST contents are shared between enchantments and are not touched here - use \"Reset list\" for those.";

            var answer = System.Windows.MessageBox.Show(
                question, "Reset plugin",
                System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning);
            if (answer != System.Windows.MessageBoxResult.Yes) return;

            foreach (var ench in edited)
            {
                _enchantmentService.ResetEnchantmentEdits(ench.Key);
                _enchantmentService.ResetEnchantmentEffects(ench.Key);
            }

            RefreshData(_activePlugins);

            System.Windows.MessageBox.Show(
                $"{edited.Count} enchantment(s) in '{plugin}' reset.",
                "Reset plugin", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
        }

        // Deletes every enchantment of this plugin that the user created. Only ever offered where
        // there are any, which in practice means the tool's own pseudo-plugin - see
        // KeyFactory.UserPluginName.
        internal async Task DeletePluginEnchantmentsAsync(string plugin)
        {
            await FlushPendingSavesAsync();

            var own = EnchantmentsOf(plugin).Where(e => e.IsUserCreated).ToList();

            if (own.Count == 0)
            {
                System.Windows.MessageBox.Show($"'{plugin}' holds none of your own enchantments.",
                    "Delete enchantments", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
                return;
            }

            var answer = System.Windows.MessageBox.Show(
                $"Delete all {own.Count} enchantment(s) you created in '{plugin}' for good?" +
                $"{Environment.NewLine}{Environment.NewLine}No plugin defines them, so a rescan cannot bring them back. " +
                "Any item you assigned them to will point at nothing." +
                $"{Environment.NewLine}{Environment.NewLine}This covers the whole plugin, regardless of any active tree filter.",
                "Delete enchantments", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning);
            if (answer != System.Windows.MessageBoxResult.Yes) return;

            int deleted = 0;
            foreach (var ench in own)
            {
                try
                {
                    if (_enchantmentService.DeleteEnchantment(ench.Key)) deleted++;
                }
                catch (Exception ex)
                {
                    AppLogger.LogError($"DeletePluginEnchantmentsAsync: deleting {ench.Key} failed", ex);
                }
            }

            SelectedNode = null;
            RefreshData(_activePlugins);

            IssueHub.Current.Report(new AppIssue(
                AppIssueSeverity.Info,
                $"{deleted} of your own enchantment(s) deleted from {plugin}. Any item you assigned them to " +
                "now points at nothing — check those items before generating a patch.",
                Category: "enchantment"));

            System.Windows.MessageBox.Show(
                $"{deleted} enchantment(s) deleted.",
                "Delete enchantments", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
        }

        internal bool PluginHasUserCreatedEnchantments(string plugin)
            => EnchantmentsOf(plugin).Any(e => e.IsUserCreated);

        // Off the UNFILTERED tree on purpose - see the scope note above.
        private IEnumerable<EnchantmentRecord> EnchantmentsOf(string plugin)
        {
            var root = TreeItems.FirstOrDefault(
                n => string.Equals(n.DisplayName, plugin, StringComparison.OrdinalIgnoreCase));

            if (root == null) return Enumerable.Empty<EnchantmentRecord>();

            var found = new List<EnchantmentRecord>();
            Collect(root, found);
            return found;

            static void Collect(EnchantmentTreeNode node, List<EnchantmentRecord> into)
            {
                if (node is EnchantmentLeafNode leaf) into.Add(leaf.Enchantment);
                foreach (var c in node.Children) Collect(c, into);
            }
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
