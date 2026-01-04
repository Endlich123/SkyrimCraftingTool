using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace SkyrimCraftingTool
{
    public static class ScrollSpeedBehavior
    {
        public static double GetScrollSpeed(DependencyObject obj)
            => (double)obj.GetValue(ScrollSpeedProperty);

        public static void SetScrollSpeed(DependencyObject obj, double value)
            => obj.SetValue(ScrollSpeedProperty, value);

        public static readonly DependencyProperty ScrollSpeedProperty =
            DependencyProperty.RegisterAttached(
                "ScrollSpeed",
                typeof(double),
                typeof(ScrollSpeedBehavior),
                new UIPropertyMetadata(1.0, OnScrollSpeedChanged));

        private static void OnScrollSpeedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is UIElement element)
            {
                element.PreviewMouseWheel -= Element_PreviewMouseWheel;
                element.PreviewMouseWheel += Element_PreviewMouseWheel;
            }
        }

        private static void Element_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (sender is DependencyObject d)
            {
                double speed = GetScrollSpeed(d);

                if (FindScrollViewer(d) is ScrollViewer sv)
                {
                    sv.ScrollToVerticalOffset(sv.VerticalOffset - e.Delta * speed);
                    e.Handled = true;
                }
            }
        }

        private static ScrollViewer FindScrollViewer(DependencyObject d)
        {
            if (d is ScrollViewer sv) return sv;

            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
            {
                var child = VisualTreeHelper.GetChild(d, i);
                var result = FindScrollViewer(child);
                if (result != null) return result;
            }

            return null;
        }
    }

}
