using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Git.Exceptions;
using Mainguard.Git.Models;
using GitLoom.Core.Services;
using Mainguard.Git.Services;
using GitLoom.Tests.Fixtures;
using LibGit2Sharp;
using Xunit;
using Repository = LibGit2Sharp.Repository;

namespace GitLoom.Tests;

// TI-29: check out a PR / branch into a worktree. The pure ref resolver is unit-tested with no IO;
// the fetch + worktree-create mechanics are proven over a LOCAL file-based fixture remote carrying a
// synthetic refs/pull/1/head — no network. A github.com origin URL rewritten to the local bare via
// url.<bare>.insteadOf lets the host resolver see "GitHub" (so the pull/{n}/head ref applies) while
// the fetch actually hits the local bare.
public class GitServiceCheckoutPrWorktreeTests : IDisposable
{
    private readonly GitService _git = new();
    private readonly List<string> _cleanup = new();

    public void Dispose()
    {
        foreach (var p in _cleanup)
        {
            try { ForceDelete(p); } catch { /* best-effort */ }
        }
    }

    // ---- Pure ref resolver --------------------------------------------------------------------

    [Theory]
    [InlineData(HostKind.GitHub, 42, "pull/42/head")]
    [InlineData(HostKind.GitLab, 7, "merge-requests/7/head")]
    public void PullRequestHeadRef_MapsSupportedHosts(HostKind host, int number, string expected)
        => Assert.Equal(expected, GitService.PullRequestHeadRef(host, number));

    [Theory]
    [InlineData(HostKind.Bitbucket)]
    [InlineData(HostKind.AzureDevOps)]
    [InlineData(HostKind.Unknown)]
    public void PullRequestHeadRef_UnsupportedHost_Throws(HostKind host)
        => Assert.Throws<GitOperationException>(() => GitService.PullRequestHeadRef(host, 1));

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public void PullRequestHeadRef_NonPositiveNumber_Throws(int number)
        => Assert.Throws<GitOperationException>(() => GitService.PullRequestHeadRef(HostKind.GitHub, number));

    // ---- Integration over a local fixture remote (network-free) --------------------------------

    [Fact]
    [Trait("Category", "RequiresGitCli")]
    public async Task CheckoutPullRequestWorktree_FetchesSyntheticPrHead_IntoWorktreeMatchingCommit()
    {
        var (clone, prSha) = ArrangePrFixture(prNumber: 1);
        var wt = NewTempPath("GitLoomWT_");

        var result = await _git.CheckoutPullRequestWorktree(clone, 1, "origin", wt, CancellationToken.None);

        Assert.Equal(wt, result);

        // A local branch pr/1 now exists at the PR commit.
        using (var repo = new Repository(clone))
        {
            var pr = repo.Branches["pr/1"];
            Assert.NotNull(pr);
            Assert.Equal(prSha, pr!.Tip.Sha);
        }

        // The worktree is registered, checked out to pr/1, and its HEAD + files match the PR commit.
        var worktrees = _git.ListWorktrees(clone);
        Assert.Contains(worktrees, w => !w.IsMain && w.Branch == "pr/1");
        using (var wtRepo = new Repository(wt))
        {
            Assert.Equal(prSha, wtRepo.Head.Tip.Sha);
        }
        Assert.Equal("pr content\n", File.ReadAllText(Path.Combine(wt, "pr.txt")));
    }

    [Fact]
    [Trait("Category", "RequiresGitCli")]
    public async Task CheckoutPullRequestWorktree_NonEmptyTarget_RefusesAndCreatesNothing()
    {
        var (clone, _) = ArrangePrFixture(prNumber: 1);
        var wt = NewTempPath("GitLoomWT_");
        Directory.CreateDirectory(wt);
        File.WriteAllText(Path.Combine(wt, "existing.txt"), "keep me");

        await Assert.ThrowsAsync<GitOperationException>(
            () => _git.CheckoutPullRequestWorktree(clone, 1, "origin", wt, CancellationToken.None));

        // Nothing was created: no pr/1 branch, the dir is untouched.
        using (var repo = new Repository(clone))
            Assert.Null(repo.Branches["pr/1"]);
        Assert.Equal(new[] { "existing.txt" }, Directory.GetFiles(wt).Select(Path.GetFileName).ToArray());
    }

    [Fact]
    [Trait("Category", "RequiresGitCli")]
    public async Task CheckoutPullRequestWorktree_ReCheckoutWhileBranchInUse_ThrowsTyped_AndCleansUp()
    {
        var (clone, _) = ArrangePrFixture(prNumber: 1);
        var first = NewTempPath("GitLoomWT_");
        await _git.CheckoutPullRequestWorktree(clone, 1, "origin", first, CancellationToken.None);

        // pr/1 is already checked out in `first`; a second checkout can't reuse the branch → typed throw,
        // and the half-made second worktree dir is cleaned up (never left behind).
        var second = NewTempPath("GitLoomWT_");
        await Assert.ThrowsAsync<GitOperationException>(
            () => _git.CheckoutPullRequestWorktree(clone, 1, "origin", second, CancellationToken.None));

        Assert.False(Directory.Exists(second) && Directory.EnumerateFileSystemEntries(second).Any());
    }

    [Fact]
    [Trait("Category", "RequiresGitCli")]
    public void CheckoutBranchWorktree_LocalBranch_ChecksOutIntoWorktree()
    {
        using var fx = new TempRepoFixture();
        var seedSha = fx.CommitFile("a.txt", "hello\n", "seed");
        fx.CreateBranch("feature");
        var wt = NewTempPath("GitLoomWT_");

        var result = _git.CheckoutBranchWorktree(fx.RepoPath, "feature", wt);

        Assert.Equal(wt, result);
        Assert.Contains(_git.ListWorktrees(fx.RepoPath), w => !w.IsMain && w.Branch == "feature");
        using var wtRepo = new Repository(wt);
        Assert.Equal(seedSha, wtRepo.Head.Tip.Sha);
        Assert.Equal("hello\n", File.ReadAllText(Path.Combine(wt, "a.txt")));
    }

    [Fact]
    [Trait("Category", "RequiresGitCli")]
    public void CheckoutBranchWorktree_RemoteTrackingBranch_CreatesLocalTrackingBranch_AndWorktree()
    {
        using var fx = new TempRepoFixture();
        fx.CommitFile("a.txt", "hello\n", "seed");
        var bare = fx.AddBareRemote("origin");
        _cleanup.Add(bare);

        // Publish a "topic" branch to the bare so the clone gets it as a remote-tracking ref.
        fx.CreateBranch("topic");
        fx.Checkout("topic");
        var topicSha = fx.CommitFile("topic.txt", "topic content\n", "topic commit");
        RunGit(fx.RepoPath, "push", "origin", "topic");

        var clone = fx.CloneBare(bare);
        _cleanup.Add(clone);
        RunGit(clone, "fetch", "origin");

        var wt = NewTempPath("GitLoomWT_");
        var result = _git.CheckoutBranchWorktree(clone, "origin/topic", wt);

        Assert.Equal(wt, result);
        using (var repo = new Repository(clone))
        {
            var local = repo.Branches["topic"];
            Assert.NotNull(local);
            Assert.True(local!.IsTracking);
            Assert.Equal("origin/topic", local.TrackedBranch.FriendlyName);
        }
        Assert.Contains(_git.ListWorktrees(clone), w => !w.IsMain && w.Branch == "topic");
        using var wtRepo = new Repository(wt);
        Assert.Equal(topicSha, wtRepo.Head.Tip.Sha);
        Assert.Equal("topic content\n", File.ReadAllText(Path.Combine(wt, "topic.txt")));
    }

    // Builds: a fixture repo with a bare "origin", a synthetic refs/pull/<n>/head in the bare pointing
    // at a PR commit, and a fresh clone whose origin URL is a github.com URL rewritten (insteadOf) to the
    // local bare. Returns (clonePath, prCommitSha). Registers everything for cleanup.
    private (string clone, string prSha) ArrangePrFixture(int prNumber)
    {
        var fx = new TempRepoFixture();
        _cleanup.Add(fx.RepoPath);
        fx.CommitFile("a.txt", "hello\n", "seed");
        var bare = fx.AddBareRemote("origin");
        _cleanup.Add(bare);

        // Create the PR commit on a side branch and push it as the synthetic pull ref, then leave HEAD
        // on the default branch and drop the side branch so it lives only as refs/pull/<n>/head.
        string defaultBranch;
        using (var repo = new Repository(fx.RepoPath)) defaultBranch = repo.Head.FriendlyName;
        fx.CreateBranch("prsrc");
        fx.Checkout("prsrc");
        var prSha = fx.CommitFile("pr.txt", "pr content\n", "pr commit");
        fx.Checkout(defaultBranch);
        RunGit(fx.RepoPath, "push", "origin", $"prsrc:refs/pull/{prNumber}/head");
        RunGit(fx.RepoPath, "branch", "-D", "prsrc");

        var clone = fx.CloneBare(bare);
        _cleanup.Add(clone);

        // Make the origin look like GitHub to the host resolver, but rewrite that URL back to the local
        // bare for the actual transport (no network). Forward-slash the bare path for git config safety.
        const string fakeUrl = "https://github.com/octocat/hello-world";
        var bareForGit = bare.Replace('\\', '/');
        RunGit(clone, "remote", "set-url", "origin", fakeUrl);
        RunGit(clone, "config", $"url.{bareForGit}.insteadOf", fakeUrl);

        return (clone, prSha);
    }

    private string NewTempPath(string prefix)
    {
        var p = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
        _cleanup.Add(p);
        return p;
    }

    private static void RunGit(string workingDir, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("failed to start git");
        p.WaitForExit();
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {p.StandardError.ReadToEnd()}");
    }

    private static void ForceDelete(string path)
    {
        if (!Directory.Exists(path)) return;
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(path, recursive: true);
    }
}
