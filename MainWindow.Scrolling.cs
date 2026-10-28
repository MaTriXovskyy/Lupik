using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace Lupik;

public partial class MainWindow
{
    // Tunnel before the viewer's ordinary wheel handling (PDF zoom, text scrolling, etc.).
    private void OnHorizontalWheel(object sender, MouseWheelEventArgs e)
    {
        if (Core.KeyState.Modifiers != ModifierKeys.Shift) return;
        e.Handled = TryHorizontalScroll(e.OriginalSource as DependencyObject, e.Delta);
    }

    private static bool TryHorizontalScroll(DependencyObject? source, int delta)
    {
        for (DependencyObject? node = source; node != null; node = ScrollParent(node))
        {
            if (node is not ScrollViewer scroller || scroller.ScrollableWidth <= 0 ||
                scroller.HorizontalScrollBarVisibility == ScrollBarVisibility.Disabled) continue;
            double step = SystemParameters.WheelScrollLines < 0 ? scroller.ViewportWidth : SystemParameters.WheelScrollLines * 16;
            scroller.ScrollToHorizontalOffset(scroller.HorizontalOffset - delta / 120.0 * step);
            return true; // also at the edge: Shift must not turn into vertical scrolling
        }
        return false;
    }

    private static DependencyObject? ScrollParent(DependencyObject node) => node switch
    {
        Visual or Visual3D => VisualTreeHelper.GetParent(node),
        FrameworkContentElement content => content.Parent,
        _ => LogicalTreeHelper.GetParent(node),
    };
}
