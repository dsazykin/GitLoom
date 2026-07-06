namespace GitLoom.Core.Models;

/// <summary>
/// The rolled-up state of a submodule as GitLoom presents it (T-16). Derived from
/// LibGit2Sharp's granular <c>SubmoduleStatus</c> flags by the pure
/// <see cref="GitLoom.Core.Services.SubmoduleStatusMapper"/> so the four values map
/// 1:1 to what the user needs to act on:
/// <list type="bullet">
///   <item><see cref="Uninitialized"/> — recorded in the superproject but not checked out
///   (fresh clone, empty directory) → needs <c>submodule update --init</c>.</item>
///   <item><see cref="UpToDate"/> — checked out at exactly the recorded commit, clean tree.</item>
///   <item><see cref="Modified"/> — checked out (or staged) at a <b>different commit</b> than the
///   superproject records → the recorded pointer is stale.</item>
///   <item><see cref="Dirty"/> — at the recorded commit but the submodule working tree has
///   uncommitted / untracked changes inside it.</item>
/// </list>
/// </summary>
public enum SubmoduleState
{
    Uninitialized,
    UpToDate,
    Modified,
    Dirty
}

/// <summary>
/// One entry from the superproject's submodule set (T-16): its path, configured URL, the
/// commit the superproject records for it, and the rolled-up <see cref="SubmoduleState"/>.
/// A plain immutable data type — reads come from <c>repo.Submodules</c>, mutations from the
/// git CLI (per the policy split), and the status is computed by the pure mapper.
/// </summary>
public sealed class SubmoduleItem
{
    /// <summary>Path of the submodule relative to the superproject working directory.</summary>
    public string Path { get; init; } = "";

    /// <summary>The submodule's configured URL (from <c>.gitmodules</c>); empty when unknown.</summary>
    public string Url { get; init; } = "";

    /// <summary>The commit SHA the superproject's index/HEAD records for the submodule (null when unset).</summary>
    public string? HeadSha { get; init; }

    /// <summary>The rolled-up state — see <see cref="SubmoduleState"/>.</summary>
    public SubmoduleState Status { get; init; }
}
