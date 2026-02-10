using System.ComponentModel;

namespace SkyrimCraftingTool;

public class VendorKeywordVM : INotifyPropertyChanged
{
    public string Keyword { get; }
    public SlotSettingsViewModel? Parent { get; }

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value)
                return;

            _isSelected = value;
            OnPropertyChanged(nameof(IsSelected));

            // Auto-Save if Parent exists
            Parent?.Save();

            OnSelectionChanged?.Invoke();
        }
    }

    public event Action? OnSelectionChanged;

    public VendorKeywordVM(string keyword, bool isSelected, SlotSettingsViewModel? parent = null)
    {
        Keyword = keyword;
        _isSelected = isSelected;
        Parent = parent;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged(string name)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
