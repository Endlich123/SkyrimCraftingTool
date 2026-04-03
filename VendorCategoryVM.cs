using System.Collections.ObjectModel;
using System.Windows.Input;

namespace SkyrimCraftingTool
{
    public class VendorCategoryVM
    {
        public string CategoryName { get; set; }
        public string IniFileName { get; set; }
        public ObservableCollection<VendorOption> Vendors { get; set; }

        public ICommand SelectAllCommand { get; set; }
    }

}
