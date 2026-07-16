using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GitLoom.Core.Agents.Bootstrap;

namespace GitLoom.Core.Daemon;

/// <summary>
/// Resolves the daemon session token ACROSS the host/VM boundary (audit fix). The daemon writes its
/// token where <em>it</em> runs — <c>~/.gitloom/daemon.token</c>, which in the shipped topology is
/// <b>inside the GitLoomEnv VM</b> — while the Windows client used to read only
/// <c>%LocalAppData%\GitLoom\daemon.token</c>, a file nothing writes on a real install, so every RPC
/// failed at token read and the control center could never authenticate.
///
/// <para>This locator owns the candidate set:</para>
/// <list type="number">
///   <item>The local per-user file (<see cref="DaemonPaths.TokenFilePath"/>) — a <c>--local-dev</c>
///   daemon on the same OS.</item>
///   <item>On Windows: the in-VM daemon's file over the 9P bridge,
///   <c>\\wsl.localhost\GitLoomEnv\home\gitloom\.gitloom\daemon.token</c>. (Touching that path also
///   wakes the distro, which is desirable — systemd then brings <c>gitloomd</c> up.)</item>
/// </list>
///
/// <para>When several candidates exist (a dev machine running both topologies), the <b>freshest</b>
/// file wins: the daemon rotates its token on every start, so the most recently written token belongs
/// to the daemon that most recently claimed loopback :5250. The client re-reads per call, so a daemon
/// restart heals on the next RPC.</para>
/// </summary>
public static class DaemonTokenLocator
{
    /// <summary>The VM user whose home holds the daemon state (the tarball's default user).</summary>
    public const string VmUserName = "gitloom";

    /// <summary>The candidate token files for this OS, in declaration order (selection is by
    /// freshest write, not list order).</summary>
    public static IReadOnlyList<string> CandidatePaths()
    {
        var candidates = new List<string> { DaemonPaths.TokenFilePath() };
        if (OperatingSystem.IsWindows())
        {
            candidates.Add(VmTokenUncPath());
        }

        return candidates;
    }

    /// <summary>The Windows-facing UNC path of the in-VM daemon's token file.</summary>
    public static string VmTokenUncPath(string distroName = WslCommands.DistroName, string vmUser = VmUserName)
        => $@"\\wsl.localhost\{distroName}\home\{vmUser}\.gitloom\daemon.token";

    /// <summary>
    /// Reads the current session token from the freshest existing candidate, or <c>null</c> when no
    /// candidate exists / none is readable. Never throws — a missing daemon is a state, not a fault.
    /// </summary>
    public static string? TryReadToken(IReadOnlyList<string>? candidates = null)
    {
        var best = (candidates ?? CandidatePaths())
            .Select(path =>
            {
                try
                {
                    var info = new FileInfo(path);
                    return info.Exists ? (Path: path, Stamp: info.LastWriteTimeUtc) : default;
                }
                catch
                {
                    return default; // an unreachable UNC candidate is simply not a candidate right now
                }
            })
            .Where(c => c.Path is not null)
            .OrderByDescending(c => c.Stamp)
            .FirstOrDefault();

        if (best.Path is null)
        {
            return null;
        }

        try
        {
            var token = File.ReadAllText(best.Path).Trim();
            return token.Length > 0 ? token : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Reads the current session token, throwing an actionable <see cref="InvalidOperationException"/>
    /// (naming every path probed) when no candidate holds one — the error a failed RPC surfaces
    /// instead of a bare <c>FileNotFoundException</c>.
    /// </summary>
    public static string ReadToken(IReadOnlyList<string>? candidates = null)
    {
        var resolved = candidates ?? CandidatePaths();
        return TryReadToken(resolved) ?? throw new InvalidOperationException(
            "No GitLoom daemon session token was found — the daemon has probably never started. "
            + $"Paths probed: {string.Join(", ", resolved)}. "
            + "Run GitLoom setup (or start gitloomd) and try again.");
    }
}
