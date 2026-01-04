using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
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
