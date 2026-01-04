using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;

namespace SkyrimCraftingTool;

public class VendorPanelVM : INotifyPropertyChanged
{
    public ObservableCollection<VendorKeywordVM> VendorOptions { get; }

    public VendorPanelVM(IEnumerable<string> allKeywords, IEnumerable<string> selectedKeywords)
    {
        var selectedSet = new HashSet<string>(selectedKeywords, StringComparer.OrdinalIgnoreCase);

        VendorOptions = new ObservableCollection<VendorKeywordVM>(
            allKeywords.Select(k =>
            {
                var vm = new VendorKeywordVM(k, selectedSet.Contains(k));
                vm.OnSelectionChanged += NotifyChange;
                return vm;
            })
        );
    }

    public List<string> GetSelectedKeywords()
    {
        return VendorOptions
            .Where(v => v.IsSelected)
            .Select(v => v.Keyword)
            .ToList();
    }

    public event Action<List<string>>? VendorsChanged;

    private void NotifyChange()
        => VendorsChanged?.Invoke(GetSelectedKeywords());

    public event PropertyChangedEventHandler? PropertyChanged;
}
