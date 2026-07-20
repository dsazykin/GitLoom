using System;
using System.IO;
using System.Linq;
using Mainguard.Agents.Services;
using Mainguard.Git.Services;
using GitLoom.Tests.Fixtures;
using LibGit2Sharp;
using Xunit;

namespace GitLoom.Tests;

// TI-01: the harness itself must be trustworthy before anything builds on it.
public class TempRepoFixtureTests
{
    [Fact]
    public void CommitFile_ShouldCreateCommit_WithFixtureIdentity()
    {
        using var fx = new TempRepoFixture();
        var sha = fx.CommitFile("a.txt", "hello\n", "first");

        using var repo = new Repository(fx.RepoPath);
        Assert.Single(repo.Commits);
        Assert.Equal(sha, repo.Head.Tip.Sha);
        Assert.Equal("test-user", repo.Head.Tip.Author.Name);
        Assert.Equal("test@gitloom.local", repo.Head.Tip.Author.Email);
    }

    [Fact]
    public void CreateConflict_ShouldProduceRealConflict()
    {
        using var fx = new TempRepoFixture();
        var (_, theirs) = fx.CreateConflict("f.txt", "ours line\n", "theirs line\n");

        var service = new GitService();
        Assert.Throws<Mainguard.Git.Exceptions.MergeConflictException>(
            () => service.Merge(fx.RepoPath, theirs));

        using var repo = new Repository(fx.RepoPath);
        Assert.True(repo.Index.Conflicts.Any(), "Merge must leave real index conflicts.");
    }

    [Fact]
    public void AddBareRemote_ShouldRoundTripPush()
    {
        using var fx = new TempRepoFixture();
        fx.CommitFile("a.txt", "v1\n", "first");
        var barePath = fx.AddBareRemote();

        // A follow-up commit pushed through the service (sets upstream + pushes).
        var sha = fx.CommitFile("a.txt", "v2\n", "second");
        string branchName;
        using (var repo = new Repository(fx.RepoPath)) branchName = repo.Head.FriendlyName;

        var service = new GitService();
        service.PushBranch(fx.RepoPath, branchName);

        using var bare = new Repository(barePath);
        Assert.Equal(sha, bare.Branches[branchName].Tip.Sha);
    }

    [Fact]
    public void Dispose_ShouldRemoveEverything()
    {
        string repoPath, barePath, clonePath;
        using (var fx = new TempRepoFixture())
        {
            fx.CommitFile("a.txt", "x\n", "seed");
            repoPath = fx.RepoPath;
            barePath = fx.AddBareRemote();
            clonePath = fx.ClonePath();
        }

        Assert.False(Directory.Exists(repoPath), "fixture repo must be deleted");
        Assert.False(Directory.Exists(barePath), "bare remote must be deleted");
        Assert.False(Directory.Exists(clonePath), "clone must be deleted");
    }
}
