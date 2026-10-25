using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace SonyA7V.Camera;

/// <summary>Route wheel input to the page instead of changing a closed parameter selector.
/// Pixel offsets preserve small high-resolution wheel deltas; an open popup keeps its own scrolling.</summary>
public static class SettingsWheelRouting {
    public static void Attach(UIElement root) => root.AddHandler(Mouse.PreviewMouseWheelEvent,
        new MouseWheelEventHandler(OnWheel), true);

    private static void OnWheel(object sender, MouseWheelEventArgs e) {
        if (e.OriginalSource is not DependencyObject source || e.Delta == 0) return;
        var chain = Ancestors(source).ToArray();
        var combo = chain.OfType<ComboBox>().FirstOrDefault();
        if (combo?.IsDropDownOpen == true) return;
        var lines = SystemParameters.WheelScrollLines;
        if (lines == 0) { if (combo is not null) e.Handled = true; return; }
        foreach (var viewer in chain.OfType<ScrollViewer>()) {
            if (viewer.ScrollableHeight <= 0 || viewer.VerticalScrollBarVisibility == ScrollBarVisibility.Disabled) continue;
            var step = lines < 0 ? viewer.ViewportHeight : lines * 24.0;
            var offset = Math.Clamp(viewer.VerticalOffset - e.Delta / 120.0 * step, 0, viewer.ScrollableHeight);
            if (Math.Abs(offset - viewer.VerticalOffset) < 0.001) continue;
            viewer.ScrollToVerticalOffset(offset);
            e.Handled = true;
            return;
        }
        // At the page boundary a closed selector must still not change its camera parameter.
        if (combo is not null) e.Handled = true;
    }

    private static IEnumerable<DependencyObject> Ancestors(DependencyObject start) {
        for (DependencyObject? node = start; node is not null;
             node = node is Visual or Visual3D ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node))
            yield return node;
    }
}
