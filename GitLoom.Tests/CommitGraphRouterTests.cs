using System.Collections.Generic;
using System.Linq;
using Mainguard.Git.Graph;
using Mainguard.Git.Models;
using Xunit;

namespace GitLoom.Tests;

public class CommitGraphRouterTests
{
    private readonly CommitGraphRouter _router = new();

    [Fact]
    public void RouteCommits_StraightLine_StaysInLaneZero()
    {
        // Arrange
        var commits = new List<GitCommitItem>
        {
            new() { Sha = "C", ParentShas = new List<string> { "B" } },
            new() { Sha = "B", ParentShas = new List<string> { "A" } },
            new() { Sha = "A", ParentShas = new List<string>() }
        };
        var fringe = new GraphFringeState();

        // Act
        var result = _router.RouteCommits(commits, fringe);

        // Assert
        Assert.Equal(3, result.Nodes.Count);
        Assert.True(result.Nodes.All(n => n.LaneIndex == 0)); // Everyone in lane 0
        Assert.Empty(result.EndFringe.ActiveLanes[0]); // Lane ends at A
    }

    [Fact]
    public void RouteCommits_WithMerge_AllocatesParallelLane()
    {
        // Arrange
        var commits = new List<GitCommitItem>
        {
            // M is a merge commit. It has two parents: Main (C) and Feature (B)
            new() { Sha = "M", ParentShas = new List<string> { "C", "B" } },
            new() { Sha = "C", ParentShas = new List<string> { "A" } },
            new() { Sha = "B", ParentShas = new List<string> { "A" } },
            new() { Sha = "A", ParentShas = new List<string>() }
        };
        var fringe = new GraphFringeState();

        // Act
        var result = _router.RouteCommits(commits, fringe);

        // Assert
        Assert.Equal(4, result.Nodes.Count);

        var nodeM = result.Nodes.Single(n => n.CommitSha == "M");
        var nodeC = result.Nodes.Single(n => n.CommitSha == "C");
        var nodeB = result.Nodes.Single(n => n.CommitSha == "B");

        // M should be in lane 0
        Assert.Equal(0, nodeM.LaneIndex);

        // Because M had 2 parents, C continues in Lane 0, B is bumped to Lane 1
        Assert.Equal(0, nodeC.LaneIndex);
        Assert.Equal(1, nodeB.LaneIndex);
    }

    [Fact]
    public void RouteCommits_OctopusMerge_AllocatesMultipleParallelLanes()
    {
        // Arrange
        var commits = new List<GitCommitItem>
        {
            // O is an octopus merge with 3 parents: C (Main), B (Feature1), A (Feature2)
            new() { Sha = "O", ParentShas = new List<string> { "C", "B", "A" } },
            new() { Sha = "C", ParentShas = new List<string>() },
            new() { Sha = "B", ParentShas = new List<string>() },
            new() { Sha = "A", ParentShas = new List<string>() }
        };
        var fringe = new GraphFringeState();

        // Act
        var result = _router.RouteCommits(commits, fringe);

        // Assert
        Assert.Equal(4, result.Nodes.Count);

        var nodeO = result.Nodes.Single(n => n.CommitSha == "O");
        Assert.Equal(0, nodeO.LaneIndex);

        var nodeC = result.Nodes.Single(n => n.CommitSha == "C");
        var nodeB = result.Nodes.Single(n => n.CommitSha == "B");
        var nodeA = result.Nodes.Single(n => n.CommitSha == "A");

        Assert.Equal(0, nodeC.LaneIndex);
        Assert.Equal(1, nodeB.LaneIndex);
        Assert.Equal(2, nodeA.LaneIndex);

        Assert.Equal(3, nodeO.OutgoingLines.Count);
        Assert.Contains(nodeO.OutgoingLines, l => l.FromLane == 0 && l.ToLane == 0); // To C
        Assert.Contains(nodeO.OutgoingLines, l => l.FromLane == 0 && l.ToLane == 1); // To B
        Assert.Contains(nodeO.OutgoingLines, l => l.FromLane == 0 && l.ToLane == 2); // To A
    }

    [Fact]
    public void RouteCommits_ComplexOverlappingTracks_ValidatesPassthroughLines()
    {
        // Arrange
        // Graph structure:
        // M1 (merges F1 into Main)
        // | \
        // |  F1
        // M2 |  (merges F2 into Main)
        // | \|
        // |  F2
        // C  |
        // | /
        // B 
        // |/
        // A

        var commits = new List<GitCommitItem>
        {
            new() { Sha = "M1", ParentShas = new List<string> { "M2", "F1" } }, // Lane 0
            new() { Sha = "F1", ParentShas = new List<string> { "B" } },        // Lane 1
            new() { Sha = "M2", ParentShas = new List<string> { "C", "F2" } },  // Lane 0
            new() { Sha = "F2", ParentShas = new List<string> { "C" } },        // Lane 2
            new() { Sha = "C", ParentShas = new List<string> { "B" } },         // Lane 0
            new() { Sha = "B", ParentShas = new List<string> { "A" } },         // Lane 0
            new() { Sha = "A", ParentShas = new List<string>() }                // Lane 0
        };
        var fringe = new GraphFringeState();

        // Act
        var result = _router.RouteCommits(commits, fringe);

        // Assert
        Assert.Equal(7, result.Nodes.Count);

        var m1 = result.Nodes.Single(n => n.CommitSha == "M1");
        var f1 = result.Nodes.Single(n => n.CommitSha == "F1");
        var m2 = result.Nodes.Single(n => n.CommitSha == "M2");
        var f2 = result.Nodes.Single(n => n.CommitSha == "F2");
        var c = result.Nodes.Single(n => n.CommitSha == "C");
        var b = result.Nodes.Single(n => n.CommitSha == "B");
        var a = result.Nodes.Single(n => n.CommitSha == "A");

        // Validate Lane Assignments
        Assert.Equal(0, m1.LaneIndex);
        Assert.Equal(1, f1.LaneIndex);

        Assert.Equal(0, m2.LaneIndex);
        Assert.Equal(2, f2.LaneIndex);

        Assert.Equal(0, c.LaneIndex);
        Assert.Equal(0, b.LaneIndex);
        Assert.Equal(0, a.LaneIndex);

        // F1 should pass straight through M2 and F2!
        Assert.Contains(m2.OutgoingLines, l => l.FromLane == 1 && l.ToLane == 1);
        Assert.Contains(f2.OutgoingLines, l => l.FromLane == 1 && l.ToLane == 1);

        // At C, the feature branch in lane 1 (which is B) merges back into the main lane 0 because C and F1 share B as a parent!
        Assert.Contains(c.OutgoingLines, l => l.FromLane == 1 && l.ToLane == 0);
    }
}
