using Mainguard.Agents.Services;
using Mainguard.Git.Services;
using Xunit;

namespace GitLoom.Tests;

// TI-07 case 7: the porcelain parser is pure (no repo). Line-oriented; paths are not quoted.
public class WorktreePorcelainParserTests
{
    [Fact]
    public void Parse_MainAndLinked_ShouldFlagMain_AndStripRefsHeads()
    {
        var input =
            "worktree /repo/main\nHEAD abc123\nbranch refs/heads/main\n\n" +
            "worktree /repo/feature\nHEAD def456\nbranch refs/heads/feature\n";

        var items = WorktreePorcelainParser.Parse(input);

        Assert.Equal(2, items.Count);
        Assert.True(items[0].IsMain);
        Assert.Equal("/repo/main", items[0].Path);
        Assert.Equal("abc123", items[0].HeadSha);
        Assert.Equal("main", items[0].Branch);
        Assert.False(items[1].IsMain);
        Assert.Equal("feature", items[1].Branch);
    }

    [Fact]
    public void Parse_Detached_ShouldSetDetached_AndNullBranch()
    {
        var input =
            "worktree /repo/main\nHEAD abc\nbranch refs/heads/main\n\n" +
            "worktree /repo/det\nHEAD zzz\ndetached\n";

        var det = WorktreePorcelainParser.Parse(input)[1];

        Assert.True(det.IsDetached);
        Assert.Null(det.Branch);
        Assert.Equal("zzz", det.HeadSha);
    }

    [Theory]
    [InlineData("locked")]
    [InlineData("locked being worked on")]
    public void Parse_Locked_ShouldSetLocked_WithOrWithoutReason(string lockedLine)
    {
        var input =
            "worktree /repo/main\nHEAD abc\nbranch refs/heads/main\n\n" +
            $"worktree /repo/wip\nHEAD yyy\nbranch refs/heads/wip\n{lockedLine}\n";

        Assert.True(WorktreePorcelainParser.Parse(input)[1].IsLocked);
    }

    [Fact]
    public void Parse_PathWithSpaces_ShouldPreserveWholePath()
    {
        var input =
            "worktree /repo/main\nHEAD abc\nbranch refs/heads/main\n\n" +
            "worktree /home/me/my worktree dir\nHEAD ppp\nbranch refs/heads/x\n";

        Assert.Equal("/home/me/my worktree dir", WorktreePorcelainParser.Parse(input)[1].Path);
    }

    [Fact]
    public void Parse_MissingBranch_ShouldLeaveBranchNull_NotDetached()
    {
        // A stanza with no trailing blank line and no branch line.
        var items = WorktreePorcelainParser.Parse("worktree /repo/main\nHEAD abc\n");

        Assert.Single(items);
        Assert.Null(items[0].Branch);
        Assert.False(items[0].IsDetached);
        Assert.True(items[0].IsMain);
    }

    [Fact]
    public void Parse_Empty_ShouldReturnEmpty()
    {
        Assert.Empty(WorktreePorcelainParser.Parse(""));
    }
}
