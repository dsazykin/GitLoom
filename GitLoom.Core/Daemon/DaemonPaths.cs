using System.IO;

using Mainguard.Git;
namespace GitLoom.Core.Daemon;

/// <summary>
/// The single source of truth for the daemon's per-user session-token file location.
/// Shared by the server (which writes it) and the client (which reads it) so neither
/// hard-codes the other's path — and the client never references a server-only
/// assembly to learn it (P2-02 rejection trigger).
/// </summary>
public static class DaemonPaths
{
    public const int DefaultLoopbackPort = 5250;

    /// <summary>
    /// The per-user token file: <c>%LocalAppData%\GitLoom\daemon.token</c> on Windows,
    /// <c>~/.gitloom/daemon.token</c> elsewhere.
    /// </summary>
    // GitLoomPaths.DataRoot() is %LocalAppData%\GitLoom on Windows and ~/.gitloom elsewhere — the
    // exact locations this method always documented — and it is the hardened resolution path
    // (DoNotVerify + $HOME fallback + loud failure; a token path must never be relative).
    public static string TokenFilePath()
        => Path.Combine(GitLoomPaths.DataRoot(), "daemon.token");
}
