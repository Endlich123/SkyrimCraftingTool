using SkyrimCraftingTool.Model;
using System.Collections.ObjectModel;
using System.Windows.Input;

namespace SkyrimCraftingTool.ViewModel
{
    // The enchantment tree used to be ONE class for all three levels, with "Enchantment == null"
    // standing in for "this is a folder". That works for a tree that only ever renders rows, but it
    // gives WPF nothing to dispatch on: a DataTemplate is picked by the item's runtime TYPE.
    //
    // Split the way MainContentView's tree has always been split (PluginNodeVM / CategoryNodeVM /
    // ItemNodeVM), so both the tree rows and the detail panel can select their template by DataType
    // instead of by a null check - see EnchantmentMenuView.xaml.
    //
    // The base keeps what the tree itself needs at every level. Collections stay typed to it, so
    // TryFindPath, TrySelectInTree and the background filter keep walking one kind of node.
    public abstract class EnchantmentTreeNode : ViewModelBase
    {
        private bool _isExpanded;
        public bool IsExpanded
        {
            get => _isExpanded;
            set => SetProperty(ref _isExpanded, value);
        }

        // Bound two-way to TreeViewItem.IsSelected, which is the only way to move the tree's
        // selection from code - TreeView.SelectedItem is read-only. Used by the "jump to the base
        // enchantment" button; the TreeView clears the previous row by itself when this turns on.
        private bool _isSelected;
        public bool IsSelected
        {
            get => _isSelected;
            set => SetProperty(ref _isSelected, value);
        }

        public string DisplayName { get; set; }

        public ObservableCollection<EnchantmentTreeNode> Children { get; set; } = new();

        // Drives the dot on a collapsed plugin or category row. Recursive rather than per-level,
        // because the base already owns Children and every level asks the same question: is there an
        // edited record anywhere below me? A leaf answers for itself.
        //
        // Computed on demand - the filter hands out copies, and a copy that shares its records
        // answers correctly without bookkeeping. It only needs telling when to ask again, which
        // EnchantmentMenuVM.RaiseTreeEditedFlags does.
        public virtual bool HasEditedDescendant
        {
            get
            {
                foreach (var child in Children)
                    if (child.HasEditedDescendant) return true;
                return false;
            }
        }

        internal void RaiseHasEditedDescendant() => OnPropertyChanged(nameof(HasEditedDescendant));

        // The filter rebuilds the tree node by node. Without a type-preserving copy every level
        // would come back as whatever class the filter happened to construct, and the templates
        // would pick the wrong one - the failure mode the split exists to remove.
        public abstract EnchantmentTreeNode CloneWithoutChildren();
    }

    // A plugin row. Carries no record of its own; its detail panel acts on everything below it.
    public sealed class EnchantmentPluginNode : EnchantmentTreeNode
    {
        // Back-reference to reach the services, the same way PluginNodeVM holds MainContentVM.
        // MUST be carried by CloneWithoutChildren: the filter hands the TreeView copies, so without
        // it every button on a filtered plugin row would silently do nothing.
        public EnchantmentMenuVM Menu { get; set; }

        public ICommand ExportPluginCommand => new RelayCommand(async () =>
        {
            if (Menu != null) await Menu.ExportPluginEnchantmentsAsync(DisplayName);
        });

        public ICommand ImportPluginCommand => new RelayCommand(async () =>
        {
            if (Menu != null) await Menu.ImportPluginEnchantmentsAsync(DisplayName);
        });

        public ICommand ResetPluginCommand => new RelayCommand(async () =>
        {
            if (Menu != null) await Menu.ResetPluginEnchantmentsAsync(DisplayName);
        });

        // Delete is offered only where there is something of the user's to delete - the same rule
        // the single record's Delete button follows via IsUserCreatedSelected. In practice that is
        // the tool's own pseudo-plugin, but it is asked, not assumed.
        public bool HasOwnEnchantments => Menu?.PluginHasUserCreatedEnchantments(DisplayName) == true;

        public ICommand DeletePluginCommand => new RelayCommand(async () =>
        {
            if (Menu != null) await Menu.DeletePluginEnchantmentsAsync(DisplayName);
        });

        public override EnchantmentTreeNode CloneWithoutChildren()
            => new EnchantmentPluginNode
            {
                DisplayName = DisplayName,
                IsExpanded = IsExpanded,
                Menu = Menu,
            };
    }

    // "Weapon Enchantments" / "Armor Enchantments" / "Other". Grouping only - no detail panel, the
    // same way CategoryNodeVM has none on the item side.
    public sealed class EnchantmentCategoryNode : EnchantmentTreeNode
    {
        public override EnchantmentTreeNode CloneWithoutChildren()
            => new EnchantmentCategoryNode { DisplayName = DisplayName, IsExpanded = IsExpanded };
    }

    // A single enchantment. The only level that carries a record.
    public sealed class EnchantmentLeafNode : EnchantmentTreeNode
    {
        public EnchantmentRecord Enchantment { get; set; }

        // The recursion bottoms out here: a leaf has no children, it has a record.
        public override bool HasEditedDescendant => Enchantment?.IsEdited == true;

        public override EnchantmentTreeNode CloneWithoutChildren()
            => new EnchantmentLeafNode
            {
                DisplayName = DisplayName,
                IsExpanded = IsExpanded,
                Enchantment = Enchantment,
            };
    }
}
