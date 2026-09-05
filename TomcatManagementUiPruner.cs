using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace MCPanel;

internal static class TomcatManagementUiPruner
{
    private static readonly HashSet<string> RetiredActionTags = new(StringComparer.Ordinal)
    {
        "Restart",
        "OpenInstance",
        "ClearCache"
    };

    internal static void RemoveRetiredActions(DependencyObject source)
    {
        var root = VisualTreeHelper.GetParent(source);
        if (root is null)
        {
            return;
        }

        RemoveRetiredActionsRecursive(root);
    }

    private static void RemoveRetiredActionsRecursive(DependencyObject node)
    {
        if (node is Panel panel)
        {
            for (var index = panel.Children.Count - 1; index >= 0; index--)
            {
                var child = panel.Children[index];
                if (child is Button button &&
                    button.Tag is string action &&
                    RetiredActionTags.Contains(action))
                {
                    panel.Children.RemoveAt(index);
                    continue;
                }

                RemoveRetiredActionsRecursive(child);
            }

            return;
        }

        var childCount = VisualTreeHelper.GetChildrenCount(node);
        for (var index = 0; index < childCount; index++)
        {
            RemoveRetiredActionsRecursive(VisualTreeHelper.GetChild(node, index));
        }
    }
}
