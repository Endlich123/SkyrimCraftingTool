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

        // Just commit the value (no further side effect needed - unlike in the single-item editor,
        // this only edits a template, no ContainerString gets rebuilt yet).
        private void TemplateSlider_ThumbDragCompleted(object sender, DragCompletedEventArgs e)
        {
            if (sender is Slider s)
                s.GetBindingExpression(Slider.ValueProperty)?.UpdateSource();
        }
    }
}
