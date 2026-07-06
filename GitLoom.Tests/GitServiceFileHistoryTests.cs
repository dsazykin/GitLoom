using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GitLoom.Core.Exceptions;
using GitLoom.Core.Models;
using GitLoom.Core.Services;
using GitLoom.Tests.Fixtures;
using LibGit2Sharp;
using Xunit;
using Repository = LibGit2Sharp.Repository;

namespace GitLoom.Tests;

/// <summary>
/// TI-12 — file history & line history (T-12). Integration against a real temp repo for the three
/// service reads (rename-following log, blob-at-commit with binary guard, adjacent-version diff) plus
/// every Master Doc §T-12 edge case (rename/follow, introduce, delete-then-gone, binary, path with
/// spaces), and a pure line-range filter test that reuses <see cref="PatchParser"/> via
/// <see cref="LineHistoryFilter"/>.
///
/// Distinct commit timestamps are load-bearing: LibGit2Sharp's single-path <c>QueryBy(path)</c> walk
/// throws when commits share the same second (documented in GitServiceHistoryTests) — every commit
/// here gets its own minute.
/// </summary>
public class GitServiceFileHistoryTests : IDisposable
{
    private readonly TempRepoFixture _fx = new();
    private readonly GitService _service = new();
    private readonly DateTimeOffset _t0 = new(2024, 1, 1, 12, 0, 0, TimeSpan.Zero);

    public void Dispose() => _fx.Dispose();

    private DateTimeOffset At(int minutes) => _t0.AddMinutes(minutes);

    // ---- TI-12 #1 ----------------------------------------------------------

    [Fact]
    public void GetFileHistory_ShouldReturnOnlyTouchingCommits_NewestFirst()
    {
        // Six commits; the target file is touched in commits 1, 3, 5 — the others touch a sibling.
        var c1 = _fx.CommitFile("target.txt", "v1\n", "1: add target", "test-user", "test@gitloom.local", At(1));
        _fx.CommitFile("other.txt", "o1\n", "2: add other", "test-user", "test@gitloom.local", At(2));
        var c3 = _fx.CommitFile("target.txt", "v2\n", "3: edit target", "test-user", "test@gitloom.local", At(3));
        _fx.CommitFile("other.txt", "o2\n", "4: edit other", "test-user", "test@gitloom.local", At(4));
        var c5 = _fx.CommitFile("target.txt", "v3\n", "5: edit target", "test-user", "test@gitloom.local", At(5));
        _fx.CommitFile("other.txt", "o3\n", "6: edit other", "test-user", "test@gitloom.local", At(6));

        var history = _service.GetFileHistory(_fx.RepoPath, "target.txt");

        Assert.Equal(new[] { c5, c3, c1 }, history.Select(v => v.Sha).ToArray());   // newest-first
        Assert.All(history, v => Assert.Equal("target.txt", v.PathAtCommit));
        Assert.Equal("5: edit target", history[0].MessageShort);
        Assert.Equal("test-user", history[0].AuthorName);
        Assert.Equal(At(5), history[0].When);
    }

    // ---- TI-12 #2 : rename following --------------------------------------

    [Fact]
    public void GetFileHistory_ShouldFollowRename_WithHistoricalPaths()
    {
        _fx.CommitFile("old.txt", "A\nB\nC\n", "1: create old", "test-user", "test@gitloom.local", At(1));
        _fx.CommitFile("old.txt", "A\nB2\nC\n", "2: edit old", "test-user", "test@gitloom.local", At(2));
        Rename("old.txt", "new.txt", "A\nB2\nC\n", "3: rename old -> new", At(3));
        var c4 = _fx.CommitFile("new.txt", "A\nB2\nC2\n", "4: edit new", "test-user", "test@gitloom.local", At(4));

        var history = _service.GetFileHistory(_fx.RepoPath, "new.txt");

        // Newest-first, and the walk crossed the rename boundary into the file's old name.
        Assert.Equal(c4, history[0].Sha);
        Assert.Equal("new.txt", history[0].PathAtCommit);
        var paths = history.Select(v => v.PathAtCommit).ToList();
        Assert.Contains("new.txt", paths);
        Assert.Contains("old.txt", paths);                 // rename following exposed the old name
        Assert.Equal("old.txt", history[^1].PathAtCommit); // the introducing revision kept its original path
        // Strictly descending by time (no same-second instability, and the order is correct).
        for (int i = 1; i < history.Count; i++)
            Assert.True(history[i - 1].When >= history[i].When);
    }

    // ---- TI-12 #3 : blob text + typed binary throw -------------------------

    [Fact]
    public void GetFileAtCommit_ShouldReturnBlobText_AndThrowTypedOnBinary()
    {
        var textSha = _fx.CommitFile("readme.txt", "hello\nworld\n", "text", "test-user", "test@gitloom.local", At(1));

        Assert.Equal("hello\nworld\n", _service.GetFileAtCommit(_fx.RepoPath, textSha, "readme.txt"));

        // A blob with NUL bytes is binary — must throw typed, never return garbage.
        var binBytes = new byte[] { 0x00, 0x01, 0x02, 0x00, 0xFF, 0x10 };
        var binSha = CommitBinary("blob.bin", binBytes, "binary", At(2));

        var ex = Assert.Throws<GitOperationException>(() => _service.GetFileAtCommit(_fx.RepoPath, binSha, "blob.bin"));
        Assert.Contains("binary", ex.Message, StringComparison.OrdinalIgnoreCase);

        // A path absent at the revision also throws typed (naming the path).
        var missing = Assert.Throws<GitOperationException>(() => _service.GetFileAtCommit(_fx.RepoPath, textSha, "nope.txt"));
        Assert.Contains("nope.txt", missing.Message);
    }

    // ---- TI-12 #4 : adjacent-version diff equals the tree diff -------------

    [Fact]
    public void GetFileDiffBetweenCommits_ShouldMatchTreeDiff()
    {
        var older = _fx.CommitFile("code.txt", "one\ntwo\nthree\n", "older", "test-user", "test@gitloom.local", At(1));
        var newer = _fx.CommitFile("code.txt", "one\nTWO\nthree\nfour\n", "newer", "test-user", "test@gitloom.local", At(2));

        var viaService = _service.GetFileDiffBetweenCommits(_fx.RepoPath, older, newer, "code.txt");

        string viaRepo;
        using (var repo = new Repository(_fx.RepoPath))
        {
            var a = repo.Lookup<Commit>(older)!;
            var b = repo.Lookup<Commit>(newer)!;
            viaRepo = repo.Diff.Compare<Patch>(a.Tree, b.Tree, new[] { "code.txt" }).Content;
        }

        Assert.Equal(viaRepo, viaService);
        Assert.Contains("+TWO", viaService);
        Assert.Contains("+four", viaService);
        Assert.Contains("-two", viaService);
    }

    // ---- TI-12 #5 : pure line-range filter (reuses PatchParser) ------------

    [Fact]
    public void LineRangeFilter_ShouldKeepVersionsIntersectingRange()
    {
        // Three synthetic revisions; the delegate returns each one's diff. Only the diffs whose hunk
        // overlaps lines 10–12 should survive — the filter is pure and parses with PatchParser.
        var vTop = new FileVersion { Sha = "top" };
        var vMid = new FileVersion { Sha = "mid" };
        var vBot = new FileVersion { Sha = "bot" };
        var history = new List<FileVersion> { vTop, vMid, vBot };

        string PatchFor(FileVersion v) => v.Sha switch
        {
            // Changes lines 1–2 → outside the range.
            "top" => "@@ -1,2 +1,2 @@\n-a\n-b\n+A\n+B\n",
            // Changes line 11 → inside the range.
            "mid" => "@@ -11,1 +11,1 @@\n-old\n+new\n",
            // Changes lines 40–41 → outside the range.
            _ => "@@ -40,2 +40,2 @@\n-x\n-y\n+X\n+Y\n",
        };

        var kept = LineHistoryFilter.FilterByLineRange(history, 10, 12, PatchFor);

        Assert.Equal(new[] { "mid" }, kept.Select(v => v.Sha).ToArray());

        // Direct predicate checks over the same coordinate space.
        Assert.True(LineHistoryFilter.PatchIntersectsRange(PatchFor(vMid), 10, 12));
        Assert.False(LineHistoryFilter.PatchIntersectsRange(PatchFor(vTop), 10, 12));
        Assert.False(LineHistoryFilter.PatchIntersectsRange(PatchFor(vBot), 10, 12));
    }

    // ---- Master Doc edge cases --------------------------------------------

    [Fact]
    public void GetFileHistory_FirstCommitIntroducingFile_ShouldAppearAsOldest_AndBlobReadable()
    {
        var intro = _fx.CommitFile("intro.txt", "seed\n", "introduce file", "test-user", "test@gitloom.local", At(1));

        var history = _service.GetFileHistory(_fx.RepoPath, "intro.txt");

        var oldest = Assert.Single(history);
        Assert.Equal(intro, oldest.Sha);
        Assert.Equal("intro.txt", oldest.PathAtCommit);
        Assert.Equal("seed\n", _service.GetFileAtCommit(_fx.RepoPath, intro, "intro.txt"));
    }

    [Fact]
    public void GetFileHistory_FileDeletedLater_ShouldStillExposePastRevisions()
    {
        var create = _fx.CommitFile("gone.txt", "content\n", "add gone", "test-user", "test@gitloom.local", At(1));
        // A sibling keeps HEAD non-empty after the deletion so the walk has somewhere to start.
        _fx.CommitFile("keep.txt", "keep\n", "add keep", "test-user", "test@gitloom.local", At(2));
        Delete("gone.txt", "delete gone", At(3));

        var history = _service.GetFileHistory(_fx.RepoPath, "gone.txt");

        // The revision that introduced the (now-deleted) file is still discoverable, and its blob
        // is still readable at that revision.
        Assert.Contains(history, v => v.Sha == create);
        Assert.Equal("content\n", _service.GetFileAtCommit(_fx.RepoPath, create, "gone.txt"));
    }

    [Fact]
    public void FileHistory_ShouldHandlePathWithSpaces()
    {
        var older = _fx.CommitFile("my file.txt", "a\nb\n", "add spaced", "test-user", "test@gitloom.local", At(1));
        var newer = _fx.CommitFile("my file.txt", "a\nB\nc\n", "edit spaced", "test-user", "test@gitloom.local", At(2));

        var history = _service.GetFileHistory(_fx.RepoPath, "my file.txt");
        Assert.Equal(new[] { newer, older }, history.Select(v => v.Sha).ToArray());

        var diff = _service.GetFileDiffBetweenCommits(_fx.RepoPath, older, newer, "my file.txt");
        Assert.True(LineHistoryFilter.PatchIntersectsRange(diff, 1, 3));
        Assert.Equal("a\nB\nc\n", _service.GetFileAtCommit(_fx.RepoPath, newer, "my file.txt"));
    }

    [Fact]
    public void GetFileDiffBetweenCommits_ShouldThrowTyped_OnUnknownCommit()
    {
        var sha = _fx.CommitFile("x.txt", "x\n", "x", "test-user", "test@gitloom.local", At(1));
        var bogus = new string('0', 40);
        Assert.Throws<GitOperationException>(() => _service.GetFileDiffBetweenCommits(_fx.RepoPath, bogus, sha, "x.txt"));
    }

    // ---- inline git helpers (own-handle discipline, like the fixture) ------

    private string CommitBinary(string rel, byte[] bytes, string message, DateTimeOffset when)
    {
        File.WriteAllBytes(Path.Combine(_fx.RepoPath, rel), bytes);
        using var repo = new Repository(_fx.RepoPath);
        Commands.Stage(repo, rel);
        var sig = new Signature("test-user", "test@gitloom.local", when);
        return repo.Commit(message, sig, sig).Sha;
    }

    private string Rename(string oldRel, string newRel, string content, string message, DateTimeOffset when)
    {
        File.Delete(Path.Combine(_fx.RepoPath, oldRel));
        File.WriteAllText(Path.Combine(_fx.RepoPath, newRel), content);
        using var repo = new Repository(_fx.RepoPath);
        Commands.Stage(repo, oldRel);   // stage the deletion of the old path
        Commands.Stage(repo, newRel);   // stage the addition of the new path
        var sig = new Signature("test-user", "test@gitloom.local", when);
        return repo.Commit(message, sig, sig).Sha;
    }

    private string Delete(string rel, string message, DateTimeOffset when)
    {
        File.Delete(Path.Combine(_fx.RepoPath, rel));
        using var repo = new Repository(_fx.RepoPath);
        Commands.Stage(repo, rel);
        var sig = new Signature("test-user", "test@gitloom.local", when);
        return repo.Commit(message, sig, sig).Sha;
    }
}
