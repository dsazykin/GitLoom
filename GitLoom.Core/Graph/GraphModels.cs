using System.Collections.Generic;

namespace GitLoom.Core.Graph;

// Represents a single dot on the graph (a commit)
public class GraphNode
{
    public string CommitSha { get; set; } = string.Empty;

    // The Y-axis position (matches the row index in the UI ListBox)
    public int RowIndex { get; set; }

    // The X-axis position (which vertical column the dot sits in)
    public int LaneIndex { get; set; }

    // We need to know where to draw lines going downwards to parents
    public List<string> ParentShas { get; set; } = new();
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