using System.Collections.Generic;
using System.Linq;
using Xunit;
using GitLoom.Core.Graph;
using GitLoom.Core.Models;

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
}