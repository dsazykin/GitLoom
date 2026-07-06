namespace GitLoom.Core.Models;

/// <summary>
/// One entry from <c>git worktree list --porcelain</c> (T-07). CLI-driven; the libgit2
/// worktree API is a locked "no" per the policy split.
/// </summary>
public sealed class WorktreeItem
{
    public string Path { get; init; } = "";
    public string? HeadSha { get; init; }
    public string? Branch { get; init; }   // friendly name, null when detached
    public bool IsDetached { get; init; }
    public bool IsLocked { get; init; }
    public bool IsMain { get; init; }       // first stanza in porcelain output
}
