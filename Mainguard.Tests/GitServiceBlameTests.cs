using System;
using System.IO;
using System.Linq;
using System.Text;
using Mainguard.Agents.Services;
using Mainguard.Git.Exceptions;
using Mainguard.Git.Models;
using Mainguard.Git.Services;
using Mainguard.Tests.Fixtures;
using Xunit;

namespace Mainguard.Tests;

// TI-11: blame. Per-line commit/author/date attribution through IGitService.GetBlame,
// exercised against a real temp repo (no network) with the exact edge cases from
// Master Doc §T-11: disjoint-edit mapping, starting-at a prior commit, missing path,
// cache hit / head-change invalidation, and the never-throw cases (uncommitted lines,
// rename across history, empty file, binary file).
public class GitServiceBlameTests : IDisposable
{
    private readonly TempRepoFixture _fx = new();
    private readonly GitService _service = new();

    public void Dispose() => _fx.Dispose();

    [Fact]
    public void GetBlame_ShouldMapLinesToCommits()
    {
        // Three commits, each editing a DISJOINT line of a 3-line file.
        var sha1 = _fx.CommitFile("f.txt", "L1a\nL2a\nL3a\n", "c1");
        var sha2 = _fx.CommitFile("f.txt", "L1a\nL2b\nL3a\n", "c2"); // only line 2
        var sha3 = _fx.CommitFile("f.txt", "L1a\nL2b\nL3c\n", "c3"); // only line 3

        var blame = _service.GetBlame(_fx.RepoPath, "f.txt");

        // 1-based, contiguous, one row per line of the current file.
        Assert.Equal(new[] { 1, 2, 3 }, blame.Select(b => b.LineNumber).ToArray());

        var byLine = blame.ToDictionary(b => b.LineNumber, b => b.Sha);
        Assert.Equal(sha1, byLine[1]);
        Assert.Equal(sha2, byLine[2]);
        Assert.Equal(sha3, byLine[3]);

        // Author/summary carried through and short SHA is 8 chars of the full SHA.
        var line3 = blame.Single(b => b.LineNumber == 3);
        Assert.Equal("test-user", line3.AuthorName);
        Assert.Equal("c3", line3.Summary);
        Assert.Equal(8, line3.ShortSha.Length);
        Assert.StartsWith(line3.ShortSha, line3.Sha);
    }

    [Fact]
    public void GetBlame_StartingAtPriorCommit_ShouldIgnoreNewerCommit()
    {
        var sha1 = _fx.CommitFile("f.txt", "L1a\nL2a\nL3a\n", "c1");
        var sha2 = _fx.CommitFile("f.txt", "L1a\nL2b\nL3a\n", "c2");
        var sha3 = _fx.CommitFile("f.txt", "L1a\nL2b\nL3c\n", "c3");

        var blame = _service.GetBlame(_fx.RepoPath, "f.txt", startingSha: sha2);

        var byLine = blame.ToDictionary(b => b.LineNumber, b => b.Sha);
        Assert.Equal(sha1, byLine[1]);
        Assert.Equal(sha2, byLine[2]);
        Assert.Equal(sha1, byLine[3]);          // line 3 not yet touched at c2
        Assert.DoesNotContain(sha3, blame.Select(b => b.Sha));  // the newer commit is invisible
    }

    [Fact]
    public void GetBlame_ShouldThrowTyped_OnPathMissingAtRevision()
    {
        _fx.CommitFile("f.txt", "hello\n", "c1");

        var ex = Assert.Throws<GitOperationException>(() =>
            _service.GetBlame(_fx.RepoPath, "does-not-exist.txt"));

        Assert.Contains("does-not-exist.txt", ex.Message);   // message names the path
    }

    [Fact]
    public void GetBlame_ShouldInvalidate_OnHeadChange()
    {
        var sha1 = _fx.CommitFile("f.txt", "L1a\nL2a\n", "c1");

        var first = _service.GetBlame(_fx.RepoPath, "f.txt");
        Assert.Equal(sha1, first.Single(b => b.LineNumber == 2).Sha);

        // New commit touching line 2 → HEAD moves → the SHA-keyed cache misses and recomputes.
        var sha2 = _fx.CommitFile("f.txt", "L1a\nL2b\n", "c2");

        var second = _service.GetBlame(_fx.RepoPath, "f.txt");
        Assert.Equal(sha2, second.Single(b => b.LineNumber == 2).Sha);
        Assert.NotSame(first, second);
    }

    [Fact]
    public void GetBlame_ShouldReturnCachedInstance_OnRepeatCall_AndRecomputeAfterInvalidate()
    {
        _fx.CommitFile("f.txt", "L1\nL2\n", "c1");

        var a = _service.GetBlame(_fx.RepoPath, "f.txt");
        var b = _service.GetBlame(_fx.RepoPath, "f.txt");
        Assert.Same(a, b);   // second call is a cache hit (same immutable instance)

        _service.InvalidateBlameCache(_fx.RepoPath);

        var c = _service.GetBlame(_fx.RepoPath, "f.txt");
        Assert.NotSame(a, c);                 // recomputed after invalidation
        Assert.Equal(a.Count, c.Count);       // ... but equivalent (HEAD unchanged)
    }

    [Fact]
    public void GetBlame_ShouldNotThrow_OnUncommittedWorkingTreeChanges()
    {
        var sha1 = _fx.CommitFile("f.txt", "L1\nL2\n", "c1");
        // Modify the working tree WITHOUT committing.
        File.WriteAllText(Path.Combine(_fx.RepoPath, "f.txt"), "L1\nL2 edited\nL3 new\n");

        var blame = _service.GetBlame(_fx.RepoPath, "f.txt");

        // Blame reflects the committed HEAD version (2 lines), never the dirty worktree,
        // and never throws on uncommitted content.
        Assert.Equal(2, blame.Count);
        Assert.All(blame, b => Assert.Equal(sha1, b.Sha));
    }

    [Fact]
    public void GetBlame_ShouldFollowRename_AcrossHistory_WithoutThrowing()
    {
        var sha1 = _fx.CommitFile("old.txt", "alpha\nbeta\n", "create old.txt");

        // Rename old.txt -> new.txt keeping content identical (a pure move).
        File.Move(Path.Combine(_fx.RepoPath, "old.txt"), Path.Combine(_fx.RepoPath, "new.txt"));
        CommitAllViaCli("rename to new.txt");

        var blame = _service.GetBlame(_fx.RepoPath, "new.txt");

        Assert.Equal(2, blame.Count);
        Assert.All(blame, b => Assert.False(string.IsNullOrEmpty(b.Sha)));
        // Blame follows the content across the rename: both lines still trace to the
        // original commit that introduced them.
        Assert.All(blame, b => Assert.Equal(sha1, b.Sha));
    }

    [Fact]
    public void GetBlame_ShouldReturnEmpty_OnEmptyFile()
    {
        _fx.CommitFile("empty.txt", "", "add empty file");

        var blame = _service.GetBlame(_fx.RepoPath, "empty.txt");

        Assert.Empty(blame);   // zero lines → zero attribution rows, no throw
    }

    [Fact]
    public void GetBlame_ShouldReturnEmpty_OnBinaryFile_WithoutThrowing()
    {
        // A blob with embedded NUL bytes is detected as binary by libgit2.
        var bytes = new byte[] { 0x00, 0x01, 0x02, 0x00, 0xFF, 0x10, 0x00, 0x42 };
        File.WriteAllBytes(Path.Combine(_fx.RepoPath, "blob.bin"), bytes);
        CommitAllViaCli("add binary blob");

        var blame = _service.GetBlame(_fx.RepoPath, "blob.bin");

        Assert.Empty(blame);   // binary → no meaningful attribution, must not throw
    }

    // Stages every change (adds, deletes, renames) and commits with the test identity.
    private void CommitAllViaCli(string message)
    {
        RunGit("add", "-A");
        RunGit("-c", "user.name=test-user", "-c", "user.email=test@mainguard.local", "commit", "-m", message);
    }

    private void RunGit(params string[] args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("git")
        {
            WorkingDirectory = _fx.RepoPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = System.Diagnostics.Process.Start(psi)
            ?? throw new InvalidOperationException("failed to start git");
        p.WaitForExit();
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {p.StandardError.ReadToEnd()}");
    }
}
