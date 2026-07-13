using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace GitLoom.Core.Agents.Bootstrap;

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

    /// <summary>Import the new payload alongside the running distro (never touching the old one).</summary>
    public static IReadOnlyList<string> ImportStaging(string installDir, string tarballPath) =>
        new[] { "--import", StagingDistroName, installDir, tarballPath, "--version", "2" };

    /// <summary>Terminate the OLD distro (scoped) before retiring it — never the VM-wide shutdown verb.</summary>
    public static IReadOnlyList<string> TerminateOld() => new[] { "--terminate", WslCommands.DistroName };

    /// <summary>Unregister the OLD distro — only ever called AFTER migration is validated.</summary>
    public static IReadOnlyList<string> UnregisterOld() => new[] { "--unregister", WslCommands.DistroName };

    /// <summary>Promote the staging VHDX to the canonical distro name in place (WSL2
    /// <c>--import-in-place</c>), so the migrated data becomes <c>GitLoomEnv</c> without a re-clone.</summary>
    public static IReadOnlyList<string> PromoteStagingInPlace(string stagingVhdxPath) =>
        new[] { "--import-in-place", WslCommands.DistroName, stagingVhdxPath, "--version", "2" };

    /// <summary>Unregister staging's temporary registration while keeping its VHDX for the promote step.</summary>
    public static IReadOnlyList<string> UnregisterStaging() => new[] { "--unregister", StagingDistroName };

    /// <summary>Every builder — used by the G-12 test to prove none emit the VM-wide shutdown verb.</summary>
    public static IReadOnlyList<IReadOnlyList<string>> AllBuilders() => new[]
    {
        ImportStaging(@"C:\GitLoom\vm-staging", @"C:\GitLoom\gitloomos.tar.gz"),
        TerminateOld(),
        UnregisterOld(),
        PromoteStagingInPlace(@"C:\GitLoom\vm-staging\ext4.vhdx"),
        UnregisterStaging(),
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
}
