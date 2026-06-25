using SkyrimCraftingTool.Model;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Input;

namespace SkyrimCraftingTool.ViewModel
{
    public class ItemNodeVM : ViewModelBase
    {
        private string _name;
        private float _weight;
        private int _value;

        private COBJNodeVM? _craftingRecipe;
        private COBJNodeVM? _temperRecipe;

        private string _searchText = string.Empty;
        private bool _showAllKeywords;
        private bool _isArmor;

        private ObservableCollection<IngredientEntryVM> _craftingIngredients = new();
        private ObservableCollection<IngredientEntryVM> _temperIngredients = new();

        private readonly CollectionViewSource _keywordViewSource;
        private readonly CollectionViewSource _selectedKeywordViewSource;

        private CancellationTokenSource _searchDebounce;

        public string Key { get; set; }

        public ObservableCollection<KeywordSelectionVM> AllKeywords { get; } = new();

        public List<FormIDRecord> AllAvailableMaterials { get; private set; }

        public ICollectionView FilteredKeywordsView => _keywordViewSource.View;
        public ICollectionView SelectedKeywordsView => _selectedKeywordViewSource.View;

        public string Name
        {
            get => _name;
            set => SetProperty(ref _name, value);
        }

        public float Weight
        {
            get => _weight;
            set => SetProperty(ref _weight, value);
        }

        public int Value
        {
            get => _value;
            set => SetProperty(ref _value, value);
        }

        public string SearchText
        {
            get => _searchText;
            set
            {
                if (SetProperty(ref _searchText, value))
                    DebouncedRefresh();
            }
        }

        public bool ShowAllKeywords
        {
            get => _showAllKeywords;
            set
            {
                if (SetProperty(ref _showAllKeywords, value))
                {
                    OnPropertyChanged(nameof(KeywordColumns));
                    OnPropertyChanged(nameof(KeywordFontSize));
                    RefreshKeywords();
                }
            }
        }

        // --------------------
        // Crafting Recipe
        // --------------------
        public COBJNodeVM? CraftingRecipe
        {
            get => _craftingRecipe;
            set
            {
                if (SetProperty(ref _craftingRecipe, value))
                {
                    CraftingIngredients = value?.Ingredients ?? new ObservableCollection<IngredientEntryVM>();
                    OnPropertyChanged(nameof(HasCraftingRecipe));
                }
            }
        }

        public bool HasCraftingRecipe => CraftingRecipe != null;

        public ObservableCollection<IngredientEntryVM> CraftingIngredients
        {
            get => _craftingIngredients;
            set => SetProperty(ref _craftingIngredients, value);
        }

        // --------------------
        // Temper Recipe
        // --------------------
        public COBJNodeVM? TemperRecipe
        {
            get => _temperRecipe;
            set
            {
                if (SetProperty(ref _temperRecipe, value))
                {
                    TemperIngredients = value?.Ingredients ?? new ObservableCollection<IngredientEntryVM>();
                    OnPropertyChanged(nameof(HasTemperRecipe));
                    OnPropertyChanged(nameof(TemperEditorID));
                }
            }
        }

        public bool HasTemperRecipe => TemperRecipe != null;

        public ObservableCollection<IngredientEntryVM> TemperIngredients
        {
            get => _temperIngredients;
            set => SetProperty(ref _temperIngredients, value);
        }

        public string TemperEditorID =>
            TemperRecipe?.Key ?? "(no temper recipe)";

        // --------------------
        // Commands
        // --------------------
        public ICommand AddIngredientCommand { get; }
        public ICommand RemoveIngredientCommand { get; }

        public ICommand AddTemperIngredientCommand { get; }
        public ICommand RemoveTemperIngredientCommand { get; }

        public int KeywordColumns => ShowAllKeywords ? 5 : 4;
        public double KeywordFontSize => ShowAllKeywords ? 11 : 12;

        // --------------------
        // Armor / Weapon Flags
        // --------------------
        public bool IsArmor
        {
            get => _isArmor;
            private set
            {
                if (SetProperty(ref _isArmor, value))
                    OnPropertyChanged(nameof(IsWeapon));
            }
        }

        public bool IsWeapon => !IsArmor;

        // --------------------
        // Armor-specific fields
        // --------------------
        private float _armorRating;
        public float ArmorRating
        {
            get => _armorRating;
            set => SetProperty(ref _armorRating, value);
        }

        private uint _bodySlotMask;
        public uint BodySlotMask
        {
            get => _bodySlotMask;
            set
            {
                if (SetProperty(ref _bodySlotMask, value))
                    UpdateSlotSelections();
            }
        }

        private SlotVM _selectedSlot;
        public SlotVM SelectedSlot
        {
            get => _selectedSlot;
            set
            {
                if (SetProperty(ref _selectedSlot, value))
                {
                    if (value != null)
                        BodySlotMask = value.Flag;
                }
            }
        }

        public ObservableCollection<SlotVM> SlotOptions { get; } = new();

        // --------------------
        // Weapon-specific fields
        // --------------------
        private int _damage;
        public int Damage
        {
            get => _damage;
            set => SetProperty(ref _damage, value);
        }

        private float _speed;
        public float Speed
        {
            get => _speed;
            set => SetProperty(ref _speed, value);
        }

        private float _reach;
        public float Reach
        {
            get => _reach;
            set => SetProperty(ref _reach, value);
        }

        private float _stagger;
        public float Stagger
        {
            get => _stagger;
            set => SetProperty(ref _stagger, value);
        }

        // --------------------
        // Constructor
        // --------------------
        public ItemNodeVM()
        {
            BindingOperations.EnableCollectionSynchronization(AllKeywords, new object());

            _keywordViewSource = new CollectionViewSource { Source = AllKeywords };
            _keywordViewSource.Filter += KeywordFilter;

            _selectedKeywordViewSource = new CollectionViewSource { Source = AllKeywords };
            _selectedKeywordViewSource.Filter += (s, e) =>
            {
                if (e.Item is KeywordSelectionVM kw)
                    e.Accepted = kw.IsSelected;
                else
                    e.Accepted = false;
            };

            AddIngredientCommand = new RelayCommand(AddCraftingIngredient);
            RemoveIngredientCommand = new RelayCommand<IngredientEntryVM>(RemoveCraftingIngredient);

            AddTemperIngredientCommand = new RelayCommand(AddTemperIngredient);
            RemoveTemperIngredientCommand = new RelayCommand<IngredientEntryVM>(RemoveTemperIngredient);

            foreach (ArmorSlotMask slot in Enum.GetValues(typeof(ArmorSlotMask)))
            {
                if (slot == ArmorSlotMask.None)
                    continue;

                uint flag = (uint)slot;
                int bit = (int)Math.Log(flag, 2);

                var opt = new SlotVM(slot.ToString(), bit);
                opt.SelectionChanged += SlotSelectionChanged;

                SlotOptions.Add(opt);
            }
        }

        public ItemNodeVM(ArmorRecord rec) : this() => ApplyArmorRecord(rec);
        public ItemNodeVM(WeaponRecord rec) : this() => ApplyWeaponRecord(rec);

        // --------------------
        // Apply Records
        // --------------------
        public void ApplyArmorRecord(ArmorRecord rec)
        {
            IsArmor = true;
            ApplyBaseRecord(rec.Key, rec.Name, rec.Value, rec.Weight);

            ArmorRating = rec.ArmorRating;
            BodySlotMask = rec.BodySlotMask;
        }

        public void ApplyWeaponRecord(WeaponRecord rec)
        {
            IsArmor = false;
            ApplyBaseRecord(rec.Key, rec.Name, rec.Value, rec.Weight);

            Damage = rec.Damage;
            Speed = rec.Speed;
            Reach = rec.Reach;
            Stagger = rec.Stagger;
        }

        private void ApplyBaseRecord(string key, string name, int value, float weight)
        {
            Key = key;
            Name = name;
            Value = value;
            Weight = weight;
        }

        // --------------------
        // Slot Mask Sync
        // --------------------
        private void SlotSelectionChanged(object sender, EventArgs e)
        {
            uint mask = 0;

            foreach (var opt in SlotOptions)
                if (opt.IsSelected)
                    mask |= opt.Flag;

            _bodySlotMask = mask;
            OnPropertyChanged(nameof(BodySlotMask));
        }

        private void UpdateSlotSelections()
        {
            foreach (var opt in SlotOptions)
                opt.IsSelected = (BodySlotMask & opt.Flag) != 0;
        }

        // --------------------
        // Keyword Filtering
        // --------------------
        public void RefreshKeywords() => _keywordViewSource.View.Refresh();

        private void DebouncedRefresh()
        {
            _searchDebounce?.Cancel();
            _searchDebounce = new CancellationTokenSource();
            var token = _searchDebounce.Token;

            Task.Delay(180, token).ContinueWith(t =>
            {
                if (!t.IsCanceled)
                {
                    System.Windows.Application.Current.Dispatcher.Invoke(RefreshKeywords);
                }
            }, TaskScheduler.Default);
        }

        private void KeywordFilter(object sender, FilterEventArgs e)
        {
            if (e.Item is not KeywordSelectionVM kw)
            {
                e.Accepted = false;
                return;
            }

            if (ShowAllKeywords)
            {
                if (!string.IsNullOrWhiteSpace(SearchText))
                    e.Accepted = kw.Name.Contains(SearchText, StringComparison.OrdinalIgnoreCase);
                else
                    e.Accepted = true;

                return;
            }

            var relevantPrefixes = _isArmor
                ? new[] { "Armor", "Clothing", "Jewelry", "VendorItemArmor", "Material" }
                : new[] { "Weap", "Weapon", "VendorItemWeapon", "Material", "DamageType" };

            bool isRelevant =
                kw.IsSelected ||
                relevantPrefixes.Any(p =>
                    kw.Name.StartsWith(p, StringComparison.OrdinalIgnoreCase));

            if (!isRelevant)
            {
                e.Accepted = false;
                return;
            }

            if (!string.IsNullOrWhiteSpace(SearchText) &&
                !kw.Name.Contains(SearchText, StringComparison.OrdinalIgnoreCase))
            {
                e.Accepted = false;
                return;
            }

            e.Accepted = true;
        }

        // --------------------
        // Crafting Ingredients
        // --------------------
        private void AddCraftingIngredient()
        {
            if (CraftingRecipe == null)
                return;

            var newEntry = new IngredientEntryVM();
            newEntry.InitializeMaterials(AllAvailableMaterials);
            CraftingRecipe.Ingredients.Add(newEntry);
            CraftingIngredients = CraftingRecipe.Ingredients;
        }

        private void RemoveCraftingIngredient(IngredientEntryVM ing)
        {
            if (CraftingRecipe == null || ing == null)
                return;

            CraftingRecipe.Ingredients.Remove(ing);
            CraftingIngredients = CraftingRecipe.Ingredients;
        }

        // --------------------
        // Temper Ingredients
        // --------------------
        private void AddTemperIngredient()
        {
            if (TemperRecipe == null)
                return;

            var newEntry = new IngredientEntryVM();
            newEntry.InitializeMaterials(AllAvailableMaterials);
            TemperRecipe.Ingredients.Add(newEntry);
            TemperIngredients = TemperRecipe.Ingredients;
        }

        private void RemoveTemperIngredient(IngredientEntryVM ing)
        {
            if (TemperRecipe == null || ing == null)
                return;

            TemperRecipe.Ingredients.Remove(ing);
            TemperIngredients = TemperRecipe.Ingredients;
        }
    }
}
