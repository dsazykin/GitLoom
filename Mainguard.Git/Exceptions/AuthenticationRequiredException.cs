namespace Mainguard.Git.Exceptions;

/// <summary>
/// Thrown when a remote operation fails because valid credentials were not
/// available. The UI catches this to prompt for a token / passphrase. When
/// <see cref="Host"/> is set (T-14), the UI routes to the per-host PAT dialog for
/// that host instead of a generic notice.
/// </summary>
public class AuthenticationRequiredException : MainguardException
{
    /// <summary>The host that needs credentials (e.g. <c>github.com</c>), when known.</summary>
    public string? Host { get; }

    public AuthenticationRequiredException(string message) : base(message) { }
    public AuthenticationRequiredException(string message, System.Exception inner) : base(message, inner) { }

    public AuthenticationRequiredException(string message, string? host) : base(message)
    {
        Host = host;
    }

    public AuthenticationRequiredException(string message, string? host, System.Exception inner) : base(message, inner)
    {
        Host = host;
    }
}
