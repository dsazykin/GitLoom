using System;
using System.IO;
using System.Runtime.InteropServices;

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
    public static string TokenFilePath()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(localAppData, "GitLoom", "daemon.token");
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".gitloom", "daemon.token");
    }
}
