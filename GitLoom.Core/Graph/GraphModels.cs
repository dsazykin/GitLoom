using System.Collections.Generic;

namespace GitLoom.Core.Graph;

public class GraphLine
{
    public int FromLane { get; set; }
    public int ToLane { get; set; }
}

// Represents a single dot on the graph (a commit)
public class GraphNode
{
    public string CommitSha { get; set; } = string.Empty;
    public int RowIndex { get; set; }
    public int LaneIndex { get; set; }
    public List<string> ParentShas { get; set; } = new();

    public List<int> IncomingLanes { get; set; } = new();
    public List<GraphLine> OutgoingLines { get; set; } = new();
}

// Represents the state of the graph at the exact moment a chunk ends,
// allowing the next chunk of 50 commits to seamlessly continue the lines.
public class GraphFringeState
{
    // The index represents the X-axis lane column.
    // The string is the SHA of the commit that lane is currently traveling down towards.
    public List<string> ActiveLanes { get; set; } = new();
}

// The output payload returned to the UI after processing a chunk of commits
public class GraphRouteResult
{
    public List<GraphNode> Nodes { get; set; } = new();
    public GraphFringeState EndFringe { get; set; } = new();
}
