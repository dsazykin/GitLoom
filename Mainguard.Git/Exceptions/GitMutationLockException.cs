namespace Mainguard.Git.Exceptions;

/// <summary>
/// A P2-09 keep-alive Git mutation could not proceed because the worktree's
/// <c>.git/index.lock</c> stayed held across the bounded exponential-backoff retry window
/// (base 100 ms, ×2, capped attempts). Because the agent is yielded/paused while the daemon
/// mutates the worktree, the lock is transient by design — a persistent one is a typed failure,
/// not something to retry forever. Distinct from the ADR-001 in-repo retry: this is the agent-side
/// worktree lock the cooperative-yield protocol is meant to have already quiesced.
/// </summary>
public sealed class GitMutationLockException : GitLoomException
{
    public GitMutationLockException(string message) : base(message) { }

    public GitMutationLockException(string message, System.Exception inner) : base(message, inner) { }
}
