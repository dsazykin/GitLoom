using System;
using System.Collections.Generic;
using Avalonia;

namespace Mainguard.App.Shell.Controls;

/// <summary>What a point in the commit graph landed on.</summary>
public enum GraphHitKind
{
    /// <summary>Nothing hit-testable under the point.</summary>
    None,
    /// <summary>A commit dot.</summary>
    Node,
    /// <summary>A ref label (branch/tag chip).</summary>
    Label
}

/// <summary>
/// The result of a single hit-test. <see cref="Sha"/> is populated for both Node and Label
/// hits; <see cref="RefName"/> only for Label hits.
/// </summary>
public readonly record struct GraphHit(GraphHitKind Kind, string? Sha, string? RefName);

/// <summary>
/// Pure, unit-testable hit-testing for the commit graph (T-09). It carries no Avalonia
/// control dependencies beyond <see cref="Point"/>/<see cref="Rect"/> so the row/node/label
/// targeting math can be exercised in isolation (TI-09 #1) instead of being buried untestably
/// in <c>CommitGraphCanvas</c>.
///
/// The geometry mirrors the canvas: a lane's dot is centered at
/// <c>lane * laneWidth + laneWidth / 2</c> horizontally and at the row's vertical midpoint.
/// Labels are recorded per render pass (their rects are computed by the view) and win over
/// nodes when both are under the point.
/// </summary>
public sealed class GraphHitTester
{
    private readonly double _rowHeight;
    private readonly double _laneWidth;
    private readonly double _nodeRadius;
    private readonly double _hitSlop;

    private IReadOnlyList<(Rect Bounds, string RefName, string Sha)> _labels =
        Array.Empty<(Rect, string, string)>();

    public GraphHitTester(double rowHeight, double laneWidth, double nodeRadius, double hitSlop)
    {
        if (rowHeight <= 0) throw new ArgumentOutOfRangeException(nameof(rowHeight));
        if (laneWidth <= 0) throw new ArgumentOutOfRangeException(nameof(laneWidth));
        _rowHeight = rowHeight;
        _laneWidth = laneWidth;
        _nodeRadius = nodeRadius;
        _hitSlop = hitSlop;
    }

    /// <summary>
    /// Records the ref-label rectangles for the current render pass. Call once per frame before
    /// hit-testing; labels are checked before nodes so a chip always wins over the dot beneath it.
    /// </summary>
    public void SetLabelBounds(IReadOnlyList<(Rect Bounds, string RefName, string Sha)> frame)
    {
        // Copy so a later mutation of the caller's list can't corrupt a recorded frame.
        _labels = frame is null
            ? Array.Empty<(Rect, string, string)>()
            : new List<(Rect, string, string)>(frame);
    }

    /// <summary>
    /// Resolves the point <paramref name="p"/> (in graph-viewport coordinates) to a node, label,
    /// or nothing. <paramref name="verticalScrollOffset"/> is added before rounding to a row so the
    /// same tester works for both the per-row canvas (offset 0) and a scrolling whole-graph canvas.
    /// </summary>
    public GraphHit HitTest(Point p, double verticalScrollOffset,
        IReadOnlyList<(int RowIndex, int LaneIndex, string Sha)> nodes)
    {
        // Labels win over nodes.
        foreach (var (bounds, refName, sha) in _labels)
        {
            if (bounds.Contains(p))
            {
                return new GraphHit(GraphHitKind.Label, sha, refName);
            }
        }

        double y = p.Y + verticalScrollOffset;
        if (y < 0)
        {
            return new GraphHit(GraphHitKind.None, null, null);
        }

        int row = (int)(y / _rowHeight);
        foreach (var n in nodes)
        {
            if (n.RowIndex != row) continue;
            double laneCenterX = n.LaneIndex * _laneWidth + _laneWidth / 2;
            if (Math.Abs(p.X - laneCenterX) <= _nodeRadius + _hitSlop)
            {
                return new GraphHit(GraphHitKind.Node, n.Sha, null);
            }
        }

        return new GraphHit(GraphHitKind.None, null, null);
    }
}
