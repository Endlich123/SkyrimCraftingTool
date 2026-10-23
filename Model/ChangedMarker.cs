using System.Windows;

namespace SkyrimCraftingTool.Model
{
    // "This control's value differs from the scanned original", for controls that cannot carry the
    // marking through BorderBrush.
    //
    // Every text field in the detail views shows an edit by binding BorderBrush through
    // BoolToChangedBorderConverter. A CheckBox cannot: its template colours the box's border itself
    // on IsChecked, and a template trigger beats the TemplateBinding underneath it - so the amber
    // would vanish at exactly the moment it matters, the moment the box is ticked.
    //
    // Hence a separate flag the template can trigger on, declared AFTER the IsChecked trigger so it
    // wins. Attached rather than reusing Tag so that what it means is written down somewhere.
    public static class ChangedMarker
    {
        public static readonly DependencyProperty IsChangedProperty =
            DependencyProperty.RegisterAttached(
                "IsChanged",
                typeof(bool),
                typeof(ChangedMarker),
                new FrameworkPropertyMetadata(false));

        public static bool GetIsChanged(DependencyObject element)
            => (bool)element.GetValue(IsChangedProperty);

        public static void SetIsChanged(DependencyObject element, bool value)
            => element.SetValue(IsChangedProperty, value);
    }
}
