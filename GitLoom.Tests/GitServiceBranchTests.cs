using System;
using System.Linq;
using GitLoom.Core.Exceptions;
using GitLoom.Core.Services;
using GitLoom.Tests.Fixtures;
using LibGit2Sharp;
using Xunit;

namespace GitLoom.Tests;

// Backfill: branch CRUD paths without coverage, including the second half of
// fix 1.12 (checking out a remote branch when a local of the same name
// already exists must reuse it, not crash or duplicate).
public class GitServiceBranchTests : IDisposable
{
    private readonly TempRepoFixture _fx = new();
    private readonly GitService _service = new();

    public void Dispose() => _fx.Dispose();

    [Fact]
    public void CheckoutBranch_ShouldReuseExistingLocal_ForRemoteBranch()
    {
        _fx.CommitFile("a.txt", "base\n", "base");
        string defaultBranch;
        using (var repo = new Repository(_fx.RepoPath)) defaultBranch = repo.Head.FriendlyName;
        _fx.CreateBranch("feature");
        _fx.Checkout("feature");
        _fx.CommitFile("a.txt", "feature\n", "feature work");
        _fx.Checkout(defaultBranch);

        var clonePath = _fx.ClonePath();

        // First checkout creates the tracking local branch...
        _service.CheckoutBranch(clonePath, "origin/feature");
        _service.CheckoutBranch(clonePath, $"origin/{defaultBranch}");
        // ...second checkout must reuse it instead of failing on a duplicate.
        _service.CheckoutBranch(clonePath, "origin/feature");

        using var local = new Repository(clonePath);
        Assert.True(local.Branches["feature"].IsCurrentRepositoryHead);
        Assert.Single(local.Branches, b => !b.IsRemote && b.FriendlyName == "feature");
    }

    [Fact]
    public void RenameBranch_ShouldRename_AndThrowTypedWhenMissing()
    {
        _fx.CommitFile("a.txt", "x\n", "seed");
        _fx.CreateBranch("old-name");

        _service.RenameBranch(_fx.RepoPath, "old-name", "new-name");

        using (var repo = new Repository(_fx.RepoPath))
        {
            Assert.Null(repo.Branches["old-name"]);
            Assert.NotNull(repo.Branches["new-name"]);
        }

        Assert.Throws<GitOperationException>(
            () => _service.RenameBranch(_fx.RepoPath, "does-not-exist", "whatever"));
    }

    [Fact]
    public void DeleteBranch_Local_ShouldRemoveIt()
    {
        _fx.CommitFile("a.txt", "x\n", "seed");
        _fx.CreateBranch("doomed");

        _service.DeleteBranch(_fx.RepoPath, "doomed");

        using var repo = new Repository(_fx.RepoPath);
        Assert.Null(repo.Branches["doomed"]);
    }

    [Fact]
    public void CreateBranch_WithCheckout_ShouldMoveHead()
    {
        _fx.CommitFile("a.txt", "x\n", "seed");

        _service.CreateBranch(_fx.RepoPath, "topic", string.Empty, checkout: true);

        using var repo = new Repository(_fx.RepoPath);
        Assert.Equal("topic", repo.Head.FriendlyName);
    }
}
