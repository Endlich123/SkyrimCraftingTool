using System.Collections.ObjectModel;

namespace SkyrimCraftingTool.ViewModel
{
    // Plugin → spell type → spell.
    //
    // Three classes rather than one with a "Spell == null" test, for the reason the enchantment tree
    // was split the same way: WPF picks a DataTemplate by the item's runtime TYPE, so a single class
    // gives it nothing to dispatch on and the levels end up told apart by DataTriggers on a null
    // check.
    public abstract class SpellTreeNode : ViewModelBase
    {
        private bool _isExpanded;
        public bool IsExpanded
        {
            get => _isExpanded;
            set => SetProperty(ref _isExpanded, value);
        }

        // Two-way to TreeViewItem.IsSelected: TreeView.SelectedItem is read-only, so this is the
        // only way to move the selection from code.
        private bool _isSelected;
        public bool IsSelected
        {
            get => _isSelected;
            set => SetProperty(ref _isSelected, value);
        }

        public string DisplayName { get; set; } = "";

        public ObservableCollection<SpellTreeNode> Children { get; set; } = new();

        // Drives the dot on a collapsed row. Recursive rather than per-level: every level asks the
        // same question, and a leaf answers for itself.
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
        // would come back as whatever class the filter constructed, and the templates would pick the
        // wrong one - the failure the split exists to remove.
        public abstract SpellTreeNode CloneWithoutChildren();
    }

    public sealed class SpellPluginNode : SpellTreeNode
    {
        // Back-reference to reach the view model. MUST survive CloneWithoutChildren: the filter
        // hands the TreeView copies, and a copy without it would leave every row inert.
        public SpellMenuVM Menu { get; set; }

        public override SpellTreeNode CloneWithoutChildren()
            => new SpellPluginNode { DisplayName = DisplayName, IsExpanded = IsExpanded, Menu = Menu };
    }

    // "Spell", "Ability", "Voice"... Grouping only, no detail panel.
    //
    // THE SPELL TYPE IS THE GROUPING LEVEL and not an editable field, which is one decision serving
    // two reasons: SkyPatcher has no operation for it, and a record that moved branch while being
    // edited would walk away from the user. The enchantment tree is built from CastType for exactly
    // the same pair of reasons.
    public sealed class SpellTypeNode : SpellTreeNode
    {
        public override SpellTreeNode CloneWithoutChildren()
            => new SpellTypeNode { DisplayName = DisplayName, IsExpanded = IsExpanded };
    }

    // A single spell. The only level that carries a record.
    public sealed class SpellLeafNode : SpellTreeNode
    {
        public Model.SpellRecord Spell { get; set; }

        public override bool HasEditedDescendant => Spell?.IsEdited == true;

        public override SpellTreeNode CloneWithoutChildren()
            => new SpellLeafNode { DisplayName = DisplayName, IsExpanded = IsExpanded, Spell = Spell };
    }
}
