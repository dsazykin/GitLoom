namespace GitLoom.Core.Exceptions;

/// <summary>
/// Thrown when a remote operation fails because valid credentials were not
/// available. The UI catches this to prompt for a token / passphrase.
/// </summary>
public class AuthenticationRequiredException : GitLoomException
{
    public AuthenticationRequiredException(string message) : base(message) { }
    public AuthenticationRequiredException(string message, System.Exception inner) : base(message, inner) { }
}
