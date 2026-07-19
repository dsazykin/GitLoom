using System;

namespace Mainguard.Git.Models;

/// <summary>
/// One recorded mutating Git operation in the operation journal (T-19). Persisted to
/// SQLite via <c>AppDbContext</c>. The pre/post ref maps are serialized snapshots of
/// every ref (branches, tags, stash, remote-tracking) plus the HEAD symbolic target,
/// so an <c>Undo</c> can restore the exact ref state the operation moved away from and
/// a <c>Redo</c> can re-apply its result.
/// </summary>
public class JournalEntry
{
    /// <summary>Auto-increment primary key. Also the stable id used by Undo/Redo.</summary>
    public long Id { get; set; }

    /// <summary>The repository this operation ran against (working-directory path).</summary>
    public string RepoPath { get; set; } = string.Empty;

    /// <summary>Short operation kind, e.g. "Commit", "Merge", "DeleteBranch".</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>Human-readable description shown in the history list.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>When the operation was recorded (UTC).</summary>
    public DateTime WhenUtc { get; set; }

    /// <summary>Serialized <c>RefSnapshot</c> captured before the mutation ran.</summary>
    public string PreStateJson { get; set; } = string.Empty;

    /// <summary>Serialized <c>RefSnapshot</c> captured after the mutation ran.</summary>
    public string PostStateJson { get; set; } = string.Empty;

    /// <summary>False for ops that cannot be reversed (push, stash pop/apply/drop, remote-branch delete).</summary>
    public bool IsUndoable { get; set; } = true;

    /// <summary>Why the op is not undoable (only set when <see cref="IsUndoable"/> is false).</summary>
    public string? UndoBlockedReason { get; set; }

    /// <summary>True once the entry has been undone (and not yet redone).</summary>
    public bool IsUndone { get; set; }

    /// <summary>
    /// True once a newer operation superseded this entry while it was undone, i.e. the
    /// redo stack was truncated. A truncated entry can never be redone.
    /// </summary>
    public bool IsTruncated { get; set; }
}
