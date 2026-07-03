using System;
using System.IO;
using System.Linq;
using GitLoom.Core.Exceptions;
using GitLoom.Core.Services;
using GitLoom.Tests.Fixtures;
using Xunit;

namespace GitLoom.Tests;

// Backfill B-7 + B-9 (test strategy doc): the fix-1.9 null-Tip guards not yet
// covered, and the fix-1.6 runner's failure surface (typed exception carrying
// git's stderr).
public class GitServiceGuardTests : IDisposable
{
    private readonly TempRepoFixture _fx = new();
    private readonly GitService _service = new();

    public void Dispose() => _fx.Dispose();

    [Fact]
    public void GetBranches_ShouldReturnEmpty_OnEmptyRepo()
    {
        // Unborn HEAD: no branches, and crucially no NullReferenceException.
        var branches = _service.GetBranches(_fx.RepoPath).ToList();
        Assert.Empty(branches);
    }

    [Fact]
    public void AmendCommitMessage_ShouldThrowTyped_OnEmptyRepo()
    {
        Assert.Throws<GitOperationException>(
            () => _service.AmendCommitMessage(_fx.RepoPath, new string('0', 40), "msg"));
    }

    [Fact]
    public void GetBranchDiffAgainstWorkingTree_ShouldThrowTyped_WhenBranchMissing()
    {
        _fx.CommitFile("a.txt", "x\n", "seed");
        Assert.Throws<GitOperationException>(
            () => _service.GetBranchDiffAgainstWorkingTree(_fx.RepoPath, "no-such-branch"));
    }

    [Fact]
    public void HasUncommittedChanges_ShouldReflectWorkingTreeState()
    {
        _fx.CommitFile("a.txt", "x\n", "seed");
        Assert.False(_service.HasUncommittedChanges(_fx.RepoPath));

        _fx.WriteFile("a.txt", "dirty\n");
        Assert.True(_service.HasUncommittedChanges(_fx.RepoPath));
    }

    [Fact]
    [Trait("Category", "RequiresGitCli")]
    public void RunGit_ShouldThrowTypedWithStderr_OnFailure()
    {
        // Exercised via AddWorktree (a RunGitChecked call site): a nonexistent
        // branch makes git exit non-zero, and the typed exception must carry
        // git's actual stderr so the UI can show a real error.
        _fx.CommitFile("a.txt", "x\n", "seed");
        var wtPath = Path.Combine(Path.GetTempPath(), "GitLoomWT_" + Guid.NewGuid().ToString("N"));

        var ex = Assert.Throws<GitOperationException>(
            () => _service.AddWorktree(_fx.RepoPath, wtPath, "branch-that-does-not-exist"));

        Assert.False(string.IsNullOrWhiteSpace(ex.Message));
        Assert.False(Directory.Exists(wtPath));
    }
}
