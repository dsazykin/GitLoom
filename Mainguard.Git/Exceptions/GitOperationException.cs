namespace Mainguard.Git.Exceptions;

/// <summary>
/// General-purpose Git failure that does not map to a more specific type
/// (branch/commit not found, nothing to amend, a failed CLI fallback, etc.).
/// </summary>
public class GitOperationException : GitLoomException
{
    public GitOperationException(string message) : base(message) { }
    public GitOperationException(string message, System.Exception inner) : base(message, inner) { }
}
