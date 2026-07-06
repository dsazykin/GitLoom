using System;
using System.Collections.Generic;
using Avalonia;
using GitLoom.App.Controls;
using Xunit;

namespace GitLoom.Tests;

/// <summary>
/// TI-09 #1 — the pure commit-graph hit-tester. All geometry is exercised without an Avalonia
/// control: node-center hits, the slop boundary, row rounding across scroll offsets, label
/// precedence over nodes, and empty space. Fixed geometry: rowHeight 20, laneWidth 15
/// (so lane L's dot is centered at L*15 + 7.5), nodeRadius 4, hitSlop 6 → a Node hit when the
/// horizontal distance to the dot is ≤ 10.
/// </summary>
public class GraphHitTesterTests
{
    private const double RowHeight = 20;
    private const double LaneWidth = 15;
    private const double NodeRadius = 4;
    private const double HitSlop = 6;

    private static GraphHitTester NewTester() => new(RowHeight, LaneWidth, NodeRadius, HitSlop);

    // A three-row graph: row0 lane0 "A", row1 lane1 "B", row2 lane0 "C".
    private static readonly IReadOnlyList<(int RowIndex, int LaneIndex, string Sha)> Nodes =
        new[] { (0, 0, "A"), (1, 1, "B"), (2, 0, "C") };

    [Theory]
    // --- node-center hit (dead center on lane 0's dot, row 0) ---
    [InlineData(7.5, 10, 0.0, GraphHitKind.Node, "A")]
    // --- exactly on the slop boundary (distance == radius + slop == 10) → still a hit ---
    [InlineData(17.5, 10, 0.0, GraphHitKind.Node, "A")]
    // --- just outside the slop (distance 11) → miss ---
    [InlineData(18.5, 10, 0.0, GraphHitKind.None, null)]
    // --- row rounding: half-row offset pushes y=10+10 into row 1; lane 1 dot at 22.5 → B ---
    [InlineData(22.5, 10, 10.0, GraphHitKind.Node, "B")]
    // --- row rounding: large offset pushes y=5+40 into row 2; lane 0 dot at 7.5 → C ---
    [InlineData(7.5, 5, 40.0, GraphHitKind.Node, "C")]
    // --- within row 0 near its bottom edge (y=19 < rowHeight) still resolves row 0 ---
    [InlineData(7.5, 19, 0.0, GraphHitKind.Node, "A")]
    // --- crossing the row boundary (y=21) lands in row 1, whose dot is lane 1, so lane-0 x misses ---
    [InlineData(7.5, 21, 0.0, GraphHitKind.None, null)]
    // --- correct row, wrong lane column → miss (row 0's dot is lane 0, x is over lane 2) ---
    [InlineData(37.5, 10, 0.0, GraphHitKind.None, "A")]
    // --- empty horizontal space far from any lane → None ---
    [InlineData(100, 10, 0.0, GraphHitKind.None, null)]
    public void HitTest_ResolvesNodesAndRows(double x, double y, double scroll, GraphHitKind expectedKind, string? expectedSha)
    {
        var tester = NewTester();

        var hit = tester.HitTest(new Point(x, y), scroll, Nodes);

        Assert.Equal(expectedKind, hit.Kind);
        if (expectedKind == GraphHitKind.Node)
        {
            Assert.Equal(expectedSha, hit.Sha);
            Assert.Null(hit.RefName);
        }
        else
        {
            Assert.Null(hit.Sha);
        }
    }

    [Fact]
    public void HitTest_LabelRect_WinsOverNode()
    {
        var tester = NewTester();
        // Label rect overlaps row 0 / lane 0, where node "A" also sits.
        tester.SetLabelBounds(new[] { (new Rect(0, 0, 50, 15), "main", "A") });

        var hit = tester.HitTest(new Point(10, 5), 0, Nodes);

        Assert.Equal(GraphHitKind.Label, hit.Kind);
        Assert.Equal("main", hit.RefName);
        Assert.Equal("A", hit.Sha);
    }

    [Fact]
    public void HitTest_OutsideLabelRect_FallsThroughToNode()
    {
        var tester = NewTester();
        tester.SetLabelBounds(new[] { (new Rect(30, 0, 50, 15), "feature", "B") });

        // Point is over lane 0's dot (row 0) but outside the label rect (which starts at x=30).
        var hit = tester.HitTest(new Point(7.5, 10), 0, Nodes);

        Assert.Equal(GraphHitKind.Node, hit.Kind);
        Assert.Equal("A", hit.Sha);
    }

    [Fact]
    public void HitTest_NegativeEffectiveOffset_ReturnsNone()
    {
        var tester = NewTester();

        var hit = tester.HitTest(new Point(7.5, -30), 0, Nodes);

        Assert.Equal(GraphHitKind.None, hit.Kind);
    }

    [Fact]
    public void HitTest_EmptyNodeList_ReturnsNone()
    {
        var tester = NewTester();

        var hit = tester.HitTest(new Point(7.5, 10), 0, Array.Empty<(int, int, string)>());

        Assert.Equal(GraphHitKind.None, hit.Kind);
    }

    [Fact]
    public void SetLabelBounds_CopiesFrame_SoLaterMutationDoesNotLeak()
    {
        var tester = NewTester();
        var frame = new List<(Rect, string, string)> { (new Rect(0, 0, 50, 15), "main", "A") };
        tester.SetLabelBounds(frame);

        // Mutating the caller's list after recording must not affect a subsequent hit-test.
        frame.Clear();

        var hit = tester.HitTest(new Point(10, 5), 0, Nodes);
        Assert.Equal(GraphHitKind.Label, hit.Kind);
        Assert.Equal("main", hit.RefName);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Constructor_RejectsNonPositiveRowHeight(double rowHeight)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new GraphHitTester(rowHeight, LaneWidth, NodeRadius, HitSlop));
    }

    [Fact]
    public void Constructor_RejectsNonPositiveLaneWidth()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new GraphHitTester(RowHeight, 0, NodeRadius, HitSlop));
    }
}
