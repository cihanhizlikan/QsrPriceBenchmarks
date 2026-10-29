using System.Windows;
using System.Windows.Media;

namespace QsrPriceBenchmarks.UiTests;

/// <summary>Walks a rendered window's visual tree, for the tests that look for an element the way a user sees it.</summary>
internal static class VisualTree
{
    public static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                yield return match;
            }
            foreach (T deeper in Descendants<T>(child))
            {
                yield return deeper;
            }
        }
    }
}
