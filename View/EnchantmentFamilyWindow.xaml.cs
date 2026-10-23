using SkyrimCraftingTool.Model;
using SkyrimCraftingTool.Services;
using SkyrimCraftingTool.ViewModel;
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace SkyrimCraftingTool.View
{
    // Review step for carrying a base enchantment's effect list down to its tier variants.
    //
    // A window rather than a section in the view, for the same reason as ChangedListsWindow: it is a
    // decision taken once, about records other than the one on screen.
    public partial class EnchantmentFamilyWindow : Window
    {
        private EnchantmentFamilyWindow(EnchantmentFamilyVM vm)
        {
            InitializeComponent();
            DataContext = vm;
            SourceInitialized += ApplyCaption;
        }

        // Returns the rows the user kept, or null when they cancelled.
        public static IReadOnlyList<FamilyChangeRowVM>? Show(
            Window? owner, EnchantmentRecord parent, IReadOnlyList<FamilyEffectChange> plan, int childCount)
        {
            EnchantmentFamilyWindow? window = null;
            var vm = new EnchantmentFamilyVM(parent, plan, childCount, ok =>
            {
                if (window == null) return;
                window.DialogResult = ok;
            });

            window = new EnchantmentFamilyWindow(vm);
            if (owner != null) window.Owner = owner;

            return window.ShowDialog() == true ? vm.Selected : null;
        }

        private void ApplyCaption(object? sender, EventArgs e)
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;

            var caption = ((SolidColorBrush)FindResource("ColorBackgroundBase")).Color;
            var text = ((SolidColorBrush)FindResource("ColorTextPrimary")).Color;
            DwmTitleBarService.ApplyAccentCaption(hwnd, caption, text);
        }
    }
}
