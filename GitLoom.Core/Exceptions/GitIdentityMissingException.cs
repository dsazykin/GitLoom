namespace GitLoom.Core.Exceptions;

/// <summary>
/// Thrown when a Git operation needs a committer identity but no
/// <c>user.name</c> / <c>user.email</c> is configured (globally or locally).
/// The UI should catch this and prompt the user to set an identity rather
/// than committing under a bogus placeholder that would pollute history.
/// </summary>
public class GitIdentityMissingException : System.Exception
{
    public GitIdentityMissingException(string message) : base(message) { }
}
