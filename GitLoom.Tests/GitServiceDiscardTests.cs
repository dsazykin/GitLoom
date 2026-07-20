using System;
using System.IO;
using Mainguard.Agents.Services;
using Mainguard.Git.Services;
using GitLoom.Tests.Fixtures;
using LibGit2Sharp;
using Xunit;

namespace GitLoom.Tests;

// Backfill B-5 (test strategy doc): the fix-1.4 discard paths not yet covered —
// staged-new files (NewInIndex) and mixed tracked/untracked selections.
public class GitServiceDiscardTests : IDisposable
{
    private readonly TempRepoFixture _fx = new();
    private readonly GitService _service = new();

    public void Dispose() => _fx.Dispose();

    [Fact]
    public void DiscardChanges_ShouldRemoveStagedNewFile_FromIndexAndWorkdir()
    {
        _fx.CommitFile("seed.txt", "seed\n", "seed");
        _fx.WriteFile("new.txt", "staged new\n");
        _service.StageFile(_fx.RepoPath, "new.txt");

        _service.DiscardChanges(_fx.RepoPath, new[] { "new.txt" });

        Assert.False(File.Exists(Path.Combine(_fx.RepoPath, "new.txt")));
        using var repo = new Repository(_fx.RepoPath);
        // No phantom "deleted" index entry may remain.
        Assert.Equal(FileStatus.Nonexistent, repo.RetrieveStatus("new.txt"));
    }

    [Fact]
    public void DiscardChanges_ShouldHandleMixedSelection_TrackedAndUntracked()
    {
        _fx.CommitFile("tracked.txt", "original\n", "seed");
        _fx.WriteFile("tracked.txt", "modified\n");
        _fx.WriteFile("junk.txt", "temporary\n");

        _service.DiscardChanges(_fx.RepoPath, new[] { "tracked.txt", "junk.txt" });

        Assert.Equal("original\n", File.ReadAllText(Path.Combine(_fx.RepoPath, "tracked.txt")));
        Assert.False(File.Exists(Path.Combine(_fx.RepoPath, "junk.txt")));
    }
}
