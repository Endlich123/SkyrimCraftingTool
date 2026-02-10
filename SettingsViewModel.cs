using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace SkyrimCraftingTool;

public class SettingsViewModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler PropertyChanged;

    public ObservableCollection<string> CraftingCategories { get; }
        = new();

    private string _selectedCraftingCategory;
    public string SelectedCraftingCategory
    {
        get => _selectedCraftingCategory;
        set
        {
            if (_selectedCraftingCategory != value)
            {
                _selectedCraftingCategory = value;
                OnPropertyChanged();
                LoadCategorySettings();
            }
        }
    }

    private CategorySettingsViewModel _currentCategorySettings;
    public CategorySettingsViewModel CurrentCategorySettings
    {
        get => _currentCategorySettings;
        set
        {
            _currentCategorySettings = value;
            OnPropertyChanged();
        }
    }

    public SettingsViewModel()
    {
        LoadCraftingCategories();
        SelectedCraftingCategory = "Random";
    }

    private void LoadCraftingCategories()
    {
        CraftingCategories.Clear();
        CraftingCategories.Add("Random");

        foreach (var perkName in GlobalState.SmithingPerkEditorIDs)
            CraftingCategories.Add(perkName);
    }

    private void LoadCategorySettings()
    {
        if (SelectedCraftingCategory == "Random")
        {
            CurrentCategorySettings = null;
            return;
        }

        CurrentCategorySettings = new CategorySettingsViewModel(SelectedCraftingCategory);
    }

    private void OnPropertyChanged([CallerMemberName] string name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
