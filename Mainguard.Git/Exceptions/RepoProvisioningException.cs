namespace Mainguard.Git.Exceptions;

/// <summary>
/// A P2-06 daemon-side provisioning/worktree Git operation failed (a non-zero
/// <c>git</c> exit from clone/fetch/config/worktree/remote). Carries the redacted
/// stderr the shared <c>RunGit</c> primitive already scrubbed of URL credentials.
/// Domain error-mapping only — it wraps the one audited runner, it is not a second one.
/// </summary>
public class RepoProvisioningException : GitLoomException
{
    public RepoProvisioningException(string message) : base(message) { }
    public RepoProvisioningException(string message, System.Exception inner) : base(message, inner) { }
}
