namespace GitLoom.Core.Exceptions;

/// <summary>
/// General-purpose Git failure that does not map to a more specific type
/// (branch/commit not found, no upstream configured, a failed CLI fallback).
/// </summary>
public class GitOperationException : GitLoomException
{
    public GitOperationException(string message) : base(message) { }
    public GitOperationException(string message, System.Exception inner) : base(message, inner) { }
}
