namespace Mainguard.Git.Exceptions;

/// <summary>
/// The hardened container spec could not be built because a mandatory security invariant
/// would be violated (P2-07): a Windows/UNC mount source (G-11), a missing G2 control
/// (seccomp denylist / <c>CAP_SYS_PTRACE</c> present / supervisor-uid == agent-uid), or a
/// secret that would land in the environment (G-13). Raised at <b>construction</b> so the
/// container is never created — the hardening is enforced, not merely inspected after.
/// </summary>
public sealed class SandboxSpecException : MainguardException
{
    public SandboxSpecException(string message) : base(message) { }
}
