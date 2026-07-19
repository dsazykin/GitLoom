using System;
using System.IO;
using System.Linq;
using Mainguard.Git.Exceptions;
using Mainguard.Git.Models;
using Mainguard.Agents.Services;
using Mainguard.Git.Services;
using GitLoom.Tests.Fixtures;
using LibGit2Sharp;
using Xunit;
using Repository = LibGit2Sharp.Repository;

namespace GitLoom.Tests;

// Backfill B-2 (test strategy doc): the pull strategies added by fix 1.5 beyond
// the already-covered conflict path. The diverged fast-forward-only case falls
// back to the git CLI, hence the trait.
[Trait("Category", "RequiresGitCli")]
public class GitServicePullTests : IDisposable
{
    private readonly TempRepoFixture _origin = new();
    private readonly GitService _service = new();

    public void Dispose() => _origin.Dispose();

    private static void CommitInRepo(string repoPath, string relativePath, string content, string message)
    {
        File.WriteAllText(Path.Combine(repoPath, relativePath), content);
        using var repo = new Repository(repoPath);
        Commands.Stage(repo, relativePath);
        var sig = new Signature("test-user", "test@gitloom.local", DateTimeOffset.Now);
        repo.Commit(message, sig, sig);
    }

    [Fact]
    public void Pull_FastForwardOnly_ShouldAdvanceHead_WhenCleanFastForward()
    {
        _origin.CommitFile("f.txt", "base\n", "base");
        var local = _origin.ClonePath();
        var originTip = _origin.CommitFile("f.txt", "v2\n", "update");

        _service.Pull(local, PullStrategy.FastForwardOnly);

        using var repo = new Repository(local);
        Assert.Equal(originTip, repo.Head.Tip.Sha);
        Assert.Single(repo.Head.Tip.Parents); // fast-forward: no merge commit
        Assert.Equal("v2\n", File.ReadAllText(Path.Combine(local, "f.txt")));
    }

    [Fact]
    public void Pull_FastForwardOnly_ShouldThrowTyped_AndLeaveTreeUntouched_WhenDiverged()
    {
        _origin.CommitFile("f.txt", "base\n", "base");
        var local = _origin.ClonePath();

        _origin.CommitFile("f.txt", "origin change\n", "origin change");
        CommitInRepo(local, "f.txt", "local change\n", "local change");

        string localTipBefore;
        using (var repo = new Repository(local)) localTipBefore = repo.Head.Tip.Sha;

        Assert.Throws<GitOperationException>(() => _service.Pull(local, PullStrategy.FastForwardOnly));

        using (var repo = new Repository(local))
        {
            Assert.Equal(localTipBefore, repo.Head.Tip.Sha); // nothing merged
            Assert.False(repo.Index.Conflicts.Any());
        }
        Assert.Equal("local change\n", File.ReadAllText(Path.Combine(local, "f.txt")));
    }

    [Fact]
    public void Pull_Rebase_ShouldReparentLocalCommit_OntoRemoteTip()
    {
        _origin.CommitFile("f.txt", "base\n", "base");
        var local = _origin.ClonePath();

        var originTip = _origin.CommitFile("remote.txt", "remote\n", "remote work");
        CommitInRepo(local, "local.txt", "local\n", "local work");

        _service.Pull(local, PullStrategy.Rebase);

        using var repo = new Repository(local);
        Assert.Equal("local work", repo.Head.Tip.MessageShort);
        Assert.Equal(originTip, repo.Head.Tip.Parents.Single().Sha); // reparented onto remote tip
        Assert.True(File.Exists(Path.Combine(local, "remote.txt")));
        Assert.True(File.Exists(Path.Combine(local, "local.txt")));
    }

    [Fact]
    public void Pull_Rebase_ShouldThrowTyped_WhenNoUpstream()
    {
        // A repo with no remote cannot pull-rebase. Since T-10 the remote is resolved
        // up front, so this surfaces the more specific RemoteNotFoundException (still a
        // typed GitLoomException) rather than a generic git-CLI failure.
        _origin.CommitFile("f.txt", "base\n", "base");

        Assert.Throws<Mainguard.Git.Exceptions.RemoteNotFoundException>(
            () => _service.Pull(_origin.RepoPath, PullStrategy.Rebase));
    }
}
