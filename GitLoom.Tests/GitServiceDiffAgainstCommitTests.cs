using System;
using Mainguard.Git.Exceptions;
using Mainguard.Agents.Services;
using Mainguard.Git.Services;
using GitLoom.Tests.Fixtures;
using Xunit;

namespace GitLoom.Tests;

// T-07 diff entry point: GetDiffAgainstCommit compares the working tree to a commit,
// whole-tree when filePath is null (used by "Diff working tree against this commit") or
// scoped to one file otherwise.
public class GitServiceDiffAgainstCommitTests : IDisposable
{
    private readonly TempRepoFixture _fx = new();
    private readonly GitService _git = new();

    public void Dispose() => _fx.Dispose();

    [Fact]
    public void GetDiffAgainstCommit_WholeTree_ShouldIncludeAllModifiedFiles()
    {
        var sha = _fx.CommitFile("a.txt", "a\n", "seed a");
        _fx.CommitFile("b.txt", "b\n", "seed b");
        _fx.WriteFile("a.txt", "A\n");
        _fx.WriteFile("b.txt", "B\n");

        var diff = _git.GetDiffAgainstCommit(_fx.RepoPath, sha); // filePath null → whole tree

        Assert.Contains("a.txt", diff);
        Assert.Contains("b.txt", diff);
        Assert.Contains("+A", diff);
        Assert.Contains("+B", diff);
    }

    [Fact]
    public void GetDiffAgainstCommit_WithFilePath_ShouldScopeToThatFile()
    {
        var sha = _fx.CommitFile("a.txt", "a\n", "seed a");
        _fx.CommitFile("b.txt", "b\n", "seed b");
        _fx.WriteFile("a.txt", "A\n");
        _fx.WriteFile("b.txt", "B\n");

        var diff = _git.GetDiffAgainstCommit(_fx.RepoPath, sha, "a.txt");

        Assert.Contains("a.txt", diff);
        Assert.DoesNotContain("b.txt", diff);
    }

    [Fact]
    public void GetDiffAgainstCommit_ShouldThrowTyped_WhenCommitMissing()
    {
        _fx.CommitFile("a.txt", "a\n", "seed");
        Assert.Throws<GitOperationException>(
            () => _git.GetDiffAgainstCommit(_fx.RepoPath, "0000000000000000000000000000000000000000"));
    }
}
