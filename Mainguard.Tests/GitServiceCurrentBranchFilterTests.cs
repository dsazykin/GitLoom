using System.Linq;
using Mainguard.Agents.Services;
using Mainguard.Git.Models;
using Mainguard.Git.Services;
using Mainguard.Tests.Fixtures;
using Xunit;

namespace Mainguard.Tests;

/// <summary>
/// T-09 §3.5 — the "current branch only" walk restricts history to what is reachable from HEAD
/// (+ upstream), excluding commits unique to an unrelated branch. Contrasted with an explicit
/// branch filter, which can reach that other branch's commits.
/// </summary>
public class GitServiceCurrentBranchFilterTests
{
    [Fact]
    public void CurrentBranchOnly_ShouldExclude_UnrelatedBranchCommit()
    {
        using var fx = new TempRepoFixture();
        var git = new GitService();

        var c1 = fx.CommitFile("a.txt", "1\n", "c1");   // shared base
        fx.CreateBranch("other");
        fx.Checkout("other");
        var cOther = fx.CommitFile("o.txt", "o\n", "only on other");
        fx.Checkout(git.GetBranches(fx.RepoPath).First(b => b.FriendlyName != "other" && !b.IsRemote).Name);
        var cMain = fx.CommitFile("b.txt", "2\n", "on main");

        var headBranch = git.GetHeadState(fx.RepoPath).CurrentBranchName;
        Assert.NotEqual("other", headBranch); // sanity: we are back on the main line

        var currentOnly = git.GetRecentCommits(fx.RepoPath, 0, 50, new CommitSearchFilter { CurrentBranchOnly = true })
            .Select(c => c.Sha).ToList();

        Assert.Contains(c1, currentOnly);
        Assert.Contains(cMain, currentOnly);
        Assert.DoesNotContain(cOther, currentOnly); // unrelated branch is excluded

        // An explicit branch filter, by contrast, reaches the other branch's unique commit.
        var otherWalk = git.GetRecentCommits(fx.RepoPath, 0, 50, new CommitSearchFilter { BranchName = "other" })
            .Select(c => c.Sha).ToList();
        Assert.Contains(cOther, otherWalk);
    }
}
