namespace GitLoom.Core.Exceptions;

/// <summary>
/// Raised when a step of the P2-05 <c>GitLoomOS</c> bootstrapper fails. Carries the
/// <see cref="StepName"/> of the failing stage so the progress UI can mark exactly that
/// stage failed and show its actionable message, without string-matching.
/// </summary>
public class BootstrapException : GitLoomException
{
    public BootstrapException(string message) : base(message) { }

    public BootstrapException(string? stepName, string message) : base(message)
    {
        StepName = stepName;
    }

    public BootstrapException(string? stepName, string message, System.Exception inner) : base(message, inner)
    {
        StepName = stepName;
    }

    /// <summary>The UI checklist label of the step that failed, when known.</summary>
    public string? StepName { get; }
}

/// <summary>
/// Raised when WSL2 itself is not present on the machine. The bootstrapper does NOT attempt
/// <c>wsl --install</c> — enablement is P2-21's installer flow — so this is an actionable, terminal
/// failure telling the user to run the installer's enablement step. Surfaced before any mutating act.
/// </summary>
public sealed class WslNotInstalledException : BootstrapException
{
    public const string DefaultMessage =
        "WSL2 is not installed or enabled. Run the Mainguard installer's setup step to enable WSL2, then retry. "
        + "(Mainguard never runs 'wsl --install' itself.)";

    public WslNotInstalledException() : base(DefaultMessage) { }

    public WslNotInstalledException(System.Exception inner) : base(null, DefaultMessage, inner) { }
}

/// <summary>
/// Raised when a <c>wsl.exe</c> invocation exits non-zero. Carries the exit code and captured
/// stderr so a step can wrap it in a typed <see cref="BootstrapException"/> naming the stage.
/// </summary>
public sealed class WslCommandException : GitLoomException
{
    public WslCommandException(int exitCode, string commandLine, string stderr)
        : base($"wsl {commandLine} failed with exit code {exitCode}.{(string.IsNullOrWhiteSpace(stderr) ? "" : " " + stderr.Trim())}")
    {
        ExitCode = exitCode;
        CommandLine = commandLine;
        Stderr = stderr;
    }

    public int ExitCode { get; }

    public string CommandLine { get; }

    public string Stderr { get; }
}
