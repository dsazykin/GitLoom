using System.Collections.Generic;

namespace GitLoom.Core.Graph;

/// <summary>
/// One routed bottom-half line segment of a graph row: from the lane it leaves at the row's
/// vertical center to the lane it lands on at the row's bottom edge. A value type on purpose —
/// a wide DAG emits millions of these (≈ lanes × commits), and a reference type here costs a
/// heap allocation per segment (Hotspot Register H2).
/// </summary>
public readonly record struct GraphLine(int FromLane, int ToLane);

/// <summary>
/// One routed graph row — the commit dot plus everything <see cref="CommitGraphRouter"/> decided
/// about its row: which lane the dot sits in, which lanes carry a line into the row's top half
/// (<see cref="IncomingLanes"/>), and where each line leaves through the bottom half
/// (<see cref="OutgoingLines"/>: parent links and passthroughs alike). Rendered one-per-row by
/// <c>CommitGraphCanvas</c>, which draws only from this data — the canvas never re-derives layout.
/// </summary>
public class GraphNode
{
    public string CommitSha { get; set; } = string.Empty;

    /// <summary>Zero-based row position within the routed chunk sequence (top = newest).</summary>
    public int RowIndex { get; set; }

    /// <summary>The lane (X column) this commit's dot occupies.</summary>
    public int LaneIndex { get; set; }

    public List<string> ParentShas { get; set; } = new();

    /// <summary>Lanes whose line continues into this row from the row above (top-half verticals).</summary>
    public List<int> IncomingLanes { get; set; } = new();

    /// <summary>Bottom-half segments: dot→parent links plus every passing branch's continuation.</summary>
    public List<GraphLine> OutgoingLines { get; set; } = new();
}

/// <summary>
/// The router's carry-over state at the exact moment a chunk ends, so the next chunk (the
/// timeline pages in 50 commits at a time) continues every branch line seamlessly. Index = lane
/// column; value = the SHA that lane is currently traveling down towards (empty string = free
/// lane). Routing chunk-by-chunk through the fringe is pinned to equal routing the whole history
/// at once (<c>CommitGraphRouterWideDagTests</c>).
/// </summary>
public class GraphFringeState
{
    public List<string> ActiveLanes { get; set; } = new();
}

/// <summary>The routed output for one chunk: the nodes to render plus the fringe to resume from.</summary>
public class GraphRouteResult
{
    public List<GraphNode> Nodes { get; set; } = new();
    public GraphFringeState EndFringe { get; set; } = new();
}
