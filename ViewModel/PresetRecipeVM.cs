using SkyrimCraftingTool.Model;
using SkyrimCraftingTool.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows.Input;

namespace SkyrimCraftingTool.ViewModel
{
    // Editor for one Craft- or Temper-RecipeConfig within a Preset slot/type node. Craft recipes
    // expose an editable Workbench; Temper recipes don't — the workbench is always auto-derived from
    // the item at COBJ-creation time (see ItemNodeVM.CreateTemperRecipe / CreateNewCOBJRecordForItem),
    // same as for real items, so there is nothing meaningful to override here.
    public class PresetRecipeVM : ViewModelBase
    {
        private readonly RecipeConfig _config;
        private readonly Action _onChanged;
        private readonly List<FormIDRecord> _allWorkbenches;
        private readonly List<FormIDRecord> _allMaterials;
        private readonly List<FormIDRecord> _allPerks;
        private readonly List<FormIDRecord> _allQuests;

        public bool IsLoading { get; private set; }
        public bool HasWorkbench { get; }

        public ObservableCollection<PresetIngredientEntryVM> Ingredients { get; } = new();
        public ObservableCollection<BaseConditionViewModel> Conditions { get; } = new();

        public ICommand AddIngredientCommand { get; }
        public ICommand RemoveIngredientCommand { get; }
        public ICommand AddConditionCommand { get; }
        public ICommand RemoveConditionCommand { get; }

        public IEnumerable<FormIDRecord> FilteredPerks => _allPerks;
        public IEnumerable<FormIDRecord> FilteredQuests => _allQuests;

        public PresetRecipeVM(RecipeConfig config, bool hasWorkbench,
            List<FormIDRecord> allWorkbenches, List<FormIDRecord> allMaterials,
            List<FormIDRecord> allPerks, List<FormIDRecord> allQuests, Action onChanged)
        {
            _config = config;
            HasWorkbench = hasWorkbench;
            _allWorkbenches = allWorkbenches ?? new List<FormIDRecord>();
            _allMaterials = allMaterials ?? new List<FormIDRecord>();
            _allPerks = allPerks ?? new List<FormIDRecord>();
            _allQuests = allQuests ?? new List<FormIDRecord>();
            _onChanged = onChanged;

            AddIngredientCommand = new RelayCommand(AddIngredient);
            RemoveIngredientCommand = new RelayCommand<PresetIngredientEntryVM>(RemoveIngredient);
            AddConditionCommand = new RelayCommand(AddCondition);
            RemoveConditionCommand = new RelayCommand<BaseConditionViewModel>(RemoveCondition);

            IsLoading = true;
            try
            {
                LoadFromConfig();
            }
            finally
            {
                IsLoading = false;
            }
        }

        // --------------------
        // Workbench (Craft only)
        // --------------------
        public bool WorkbenchEnabled
        {
            get => _config.WorkbenchKey.Enabled;
            set
            {
                if (_config.WorkbenchKey.Enabled == value) return;
                _config.WorkbenchKey.Enabled = value;
                OnPropertyChanged();
                NotifyChanged();
            }
        }

        private FormIDRecord _selectedWorkbench;
        public FormIDRecord SelectedWorkbench
        {
            get => _selectedWorkbench;
            set
            {
                if (SetProperty(ref _selectedWorkbench, value))
                {
                    _config.WorkbenchKey.Value = value?.Key ?? "";
                    if (value != null && string.IsNullOrEmpty(WorkbenchSearchText))
                        WorkbenchSearchText = value.Name;
                    NotifyChanged();
                }
            }
        }

        private string _workbenchSearchText = "";
        public string WorkbenchSearchText
        {
            get => _workbenchSearchText;
            set
            {
                if (SetProperty(ref _workbenchSearchText, value))
                    OnPropertyChanged(nameof(FilteredWorkbenches));
            }
        }

        public IEnumerable<FormIDRecord> FilteredWorkbenches =>
            _allWorkbenches.Where(w =>
                string.IsNullOrWhiteSpace(WorkbenchSearchText)
                || w.Name.Contains(WorkbenchSearchText, StringComparison.OrdinalIgnoreCase));

        // --------------------
        // Ingredients
        // --------------------
        public bool IngredientsEnabled
        {
            get => _config.Ingredients.Enabled;
            set
            {
                if (_config.Ingredients.Enabled == value) return;
                _config.Ingredients.Enabled = value;
                OnPropertyChanged();
                NotifyChanged();
            }
        }

        private void AddIngredient()
        {
            var entry = new PresetIngredientEntryVM(SyncIngredientsAndNotify, () => IsLoading, () => Ingredients);
            entry.InitializeMaterials(_allMaterials);
            Ingredients.Add(entry);
            SyncIngredientsAndNotify();
        }

        private void RemoveIngredient(PresetIngredientEntryVM entry)
        {
            if (entry == null) return;
            Ingredients.Remove(entry);
            foreach (var e in Ingredients) e.RefreshMaterialFilter(); // freed material reappears
            SyncIngredientsAndNotify();
        }

        private void SyncIngredientsAndNotify()
        {
            // Skip empty rows; merge any duplicate keys (sum counts) so nothing dupe-y ever reaches
            // the JSON, even from a bulk/programmatic path.
            _config.Ingredients.Value = Ingredients
                .Where(i => !string.IsNullOrEmpty(i.Key))
                .GroupBy(i => i.Key)
                .Select(g => new IngredientEntry { Key = g.Key, Count = g.Sum(i => i.Count) })
                .ToList();
            NotifyChanged();
        }

        // --------------------
        // Conditions
        // --------------------
        public bool ConditionsEnabled
        {
            get => _config.Conditions.Enabled;
            set
            {
                if (_config.Conditions.Enabled == value) return;
                _config.Conditions.Enabled = value;
                OnPropertyChanged();
                NotifyChanged();
            }
        }

        private bool _isReplacingType;

        private void AddCondition()
        {
            var cond = new PerkConditionViewModel();
            SubscribeCondition(cond);
            Conditions.Add(cond);
            SyncConditionsAndNotify();
        }

        private void RemoveCondition(BaseConditionViewModel cond)
        {
            if (cond == null) return;
            UnsubscribeCondition(cond);
            Conditions.Remove(cond);
            SyncConditionsAndNotify();
        }

        private void SubscribeCondition(BaseConditionViewModel cond) => cond.PropertyChanged += OnConditionPropertyChanged;
        private void UnsubscribeCondition(BaseConditionViewModel cond) => cond.PropertyChanged -= OnConditionPropertyChanged;

        private void OnConditionPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (_isReplacingType) return;
            if (sender is not BaseConditionViewModel cond) return;

            if (e.PropertyName == nameof(BaseConditionViewModel.Type))
            {
                ReplaceForTypeChange(cond);
                return;
            }

            SyncConditionsAndNotify();
        }

        // Same Remove+Insert swap as ItemNodeVM.ReplaceConditionForTypeChange — an indexer replace
        // leaves WPF's ItemsControl showing the old item's DataTemplate since the generated container
        // is reused instead of re-selected for the new concrete CLR type.
        private void ReplaceForTypeChange(BaseConditionViewModel oldCondition)
        {
            var index = Conditions.IndexOf(oldCondition);
            if (index < 0) return;

            var newCondition = CreateConditionViewModel(oldCondition.Type);
            newCondition.RunOnPlayer = oldCondition.RunOnPlayer;
            try { newCondition.ComparisonValue = oldCondition.ComparisonValue; } catch { /* not meaningful across all types */ }

            _isReplacingType = true;
            try
            {
                UnsubscribeCondition(oldCondition);
                SubscribeCondition(newCondition);
                Conditions.RemoveAt(index);
                Conditions.Insert(index, newCondition);
            }
            finally
            {
                _isReplacingType = false;
            }

            SyncConditionsAndNotify();
        }

        private static BaseConditionViewModel CreateConditionViewModel(CustomConditionType type) => type switch
        {
            CustomConditionType.HasPerk => new PerkConditionViewModel(),
            CustomConditionType.GetIsSex => new SexConditionViewModel(),
            CustomConditionType.GetActorValue => new ActorValueConditionViewModel(),
            CustomConditionType.GetLevel => new LevelConditionViewModel(),
            CustomConditionType.GetStageDone => new QuestStageConditionViewModel(),
            _ => new PerkConditionViewModel()
        };

        private void SyncConditionsAndNotify()
        {
            // Skip half-built conditions (HasPerk with no perk, GetStageDone with no quest) - same as
            // the item recipe save path and the empty-ingredient filter.
            _config.Conditions.Value = Conditions
                .Where(ConditionMapper.HasUsableTarget)
                .Select(PresetConditionMapper.ToEntry)
                .ToList();
            NotifyChanged();
        }

        // --------------------
        // Load
        // --------------------
        private void LoadFromConfig()
        {
            SelectedWorkbench = _allWorkbenches.FirstOrDefault(w => w.Key == _config.WorkbenchKey.Value);
            if (SelectedWorkbench != null)
                WorkbenchSearchText = SelectedWorkbench.Name;
            OnPropertyChanged(nameof(WorkbenchEnabled));

            // Merge duplicate keys (sum counts) - same as COBJNodeVM for the item side.
            var mergedCounts = new Dictionary<string, int>();
            var keyOrder = new List<string>();
            foreach (var ing in _config.Ingredients.Value ?? new List<IngredientEntry>())
            {
                if (string.IsNullOrEmpty(ing.Key)) continue;
                int c = Math.Max(1, ing.Count);
                if (mergedCounts.TryGetValue(ing.Key, out var existing))
                    mergedCounts[ing.Key] = existing + c;
                else
                {
                    mergedCounts[ing.Key] = c;
                    keyOrder.Add(ing.Key);
                }
            }

            foreach (var key in keyOrder)
            {
                var entry = new PresetIngredientEntryVM(SyncIngredientsAndNotify, () => IsLoading, () => Ingredients);
                entry.InitializeMaterials(_allMaterials);

                var mat = _allMaterials.FirstOrDefault(m => m.Key == key);
                if (mat != null)
                    entry.SetSelectedMaterialSilent(mat);
                else
                    entry.Key = key;

                entry.Count = mergedCounts[key];
                Ingredients.Add(entry);
            }
            OnPropertyChanged(nameof(IngredientsEnabled));

            foreach (var cond in _config.Conditions.Value ?? new List<ConditionEntry>())
            {
                var vm = PresetConditionMapper.ToViewModel(cond, _allPerks, _allQuests);
                SubscribeCondition(vm);
                Conditions.Add(vm);
            }
            OnPropertyChanged(nameof(ConditionsEnabled));
        }

        private void NotifyChanged()
        {
            if (IsLoading) return;
            _onChanged();
        }
    }
}
