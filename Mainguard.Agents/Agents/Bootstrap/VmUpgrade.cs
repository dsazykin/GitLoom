using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Mainguard.Agents.Agents.Bootstrap;

/// <summary>
/// The in-place VM upgrade (vN→vN+1) plumbing (P2-21 §3.6). Strategy: import the new payload
/// <b>alongside</b> as <see cref="StagingDistroName"/>, migrate the user's <c>~/gitloom</c>
/// (provisioned repos + worktrees) from the old distro into staging via a tar stream, validate, then
/// retire the old distro and promote staging to the canonical name. The old distro is <b>never</b>
/// unregistered before the migration is validated (the "preserve provisioned repos" invariant). Every
/// lifecycle verb is scoped to our own distros — G-12: never the VM-wide WSL shutdown verb.
/// </summary>
public static class VmUpgradeCommands
{
    /// <summary>The distro the new payload is imported to before promotion.</summary>
    public const string StagingDistroName = WslCommands.DistroName + "-staging";

    /// <summary>The user-data root inside the VM that MUST survive an upgrade.</summary>
    public const string UserDataPath = "/home/gitloom/gitloom";

    /// <summary>The daemon's own state root (SQLite DB, budgets/spend ledger, keyring, leader
    /// registry) — losing it on upgrade would silently zero the user's budget history, so it
    /// migrates alongside <see cref="UserDataPath"/>.</summary>
    public const string DaemonStatePath = "/home/gitloom/.gitloom";

    /// <summary>The session-token file inside <see cref="DaemonStatePath"/> — deliberately excluded
    /// from migration (the daemon writes a fresh one on every start; a stale copy would only
    /// mislead the client's token re-read).</summary>
    public const string DaemonTokenFileName = "daemon.token";

    /// <summary>The per-subsystem daemon logs dir inside <see cref="DaemonStatePath"/> — deliberately
    /// excluded from migration alongside the token: the logs are diagnostic, can be large, and the new
    /// distro should start with a fresh set rather than copying stale/oversized files across the
    /// tier-2 upgrade. Its absence in staging is therefore correct (see the validation filter).</summary>
    public const string DaemonLogsDirName = "logs";

    /// <summary>The release stamp inside the VM naming the installed payload version — the
    /// daemon-down fallback source for the upgrade-availability check.</summary>
    public const string InstalledReleaseStampPath = "/etc/gitloomos-release";

    /// <summary>Import the new payload alongside the running distro (never touching the old one).</summary>
    public static IReadOnlyList<string> ImportStaging(string installDir, string tarballPath) =>
        new[] { "--import", StagingDistroName, installDir, tarballPath, "--version", "2" };

    /// <summary>Terminate the OLD distro (scoped) before retiring it — never the VM-wide shutdown verb.</summary>
    public static IReadOnlyList<string> TerminateOld() => new[] { "--terminate", WslCommands.DistroName };

    /// <summary>Unregister the OLD distro — only ever called AFTER migration is validated.</summary>
    public static IReadOnlyList<string> UnregisterOld() => new[] { "--unregister", WslCommands.DistroName };

    /// <summary>Terminate the STAGING distro (scoped) so its VHDX unlocks before the promote.</summary>
    public static IReadOnlyList<string> TerminateStaging() => new[] { "--terminate", StagingDistroName };

    /// <summary>Promote the staging VHDX to the canonical distro name in place (WSL2
    /// <c>--import-in-place</c>), so the migrated data becomes <c>GitLoomEnv</c> without a re-clone.</summary>
    public static IReadOnlyList<string> PromoteStagingInPlace(string stagingVhdxPath) =>
        new[] { "--import-in-place", WslCommands.DistroName, stagingVhdxPath, "--version", "2" };

    /// <summary>Unregister staging's temporary registration while keeping its VHDX for the promote
    /// step — only ever issued AFTER the VHDX has been moved out of staging's install dir
    /// (unregistering deletes whatever is still there).</summary>
    public static IReadOnlyList<string> UnregisterStaging() => new[] { "--unregister", StagingDistroName };

    /// <summary>An in-STAGING command as root: <c>wsl -d GitLoomEnv-staging -u root -- &lt;cmd…&gt;</c>
    /// (the staging twin of <see cref="WslCommands.InDistroAsRoot"/>).</summary>
    public static IReadOnlyList<string> InStagingAsRoot(params string[] command) =>
        new[] { "-d", StagingDistroName, "-u", "root", "--" }.Concat(command).ToArray();

    /// <summary>Stops staging's own auto-started daemon before data lands there — booting staging
    /// (any in-staging command) starts systemd, whose shipped-enabled <c>gitloomd</c> would
    /// otherwise be writing its own fresh DB while the migrated one is extracted over it.</summary>
    public static IReadOnlyList<string> StopUnitInStaging() =>
        InStagingAsRoot("systemctl", "stop", DaemonUpdateCommands.UnitName);

    /// <summary>Exit 0 iff <paramref name="path"/> is a directory in the OLD distro — the
    /// "anything to migrate?" probe (a fresh VM may have no user data yet).</summary>
    public static IReadOnlyList<string> ProbeOldDirectory(string path) =>
        WslCommands.InDistroAsRoot("test", "-d", path);

    /// <summary>Packs a tree in the OLD distro into a tar archive at <paramref name="vmTarPath"/> —
    /// a <c>/mnt/…</c>-translated host temp file, so the bytes cross distros via the host disk:
    /// binary-safe and unbounded, with no shell pipe and no string-typed stdin/stdout round-trip
    /// (see <see cref="VmUpgradeOrchestrator"/> for why the host-file transport was chosen).</summary>
    public static IReadOnlyList<string> ExportTreeToTar(string treePath, string vmTarPath, bool excludeDaemonToken = false) =>
        excludeDaemonToken
            ? WslCommands.InDistroAsRoot(
                "tar", "-C", treePath,
                "--exclude=./" + DaemonTokenFileName,
                "--exclude=./" + DaemonLogsDirName,
                "-cpf", vmTarPath, ".")
            : WslCommands.InDistroAsRoot("tar", "-C", treePath, "-cpf", vmTarPath, ".");

    /// <summary>Creates the destination tree root inside STAGING before the extract.</summary>
    public static IReadOnlyList<string> MakeStagingDirectory(string path) =>
        InStagingAsRoot("mkdir", "-p", path);

    /// <summary>Unpacks the migrated tree into STAGING from the host-side tar archive.</summary>
    public static IReadOnlyList<string> ExtractTreeFromTar(string treePath, string vmTarPath) =>
        InStagingAsRoot("tar", "-C", treePath, "-xpf", vmTarPath);

    /// <summary>Re-owns the migrated tree for the service user (tar ran as root; uid/gid match
    /// between payload builds, but the chown makes the invariant explicit).</summary>
    public static IReadOnlyList<string> ChownTreeInStaging(string treePath) =>
        InStagingAsRoot("chown", "-R", "gitloom:gitloom", treePath);

    /// <summary>Enumerates a tree in the OLD distro (repos at depth 2, worktrees at depth 3) — the
    /// expected-set half of the migration validation. Depth-bounded so the listing stays proportional
    /// to provisioned repos/worktrees, not to every object file inside them.</summary>
    public static IReadOnlyList<string> EnumerateOldTree(string treePath) =>
        WslCommands.InDistroAsRoot("find", treePath, "-mindepth", "1", "-maxdepth", "3");

    /// <summary>Enumerates the same tree inside STAGING — the present-set half of the validation.
    /// One spawn per tree instead of one <c>test -e</c> per path (the per-path spawn-burst class
    /// drove the WSL service into <c>Wsl/Service/E_UNEXPECTED</c>; see HealthCheckStep).</summary>
    public static IReadOnlyList<string> EnumerateStagingTree(string treePath) =>
        InStagingAsRoot("find", treePath, "-mindepth", "1", "-maxdepth", "3");

    /// <summary>Reads the installed payload's release stamp from the OLD distro — the
    /// daemon-down fallback for the upgrade-availability check.</summary>
    public static IReadOnlyList<string> ReadInstalledReleaseStamp() =>
        WslCommands.InDistro("cat", InstalledReleaseStampPath);

    /// <summary>Every builder — used by the G-12 test to prove none emit the VM-wide shutdown verb
    /// and every verb/command is scoped to <c>GitLoomEnv</c> / <c>GitLoomEnv-staging</c> only.</summary>
    public static IReadOnlyList<IReadOnlyList<string>> AllBuilders() => new[]
    {
        ImportStaging(@"C:\GitLoom\vm-staging", @"C:\GitLoom\gitloomos.tar.gz"),
        TerminateOld(),
        UnregisterOld(),
        TerminateStaging(),
        PromoteStagingInPlace(@"C:\GitLoom\vm-staging\ext4.vhdx"),
        UnregisterStaging(),
        InStagingAsRoot("true"),
        StopUnitInStaging(),
        ProbeOldDirectory(UserDataPath),
        ExportTreeToTar(UserDataPath, "/mnt/c/tmp/user-data.tar"),
        ExportTreeToTar(DaemonStatePath, "/mnt/c/tmp/daemon-state.tar", excludeDaemonToken: true),
        MakeStagingDirectory(UserDataPath),
        ExtractTreeFromTar(UserDataPath, "/mnt/c/tmp/user-data.tar"),
        ChownTreeInStaging(UserDataPath),
        EnumerateOldTree(UserDataPath),
        EnumerateStagingTree(UserDataPath),
        ReadInstalledReleaseStamp(),
    };
}

/// <summary>An ordered upgrade step (for the plan + the ordering-invariant test).</summary>
/// <param name="Id">Stable id.</param>
/// <param name="MutatesOldDistro">True only for the retire steps that touch the old distro.</param>
public sealed record VmUpgradeStep(string Id, string Description, bool MutatesOldDistro);

/// <summary>The canonical, ordered upgrade plan. The invariant test asserts every data-migration step
/// precedes any step that mutates/retires the old distro.</summary>
public static class VmUpgradePlan
{
    public static IReadOnlyList<VmUpgradeStep> Steps() => new[]
    {
        new VmUpgradeStep("import-staging", "Import the new GitLoomOS payload as GitLoomEnv-staging.", false),
        new VmUpgradeStep("migrate-user-data", "Stream ~/gitloom (repos + worktrees) from old → staging.", false),
        new VmUpgradeStep("validate-migration", "Verify every repo/worktree is present in staging.", false),
        new VmUpgradeStep("terminate-old", "Terminate the old GitLoomEnv distro (scoped).", true),
        new VmUpgradeStep("unregister-old", "Unregister the old GitLoomEnv distro.", true),
        new VmUpgradeStep("promote-staging", "Import-in-place the staging VHDX as GitLoomEnv.", true),
    };
}

/// <summary>
/// The pure, filesystem-level migration + validation logic (the automatable core of upgrade test #6).
/// The real upgrade streams a tar between distros; this class performs and validates the equivalent
/// directory-tree migration so "provisioned repos/worktrees preserved" is an automated cross-platform
/// test, not only a manual VM-snapshot matrix.
/// </summary>
public static class VmUpgradeMigrator
{
    /// <summary>Copies the entire user-data tree (repos + worktrees, including <c>.git</c>) from
    /// <paramref name="sourceRoot"/> to <paramref name="destRoot"/>, preserving relative structure and
    /// file bytes. Returns the set of migrated repo directory names for validation.</summary>
    public static void Migrate(string sourceRoot, string destRoot)
    {
        if (!Directory.Exists(sourceRoot))
            throw new DirectoryNotFoundException($"Upgrade source '{sourceRoot}' does not exist.");
        Directory.CreateDirectory(destRoot);

        foreach (var dir in Directory.EnumerateDirectories(sourceRoot, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(sourceRoot, dir);
            Directory.CreateDirectory(Path.Combine(destRoot, rel));
        }

        foreach (var file in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(sourceRoot, file);
            var target = Path.Combine(destRoot, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    /// <summary>Validates that every path in <paramref name="expectedRelativePaths"/> (repos/worktrees
    /// and their key files) exists under <paramref name="destRoot"/>. Returns the missing set — empty
    /// means the migration preserved everything.</summary>
    public static IReadOnlyList<string> FindMissing(string destRoot, IEnumerable<string> expectedRelativePaths) =>
        expectedRelativePaths
            .Where(rel => !File.Exists(Path.Combine(destRoot, rel)) && !Directory.Exists(Path.Combine(destRoot, rel)))
            .ToList();

    /// <summary>The in-distro form of <see cref="FindMissing(string, IEnumerable{string})"/>: the
    /// pure set difference between two <c>find</c> listings taken inside the old and staging distros
    /// (see <see cref="VmUpgradeCommands.EnumerateOldTree"/>). Both listings are newline-separated
    /// absolute in-VM paths; the roots are identical in both distros, so no relativization is
    /// needed. Empty result = every enumerated repo/worktree path is present in staging.</summary>
    public static IReadOnlyList<string> FindMissingFromListings(string oldListing, string stagingListing) =>
        FindMissingFromListings(ParseFindListing(oldListing), ParseFindListing(stagingListing));

    /// <summary>Sequence form (used by the orchestrator after filtering deliberately-unmigrated
    /// entries — the rotating daemon token — out of the expected set).</summary>
    public static IReadOnlyList<string> FindMissingFromListings(
        IEnumerable<string> expectedPaths, IEnumerable<string> presentPaths)
    {
        var present = new HashSet<string>(presentPaths, StringComparer.Ordinal);
        return expectedPaths.Where(p => !present.Contains(p)).ToList();
    }

    /// <summary>Parses a <c>find</c> listing into trimmed, non-empty lines.</summary>
    public static IReadOnlyList<string> ParseFindListing(string listing) =>
        string.IsNullOrEmpty(listing)
            ? Array.Empty<string>()
            : listing.Split('\n').Select(l => l.TrimEnd('\r').Trim()).Where(l => l.Length > 0).ToList();
}

/// <summary>Pure parser for the <c>gitloomos-release</c> stamp (both the app-bundled copy the
/// packaging step co-locates at <c>payload/gitloomos-release</c> and the in-VM
/// <c>/etc/gitloomos-release</c>) — the same line shape the daemon's <c>DaemonInfoProvider</c>
/// reads server-side; duplicated here because Core never references the server assembly.</summary>
public static class GitLoomOsReleaseStamp
{
    private const string VersionKey = "GITLOOMOS_VERSION=";

    /// <summary>The value of the first <c>GITLOOMOS_VERSION=</c> line, trimmed; empty when the key
    /// (or the content) is missing.</summary>
    public static string ParseVersion(string? releaseFileContent)
    {
        if (string.IsNullOrEmpty(releaseFileContent))
        {
            return string.Empty;
        }

        foreach (var raw in releaseFileContent.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith(VersionKey, StringComparison.Ordinal))
            {
                return line[VersionKey.Length..].Trim();
            }
        }

        return string.Empty;
    }
}

/// <summary>
/// The pure tier-2 offer decision: is an in-place VM upgrade worth offering? Offered ONLY when the
/// installed payload is provably OLDER than the app-expected payload — a proper version compare,
/// never string inequality, so a user running a NEWER payload than the app (e.g. after an app
/// rollback) is never offered a downgrade, and a garbled/absent version on either side means no
/// offer (an upgrade that replaces the distro must never run on guesswork).
/// </summary>
public static class VmUpgradePolicy
{
    public static bool IsUpgradeAvailable(string? installedVersion, string? expectedVersion)
    {
        var installed = TryParseVersion(installedVersion);
        var expected = TryParseVersion(expectedVersion);
        return installed is not null && expected is not null && installed < expected;
    }

    /// <summary>Tolerant SemVer-ish parse: strips build metadata (<c>+…</c>) and a pre-release
    /// suffix (<c>-…</c>), then requires a dotted numeric core. Null for garbage/blank input.</summary>
    public static Version? TryParseVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return null;
        }

        var core = version.Trim();
        var plus = core.IndexOf('+');
        if (plus >= 0)
        {
            core = core[..plus];
        }

        var dash = core.IndexOf('-');
        if (dash >= 0)
        {
            core = core[..dash];
        }

        return Version.TryParse(core, out var parsed) ? parsed : null;
    }
}
