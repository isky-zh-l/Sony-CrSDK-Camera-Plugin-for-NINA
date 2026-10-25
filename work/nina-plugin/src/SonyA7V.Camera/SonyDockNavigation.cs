using System.Collections;
using System.Windows;
using System.Windows.Media;

namespace SonyA7V.Camera;

/// <summary>Activate the existing NINA/AvalonDock document, including a tab hidden behind Image.
/// Reflection is restricted to the host-owned layout model; no SDK or extra docking binaries are loaded.</summary>
public static class SonyDockNavigation {
    public static bool TryActivate(DependencyObject root, string contentId) {
        foreach(var element in Visuals(root)) {
            if(!IsDockManager(element.GetType()))continue;
            var layout=element.GetType().GetProperty("Layout")?.GetValue(element);
            if(layout is null)continue;
            var queue=new Queue<object>();var seen=new HashSet<object>(ReferenceEqualityComparer.Instance);queue.Enqueue(layout);
            while(queue.Count>0&&seen.Count<2048){
                var node=queue.Dequeue();if(!seen.Add(node))continue;var type=node.GetType();
                if(string.Equals(type.GetProperty("ContentId")?.GetValue(node) as string,contentId,StringComparison.Ordinal)){
                    type.GetMethod("Show",Type.EmptyTypes)?.Invoke(node,null);
                    type.GetProperty("IsSelected")?.SetValue(node,true);
                    type.GetProperty("IsActive")?.SetValue(node,true);
                    return true;
                }
                if(type.GetProperty("Children")?.GetValue(node) is IEnumerable children)
                    foreach(var child in children)if(child is not null)queue.Enqueue(child);
            }
        }return false;
    }

    private static bool IsDockManager(Type? type){for(;type is not null;type=type.BaseType)if(type.FullName=="AvalonDock.DockingManager")return true;return false;}

    private static IEnumerable<DependencyObject> Visuals(DependencyObject node){
        yield return node;
        for(int i=0;i<VisualTreeHelper.GetChildrenCount(node);i++)
            foreach(var child in Visuals(VisualTreeHelper.GetChild(node,i)))yield return child;
    }
}
