using System;
using System.Linq;
using GitLoom.Core.Exceptions;
using GitLoom.Core.Services;
using GitLoom.Tests.Fixtures;
using LibGit2Sharp;
using Xunit;

namespace GitLoom.Tests;

// TI-03: conflict index plumbing — GetConflicts / GetConflictBlobs / ResolveConflict /
// HasUnresolvedConflicts read and write the merge index stages (repo.Index.Conflicts),
// never working-tree markers.
public class GitServiceConflictTests : IDisposable
{
    private readonly TempRepoFixture _fx = new();
    private readonly GitService _service = new();

    public void Dispose() => _fx.Dispose();

    // Drives a real merge so libgit2 records stage-1/2/3 entries; returns HEAD-on-ours.
    private void MakeConflict(string path, string ours, string theirs)
    {
        var (_, theirsBranch) = _fx.CreateConflict(path, ours, theirs);
        Assert.Throws<MergeConflictException>(() => _service.Merge(_fx.RepoPath, theirsBranch));
        Assert.True(_service.HasUnresolvedConflicts(_fx.RepoPath));   // precondition
    }

    [Fact]
    public void GetConflicts_ShouldListConflictedPath_WithAllStagesPresent()
    {
        MakeConflict("f.txt", "ours\n", "theirs\n");

        var conflict = Assert.Single(_service.GetConflicts(_fx.RepoPath));
        Assert.Equal("f.txt", conflict.Path);
        Assert.True(conflict.HasBase);
        Assert.True(conflict.HasOurs);
        Assert.True(conflict.HasTheirs);
    }

    [Fact]
    public void GetConflicts_ShouldReturnEmpty_OnCleanRepo()
    {
        _fx.CommitFile("a.txt", "hello\n", "init");
        Assert.Empty(_service.GetConflicts(_fx.RepoPath));
    }

    [Fact]
    public void GetConflictBlobs_ShouldReturnThreeDistinctTexts()
    {
        MakeConflict("f.txt", "ours\n", "theirs\n");

        var (baseText, oursText, theirsText) = _service.GetConflictBlobs(_fx.RepoPath, "f.txt");
        Assert.Equal("base\n", baseText);   // fixture seeds "base\n"
        Assert.Equal("ours\n", oursText);
        Assert.Equal("theirs\n", theirsText);
    }

    [Fact]
    public void GetConflictBlobs_ShouldThrowTyped_OnNonConflictedPath()
    {
        _fx.CommitFile("clean.txt", "hi\n", "init");

        var ex = Assert.Throws<GitOperationException>(
            () => _service.GetConflictBlobs(_fx.RepoPath, "clean.txt"));
        Assert.Contains("clean.txt", ex.Message);
    }

    [Fact]
    public void GetConflictBlobs_AddAdd_ShouldReturnEmptyBase()
    {
        // Both branches ADD the same new path with different content (no common ancestor for it).
        _fx.CommitFile("seed.txt", "seed\n", "init");
        _fx.CreateBranch("theirs");
        _fx.CreateBranch("ours");
        _fx.Checkout("theirs");
        _fx.CommitFile("added.txt", "theirs-add\n", "theirs adds");
        _fx.Checkout("ours");
        _fx.CommitFile("added.txt", "ours-add\n", "ours adds");

        Assert.Throws<MergeConflictException>(() => _service.Merge(_fx.RepoPath, "theirs"));

        var conflict = Assert.Single(_service.GetConflicts(_fx.RepoPath), c => c.Path == "added.txt");
        Assert.False(conflict.HasBase);

        var (baseText, oursText, theirsText) = _service.GetConflictBlobs(_fx.RepoPath, "added.txt");
        Assert.Equal("", baseText);
        Assert.Equal("ours-add\n", oursText);
        Assert.Equal("theirs-add\n", theirsText);
    }

    [Fact]
    public void GetConflictBlobs_ModifyDelete_ShouldFlagMissingSide()
    {
        // ours modifies, theirs deletes -> modify/delete conflict, theirs stage absent.
        _fx.CommitFile("md.txt", "original\n", "init");
        _fx.CreateBranch("theirs");
        _fx.CreateBranch("ours");
        _fx.Checkout("theirs");
        DeleteAndCommit("md.txt", "theirs deletes");
        _fx.Checkout("ours");
        _fx.CommitFile("md.txt", "ours-modified\n", "ours modifies");

        Assert.Throws<MergeConflictException>(() => _service.Merge(_fx.RepoPath, "theirs"));

        var conflict = Assert.Single(_service.GetConflicts(_fx.RepoPath), c => c.Path == "md.txt");
        Assert.True(conflict.HasOurs);
        Assert.False(conflict.HasTheirs);

        var (_, oursText, theirsText) = _service.GetConflictBlobs(_fx.RepoPath, "md.txt");
        Assert.Equal("ours-modified\n", oursText);
        Assert.Equal("", theirsText);
    }

    [Fact]
    public void ResolveConflict_ShouldClearConflict_AndStageContent()
    {
        MakeConflict("f.txt", "ours\n", "theirs\n");

        _service.ResolveConflict(_fx.RepoPath, "f.txt", "merged\n");

        using var repo = new Repository(_fx.RepoPath);
        Assert.Null(repo.Index.Conflicts["f.txt"]);
        Assert.Equal("merged\n", System.IO.File.ReadAllText(System.IO.Path.Combine(_fx.RepoPath, "f.txt")));
        // The staged (stage-0) blob equals the merged content.
        var staged = repo.Index["f.txt"];
        Assert.NotNull(staged);
        Assert.Equal("merged\n", repo.Lookup<Blob>(staged!.Id).GetContentText());
    }

    [Fact]
    public void ResolveConflict_SubdirectoryPath_ShouldWork()
    {
        MakeConflict("sub/dir/file.txt", "ours\n", "theirs\n");

        _service.ResolveConflict(_fx.RepoPath, "sub/dir/file.txt", "merged\n");

        Assert.False(_service.HasUnresolvedConflicts(_fx.RepoPath));
        Assert.Equal("merged\n",
            System.IO.File.ReadAllText(System.IO.Path.Combine(_fx.RepoPath, "sub", "dir", "file.txt")));
    }

    [Fact]
    public void ResolveConflict_ThenCommit_ShouldCreateTwoParentMergeCommit()
    {
        MakeConflict("f.txt", "ours\n", "theirs\n");

        _service.ResolveConflict(_fx.RepoPath, "f.txt", "merged\n");
        _service.Commit(_fx.RepoPath, "merge");

        using var repo = new Repository(_fx.RepoPath);
        Assert.Equal(2, repo.Head.Tip.Parents.Count());
    }

    [Fact]
    public void HasUnresolvedConflicts_ShouldTrackResolutionProgress()
    {
        MakeConflict("f.txt", "ours\n", "theirs\n");
        Assert.True(_service.HasUnresolvedConflicts(_fx.RepoPath));

        _service.ResolveConflict(_fx.RepoPath, "f.txt", "merged\n");
        Assert.False(_service.HasUnresolvedConflicts(_fx.RepoPath));
    }

    private void DeleteAndCommit(string relativePath, string message)
    {
        System.IO.File.Delete(System.IO.Path.Combine(_fx.RepoPath, relativePath));
        using var repo = new Repository(_fx.RepoPath);
        Commands.Stage(repo, relativePath);
        var sig = new Signature("test-user", "test@gitloom.local", DateTimeOffset.Now);
        repo.Commit(message, sig, sig);
    }
}
