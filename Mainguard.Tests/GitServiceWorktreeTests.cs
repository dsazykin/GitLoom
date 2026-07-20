using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Mainguard.Agents.Services;
using Mainguard.Git.Exceptions;
using Mainguard.Git.Services;
using Mainguard.Tests.Fixtures;
using Xunit;

namespace Mainguard.Tests;

// TI-07: worktree porcelain. All operations CLI-driven; assertions read real worktree state.
[Trait("Category", "RequiresGitCli")]
public class GitServiceWorktreeTests : IDisposable
{
    private readonly TempRepoFixture _fx = new();
    private readonly GitService _git = new();
    private readonly List<string> _worktreePaths = new();

    public GitServiceWorktreeTests() => _fx.CommitFile("a.txt", "hello\n", "seed");

    public void Dispose()
    {
        foreach (var path in _worktreePaths)
        {
            try { ForceDelete(path); } catch { /* best-effort */ }
        }
        _fx.Dispose();
    }

    private string NewWorktreePath()
    {
        var p = Path.Combine(Path.GetTempPath(), "MainguardWT_" + Guid.NewGuid().ToString("N"));
        _worktreePaths.Add(p);
        return p;
    }

    [Fact]
    public void ListWorktrees_ShouldParseMainAndLinked_WithBranchAndSha()
    {
        _fx.CreateBranch("feature");
        var wt = NewWorktreePath();
        _git.AddWorktree(_fx.RepoPath, wt, "feature", createBranch: false);

        var items = _git.ListWorktrees(_fx.RepoPath);

        Assert.Equal(2, items.Count);
        Assert.True(items[0].IsMain);
        Assert.NotNull(items[0].Branch);
        var linked = Assert.Single(items, w => !w.IsMain);
        Assert.Equal("feature", linked.Branch);
        Assert.False(string.IsNullOrEmpty(linked.HeadSha));
    }

    [Fact]
    public void ListWorktrees_ShouldParseDetached_AndLocked()
    {
        var headSha = RunGit(_fx.RepoPath, "rev-parse", "HEAD").Output.Trim();
        var wt = NewWorktreePath();
        // Arrange a detached + locked worktree via raw git (the service doesn't add those forms).
        Assert.Equal(0, RunGit(_fx.RepoPath, "worktree", "add", "--detach", wt, headSha).Code);
        Assert.Equal(0, RunGit(_fx.RepoPath, "worktree", "lock", wt).Code);

        var linked = Assert.Single(_git.ListWorktrees(_fx.RepoPath), w => !w.IsMain);

        Assert.True(linked.IsDetached);
        Assert.Null(linked.Branch);
        Assert.True(linked.IsLocked);
    }

    [Fact]
    public void AddWorktree_WithCreateBranch_ShouldCreateBranchAndDir()
    {
        var wt = NewWorktreePath();
        _git.AddWorktree(_fx.RepoPath, wt, "brand-new", createBranch: true);

        Assert.True(Directory.Exists(wt));
        Assert.Contains(_git.ListWorktrees(_fx.RepoPath), w => w.Branch == "brand-new");
    }

    [Fact]
    public void AddWorktree_OnCheckedOutBranch_ShouldThrowTyped()
    {
        // The main worktree already has its branch checked out; adding it again must fail.
        var mainBranch = _git.ListWorktrees(_fx.RepoPath)[0].Branch!;
        var wt = NewWorktreePath();

        Assert.Throws<GitOperationException>(
            () => _git.AddWorktree(_fx.RepoPath, wt, mainBranch, createBranch: false));
    }

    [Fact]
    public void RemoveWorktree_Dirty_ShouldThrowWithoutForce_AndSucceedWithForce()
    {
        _fx.CreateBranch("feature");
        var wt = NewWorktreePath();
        _git.AddWorktree(_fx.RepoPath, wt, "feature", createBranch: false);
        File.WriteAllText(Path.Combine(wt, "dirty.txt"), "uncommitted\n"); // makes the worktree dirty

        Assert.Throws<GitOperationException>(() => _git.RemoveWorktree(_fx.RepoPath, wt, force: false));

        _git.RemoveWorktree(_fx.RepoPath, wt, force: true);
        Assert.DoesNotContain(_git.ListWorktrees(_fx.RepoPath), w => w.Branch == "feature");
    }

    [Fact]
    public void PruneWorktrees_ShouldCleanMetadata_AfterManualDelete()
    {
        _fx.CreateBranch("feature");
        var wt = NewWorktreePath();
        _git.AddWorktree(_fx.RepoPath, wt, "feature", createBranch: false);

        ForceDelete(wt); // manually delete the worktree dir on disk
        _git.PruneWorktrees(_fx.RepoPath);

        var items = _git.ListWorktrees(_fx.RepoPath);
        Assert.Single(items);
        Assert.True(items[0].IsMain);
    }

    private static (int Code, string Output, string Err) RunGit(string cwd, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("failed to start git");
        var o = p.StandardOutput.ReadToEnd();
        var e = p.StandardError.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, o, e);
    }

    private static void ForceDelete(string path)
    {
        if (!Directory.Exists(path)) return;
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(path, recursive: true);
    }
}
