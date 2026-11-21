using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Alondra.Core.Layout;

namespace Alondra.App.Shell;

public sealed class BreadthChangedEventArgs : EventArgs
{
    public required string PaneId { get; init; }
    public required DockEdge Edge { get; init; }
    public required double Breadth { get; init; }
}

public sealed class MailDockPanel : Panel
{
    private IReadOnlyList<SplitterHit> _splitters = [];
    private string? _dragPane;
    private DockEdge _dragEdge;
    private double _startBreadth;
    private Point _startPoint;
    private double? _dragFolder;
    private double? _dragReading;

    public static readonly DependencyProperty FoldersDockProperty = DependencyProperty.Register(
        nameof(FoldersDock), typeof(DockEdge), typeof(MailDockPanel), new FrameworkPropertyMetadata(DockEdge.Top, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty ReadingDockProperty = DependencyProperty.Register(
        nameof(ReadingDock), typeof(DockEdge), typeof(MailDockPanel), new FrameworkPropertyMetadata(DockEdge.Right, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty FolderBreadthProperty = DependencyProperty.Register(
        nameof(FolderBreadth), typeof(double), typeof(MailDockPanel), new FrameworkPropertyMetadata(52d, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty ReadingBreadthProperty = DependencyProperty.Register(
        nameof(ReadingBreadth), typeof(double), typeof(MailDockPanel), new FrameworkPropertyMetadata(330d, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public DockEdge FoldersDock
    {
        get => (DockEdge)GetValue(FoldersDockProperty);
        set => SetValue(FoldersDockProperty, value);
    }

    public DockEdge ReadingDock
    {
        get => (DockEdge)GetValue(ReadingDockProperty);
        set => SetValue(ReadingDockProperty, value);
    }

    public double FolderBreadth
    {
        get => (double)GetValue(FolderBreadthProperty);
        set => SetValue(FolderBreadthProperty, value);
    }

    public double ReadingBreadth
    {
        get => (double)GetValue(ReadingBreadthProperty);
        set => SetValue(ReadingBreadthProperty, value);
    }

    public event EventHandler<BreadthChangedEventArgs>? BreadthCommitted;

    protected override Size MeasureOverride(Size availableSize)
    {
        var size = new Size(Math.Max(0, availableSize.Width), Math.Max(0, availableSize.Height));
        if (double.IsInfinity(size.Width) || double.IsInfinity(size.Height))
        {
            foreach (UIElement child in InternalChildren)
                child.Measure(size);
            return new Size(double.IsInfinity(size.Width) ? 0 : size.Width, double.IsInfinity(size.Height) ? 0 : size.Height);
        }

        // Measure each pane at the size it will be arranged to. Measuring at the full panel size made wrapped text
        // (the reading pane) ask for more width than its slot, and WPF then clipped it instead of wrapping it.
        var frames = LayoutArranger.Arrange(FoldersDock, ReadingDock, size.Width, size.Height, _dragFolder ?? FolderBreadth, _dragReading ?? ReadingBreadth);
        MeasureNamed(0, frames.Folders);
        MeasureNamed(1, frames.Inbox);
        MeasureNamed(2, frames.Reader);
        return size;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var frames = LayoutArranger.Arrange(FoldersDock, ReadingDock, finalSize.Width, finalSize.Height, _dragFolder ?? FolderBreadth, _dragReading ?? ReadingBreadth);
        _splitters = frames.Splitters;
        ArrangeNamed(0, frames.Folders);
        ArrangeNamed(1, frames.Inbox);
        ArrangeNamed(2, frames.Reader);
        return finalSize;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        var point = e.GetPosition(this);
        foreach (var splitter in _splitters)
        {
            if (!Hit(splitter.Bounds, point))
                continue;
            _dragPane = splitter.PaneId;
            _dragEdge = splitter.Edge;
            _startBreadth = splitter.PaneId == "folders" ? (_dragFolder ?? FolderBreadth) : (_dragReading ?? ReadingBreadth);
            _startPoint = point;
            CaptureMouse();
            e.Handled = true;
            return;
        }

        base.OnMouseLeftButtonDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var point = e.GetPosition(this);
        if (_dragPane is null)
        {
            Cursor = _splitters.Any(splitter => Hit(splitter.Bounds, point))
                ? (_splitters.First(splitter => Hit(splitter.Bounds, point)).Edge is DockEdge.Left or DockEdge.Right ? Cursors.SizeWE : Cursors.SizeNS)
                : null;
            base.OnMouseMove(e);
            return;
        }

        var next = Math.Clamp(LayoutArranger.Resize(_dragEdge, _startBreadth, point.X - _startPoint.X, point.Y - _startPoint.Y), 44, 1200);
        if (_dragPane == "folders")
            _dragFolder = next;
        else
            _dragReading = next;
        InvalidateMeasure();
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (_dragPane is null)
        {
            base.OnMouseLeftButtonUp(e);
            return;
        }

        var breadth = _dragPane == "folders" ? (_dragFolder ?? FolderBreadth) : (_dragReading ?? ReadingBreadth);
        var pane = _dragPane;
        var edge = _dragEdge;
        _dragPane = null;
        _dragFolder = null;
        _dragReading = null;
        BreadthCommitted?.Invoke(this, new BreadthChangedEventArgs { PaneId = pane, Edge = edge, Breadth = breadth });
        ReleaseMouseCapture();
        e.Handled = true;
    }

    private void MeasureNamed(int index, Box box)
    {
        if (index < InternalChildren.Count)
            InternalChildren[index].Measure(box.HasArea ? new Size(box.Width, box.Height) : new Size(0, 0));
    }

    private void ArrangeNamed(int index, Box box)
    {
        if (index >= InternalChildren.Count)
            return;
        var child = InternalChildren[index];
        if (!box.HasArea)
        {
            child.Arrange(new Rect(0, 0, 0, 0));
            return;
        }

        child.Arrange(new Rect(box.X, box.Y, box.Width, box.Height));
    }

    private static bool Hit(Box box, Point point) =>
        point.X >= box.X && point.X <= box.X + box.Width && point.Y >= box.Y && point.Y <= box.Y + box.Height;
}
