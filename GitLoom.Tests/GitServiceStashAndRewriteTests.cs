using System;
using System.IO;
using System.Linq;
using Mainguard.Git.Exceptions;
using GitLoom.Core.Services;
using Mainguard.Git.Services;
using GitLoom.Tests.Fixtures;
using LibGit2Sharp;
using Xunit;

namespace GitLoom.Tests;

// Backfill B-10 (test strategy doc): stash lifecycle, reset modes, revert and
// amend — mutating operations that shipped with zero coverage. StashPop/Apply
// go through the git CLI fallback, hence the trait on those tests.
public class GitServiceStashAndRewriteTests : IDisposable
{
    private readonly TempRepoFixture _fx = new();
    private readonly GitService _service = new();

    public void Dispose() => _fx.Dispose();

    [Fact]
    [Trait("Category", "RequiresGitCli")]
    public void StashPush_ThenPop_ShouldRestoreChanges_AndRemoveStash()
    {
        _fx.CommitFile("a.txt", "original\n", "seed");
        _fx.WriteFile("a.txt", "work in progress\n");

        _service.StashPush(_fx.RepoPath, "wip");
        Assert.Equal("original\n", File.ReadAllText(Path.Combine(_fx.RepoPath, "a.txt")));
        Assert.Single(_service.GetStashes(_fx.RepoPath));

        _service.StashPop(_fx.RepoPath, 0);
        Assert.Equal("work in progress\n", File.ReadAllText(Path.Combine(_fx.RepoPath, "a.txt")));
        Assert.Empty(_service.GetStashes(_fx.RepoPath));
    }

    [Fact]
    [Trait("Category", "RequiresGitCli")]
    public void StashApply_ShouldKeepStash_AndStashDrop_ShouldRemoveIt()
    {
        _fx.CommitFile("a.txt", "original\n", "seed");
        _fx.WriteFile("a.txt", "work in progress\n");

        _service.StashPush(_fx.RepoPath, "wip");
        _service.StashApply(_fx.RepoPath, 0);

        Assert.Equal("work in progress\n", File.ReadAllText(Path.Combine(_fx.RepoPath, "a.txt")));
        Assert.Single(_service.GetStashes(_fx.RepoPath)); // apply keeps the stash

        _service.DiscardChanges(_fx.RepoPath, new[] { "a.txt" });
        _service.StashDrop(_fx.RepoPath, 0);
        Assert.Empty(_service.GetStashes(_fx.RepoPath));
    }

    [Fact]
    public void ResetToCommit_Hard_ShouldRestoreFileAndCleanTree()
    {
        var first = _fx.CommitFile("a.txt", "v1\n", "first");
        _fx.CommitFile("a.txt", "v2\n", "second");

        _service.ResetToCommit(_fx.RepoPath, first, ResetMode.Hard);

        using var repo = new Repository(_fx.RepoPath);
        Assert.Equal(first, repo.Head.Tip.Sha);
        Assert.Equal("v1\n", File.ReadAllText(Path.Combine(_fx.RepoPath, "a.txt")));
        Assert.False(repo.RetrieveStatus().IsDirty);
    }

    [Fact]
    public void ResetToCommit_Soft_ShouldMoveHead_AndKeepChangeStaged()
    {
        var first = _fx.CommitFile("a.txt", "v1\n", "first");
        _fx.CommitFile("a.txt", "v2\n", "second");

        _service.ResetToCommit(_fx.RepoPath, first, ResetMode.Soft);

        using var repo = new Repository(_fx.RepoPath);
        Assert.Equal(first, repo.Head.Tip.Sha);
        Assert.Equal("v2\n", File.ReadAllText(Path.Combine(_fx.RepoPath, "a.txt")));
        // Soft: index still holds v2, i.e. the change appears staged.
        var staged = repo.Diff.Compare<TreeChanges>(repo.Head.Tip.Tree, DiffTargets.Index);
        Assert.Contains(staged, c => c.Path == "a.txt");
    }

    [Fact]
    public void ResetToCommit_Mixed_ShouldMoveHead_AndLeaveChangeUnstaged()
    {
        var first = _fx.CommitFile("a.txt", "v1\n", "first");
        _fx.CommitFile("a.txt", "v2\n", "second");

        _service.ResetToCommit(_fx.RepoPath, first, ResetMode.Mixed);

        using var repo = new Repository(_fx.RepoPath);
        Assert.Equal(first, repo.Head.Tip.Sha);
        Assert.Equal("v2\n", File.ReadAllText(Path.Combine(_fx.RepoPath, "a.txt")));
        // Mixed: index matches HEAD (nothing staged), the edit is workdir-only.
        var staged = repo.Diff.Compare<TreeChanges>(repo.Head.Tip.Tree, DiffTargets.Index);
        Assert.Empty(staged);
        Assert.True(repo.RetrieveStatus().IsDirty);
    }

    [Fact]
    public void RevertCommit_ShouldCreateInverseCommit()
    {
        _fx.CommitFile("a.txt", "one\n", "first");
        var second = _fx.CommitFile("a.txt", "two\n", "second");

        _service.RevertCommit(_fx.RepoPath, second);

        using var repo = new Repository(_fx.RepoPath);
        Assert.Equal(3, repo.Commits.Count());
        Assert.Equal("one\n", File.ReadAllText(Path.Combine(_fx.RepoPath, "a.txt")));
    }

    [Fact]
    public void AmendCommitMessage_ShouldRewriteHeadMessage_WithoutNewCommit()
    {
        var sha = _fx.CommitFile("a.txt", "x\n", "typo mesage");

        _service.AmendCommitMessage(_fx.RepoPath, sha, "fixed message");

        using var repo = new Repository(_fx.RepoPath);
        Assert.Single(repo.Commits);
        Assert.Equal("fixed message", repo.Head.Tip.MessageShort);
    }

    [Fact]
    public void AmendCommitMessage_ShouldThrowTyped_WhenNotHead()
    {
        var first = _fx.CommitFile("a.txt", "one\n", "first");
        _fx.CommitFile("a.txt", "two\n", "second");

        Assert.Throws<GitOperationException>(
            () => _service.AmendCommitMessage(_fx.RepoPath, first, "rewritten"));
    }
}
