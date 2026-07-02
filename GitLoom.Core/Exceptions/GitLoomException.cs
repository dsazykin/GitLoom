namespace GitLoom.Core.Exceptions;

/// <summary>
/// Base type for every Git error GitLoom raises deliberately. Catching this
/// lets the UI distinguish an expected, user-actionable Git failure from an
/// unexpected crash, without string-matching exception messages.
/// </summary>
public class GitLoomException : System.Exception
{
    public GitLoomException(string message) : base(message) { }
    public GitLoomException(string message, System.Exception inner) : base(message, inner) { }
}
