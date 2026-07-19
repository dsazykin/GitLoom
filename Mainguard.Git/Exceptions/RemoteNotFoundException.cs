namespace Mainguard.Git.Exceptions;

/// <summary>
/// Thrown when an operation references a remote that does not exist on the
/// repository (e.g. no <c>origin</c> configured).
/// </summary>
public class RemoteNotFoundException : GitLoomException
{
    public RemoteNotFoundException(string message) : base(message) { }
}
