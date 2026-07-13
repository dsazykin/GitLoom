namespace GitLoom.Core.Exceptions;

/// <summary>
/// A P2-06 agent-worktree request was refused because it would collide with or
/// disturb existing state: the <c>agent/&lt;id&gt;</c> branch or the worktree path
/// already exists (duplicate agent id), or a non-forced removal was attempted on a
/// dirty worktree. Thrown BEFORE any mutation, so the caller is left with no residue.
/// </summary>
public class AgentWorktreeConflictException : GitLoomException
{
    public AgentWorktreeConflictException(string message) : base(message) { }
    public AgentWorktreeConflictException(string message, System.Exception inner) : base(message, inner) { }
}
