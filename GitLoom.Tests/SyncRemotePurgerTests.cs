using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GitLoom.Core.Agents.Bootstrap;
using Mainguard.Git.Exceptions;
using GitLoom.Core.Services;
using Mainguard.Git.Services;
using GitLoom.Tests.Fixtures;
using Xunit;

namespace GitLoom.Tests;

/// <summary>
/// P2-22 Q2: the optional (default-OFF) sync-remote-removal step. Proves the purger strips the resolved
/// remote from a real temp repo through the GitService primitive, and tolerates a missing repo folder
/// and an already-removed / renamed remote (RemoteNotFoundException) without aborting the loop.
/// </summary>
public class SyncRemotePurgerTests
{
    private const string RemoteName = "gitloom-vm";

    [Fact]
    public void Run_RemovesResolvedRemote_FromRealRepo()
    {
        using var fx = new TempRepoFixture();
        var git = new GitService();
        fx.CommitFile("a.txt", "x\n", "seed");
        git.AddRemote(fx.RepoPath, RemoteName, @"\\wsl.localhost\GitLoomEnv\home\me\gitloom\repos\abc.git");
        Assert.Contains(git.GetRemotes(fx.RepoPath), r => r.Name == RemoteName);

        var purger = new SyncRemotePurger(new[] { fx.RepoPath }, RemoteName, (path, name) => git.RemoveRemote(path, name));
        var report = purger.Run();

        Assert.Equal(1, report.Removed);
        Assert.Equal(0, report.Tolerated);
        Assert.Equal(new[] { fx.RepoPath }, report.RemovedFrom);
        Assert.DoesNotContain(git.GetRemotes(fx.RepoPath), r => r.Name == RemoteName);
    }

    [Fact]
    public void Run_MissingRepoAndRenamedRemote_AreTolerated()
    {
        using var withRemote = new TempRepoFixture();
        using var withoutRemote = new TempRepoFixture();
        var git = new GitService();
        withRemote.CommitFile("a.txt", "x\n", "seed");
        withoutRemote.CommitFile("a.txt", "y\n", "seed");
        git.AddRemote(withRemote.RepoPath, RemoteName, "https://example.test/mirror.git");
        // withoutRemote has a differently-named ("origin-like") remote → our remote is "renamed"/absent.
        git.AddRemote(withoutRemote.RepoPath, "origin", "https://example.test/other.git");

        var missingRepo = Path.Combine(Path.GetTempPath(), "gitloom-gone-" + Guid.NewGuid().ToString("N"));

        var paths = new List<string> { missingRepo, withoutRemote.RepoPath, withRemote.RepoPath };
        var purger = new SyncRemotePurger(paths, RemoteName, (path, name) => git.RemoveRemote(path, name));
        var report = purger.Run();

        // Only the repo that actually had the remote is stripped; the missing folder and the repo with a
        // renamed/absent remote are tolerated.
        Assert.Equal(1, report.Removed);
        Assert.Equal(2, report.Tolerated);
        Assert.Equal(new[] { withRemote.RepoPath }, report.RemovedFrom);
        Assert.DoesNotContain(git.GetRemotes(withRemote.RepoPath), r => r.Name == RemoteName);
        // The untouched repo keeps its own remote.
        Assert.Contains(git.GetRemotes(withoutRemote.RepoPath), r => r.Name == "origin");
    }

    [Fact]
    public void Run_ToleratesRemoteNotFound_FromAction()
    {
        // A remove action that always reports "no such remote" must never take the loop down.
        var purger = new SyncRemotePurger(
            new[] { Directory.GetCurrentDirectory() },
            RemoteName,
            (_, _) => throw new RemoteNotFoundException("No remote named 'gitloom-vm'."));

        var report = purger.Run();

        Assert.Equal(0, report.Removed);
        Assert.Equal(1, report.Tolerated);
        Assert.Empty(report.RemovedFrom);
    }
}
