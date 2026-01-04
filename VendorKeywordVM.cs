using System.ComponentModel;

namespace SkyrimCraftingTool;

public class VendorKeywordVM : INotifyPropertyChanged
{
    public string Keyword { get; }

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
            OnSelectionChanged?.Invoke();
        }
    }

    public event Action? OnSelectionChanged;

    public VendorKeywordVM(string keyword, bool isSelected)
    {
        Keyword = keyword;
        _isSelected = isSelected;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged(string name)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
