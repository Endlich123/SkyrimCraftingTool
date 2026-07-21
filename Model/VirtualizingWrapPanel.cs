using System.Windows;
using System.Windows.Controls;

namespace SkyrimCraftingTool.Model
{
    public class VirtualizingWrapPanel : VirtualizingPanel
    {
        protected override System.Windows.Size MeasureOverride(System.Windows.Size availableSize)
        {
            foreach (UIElement child in InternalChildren)
                child.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));

            return availableSize;
        }

        protected override System.Windows.Size ArrangeOverride(System.Windows.Size finalSize)
        {
            double x = 0;
            double y = 0;
            double rowHeight = 0;

            foreach (UIElement child in InternalChildren)
            {
                if (x + child.DesiredSize.Width > finalSize.Width)
                {
                    x = 0;
                    y += rowHeight;
                    rowHeight = 0;
                }

                child.Arrange(new Rect(new System.Windows.Point(x, y), child.DesiredSize));

                x += child.DesiredSize.Width;
                rowHeight = Math.Max(rowHeight, child.DesiredSize.Height);
            }

            return finalSize;
        }
    }

}