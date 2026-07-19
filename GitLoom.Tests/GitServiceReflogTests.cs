using System;
using System.IO;
using System.Linq;
using GitLoom.Core;
using Mainguard.Git.Exceptions;
using GitLoom.Core.Services;
using Mainguard.Git.Services;
using GitLoom.Tests.Fixtures;
using LibGit2Sharp;
using Xunit;
using Repository = LibGit2Sharp.Repository;

namespace GitLoom.Tests;

/// <summary>
/// TI-20 — the reflog read service (<see cref="GitService.GetReflog"/>) plus the recovery flow it
/// feeds. Covers the contract cases (commit→hard-reset shows both moves with correct from/to;
/// create-branch-here at the pre-reset entry restores the commit; deleted-branch recovery finds the
/// orphaned tip) and every Master-Doc edge (fresh/empty reflog, detached-HEAD, multi-line commit
/// message collapsed to one line, a branch with no reflog, missing-ref typed throw, take/ordering).
/// The destructive-action-is-journaled requirement is asserted at the VM level in
/// <c>ReflogViewModelTests</c> (the reflog restore routes through the journaled ResetToCommit).
/// All LibGit2Sharp-driven, so no git CLI is required.
/// </summary>
public class GitServiceReflogTests : IDisposable
{
    private readonly TempRepoFixture _fx = new();
    private readonly GitService _git = new();

    public void Dispose() => _fx.Dispose();

    private static bool IsZero(string sha) => string.IsNullOrEmpty(sha) || sha.All(c => c == '0');

    private string HeadBranchName()
    {
        using var repo = new Repository(_fx.RepoPath);
        return repo.Head.FriendlyName;
    }

    // ---- Contract: commit → hard reset → both moves with correct from/to ----

    [Fact]
    public void GetReflog_ShouldShowBothMoves_WithCorrectFromTo_AfterCommitThenHardReset()
    {
        var c1 = _fx.CommitFile("a.txt", "1\n", "first");
        var c2 = _fx.CommitFile("a.txt", "2\n", "second");
        _git.ResetToCommit(_fx.RepoPath, c1, ResetMode.Hard);

        var reflog = _git.GetReflog(_fx.RepoPath, "HEAD");

        // Newest-first: the top entry is the reset (c2 → c1).
        Assert.Equal(c1, reflog[0].ToSha);
        Assert.Equal(c2, reflog[0].FromSha);

        // The second commit's move (c1 → c2) is also present with the right from/to.
        Assert.Contains(reflog, e => e.ToSha == c2 && e.FromSha == c1);

        // The very first commit's move records a zero "from" (ref creation).
        var creation = reflog.Last();
        Assert.Equal(c1, creation.ToSha);
        Assert.True(IsZero(creation.FromSha));
    }

    [Fact]
    public void GetReflog_ShouldReturnEntriesNewestFirst()
    {
        _fx.CommitFile("a.txt", "1\n", "first");
        var c2 = _fx.CommitFile("a.txt", "2\n", "second");

        var reflog = _git.GetReflog(_fx.RepoPath, "HEAD");

        Assert.Equal(c2, reflog[0].ToSha); // most recent move first
        Assert.True(reflog[0].When >= reflog[^1].When);
    }

    // ---- Contract: create-branch-here at the pre-reset entry restores the commit ----

    [Fact]
    public void CreateBranchAt_FromPreResetReflogEntry_ShouldRestoreOrphanedCommit()
    {
        var c1 = _fx.CommitFile("a.txt", "1\n", "first");
        var c2 = _fx.CommitFile("a.txt", "2\n", "second");
        _git.ResetToCommit(_fx.RepoPath, c1, ResetMode.Hard); // c2 now orphaned

        // The reflog still remembers the pre-reset tip.
        var reflog = _git.GetReflog(_fx.RepoPath, "HEAD");
        var preReset = reflog.First(e => e.ToSha == c2);

        _git.CreateBranchAt(_fx.RepoPath, "recovered", preReset.ToSha, checkout: false);

        using var repo = new Repository(_fx.RepoPath);
        Assert.Equal(c2, repo.Branches["recovered"]?.Tip?.Sha);
    }

    // ---- Contract: deleted-branch recovery finds the orphaned tip via HEAD reflog ----

    [Fact]
    public void GetReflog_ShouldExposeOrphanedTip_ForDeletedBranchRecovery()
    {
        _fx.CommitFile("a.txt", "1\n", "base");
        var main = HeadBranchName();

        _git.CreateBranch(_fx.RepoPath, "feature", "", checkout: true);
        var c2 = _fx.CommitFile("a.txt", "2\n", "on feature");
        _git.CheckoutBranch(_fx.RepoPath, main);
        _git.DeleteBranch(_fx.RepoPath, "feature", force: true); // c2 now orphaned, branch reflog gone

        // HEAD's reflog still recorded the commit that produced c2.
        var reflog = _git.GetReflog(_fx.RepoPath, "HEAD");
        var orphan = reflog.First(e => e.ToSha == c2);

        _git.CreateBranchAt(_fx.RepoPath, "feature-recovered", orphan.ToSha, checkout: false);

        using var repo = new Repository(_fx.RepoPath);
        Assert.Equal(c2, repo.Branches["feature-recovered"]?.Tip?.Sha);
    }

    // ---- Edge: fresh repo / empty reflog ----

    [Fact]
    public void GetReflog_ShouldBeEmpty_OnFreshUnbornRepo()
    {
        var reflog = _git.GetReflog(_fx.RepoPath, "HEAD");
        Assert.Empty(reflog);
    }

    // ---- Edge: detached-HEAD reflog ----

    [Fact]
    public void GetReflog_ShouldRecordCheckoutMove_WhenDetachingHead()
    {
        var c1 = _fx.CommitFile("a.txt", "1\n", "first");
        _fx.CommitFile("a.txt", "2\n", "second");
        _git.CheckoutRevision(_fx.RepoPath, c1); // detached HEAD at c1

        Assert.True(_git.GetHeadState(_fx.RepoPath).IsDetached);

        var reflog = _git.GetReflog(_fx.RepoPath, "HEAD");
        Assert.Equal(c1, reflog[0].ToSha); // top move landed on the detached target
    }

    // ---- Edge: multi-line commit message collapses to a single reflog row ----

    [Fact]
    public void GetReflog_ShouldKeepMessageSingleLine_ForMultiLineCommitMessage()
    {
        _fx.CommitFile("a.txt", "1\n", "subject line\n\na body paragraph\nand more");

        var reflog = _git.GetReflog(_fx.RepoPath, "HEAD");

        Assert.DoesNotContain('\n', reflog[0].Message);
        Assert.DoesNotContain('\r', reflog[0].Message);
        Assert.Contains("subject line", reflog[0].Message);
    }

    // ---- Edge: a branch with no reflog returns empty (no throw) ----

    [Fact]
    public void GetReflog_ShouldReturnEmpty_ForBranchWithNoReflog()
    {
        // Disable ref-update logging, then create a branch: it gets no reflog file.
        using (var repo = new Repository(_fx.RepoPath))
            repo.Config.Set("core.logallrefupdates", false, ConfigurationLevel.Local);

        var c1 = _fx.CommitFile("a.txt", "1\n", "first");
        _git.CreateBranchAt(_fx.RepoPath, "nolog", c1, checkout: false);

        var reflog = _git.GetReflog(_fx.RepoPath, "nolog");
        Assert.Empty(reflog);
    }

    // ---- Edge: friendly branch name resolves to its reflog ----

    [Fact]
    public void GetReflog_ShouldResolveFriendlyBranchName()
    {
        var c1 = _fx.CommitFile("a.txt", "1\n", "first");
        var branch = HeadBranchName();

        var reflog = _git.GetReflog(_fx.RepoPath, branch);
        Assert.Contains(reflog, e => e.ToSha == c1);
    }

    // ---- Edge: missing ref throws typed ----

    [Fact]
    public void GetReflog_ShouldThrowTyped_ForMissingRef()
    {
        _fx.CommitFile("a.txt", "1\n", "first");

        var ex = Assert.Throws<GitOperationException>(
            () => _git.GetReflog(_fx.RepoPath, "no-such-branch"));
        Assert.Contains("no-such-branch", ex.Message);
    }

    // ---- Edge: take caps the result to the newest N ----

    [Fact]
    public void GetReflog_ShouldCapToTake_KeepingNewest()
    {
        _fx.CommitFile("a.txt", "1\n", "first");
        var c2 = _fx.CommitFile("a.txt", "2\n", "second");
        var c3 = _fx.CommitFile("a.txt", "3\n", "third");

        var reflog = _git.GetReflog(_fx.RepoPath, "HEAD", take: 2);

        Assert.Equal(2, reflog.Count);
        Assert.Equal(c3, reflog[0].ToSha); // newest kept
        Assert.Equal(c2, reflog[1].ToSha); // second-newest retained, oldest dropped
    }
}
