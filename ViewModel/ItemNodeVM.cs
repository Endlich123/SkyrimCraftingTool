using SkyrimCraftingTool.Model;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Data;
using System.Windows.Input;

namespace SkyrimCraftingTool.ViewModel
{
    public class ItemNodeVM : ViewModelBase
    {
        public MainContentVM Main { get; }

        // flag
        public bool IsLoading { get; set; }

        public ItemNodeVM(ArmorRecord armor, MainContentVM main) : this()
        {
            Main = main;
            ApplyArmorRecord(armor);
        }

        public ItemNodeVM(WeaponRecord weapon, MainContentVM main) : this()
        {
            Main = main;
            ApplyWeaponRecord(weapon);
        }


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

        private CollectionViewSource? _keywordViewSource;
        private CollectionViewSource? _selectedKeywordViewSource;

        private CancellationTokenSource _searchDebounce;

        public string Key { get; set; }

        public ObservableCollection<KeywordSelectionVM> AllKeywords { get; } = new();

        public List<FormIDRecord> AllAvailableMaterials { get; private set; }

        public ICollectionView FilteredKeywordsView
        {
            get
            {
                EnsureViewSources();
                return _keywordViewSource!.View;
            }
        }

        public ICollectionView SelectedKeywordsView
        {
            get
            {
                EnsureViewSources();
                return _selectedKeywordViewSource!.View;
            }
        }

        public event Action<ItemNodeVM, string>? FieldChanged;

        public void NotifyFieldChanged(string fieldName)
        {
            if (IsLoading)
                return;

            FieldChanged?.Invoke(this, fieldName);
        }

        public string Name
        {
            get => _name;
            set
            {
                if (SetProperty(ref _name, value))
                    NotifyFieldChanged(nameof(Name));
            }
        }

        public float Weight
        {
            get => _weight;
            set
            {
                if (SetProperty(ref _weight, value))
                    NotifyFieldChanged(nameof(Weight));
            }
        }

        public int Value
        {
            get => _value;
            set
            {
                if (SetProperty(ref _value, value))
                    NotifyFieldChanged(nameof(Value));
            }
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

        public ObservableCollection<KeywordSelectionVM> SelectedKeywords =>
            new(AllKeywords.Where(k => k.IsSelected));

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
                    // Zutaten nur setzen, ohne Autosave beim Laden
                    CraftingIngredients = value?.Ingredients ?? new ObservableCollection<IngredientEntryVM>();
                    OnPropertyChanged(nameof(HasCraftingRecipe));
                    OnPropertyChanged(nameof(CraftingEditorID));

                    if (!IsLoading)
                    {
                        NotifyFieldChanged(nameof(CraftingRecipe));
                        NotifyFieldChanged(nameof(CraftingIngredients));
                    }
                }
            }
        }

        public bool HasCraftingRecipe => CraftingRecipe != null;

        public ObservableCollection<IngredientEntryVM> CraftingIngredients
        {
            get => _craftingIngredients;
            set
            {
                if (SetProperty(ref _craftingIngredients, value))
                {
                    if (!IsLoading)
                        NotifyFieldChanged(nameof(CraftingIngredients));
                }
                else
                {
                    OnPropertyChanged(nameof(CraftingIngredients));
                }
            }
        }

        public string CraftingEditorID =>
            CraftingRecipe?.Key ?? "(no crafting recipe)";

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


                    if (!IsLoading)
                    {
                        NotifyFieldChanged(nameof(TemperRecipe));
                        NotifyFieldChanged(nameof(TemperIngredients));
                    }
                }
            }
        }

        public bool HasTemperRecipe => TemperRecipe != null;

        public ObservableCollection<IngredientEntryVM> TemperIngredients
        {
            get => _temperIngredients;
            set
            {
                if (SetProperty(ref _temperIngredients, value))
                {
                    if (!IsLoading)
                        NotifyFieldChanged(nameof(TemperIngredients));
                }
            }
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
            set
            {
                if (SetProperty(ref _armorRating, value))
                    NotifyFieldChanged(nameof(ArmorRating));
            }
        }

        private uint _bodySlotMask;
        private bool _isSyncingSlots;

        public uint BodySlotMask
        {
            get => _bodySlotMask;
            set
            {
                if (SetProperty(ref _bodySlotMask, value))
                {
                    SyncDataToGui();
                    NotifyFieldChanged(nameof(BodySlotMask));
                }
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
                        BodySlotMask = value.Flag; // explizite User-Aktion
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
            set
            {
                if (SetProperty(ref _damage, value))
                    NotifyFieldChanged(nameof(Damage));
            }
        }

        private float _speed;
        public float Speed
        {
            get => _speed;
            set
            {
                if (SetProperty(ref _speed, value))
                    NotifyFieldChanged(nameof(Speed));
            }
        }

        private float _reach;
        public float Reach
        {
            get => _reach;
            set
            {
                if (SetProperty(ref _reach, value))
                    NotifyFieldChanged(nameof(Reach));
            }
        }

        private float _stagger;
        public float Stagger
        {
            get => _stagger;
            set
            {
                if (SetProperty(ref _stagger, value))
                    NotifyFieldChanged(nameof(Stagger));
            }
        }

        // --------------------
        // Constructor
        // --------------------
        public ItemNodeVM()
        {
            BindingOperations.EnableCollectionSynchronization(AllKeywords, new object());

            // CollectionViewSource objects must be created on the UI thread.
            // Delay creation until needed to avoid Dispatcher ownership issues when ItemNodeVM
            // instances are created on a background thread.
            _keywordViewSource = null;
            _selectedKeywordViewSource = null;

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
            BodySlotMask = rec.BodySlotMask; // Daten → GUI über offiziellen Setter
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
            if (_isSyncingSlots) return; // keine GUI→Daten während SyncDataToGui

            uint mask = 0;

            foreach (var opt in SlotOptions)
            {
                if (opt.IsSelected)
                    mask |= opt.Flag;
            }

            BodySlotMask = mask; // offizieller Setter, inkl. Autosave
        }

        private void SyncDataToGui()
        {
            _isSyncingSlots = true;

            foreach (var opt in SlotOptions)
            {
                // Silent-Update: keine SelectionChanged-Events
                opt.SetSelectedSilent((BodySlotMask & opt.Flag) != 0);
            }

            _isSyncingSlots = false;
        }

        // --------------------
        // Keyword Filtering
        // --------------------
        private void EnsureViewSources()
        {
            if (_keywordViewSource != null && _selectedKeywordViewSource != null)
                return;

            var app = System.Windows.Application.Current;
            if (app == null)
            {
                // fallback: create on current thread
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
                return;
            }

            var disp = app.Dispatcher;
            if (disp.CheckAccess())
            {
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
            }
            else
            {
                disp.Invoke(() =>
                {
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
                });
            }
        }

        public void RefreshKeywords()
        {
            EnsureViewSources();

            var view = _keywordViewSource?.View;
            if (view == null) return;

            var disp = System.Windows.Application.Current?.Dispatcher;
            if (disp != null && !disp.CheckAccess())
            {
                disp.Invoke(() => view.Refresh());
            }
            else
            {
                view.Refresh();
            }
        }

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
            var newEntry = new IngredientEntryVM(this, false);

            newEntry.InitializeMaterials(Main.AllAvailableMaterials);

            CraftingIngredients.Add(newEntry);
            NotifyFieldChanged(nameof(CraftingIngredients));
        }

        private void RemoveCraftingIngredient(IngredientEntryVM ing)
        {
            if (ing == null) return;

            CraftingIngredients.Remove(ing);

            NotifyFieldChanged(nameof(CraftingIngredients));
        }




        // --------------------
        // Temper Ingredients
        // --------------------
        private void AddTemperIngredient()
        {
            var newEntry = new IngredientEntryVM(this, true);

            newEntry.InitializeMaterials(Main.AllAvailableMaterials);

            TemperIngredients.Add(newEntry);
            NotifyFieldChanged(nameof(TemperIngredients));
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
