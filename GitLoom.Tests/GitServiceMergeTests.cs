using System;
using System.Linq;
using Mainguard.Git.Exceptions;
using GitLoom.Core.Services;
using Mainguard.Git.Services;
using GitLoom.Tests.Fixtures;
using LibGit2Sharp;
using Xunit;

namespace GitLoom.Tests;

// Backfill B-3 (test strategy doc): merge/rebase/cherry-pick conflict and
// missing-branch failures must surface as typed exceptions and leave the
// repository in the documented in-progress state.
public class GitServiceMergeTests : IDisposable
{
    private readonly TempRepoFixture _fx = new();
    private readonly GitService _service = new();

    public void Dispose() => _fx.Dispose();

    [Fact]
    public void Merge_ShouldThrowMergeConflict_AndLeaveMergeInProgress()
    {
        var (_, theirs) = _fx.CreateConflict("f.txt", "ours\n", "theirs\n");

        Assert.Throws<MergeConflictException>(() => _service.Merge(_fx.RepoPath, theirs));

        // The merge must be left in progress (MERGE_HEAD present) so the user
        // can resolve it — never auto-aborted.
        Assert.True(_service.IsMergeInProgress(_fx.RepoPath));
        using var repo = new Repository(_fx.RepoPath);
        Assert.True(repo.Index.Conflicts.Any());
    }

    [Fact]
    public void Merge_ShouldThrowTyped_WhenBranchMissing()
    {
        _fx.CommitFile("a.txt", "x\n", "seed");
        Assert.Throws<GitOperationException>(() => _service.Merge(_fx.RepoPath, "no-such-branch"));
    }

    [Fact]
    public void Rebase_ShouldThrowMergeConflict_AndAbortRestoresHead()
    {
        var (ours, theirs) = _fx.CreateConflict("f.txt", "ours\n", "theirs\n");
        string oursTip;
        using (var repo = new Repository(_fx.RepoPath)) oursTip = repo.Branches[ours].Tip.Sha;

        Assert.Throws<MergeConflictException>(() => _service.Rebase(_fx.RepoPath, theirs));
        Assert.True(_service.IsRebasing(_fx.RepoPath), "conflicted rebase must stay in progress");

        _service.AbortRebase(_fx.RepoPath);
        Assert.False(_service.IsRebasing(_fx.RepoPath));
        using (var repo = new Repository(_fx.RepoPath))
        {
            Assert.Equal(oursTip, repo.Head.Tip.Sha);
        }
    }

    [Fact]
    public void Rebase_ShouldThrowTyped_WhenBranchMissing()
    {
        _fx.CommitFile("a.txt", "x\n", "seed");
        Assert.Throws<GitOperationException>(() => _service.Rebase(_fx.RepoPath, "no-such-branch"));
    }

    [Fact]
    public void CherryPick_ShouldThrowMergeConflict_OnConflictingCommit()
    {
        var (_, theirs) = _fx.CreateConflict("f.txt", "ours\n", "theirs\n");
        string theirsTip;
        using (var repo = new Repository(_fx.RepoPath)) theirsTip = repo.Branches[theirs].Tip.Sha;

        Assert.Throws<MergeConflictException>(() => _service.CherryPick(_fx.RepoPath, theirsTip));
    }

    [Fact]
    public void CherryPick_ShouldThrowTyped_WhenCommitMissing()
    {
        _fx.CommitFile("a.txt", "x\n", "seed");
        Assert.Throws<GitOperationException>(
            () => _service.CherryPick(_fx.RepoPath, new string('0', 40)));
    }

    [Fact]
    public void Merge_NonConflicting_ShouldStageWithoutCommit_AndExposeMergeMessage()
    {
        _fx.CommitFile("a.txt", "base\n", "seed");
        string defaultBranch;
        using (var repo = new Repository(_fx.RepoPath)) defaultBranch = repo.Head.FriendlyName;

        // Diverge so the merge is a real 3-way merge, not a fast-forward
        // (a fast-forward would not enter the merging state at all).
        _fx.CreateBranch("feature");
        _fx.Checkout("feature");
        _fx.CommitFile("b.txt", "feature\n", "feature work");
        _fx.Checkout(defaultBranch);
        _fx.CommitFile("c.txt", "main\n", "main work");

        _service.Merge(_fx.RepoPath, "feature");

        // CommitOnSuccess = false: merge is staged, awaiting an explicit commit.
        Assert.True(_service.IsMergeInProgress(_fx.RepoPath));
        Assert.False(string.IsNullOrWhiteSpace(_service.GetMergeMessage(_fx.RepoPath)));
    }
}
