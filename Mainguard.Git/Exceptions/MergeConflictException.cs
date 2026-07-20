namespace Mainguard.Git.Exceptions;

/// <summary>
/// Thrown when a merge, rebase, pull, or cherry-pick leaves the working tree
/// in a conflicted state. The UI catches this to route the user into the
/// conflict resolver rather than reporting a generic failure.
/// </summary>
public class MergeConflictException : MainguardException
{
    public MergeConflictException(string message) : base(message) { }
}
