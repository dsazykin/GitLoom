using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mainguard.Agents;
using Mainguard.Git.Exceptions;
using Mainguard.Git.Models;
using Mainguard.Agents.Services;
using Mainguard.Git.Services;
using GitLoom.Tests.Fixtures;
using LibGit2Sharp;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Repository = LibGit2Sharp.Repository;

using Mainguard.Git;
namespace GitLoom.Tests;

// TI-19: the operation journal is the heart of undo/redo. The [Theory] round-trips
// EVERY mutating op kind (perform → Undo restores all ref SHAs + HEAD target byte-exactly
// → Redo restores the post-state) via CaptureRefState + dictionary equality, plus the five
// named tests (branch-delete upstream, dirty-tree refusal, redo truncation, non-undoable
// flagging, SQLite persistence). Every case here is LibGit2Sharp-driven, so it needs no
// git CLI; the interactive-rebase round-trip lives in OperationJournalCliTests.
public class OperationJournalTests : IDisposable
{
    private readonly TempRepoFixture _fx = new();
    private readonly string _dbPath;
    private readonly OperationJournal _journal;
    private readonly GitService _git;

    public OperationJournalTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), "gitloom-journal-" + Guid.NewGuid().ToString("N") + ".db");
        using (var ctx = new AppDbContext(_dbPath)) ctx.Database.Migrate();
        _journal = new OperationJournal(() => new AppDbContext(_dbPath));
        _git = new GitService(null, _journal);
    }

    public void Dispose()
    {
        _fx.Dispose();
        SqliteConnectionCloser();
        try { File.Delete(_dbPath); } catch { }
    }

    private static void SqliteConnectionCloser() => Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

    // ---- ref-state capture (the byte-exact oracle) -----------------------

    private (Dictionary<string, string> Refs, string Head) CaptureRefState()
    {
        using var repo = new Repository(_fx.RepoPath);
        var refs = repo.Refs.OfType<DirectReference>()
            .ToDictionary(r => r.CanonicalName, r => r.TargetIdentifier, StringComparer.Ordinal);
        var head = repo.Refs.Head is SymbolicReference sym
            ? sym.TargetIdentifier
            : repo.Refs.Head.TargetIdentifier;
        return (refs, head);
    }

    private void AssertRefState((Dictionary<string, string> Refs, string Head) expected)
    {
        var actual = CaptureRefState();
        Assert.Equal(expected.Head, actual.Head);
        Assert.Equal(expected.Refs, actual.Refs);
    }

    private long LatestEntryId()
    {
        using var ctx = new AppDbContext(_dbPath);
        return ctx.JournalEntries.OrderByDescending(e => e.Id).First().Id;
    }

    public enum OpKind
    {
        Commit, Amend, MergeFastForward, Rebase,
        ResetSoft, ResetMixed, ResetHard,
        Revert, CherryPick,
        CreateBranch, CreateBranchAt, RenameBranch, DeleteBranch,
        StashPush, TagCreateAnnotated, TagDelete, Checkout,
    }

    // Arranges the repository for a kind and returns the delegate that performs the op
    // itself (so the round-trip can snapshot exactly around the mutating call).
    private Action ArrangeAndBuild(OpKind kind)
    {
        switch (kind)
        {
            case OpKind.Commit:
                {
                    _fx.CommitFile("base.txt", "1\n", "base");
                    _fx.WriteFile("new.txt", "hello\n");
                    _git.StageFile(_fx.RepoPath, "new.txt");
                    return () => _git.Commit(_fx.RepoPath, "add new");
                }
            case OpKind.Amend:
                {
                    _fx.CommitFile("a.txt", "1\n", "c1");
                    var c2 = _fx.CommitFile("a.txt", "2\n", "c2");
                    return () => _git.AmendCommitMessage(_fx.RepoPath, c2, "c2 amended");
                }
            case OpKind.MergeFastForward:
                {
                    _fx.CommitFile("a.txt", "1\n", "c1");
                    _fx.CreateBranch("feature");
                    _fx.Checkout("feature");
                    _fx.CommitFile("f.txt", "f\n", "feature commit");
                    _fx.Checkout("master");
                    return () => _git.Merge(_fx.RepoPath, "feature");
                }
            case OpKind.Rebase:
                {
                    _fx.CommitFile("a.txt", "1\n", "c1");
                    _fx.CreateBranch("feature");
                    _fx.Checkout("feature");
                    _fx.CommitFile("f.txt", "f\n", "feature commit");
                    _fx.Checkout("master");
                    _fx.CommitFile("m.txt", "m\n", "master commit");
                    _fx.Checkout("feature");
                    return () => _git.Rebase(_fx.RepoPath, "master");
                }
            case OpKind.ResetSoft:
            case OpKind.ResetMixed:
            case OpKind.ResetHard:
                {
                    var c1 = _fx.CommitFile("a.txt", "1\n", "c1");
                    _fx.CommitFile("a.txt", "2\n", "c2");
                    var mode = kind == OpKind.ResetSoft ? ResetMode.Soft
                        : kind == OpKind.ResetMixed ? ResetMode.Mixed : ResetMode.Hard;
                    return () => _git.ResetToCommit(_fx.RepoPath, c1, mode);
                }
            case OpKind.Revert:
                {
                    _fx.CommitFile("a.txt", "1\n", "c1");
                    var c2 = _fx.CommitFile("a.txt", "2\n", "c2");
                    return () => _git.RevertCommit(_fx.RepoPath, c2);
                }
            case OpKind.CherryPick:
                {
                    // GitService.CherryPick stages without committing (CommitOnSuccess=false), so
                    // refs do not move — the round-trip proves undo/redo leaves them byte-exact.
                    var c1 = _fx.CommitFile("a.txt", "1\n", "c1");
                    _fx.CreateBranch("feature");
                    _fx.Checkout("feature");
                    var pick = _fx.CommitFile("b.txt", "b\n", "pick me");
                    _fx.Checkout("master");
                    return () => _git.CherryPick(_fx.RepoPath, pick);
                }
            case OpKind.CreateBranch:
                {
                    _fx.CommitFile("a.txt", "1\n", "c1");
                    return () => _git.CreateBranch(_fx.RepoPath, "feature", "", checkout: false);
                }
            case OpKind.CreateBranchAt:
                {
                    var c1 = _fx.CommitFile("a.txt", "1\n", "c1");
                    _fx.CommitFile("a.txt", "2\n", "c2");
                    return () => _git.CreateBranchAt(_fx.RepoPath, "feature", c1, checkout: false);
                }
            case OpKind.RenameBranch:
                {
                    _fx.CommitFile("a.txt", "1\n", "c1");
                    _fx.CreateBranch("old");
                    return () => _git.RenameBranch(_fx.RepoPath, "old", "renamed");
                }
            case OpKind.DeleteBranch:
                {
                    _fx.CommitFile("a.txt", "1\n", "c1");
                    _fx.CreateBranch("feature");
                    return () => _git.DeleteBranch(_fx.RepoPath, "feature");
                }
            case OpKind.StashPush:
                {
                    _fx.CommitFile("a.txt", "1\n", "c1");
                    _fx.WriteFile("a.txt", "2\n"); // dirty the tree so there is something to stash
                    return () => _git.StashPush(_fx.RepoPath, "wip");
                }
            case OpKind.TagCreateAnnotated:
                {
                    var c1 = _fx.CommitFile("a.txt", "1\n", "c1");
                    return () => _git.CreateTag(_fx.RepoPath, "v1", c1, "release one");
                }
            case OpKind.TagDelete:
                {
                    var c1 = _fx.CommitFile("a.txt", "1\n", "c1");
                    using (var repo = new Repository(_fx.RepoPath)) repo.Tags.Add("v1", repo.Lookup<Commit>(c1));
                    return () => _git.DeleteTag(_fx.RepoPath, "v1");
                }
            case OpKind.Checkout:
                {
                    var c1 = _fx.CommitFile("a.txt", "1\n", "c1");
                    _fx.CreateBranch("feature");
                    _fx.Checkout("feature");
                    _fx.CommitFile("f.txt", "f\n", "feature commit");
                    _fx.Checkout("master");
                    return () => _git.CheckoutBranch(_fx.RepoPath, "feature");
                }
            default:
                throw new ArgumentOutOfRangeException(nameof(kind));
        }
    }

    [Theory]
    [InlineData(OpKind.Commit)]
    [InlineData(OpKind.Amend)]
    [InlineData(OpKind.MergeFastForward)]
    [InlineData(OpKind.Rebase)]
    [InlineData(OpKind.ResetSoft)]
    [InlineData(OpKind.ResetMixed)]
    [InlineData(OpKind.ResetHard)]
    [InlineData(OpKind.Revert)]
    [InlineData(OpKind.CherryPick)]
    [InlineData(OpKind.CreateBranch)]
    [InlineData(OpKind.CreateBranchAt)]
    [InlineData(OpKind.RenameBranch)]
    [InlineData(OpKind.DeleteBranch)]
    [InlineData(OpKind.StashPush)]
    [InlineData(OpKind.TagCreateAnnotated)]
    [InlineData(OpKind.TagDelete)]
    [InlineData(OpKind.Checkout)]
    public void RoundTrip_EveryOpKind_UndoRestoresPreState_RedoRestoresPostState(OpKind kind)
    {
        var perform = ArrangeAndBuild(kind);

        var pre = CaptureRefState();
        perform();
        var post = CaptureRefState();

        var entryId = LatestEntryId();

        _journal.Undo(_fx.RepoPath, entryId);
        AssertRefState(pre);

        _journal.Redo(_fx.RepoPath, entryId);
        AssertRefState(post);
    }

    // ---- #2 branch-delete undo restores upstream config ------------------

    [Fact]
    public void Undo_BranchDelete_ShouldRestoreUpstreamConfig()
    {
        _fx.CommitFile("a.txt", "1\n", "c1");
        _fx.AddBareRemote("origin");
        using (var repo = new Repository(_fx.RepoPath))
        {
            var f = repo.CreateBranch("feature");
            repo.Branches.Update(f, b => { b.Remote = "origin"; b.UpstreamBranch = "refs/heads/master"; });
        }

        _git.DeleteBranch(_fx.RepoPath, "feature");
        using (var repo = new Repository(_fx.RepoPath))
            Assert.Null(repo.Branches["feature"]); // gone after delete

        _journal.Undo(_fx.RepoPath, LatestEntryId());

        using (var repo = new Repository(_fx.RepoPath))
        {
            var f = repo.Branches["feature"];
            Assert.NotNull(f);
            Assert.Equal("origin", f!.RemoteName);
            Assert.Equal("refs/heads/master", f.UpstreamBranchCanonicalName);
        }
    }

    // ---- #3 dirty tree refuses, mutates nothing --------------------------

    [Fact]
    public void Undo_WithDirtyTree_ShouldRefuseTyped_AndChangeNothing()
    {
        _fx.CommitFile("a.txt", "1\n", "c1");
        _fx.WriteFile("new.txt", "x\n");
        _git.StageFile(_fx.RepoPath, "new.txt");
        _git.Commit(_fx.RepoPath, "add new"); // moves HEAD; tree clean afterwards
        var entryId = LatestEntryId();

        // Dirty the tree with a genuine unstaged modification (would be clobbered by a reset).
        _fx.WriteFile("a.txt", "DIRTY\n");
        var before = CaptureRefState();

        Assert.Throws<UndoBlockedException>(() => _journal.Undo(_fx.RepoPath, entryId));

        AssertRefState(before);                          // refs unchanged
        Assert.Equal("DIRTY\n", File.ReadAllText(Path.Combine(_fx.RepoPath, "a.txt"))); // worktree unchanged

        using var ctx = new AppDbContext(_dbPath);
        Assert.False(ctx.JournalEntries.First(e => e.Id == entryId).IsUndone); // not marked undone
    }

    // ---- #4 a new op after an undo truncates the redo stack --------------

    [Fact]
    public void NewOperationAfterUndo_ShouldTruncateRedo()
    {
        var c1 = _fx.CommitFile("a.txt", "1\n", "c1");
        _git.CreateTag(_fx.RepoPath, "v1", c1, null);
        _git.CreateTag(_fx.RepoPath, "v2", c1, null);
        var v2Entry = LatestEntryId();

        _journal.Undo(_fx.RepoPath, v2Entry);            // v2 removed, entry undone
        _git.CreateTag(_fx.RepoPath, "v3", c1, null);    // a new op supersedes the undone v2

        var ex = Assert.Throws<UndoBlockedException>(() => _journal.Redo(_fx.RepoPath, v2Entry));
        Assert.Contains("redo", ex.Message, StringComparison.OrdinalIgnoreCase);

        using var repo = new Repository(_fx.RepoPath);
        Assert.Null(repo.Tags["v2"]);                    // redo really was blocked
        Assert.NotNull(repo.Tags["v3"]);
    }

    // ---- #5 non-undoable ops are journaled + flagged, not dropped --------

    [Fact]
    public void NonUndoableOps_ShouldBeJournaledFlagged_WithReason()
    {
        _fx.CommitFile("a.txt", "1\n", "c1");
        _fx.WriteFile("a.txt", "2\n");
        _git.StashPush(_fx.RepoPath, "wip");
        _git.StashDrop(_fx.RepoPath, 0); // dropping a stash is non-undoable

        var entry = _journal.GetHistory(_fx.RepoPath).First(e => e.Kind == JournalKinds.StashDrop);
        Assert.False(entry.IsUndoable);
        Assert.False(string.IsNullOrWhiteSpace(entry.UndoBlockedReason));

        var ex = Assert.Throws<UndoBlockedException>(() => _journal.Undo(_fx.RepoPath, entry.Id));
        Assert.Equal(entry.UndoBlockedReason, ex.Message);
    }

    // ---- #6 persistence survives a fresh AppDbContext --------------------

    [Fact]
    public void Journal_ShouldPersistAcrossContextReopen()
    {
        _fx.CommitFile("a.txt", "1\n", "c1");
        _git.CreateBranch(_fx.RepoPath, "feature", "", checkout: false);
        var entryId = LatestEntryId();

        // A brand-new journal + context instance (simulating an app restart) still sees it.
        var reopened = new OperationJournal(() => new AppDbContext(_dbPath));
        var history = reopened.GetHistory(_fx.RepoPath);
        Assert.Contains(history, e => e.Id == entryId && e.Kind == JournalKinds.CreateBranch);

        // And it is still functional: undo through the reopened journal removes the branch.
        reopened.Undo(_fx.RepoPath, entryId);
        using var repo = new Repository(_fx.RepoPath);
        Assert.Null(repo.Branches["feature"]);
    }
}
