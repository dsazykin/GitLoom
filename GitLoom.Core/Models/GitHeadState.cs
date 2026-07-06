namespace GitLoom.Core.Models;

/// <summary>
/// A snapshot of the repository HEAD used to drive context-menu rules in the
/// commit graph (T-09): whether HEAD is attached to a branch, detached, or unborn,
/// and the commit it currently points at. Read through <see cref="Services.IGitService"/>
/// so the ViewModel never opens a native handle itself.
/// </summary>
public sealed class GitHeadState
{
    /// <summary>The SHA of the commit HEAD points at, or <c>null</c> when the branch is unborn.</summary>
    public string? Sha { get; init; }

    /// <summary>True when HEAD is detached (checked out at a raw commit/tag, not a branch tip).</summary>
    public bool IsDetached { get; init; }

    /// <summary>True when HEAD is unborn — a fresh repository with no commits yet.</summary>
    public bool IsUnborn { get; init; }

    /// <summary>The friendly name of the current branch when attached; <c>null</c> when detached or unborn.</summary>
    public string? CurrentBranchName { get; init; }
}
