using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace Alondra1.App.Shell;

/// <summary>
/// Geometry assertions for snapshots (client spec §6): measured in DIPs with a tolerance of one physical pixel at the
/// current display scale. Each line is "ok name: detail" or "FAIL name: detail"; the snapshot matrix script fails on any FAIL.
/// </summary>
public static class GeometryCheck
{
    public sealed record Parts(Window Window, FrameworkElement Header, FrameworkElement Toolbar, FrameworkElement Content,
        FrameworkElement? ListPane, FrameworkElement? ReaderPane, FrameworkElement? ColumnHeader, ListBox? Messages);

    public static IReadOnlyList<string> Run(Parts parts)
    {
        var lines = new List<string>();
        var dpi = VisualTreeHelper.GetDpi(parts.Window);
        var tolerance = 1.0 / dpi.DpiScaleY + 0.01;
        void Check(string name, bool ok, string detail) => lines.Add($"{(ok ? "ok" : "FAIL")} {name}: {detail}");

        var root = (FrameworkElement)parts.Window.Content;
        Rect Bounds(FrameworkElement element) => element.TransformToAncestor(root).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));

        // Shell rows and insets.
        Check("header height", Math.Abs(parts.Header.ActualHeight - 56) <= tolerance, $"{parts.Header.ActualHeight:0.##}");
        Check("toolbar height", Math.Abs(parts.Toolbar.ActualHeight - 48) <= tolerance, $"{parts.Toolbar.ActualHeight:0.##}");
        var toolbar = Bounds(parts.Toolbar);
        var content = Bounds(parts.Content);
        var width = root.ActualWidth;
        var outer = 1.0; // the window's 1 px border
        Check("toolbar insets", Math.Abs(toolbar.Left - outer - 10) <= tolerance + 0.5 && Math.Abs(width - toolbar.Right - outer - 10) <= tolerance + 0.5,
            $"left {toolbar.Left - outer:0.#}, right {width - toolbar.Right - outer:0.#}");
        Check("content insets", Math.Abs(content.Left - outer - 10) <= tolerance + 0.5 && Math.Abs(width - content.Right - outer - 10) <= tolerance + 0.5,
            $"left {content.Left - outer:0.#}, right {width - content.Right - outer:0.#}");
        Check("toolbar to content gap", Math.Abs(content.Top - toolbar.Bottom - 8) <= tolerance + 0.5, $"{content.Top - toolbar.Bottom:0.#}");

        // Toolbar controls: one center line, one height, inside the toolbar, no overlaps.
        var controls = Interactive(parts.Toolbar).Where(element => element.ActualHeight >= 24).ToList();
        var centerLine = toolbar.Top + (toolbar.Height / 2);
        var offCenter = controls.Select(element => (element, delta: (Bounds(element).Top + (Bounds(element).Height / 2)) - centerLine))
            .Where(item => Math.Abs(item.delta) > tolerance + 0.5).ToList();
        Check("toolbar center line", offCenter.Count == 0, offCenter.Count == 0 ? $"{controls.Count} controls" : string.Join(", ", offCenter.Select(item => $"{Name(item.element)} {item.delta:+0.#;-0.#}")));
        var heights = controls.Select(element => Math.Round(element.ActualHeight)).Distinct().OrderBy(value => value).ToList();
        Check("toolbar control heights", heights.All(value => Math.Abs(value - 36) <= 1), string.Join("/", heights));
        var outside = controls.Where(element => Bounds(element).Right > toolbar.Right + tolerance || Bounds(element).Left < toolbar.Left - tolerance).ToList();
        Check("toolbar overflow", outside.Count == 0, outside.Count == 0 ? "none" : string.Join(", ", outside.Select(Name)));
        var overlaps = Overlaps(controls.Concat(Interactive(parts.Header)).ToList(), Bounds);
        Check("control overlaps", overlaps.Count == 0, overlaps.Count == 0 ? "none" : string.Join(", ", overlaps));

        // Mail panes: one 8 px gutter; header columns over row columns.
        if (parts.ListPane is { IsVisible: true, ActualWidth: > 0 } list && parts.ReaderPane is { IsVisible: true, ActualWidth: > 0 } reader)
        {
            var a = Bounds(list);
            var b = Bounds(reader);
            var gap = Math.Abs(b.Top - a.Top) < 1 ? b.Left - a.Right : b.Top - a.Bottom;
            Check("pane gutter", Math.Abs(gap - 8) <= tolerance + 0.5, $"{gap:0.#}");
            Check("pane tops", Math.Abs(a.Top - b.Top) <= tolerance || Math.Abs(a.Left - b.Left) <= tolerance, $"{a.Top:0.#} / {b.Top:0.#}");
        }

        if (parts.ColumnHeader is { IsVisible: true } header && parts.Messages is { } messages
            && messages.ItemContainerGenerator.ContainerFromIndex(0) is ListBoxItem { IsVisible: true } row)
        {
            var headerColumns = TextColumns(header).Select(item => Bounds(item).Left).ToList();
            var rowColumns = TextColumns(row).Select(item => Bounds(item).Left).ToList();
            var from = headerColumns.FirstOrDefault();
            var fromRow = rowColumns.FirstOrDefault();
            var aligned = Math.Abs(from - fromRow) <= tolerance + 0.5;
            Check("From column", aligned, $"header {from:0.##}, row {fromRow:0.##}");
            if (!aligned)
            {
                lines.Add("  header chain: " + Chain(TextColumns(header).First(), root));
                lines.Add("  row chain: " + Chain(TextColumns(row).First(), root));
            }
        }

        return lines;
    }

    /// <summary>Left edges of an element and its ancestors (for finding where an offset comes from).</summary>
    private static string Chain(FrameworkElement element, FrameworkElement root)
    {
        var parts = new List<string>();
        for (DependencyObject? node = element; node is not null && node != root; node = VisualTreeHelper.GetParent(node))
        {
            if (node is FrameworkElement item)
                parts.Add($"{item.GetType().Name}@{item.TransformToAncestor(root).Transform(new Point(0, 0)).X:0.##}");
        }

        return string.Join(" < ", parts);
    }

    private static IEnumerable<FrameworkElement> Interactive(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is FrameworkElement { IsVisible: false })
                continue;
            if (child is ButtonBase or ComboBox or ListBoxItem or TextBox)
            {
                yield return (FrameworkElement)child;
                continue;
            }

            foreach (var nested in Interactive(child))
                yield return nested;
        }
    }

    private static IEnumerable<TextBlock> TextColumns(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is TextBlock { IsVisible: true } text && Grid.GetColumn(text) == 1)
                yield return text;
            foreach (var nested in TextColumns(child))
                yield return nested;
        }
    }

    private static List<string> Overlaps(IReadOnlyList<FrameworkElement> elements, Func<FrameworkElement, Rect> bounds)
    {
        var found = new List<string>();
        for (var i = 0; i < elements.Count; i++)
        {
            for (var j = i + 1; j < elements.Count; j++)
            {
                if (IsAncestor(elements[i], elements[j]) || IsAncestor(elements[j], elements[i]))
                    continue;
                var a = bounds(elements[i]);
                var b = bounds(elements[j]);
                // A button placed inside a text box on purpose (Clear search) is part of that box, not an overlap.
                if ((elements[i] is TextBox && a.Contains(b)) || (elements[j] is TextBox && b.Contains(a)))
                    continue;
                a.Inflate(-1, -1);
                b.Inflate(-1, -1);
                if (a.IntersectsWith(b))
                    found.Add($"{Name(elements[i])} × {Name(elements[j])}");
            }
        }

        return found;
    }

    private static bool IsAncestor(DependencyObject ancestor, DependencyObject node)
    {
        for (var current = VisualTreeHelper.GetParent(node); current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current == ancestor)
                return true;
        }

        return false;
    }

    private static string Name(FrameworkElement element)
    {
        var name = System.Windows.Automation.AutomationProperties.GetName(element);
        if (string.IsNullOrEmpty(name))
            name = element.Name;
        if (string.IsNullOrEmpty(name) && element is ContentControl { Content: string text })
            name = text;
        if (string.IsNullOrEmpty(name) && element is ListBoxItem { Content: var content })
            name = content?.ToString();
        return string.IsNullOrEmpty(name) ? element.GetType().Name : name;
    }
}
