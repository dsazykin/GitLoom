using System;
using System.Collections.Generic;
using Mainguard.Git.Models;

namespace Mainguard.Git.Services;

/// <summary>
/// Operation journal (T-19): unlimited undo/redo layered on top of Git's own reflog.
/// Every mutating <see cref="GitService"/> method wraps itself in
/// <c>using var op = journal.BeginOperation(...)</c>; the scope snapshots every ref +
/// HEAD when it opens and again when it disposes, persisting a <see cref="JournalEntry"/>.
/// </summary>
public interface IOperationJournal
{
    /// <summary>
    /// Begins an operation: snapshots the repo's ref state now, and captures the post-state
    /// (persisting a <see cref="JournalEntry"/>) when the returned scope is disposed. Pass
    /// <paramref name="undoBlockedReason"/> for non-undoable ops (push, stash pop) so they are
    /// journaled and flagged rather than silently dropped.
    /// </summary>
    IDisposable BeginOperation(string repoPath, string kind, string description, string? undoBlockedReason = null);

    /// <summary>Most-recent-first history for a repository.</summary>
    IReadOnlyList<JournalEntry> GetHistory(string repoPath, int take = 100);

    /// <summary>
    /// Restores the repository to the entry's pre-operation ref state. Refuses (typed,
    /// mutating nothing) if the working tree is dirty in a way a reset would clobber, or if
    /// the entry is not undoable.
    /// </summary>
    void Undo(string repoPath, long entryId);

    /// <summary>Re-applies the entry's post-operation ref state.</summary>
    void Redo(string repoPath, long entryId);
}
