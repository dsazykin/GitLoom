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
/// <para><b>Locations:</b> <c>%LocalAppData%\Mainguard</c> on Windows; <c>~/.gitloom</c> elsewhere
/// (the same directory that holds <c>daemon.token</c> — one place to look in the VM).
/// <b>Phase-4 note:</b> the Windows data root migrated <c>GitLoom → Mainguard</c> (see
/// <see cref="MigrateLegacyWindowsDataRootOnce"/>). The Unix branch is DELIBERATELY still
/// <c>~/.gitloom</c>: it is the in-VM daemon identity (<c>/home/gitloom/.gitloom</c>, the systemd
/// unit's <c>Environment=HOME</c>), which moves only as part of the coordinated WSL-distro /
/// daemon (<c>GitLoomEnv</c>/<c>gitloomd</c>) re-identity migration — an owner decision still open.
/// Splitting them here is correct: the host data root migrates now; the VM identity does not.</para>
/// </summary>
public static class MainguardPaths
{
    /// <summary>The legacy Windows data-root folder name, migrated away from on first run (Phase 4).</summary>
    private const string LegacyWindowsFolder = "GitLoom";

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

        return Path.Combine(HomeDirectory(), ".gitloom");
    }

    /// <summary>
    /// Windows only, one-shot, best-effort: if the legacy <c>%LocalAppData%\GitLoom</c> data root
    /// exists and the new <c>%LocalAppData%\Mainguard</c> one does not, MOVE it (a single atomic
    /// rename on the same volume) so an upgrading install keeps its keyring / SQLite / settings /
    /// daemon token / OOBE state. Idempotent: a no-op once the new root exists or on a fresh install.
    /// Never throws — a migration hiccup must not block startup; the app then simply uses whichever
    /// root <see cref="DataRoot"/> resolves. Call ONCE, before anything opens a file under the root.
    /// </summary>
    /// <param name="log">Optional sink for the one-line migration outcome (provisioning log).</param>
    public static void MigrateLegacyWindowsDataRootOnce(Action<string>? log = null)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return;

        string localAppData;
        try
        {
            localAppData = WindowsLocalAppData();
        }
        catch (InvalidOperationException)
        {
            return; // base unresolvable — DataRoot() will throw the actionable error at the real call site.
        }

        TryMigrateDataRoot(
            Path.Combine(localAppData, LegacyWindowsFolder),
            Path.Combine(localAppData, WindowsFolder),
            log);
    }

    /// <summary>
    /// OS-agnostic core of the data-root migration, factored out so the move policy is unit-testable
    /// without a Windows <c>%LocalAppData%</c>. Moves <paramref name="legacyDir"/> to
    /// <paramref name="currentDir"/> only when the current dir does NOT yet exist and the legacy one
    /// does; a no-op otherwise. Returns <c>true</c> iff a move actually happened. Never throws.
    /// </summary>
    internal static bool TryMigrateDataRoot(string legacyDir, string currentDir, Action<string>? log)
    {
        try
        {
            if (Directory.Exists(currentDir))
                return false; // already migrated, or a fresh Mainguard install — leave it alone.
            if (!Directory.Exists(legacyDir))
                return false; // nothing to migrate (fresh install).

            Directory.Move(legacyDir, currentDir);
            log?.Invoke($"Migrated Mainguard data root '{legacyDir}' -> '{currentDir}'.");
            return true;
        }
        catch (Exception ex)
        {
            // Cross-volume move, a locked handle, an ACL denial: log and continue. The new root is
            // then created fresh by the first caller; the legacy dir is left untouched for recovery.
            log?.Invoke($"Data-root migration skipped ('{legacyDir}' -> '{currentDir}'): {ex.Message}");
            return false;
        }
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
                "A service running gitloomd must set it explicitly (systemd: Environment=HOME=/home/gitloom). " +
                $"(Resolved value: '{home}'.)");
        }

        return home;
    }
}
