using System;
using System.IO;
using System.Linq;
using GitLoom.Core.Exceptions;
using GitLoom.Core.Services;
using GitLoom.Tests.Fixtures;
using Xunit;

namespace GitLoom.Tests;

// Backfill B-1 (test strategy doc): fix 1.13 shipped three partial-staging
// entry points but only StageHunk was covered. These drive git apply, hence
// the trait.
[Trait("Category", "RequiresGitCli")]
public class GitServicePartialStagingTests : IDisposable
{
    private readonly TempRepoFixture _fx = new();
    private readonly GitService _service = new();

    public GitServicePartialStagingTests()
    {
        // A 12-line file gives two well-separated hunks when lines 2 and 11 change.
        var original = string.Join("\n", Enumerable.Range(1, 12).Select(n => $"L{n}")) + "\n";
        _fx.CommitFile("f.txt", original, "seed");
    }

    public void Dispose() => _fx.Dispose();

    private void ModifyBothRegions()
    {
        var text = File.ReadAllText(Path.Combine(_fx.RepoPath, "f.txt"))
            .Replace("L2\n", "L2-changed\n")
            .Replace("L11\n", "L11-changed\n");
        _fx.WriteFile("f.txt", text);
    }

    private static string FirstHunkOf(string patch)
    {
        var lines = patch.Replace("\r\n", "\n").Split('\n');
        var sb = new System.Text.StringBuilder();
        int i = 0;
        for (; i < lines.Length && !lines[i].StartsWith("@@"); i++)
            sb.Append(lines[i]).Append('\n');
        if (i < lines.Length) { sb.Append(lines[i]).Append('\n'); i++; }
        for (; i < lines.Length && !lines[i].StartsWith("@@"); i++)
            sb.Append(lines[i]).Append('\n');
        return sb.ToString();
    }

    [Fact]
    public void UnstageHunk_ShouldRemoveOnlySelectedHunk_FromIndex()
    {
        ModifyBothRegions();
        _service.StageFile(_fx.RepoPath, "f.txt"); // both hunks staged

        var stagedPatch = _service.GetFileDiff(_fx.RepoPath, "f.txt", isStaged: true);
        _service.UnstageHunk(_fx.RepoPath, FirstHunkOf(stagedPatch));

        var staged = _service.GetFileDiff(_fx.RepoPath, "f.txt", isStaged: true);
        var unstaged = _service.GetFileDiff(_fx.RepoPath, "f.txt", isStaged: false);

        Assert.DoesNotContain("L2-changed", staged);
        Assert.Contains("L11-changed", staged);
        Assert.Contains("L2-changed", unstaged);
        // The working tree keeps both edits regardless.
        var content = File.ReadAllText(Path.Combine(_fx.RepoPath, "f.txt"));
        Assert.Contains("L2-changed", content);
        Assert.Contains("L11-changed", content);
    }

    [Fact]
    public void DiscardHunk_ShouldRevertOnlySelectedHunk_InWorkdir()
    {
        ModifyBothRegions();

        var patch = _service.GetFileDiff(_fx.RepoPath, "f.txt", isStaged: false);
        _service.DiscardHunk(_fx.RepoPath, FirstHunkOf(patch));

        var content = File.ReadAllText(Path.Combine(_fx.RepoPath, "f.txt"));
        Assert.Contains("L2\n", content);              // first edit reverted
        Assert.DoesNotContain("L2-changed", content);
        Assert.Contains("L11-changed", content);       // second edit untouched
    }

    [Fact]
    public void StageHunk_ShouldThrowTyped_OnCorruptPatch()
    {
        ModifyBothRegions();
        Assert.Throws<GitOperationException>(
            () => _service.StageHunk(_fx.RepoPath, "this is not a unified diff\n"));
    }

    [Fact]
    public void StageHunk_ShouldNoOp_OnEmptyPatch()
    {
        ModifyBothRegions();
        _service.StageHunk(_fx.RepoPath, ""); // must not throw, must not launch git

        var staged = _service.GetFileDiff(_fx.RepoPath, "f.txt", isStaged: true);
        Assert.DoesNotContain("L2-changed", staged);
        Assert.DoesNotContain("L11-changed", staged);
    }
}
