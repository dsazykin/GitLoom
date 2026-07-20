using System;
using System.IO;
using System.Runtime.InteropServices;

namespace Mainguard.Git;

/// <summary>
/// The single source of truth for Mainguard's per-user data root — every component that persists
/// per-user state (keyring, SQLite, settings, daemon token, adapter cache, OOBE state) resolves
/// its directory through here instead of calling <see cref="Environment.GetFolderPath(Environment.SpecialFolder)"/>
/// directly.
///
/// <para><b>Why this exists (the daemon crash-loop class of bug):</b> on Unix,
/// <c>GetFolderPath(..., SpecialFolderOption.None)</c> — the default — VERIFIES the directory exists
/// (an <c>access(2)</c> check) and returns <c>""</c> when it doesn't. A fresh service account's home
/// has no <c>~/.local/share</c>, so every <c>Path.Combine(GetFolderPath(LocalApplicationData), "Mainguard")</c>
/// silently produced the RELATIVE path <c>Mainguard/…</c>, which resolved against the process CWD
/// (<c>/</c> or a root-owned dir) → <c>EACCES</c> → unhandled exception → systemd
/// restart loop. Setting <c>HOME</c> did not help, because the subdirectory still did not exist.
/// Resolution here uses <c>DoNotVerify</c>, falls back to <c>$HOME</c>, and FAILS LOUDLY with a
/// named cause rather than ever returning an empty or relative path.</para>
///
/// <para><b>Locations:</b> <c>%LocalAppData%\Mainguard</c> on Windows; <c>~/.mainguard</c> elsewhere
/// (the same directory that holds <c>daemon.token</c> — one place to look in the VM). The in-VM daemon
/// identity is <c>/home/mainguard/.mainguard</c> (the systemd unit's <c>Environment=HOME</c>).</para>
/// </summary>
public static class MainguardPaths
{
    /// <summary>The current Windows data-root folder name under <c>%LocalAppData%</c>.</summary>
    private const string WindowsFolder = "Mainguard";

    /// <summary>
    /// Mainguard's per-user data root. Always absolute; never verified-to-exist (callers create what
    /// they need). Throws <see cref="InvalidOperationException"/> with an actionable message when
    /// the base cannot be resolved — a service context must set <c>HOME</c> (or run as a user with
    /// a passwd entry) rather than let a relative path escape.
    /// </summary>
    public static string DataRoot()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return Path.Combine(WindowsLocalAppData(), WindowsFolder);
        }

        return Path.Combine(HomeDirectory(), ".mainguard");
    }

    /// <summary>Resolves <c>%LocalAppData%</c> (DoNotVerify), or throws the actionable error.</summary>
    private static string WindowsLocalAppData()
    {
        var localAppData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolderOption.DoNotVerify);
        if (string.IsNullOrEmpty(localAppData) || !Path.IsPathRooted(localAppData))
        {
            throw new InvalidOperationException(
                "Cannot resolve %LocalAppData% — Mainguard's data root is unknown. " +
                $"(Resolved value: '{localAppData}'.)");
        }

        return localAppData;
    }

    /// <summary>
    /// The user's home directory, resolved robustly for BOTH interactive and service contexts:
    /// <c>GetFolderPath(UserProfile, DoNotVerify)</c> (env <c>HOME</c>, then the passwd entry —
    /// without the exists-check that turns a never-materialized home into <c>""</c>), then
    /// <c>$HOME</c> directly. Never returns an empty or relative path — throws with the systemd
    /// remedy instead.
    /// </summary>
    public static string HomeDirectory()
    {
        var home = Environment.GetFolderPath(
            Environment.SpecialFolder.UserProfile,
            Environment.SpecialFolderOption.DoNotVerify);
        if (string.IsNullOrEmpty(home))
        {
            home = Environment.GetEnvironmentVariable("HOME");
        }

        if (string.IsNullOrEmpty(home) || !Path.IsPathRooted(home))
        {
            throw new InvalidOperationException(
                "Cannot resolve the user home directory: HOME is not set and no passwd entry resolved. " +
                "A service running mainguardd must set it explicitly (systemd: Environment=HOME=/home/mainguard). " +
                $"(Resolved value: '{home}'.)");
        }

        return home;
    }
}
