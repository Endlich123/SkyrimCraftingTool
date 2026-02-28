using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace SkyrimCraftingTool;

public class SettingsViewModel : INotifyPropertyChanged
{
    public ObservableCollection<string> CraftingCategories { get; } = new();

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

    public CategorySettingsViewModel CurrentCategorySettings { get; private set; }

    public SettingsViewModel()
    {
        CraftingCategories.Add("Random");
        foreach (var perk in GlobalState.SmithingPerkEditorIDs)
            CraftingCategories.Add(perk);

        SelectedCraftingCategory = "Random";
    }

    private void LoadCategorySettings()
    {
        if (SelectedCraftingCategory == "Random")
        {
            CurrentCategorySettings = null;
            OnPropertyChanged(nameof(CurrentCategorySettings));
            return;
        }

        CurrentCategorySettings = new CategorySettingsViewModel(SelectedCraftingCategory);
        OnPropertyChanged(nameof(CurrentCategorySettings));
    }

    public void SaveCurrent()
    {
        CurrentCategorySettings?.Save();
    }

    public event PropertyChangedEventHandler PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

