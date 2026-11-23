using Alondra1.Core.Config;
using Alondra1.Core.Layout;

namespace Alondra1.Tests;

/// <summary>Client spec M3 §5.1: split minimums (list 420, reader 440), one pane at a time below them, 55/45 default.</summary>
public class PaneLayoutTests
{
    [Theory]
    [InlineData(DockEdge.Hidden, DockEdge.Right, 868, true)]   // 420 + 8 + 440
    [InlineData(DockEdge.Hidden, DockEdge.Right, 867, false)]
    [InlineData(DockEdge.Left, DockEdge.Right, 868 + 152 + 8, true)]
    [InlineData(DockEdge.Left, DockEdge.Right, 868 + 152 + 7, false)]
    [InlineData(DockEdge.Hidden, DockEdge.Left, 600, false)]
    [InlineData(DockEdge.Hidden, DockEdge.Hidden, 300, true)]  // no reading pane: nothing to split
    public void Split_needs_room_for_both_minimums(DockEdge folders, DockEdge reading, double width, bool fits) =>
        Assert.Equal(fits, LayoutArranger.FitsSplit(folders, reading, width, 700, folderBreadth: 152));

    [Theory]
    [InlineData(448, true)]
    [InlineData(447, false)]
    public void Stacked_reader_needs_height_for_both(double height, bool fits) =>
        Assert.Equal(fits, LayoutArranger.FitsSplit(DockEdge.Hidden, DockEdge.Bottom, 600, height, 52));

    [Fact]
    public void One_pane_shows_the_list_or_the_reader_in_the_same_place()
    {
        var list = LayoutArranger.Arrange(DockEdge.Left, DockEdge.Right, 800, 600, 152, 520, single: SinglePane.List);
        Assert.True(list.Inbox.HasArea);
        Assert.False(list.Reader.HasArea);
        Assert.Equal(800 - 152 - 8, list.Inbox.Width);

        var reader = LayoutArranger.Arrange(DockEdge.Left, DockEdge.Right, 800, 600, 152, 520, single: SinglePane.Reader);
        Assert.False(reader.Inbox.HasArea);
        Assert.Equal(list.Inbox, reader.Reader);
        Assert.True(reader.Folders.HasArea); // folders stay where they are
        Assert.Single(reader.Splitters);     // only the folder splitter remains
    }

    [Fact]
    public void While_split_neither_pane_goes_below_its_minimum()
    {
        // A reader dragged narrow is held at 440; a reader dragged wide leaves the list 420.
        var narrow = LayoutArranger.Arrange(DockEdge.Hidden, DockEdge.Right, 1200, 700, 0, 200);
        Assert.Equal(440, narrow.Reader.Width);
        var wide = LayoutArranger.Arrange(DockEdge.Hidden, DockEdge.Right, 1200, 700, 0, 1000);
        Assert.Equal(420, wide.Inbox.Width);
        Assert.Equal(1200 - 420 - 8, wide.Reader.Width);
        Assert.False(wide.Inbox.Overlaps(wide.Reader));
    }

    [Fact]
    public void Default_split_is_about_55_45_at_the_default_window()
    {
        var layout = new LayoutSettings();
        // The default 1180 px window leaves 1160 px for content (10 px insets); folders are on top (toolbar tabs).
        var frames = LayoutArranger.Arrange(DockEdge.Hidden, layout.ReadingDock, 1160, 650, 0, layout.ReadingBreadthSide);
        var share = frames.Inbox.Width / (frames.Inbox.Width + frames.Reader.Width);
        Assert.InRange(share, 0.53, 0.57);
    }
}
