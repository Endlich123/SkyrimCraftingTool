using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SkyrimCraftingTool.ViewModel;

namespace SkyrimCraftingTool.View
{
    // The NPC tab's view. See NpcMenuVM for the tree shape and why factions are not part of it.
    public partial class NpcMenuView : UserControl
    {
        public NpcMenuView()
        {
            InitializeComponent();
        }

        // Branch nodes are selectable too (a tree where only leaves respond feels broken), but only
        // a leaf changes the detail pane - clicking "Skyrim.esm" must not blank out the NPC the user
        // was just looking at.
        private void TreeView_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            if (DataContext is not NpcMenuVM vm) return;

            // Ctrl held: the click is about building a selection, not about opening a record. The
            // detail pane is left alone so the bulk panel can take over.
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            {
                if (e.NewValue is NpcNodeVM toggled) vm.ToggleSelection(toggled);
                return;
            }

            // A branch with Shift: take everything under it. One click on a class node instead of
            // 389 Ctrl-clicks, which is the difference between the bulk editor being usable and not.
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) && e.NewValue is NpcTreeNodeVM branch)
            {
                vm.SelectBranch(branch);
                return;
            }

            if (e.NewValue is NpcNodeVM npc)
            {
                vm.ClearSelection();
                vm.SelectedNpc = npc;
            }
        }
    }
}
