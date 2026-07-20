namespace Mainguard.Git.Exceptions;

public class SshAuthenticationException : System.Exception
{
    public SshAuthenticationException(string message) : base(message) { }
}
