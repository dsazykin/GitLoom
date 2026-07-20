namespace Mainguard.Git.Exceptions;

/// <summary>
/// Base type for every Git error Mainguard raises deliberately. Catching this
/// lets the UI distinguish an expected, user-actionable Git failure from an
/// unexpected crash, without string-matching exception messages.
/// </summary>
public class MainguardException : System.Exception
{
    public MainguardException(string message) : base(message) { }
    public MainguardException(string message, System.Exception inner) : base(message, inner) { }
}
