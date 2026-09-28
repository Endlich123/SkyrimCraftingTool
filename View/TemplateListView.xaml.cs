using System.Windows.Controls;

namespace SkyrimCraftingTool.View
{
    // Every template, listed and editable. Shares its view model - and therefore its records, its
    // edits and its selection - with the NPC tab, because a template is an NPC.
    public partial class TemplateListView : UserControl
    {
        public TemplateListView()
        {
            InitializeComponent();
        }
    }
}
