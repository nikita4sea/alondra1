namespace Alondra.Core.Layout;

public enum DockEdge
{
    Left,
    Right,
    Top,
    Bottom,
    Hidden,
}

public readonly record struct Box(double X, double Y, double Width, double Height)
{
    public bool HasArea => Width > 0.5 && Height > 0.5;

    public bool Overlaps(Box other)
    {
        if (!HasArea || !other.HasArea)
            return false;
        return X < other.X + other.Width - 0.5
            && other.X < X + Width - 0.5
            && Y < other.Y + other.Height - 0.5
            && other.Y < Y + Height - 0.5;
    }
}

public readonly record struct SplitterHit(Box Bounds, string PaneId, DockEdge Edge);

public sealed record LayoutFrames(Box Folders, Box Inbox, Box Reader, IReadOnlyList<SplitterHit> Splitters);

public abstract record LayoutNode;

public sealed record LayoutLeaf(string Id) : LayoutNode;

public sealed record LayoutSplit(bool Horizontal, LayoutNode First, LayoutNode Second, string PaneId, DockEdge Edge) : LayoutNode;

/// <summary>
/// Mirrors the reference dock() order: the reading pane is placed around the list,
/// then the folder pane is placed around that result.
/// </summary>
public static class LayoutArranger
{
    public const double MinListWidth = 160;
    public const double MinListHeight = 96;
    public const double MinFolderSide = 96;
    public const double MinFolderBand = 44;
    public const double MinReaderSide = 108;
    public const double MinReaderBand = 96;

    public static LayoutNode Compose(DockEdge folders, DockEdge reading)
    {
        LayoutNode node = new LayoutLeaf("inbox");
        node = Dock(node, "reader", reading);
        node = Dock(node, "folders", folders);
        return node;
    }

    public static string Describe(LayoutNode node) => node switch
    {
        LayoutLeaf leaf => leaf.Id,
        LayoutSplit split => (split.Horizontal ? "H" : "V") + "(" + Describe(split.First) + "|" + Describe(split.Second) + ")",
        _ => "?",
    };

    public static LayoutFrames Arrange(
        DockEdge folders,
        DockEdge reading,
        double width,
        double height,
        double folderBreadth,
        double readingBreadth,
        double gap = 6,
        double pad = 6)
    {
        var map = new Dictionary<string, Box>(StringComparer.Ordinal);
        var splitters = new List<SplitterHit>();
        var inner = new Box(pad, pad, Math.Max(0, width - (2 * pad)), Math.Max(0, height - (2 * pad)));
        ArrangeNode(Compose(folders, reading), inner, folderBreadth, readingBreadth, gap, map, splitters);
        map.TryGetValue("folders", out var folderBox);
        map.TryGetValue("inbox", out var inboxBox);
        map.TryGetValue("reader", out var readerBox);
        return new LayoutFrames(folderBox, inboxBox, readerBox, splitters);
    }

    public static double BreadthFor(DockEdge edge, double side, double band) =>
        edge is DockEdge.Left or DockEdge.Right ? side : band;

    public static double Resize(DockEdge edge, double startBreadth, double deltaX, double deltaY) => edge switch
    {
        DockEdge.Left => startBreadth + deltaX,
        DockEdge.Right => startBreadth - deltaX,
        DockEdge.Top => startBreadth + deltaY,
        DockEdge.Bottom => startBreadth - deltaY,
        _ => startBreadth,
    };

    private static LayoutNode Dock(LayoutNode center, string pane, DockEdge edge)
    {
        if (edge == DockEdge.Hidden)
            return center;
        var leaf = new LayoutLeaf(pane);
        var horizontal = edge is DockEdge.Left or DockEdge.Right;
        if (edge is DockEdge.Left or DockEdge.Top)
            return new LayoutSplit(horizontal, leaf, center, pane, edge);
        return new LayoutSplit(horizontal, center, leaf, pane, edge);
    }

    private static void ArrangeNode(
        LayoutNode node,
        Box box,
        double folderBreadth,
        double readingBreadth,
        double gap,
        Dictionary<string, Box> map,
        List<SplitterHit> splitters)
    {
        if (node is LayoutLeaf leaf)
        {
            map[leaf.Id] = box;
            return;
        }

        var split = (LayoutSplit)node;
        var paneIsFirst = split.First is LayoutLeaf first && first.Id == split.PaneId;
        var rest = paneIsFirst ? split.Second : split.First;
        var restMin = MinSize(rest, gap);
        var requested = split.PaneId == "folders" ? folderBreadth : readingBreadth;
        var paneMin = PaneMin(split);
        if (split.Horizontal)
        {
            var (breadth, restSize) = SplitAxis(box.Width, requested, paneMin, restMin.Width, gap);
            Box firstBox;
            Box secondBox;
            if (paneIsFirst)
            {
                firstBox = new Box(box.X, box.Y, breadth, box.Height);
                secondBox = new Box(box.X + breadth + (breadth > 0 ? gap : 0), box.Y, restSize, box.Height);
                if (breadth > 0)
                    splitters.Add(new SplitterHit(new Box(box.X + breadth, box.Y, gap, box.Height), split.PaneId, split.Edge));
            }
            else
            {
                firstBox = new Box(box.X, box.Y, restSize, box.Height);
                secondBox = new Box(box.X + restSize + (breadth > 0 ? gap : 0), box.Y, breadth, box.Height);
                if (breadth > 0)
                    splitters.Add(new SplitterHit(new Box(box.X + restSize, box.Y, gap, box.Height), split.PaneId, split.Edge));
            }

            ArrangeNode(split.First, firstBox, folderBreadth, readingBreadth, gap, map, splitters);
            ArrangeNode(split.Second, secondBox, folderBreadth, readingBreadth, gap, map, splitters);
            return;
        }

        var (band, restHeight) = SplitAxis(box.Height, requested, paneMin, restMin.Height, gap);
        Box top;
        Box bottom;
        if (paneIsFirst)
        {
            top = new Box(box.X, box.Y, box.Width, band);
            bottom = new Box(box.X, box.Y + band + (band > 0 ? gap : 0), box.Width, restHeight);
            if (band > 0)
                splitters.Add(new SplitterHit(new Box(box.X, box.Y + band, box.Width, gap), split.PaneId, split.Edge));
        }
        else
        {
            top = new Box(box.X, box.Y, box.Width, restHeight);
            bottom = new Box(box.X, box.Y + restHeight + (band > 0 ? gap : 0), box.Width, band);
            if (band > 0)
                splitters.Add(new SplitterHit(new Box(box.X, box.Y + restHeight, box.Width, gap), split.PaneId, split.Edge));
        }

        ArrangeNode(split.First, top, folderBreadth, readingBreadth, gap, map, splitters);
        ArrangeNode(split.Second, bottom, folderBreadth, readingBreadth, gap, map, splitters);
    }

    private static (double Pane, double Remainder) SplitAxis(double available, double requested, double paneMin, double restMin, double gap)
    {
        var room = Math.Max(0, available);
        if (room <= restMin)
            return (0, room);
        var usable = room - gap;
        if (usable <= restMin)
            return (0, room);
        var max = usable - restMin;
        var breadth = requested;
        if (breadth < paneMin)
            breadth = paneMin;
        if (breadth > max)
            breadth = max;
        if (breadth < 0)
            breadth = 0;
        return (breadth, usable - breadth);
    }

    private static double PaneMin(LayoutSplit split)
    {
        if (split.PaneId == "folders")
            return split.Horizontal ? MinFolderSide : MinFolderBand;
        return split.Horizontal ? MinReaderSide : MinReaderBand;
    }

    private static (double Width, double Height) MinSize(LayoutNode node, double gap)
    {
        if (node is LayoutLeaf leaf)
            return leaf.Id == "inbox" ? (MinListWidth, MinListHeight) : (0, 0);

        var split = (LayoutSplit)node;
        var paneIsFirst = split.First is LayoutLeaf first && first.Id == split.PaneId;
        var rest = paneIsFirst ? split.Second : split.First;
        var restMin = MinSize(rest, gap);
        var paneAxis = PaneMin(split);
        var paneCross = split.PaneId == "folders"
            ? (split.Horizontal ? MinFolderBand : MinFolderSide)
            : (split.Horizontal ? MinReaderBand : MinReaderSide);
        if (split.Horizontal)
            return (paneAxis + gap + restMin.Width, Math.Max(paneCross, restMin.Height));
        return (Math.Max(paneCross, restMin.Width), paneAxis + gap + restMin.Height);
    }
}
