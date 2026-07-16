using System;

namespace GitLoom.Core.Agents;

/// <summary>
/// Pure P2-06 helper: translates the HOST-side repo path the client sends (a Windows path like
/// <c>C:\Users\me\repo</c>) into the path the DAEMON can actually open from inside the WSL2 VM
/// (<c>/mnt/c/Users/me/repo</c>, the drvfs mount — acceptable for git object transfer, §P2-06 step 1).
///
/// <para><b>Why this exists:</b> the daemon runs on Linux, so <c>git clone --bare C:\Users\…</c> can
/// never resolve — the shipped Windows topology needs the drive-letter → <c>/mnt/&lt;drive&gt;</c>
/// translation, while the Linux CI leg (which passes native Linux paths as the "windows repo path")
/// must pass through untouched. The repo HASH stays derived from the caller's normalized path
/// (<see cref="RepoPathHasher"/>) — translation affects only what git is told to read, never the
/// mirror's identity.</para>
/// </summary>
public static class HostPathTranslator
{
    /// <summary>
    /// The git-openable form of <paramref name="hostPath"/> for the process we are running in:
    /// <list type="bullet">
    ///   <item>On Linux, a Windows drive-letter path (<c>C:\x</c> or <c>C:/x</c>) becomes
    ///   <c>/mnt/c/x</c> (drive lower-cased, separators unified — WSL's default drvfs layout).</item>
    ///   <item>Anything else (a native Linux path, a relative test path) passes through unchanged.</item>
    ///   <item>A UNC path (<c>\\server\share</c>) is refused with a typed failure — there is no
    ///   general WSL mount for UNC sources and silently mangling it would produce a nonsense clone
    ///   source.</item>
    /// </list>
    /// On Windows (a <c>--local-dev</c> daemon) every path passes through unchanged — the host and
    /// the daemon share one filesystem view there.
    /// </summary>
    public static string ToDaemonOpenablePath(string hostPath)
        => ToDaemonOpenablePath(hostPath, OperatingSystem.IsWindows());

    /// <summary>Testable core: <paramref name="daemonIsWindows"/> pins the branch under test.</summary>
    public static string ToDaemonOpenablePath(string hostPath, bool daemonIsWindows)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostPath);

        if (daemonIsWindows)
        {
            return hostPath; // local-dev daemon: same filesystem view as the client.
        }

        if (hostPath.StartsWith(@"\\", StringComparison.Ordinal) || hostPath.StartsWith("//", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"UNC repo sources are not supported by the VM provisioner (got '{hostPath}'). "
                + "Open the repository from a local drive path.", nameof(hostPath));
        }

        if (!IsWindowsDrivePath(hostPath))
        {
            return hostPath; // native Linux path (tests, CI, in-VM callers): already openable.
        }

        var drive = char.ToLowerInvariant(hostPath[0]);
        var rest = hostPath[2..].Replace('\\', '/').TrimStart('/');
        return rest.Length == 0 ? $"/mnt/{drive}" : $"/mnt/{drive}/{rest}";
    }

    /// <summary>True for <c>X:</c>, <c>X:\…</c>, <c>X:/…</c> (a Windows drive-letter path).</summary>
    public static bool IsWindowsDrivePath(string path)
        => path.Length >= 2
           && path[1] == ':'
           && char.IsAsciiLetter(path[0])
           && (path.Length == 2 || path[2] is '\\' or '/');
}
