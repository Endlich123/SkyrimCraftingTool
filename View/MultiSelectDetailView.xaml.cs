using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace SkyrimCraftingTool.View
{
    public partial class MultiSelectDetailView : System.Windows.Controls.UserControl
    {
        public MultiSelectDetailView()
        {
            InitializeComponent();
        }

        // The LVLi template slider used to be committed here by hand (Explicit binding +
        // UpdateSource on DragCompleted). That dropped every non-drag way of moving a WPF slider -
        // track click, arrow keys, Page Up/Down, wheel - so the template silently applied level 0.
        // It binds with UpdateSourceTrigger=PropertyChanged now and needs no code-behind: unlike the
        // item editor there is no ContainerString to rebuild here, this only fills a template.
    }
}
