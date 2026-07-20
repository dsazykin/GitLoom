using LibGit2Sharp;
using Mainguard.Git.Models;

namespace Mainguard.Git.Services;

/// <summary>
/// Pure mapper from LibGit2Sharp's granular <see cref="SubmoduleStatus"/> flag set to the
/// four-value <see cref="SubmoduleState"/> GitLoom presents (T-16). No repo/IO — unit-testable
/// against every flag combination. The only place the flag semantics are interpreted, so the
/// mapping and its precedence are pinned by tests rather than re-derived at each call site.
/// </summary>
public static class SubmoduleStatusMapper
{
    // The submodule's own working tree has uncommitted content (index dirty, modified
    // tracked files, or untracked files) — the "-dirty" suffix git appends.
    private const SubmoduleStatus DirtyMask =
        SubmoduleStatus.WorkDirFilesIndexDirty
        | SubmoduleStatus.WorkDirFilesModified
        | SubmoduleStatus.WorkDirFilesUntracked;

    // The checked-out (or staged) submodule commit differs from what the superproject records
    // — a pointer change git surfaces with the "+" prefix in `git submodule status`. Covers a
    // staged bump (Index*) and a working-tree checkout at another commit (WorkDir* other than
    // uninitialized), including add/delete of the gitlink.
    private const SubmoduleStatus CommitChangedMask =
        SubmoduleStatus.IndexAdded
        | SubmoduleStatus.IndexDeleted
        | SubmoduleStatus.IndexModified
        | SubmoduleStatus.WorkDirAdded
        | SubmoduleStatus.WorkDirDeleted
        | SubmoduleStatus.WorkDirModified;

    /// <summary>
    /// Rolls <paramref name="status"/> up to a single <see cref="SubmoduleState"/>. Precedence,
    /// highest first: <b>Uninitialized</b> (not checked out — nothing else is meaningful yet) →
    /// <b>Modified</b> (recorded commit is stale — the superproject-significant change) → <b>Dirty</b>
    /// (uncommitted work inside a submodule that is otherwise at the recorded commit) → <b>UpToDate</b>.
    /// </summary>
    public static SubmoduleState Map(SubmoduleStatus status)
    {
        // Not initialized: git reports an empty workdir directory, or the gitlink is simply
        // absent from the working tree (in config/head but never checked out — a fresh clone).
        if (status.HasFlag(SubmoduleStatus.WorkDirUninitialized)
            || !status.HasFlag(SubmoduleStatus.InWorkDir))
        {
            return SubmoduleState.Uninitialized;
        }

        if ((status & CommitChangedMask) != 0)
            return SubmoduleState.Modified;

        if ((status & DirtyMask) != 0)
            return SubmoduleState.Dirty;

        return SubmoduleState.UpToDate;
    }
}
