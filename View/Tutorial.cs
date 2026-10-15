using System.Windows;

namespace SkyrimCraftingTool.View
{
    // Marks a control as something a tour can point at: views:Tutorial.Id="items.tree".
    // Ids are "<tour>.<what>", and a step in Tutorials/<tour>.json names the same string as Target.
    // Nothing else reads this - a marked control behaves exactly as an unmarked one.
    public static class Tutorial
    {
        public static readonly DependencyProperty IdProperty =
            DependencyProperty.RegisterAttached("Id", typeof(string), typeof(Tutorial), new PropertyMetadata(null));

        public static string? GetId(DependencyObject element) => (string?)element.GetValue(IdProperty);
        public static void SetId(DependencyObject element, string? value) => element.SetValue(IdProperty, value);
    }
}
