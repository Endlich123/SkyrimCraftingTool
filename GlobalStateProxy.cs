using System.Collections.ObjectModel;
using System.Windows;

namespace SkyrimCraftingTool
{
    public class GlobalStateProxy : DependencyObject
    {
        public static readonly DependencyProperty SelectedVendorKeywordsProperty =
            DependencyProperty.Register(
                nameof(SelectedVendorKeywords),
                typeof(ObservableCollection<string>),
                typeof(GlobalStateProxy),
                new PropertyMetadata(null));

        public ObservableCollection<string> SelectedVendorKeywords
        {
            get => (ObservableCollection<string>)GetValue(SelectedVendorKeywordsProperty);
            set => SetValue(SelectedVendorKeywordsProperty, value);
        }
    }
}
