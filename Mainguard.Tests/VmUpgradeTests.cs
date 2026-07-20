using System;
using System.IO;
using System.Linq;
using Mainguard.Agents.Agents.Bootstrap;
using Xunit;

namespace Mainguard.Tests;

// TI-P2-21 #6 / plan §6 #6 + §4 edge row + invariant 3: the vN→vN+1 upgrade preserves provisioned
// repos/worktrees (automated on directory-level state), the plan never retires the old distro before
// migration is validated, and no upgrade builder emits the VM-wide shutdown verb (G-12).
public class VmUpgradeTests
{
    // ---- #6: repos + worktrees intact after the migration ----------------------------------------

    [Fact]
    public void Upgrade_PreservesReposAndWorktrees()
    {
        var root = NewTempDir();
        try
        {
            var oldData = Path.Combine(root, "old", "mainguard");
            var newData = Path.Combine(root, "staging", "mainguard");

            // Seed a provisioned layout: two bare repos + a worktree, with content.
            SeedRepo(Path.Combine(oldData, "repos", "acme.git"), "HEAD", "ref: refs/heads/main\n");
            SeedRepo(Path.Combine(oldData, "repos", "acme.git", "refs", "heads"), "main", "deadbeef\n");
            SeedRepo(Path.Combine(oldData, "worktrees", "agent-7"), "README.md", "agent work in progress\n");
            SeedRepo(Path.Combine(oldData, "worktrees", "agent-7", ".git"), "HEAD", "ref: refs/heads/feature\n");

            var expected = new[]
            {
                Path.Combine("repos", "acme.git", "HEAD"),
                Path.Combine("repos", "acme.git", "refs", "heads", "main"),
                Path.Combine("worktrees", "agent-7", "README.md"),
                Path.Combine("worktrees", "agent-7", ".git", "HEAD"),
            };

            VmUpgradeMigrator.Migrate(oldData, newData);
            var missing = VmUpgradeMigrator.FindMissing(newData, expected);

            Assert.Empty(missing);
            // Byte-fidelity: the worktree file content survives.
            Assert.Equal("agent work in progress\n",
                File.ReadAllText(Path.Combine(newData, "worktrees", "agent-7", "README.md")));
            Assert.Equal("deadbeef\n",
                File.ReadAllText(Path.Combine(newData, "repos", "acme.git", "refs", "heads", "main")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Upgrade_FindMissing_ReportsAbsentRepo()
    {
        var root = NewTempDir();
        try
        {
            var dest = Path.Combine(root, "staging");
            Directory.CreateDirectory(dest);
            var missing = VmUpgradeMigrator.FindMissing(dest, new[] { Path.Combine("repos", "gone.git", "HEAD") });
            Assert.Single(missing);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // ---- invariant 3 / §4 edge row: never retire the old distro before migration is validated -----

    [Fact]
    public void Upgrade_Plan_MigratesAndValidates_BeforeAnyOldDistroMutation()
    {
        var steps = VmUpgradePlan.Steps();

        var firstMutatingIndex = steps.ToList().FindIndex(s => s.MutatesOldDistro);
        var validateIndex = steps.ToList().FindIndex(s => s.Id == "validate-migration");
        var migrateIndex = steps.ToList().FindIndex(s => s.Id == "migrate-user-data");

        Assert.True(migrateIndex >= 0 && validateIndex >= 0 && firstMutatingIndex >= 0);
        Assert.True(migrateIndex < firstMutatingIndex, "must migrate before touching the old distro");
        Assert.True(validateIndex < firstMutatingIndex, "must validate the migration before retiring the old distro");
    }

    // ---- G-12: no upgrade builder emits the VM-wide shutdown verb; lifecycle is distro-scoped ------

    [Fact]
    public void Upgrade_Commands_NeverEmitShutdown_AndAreDistroScoped()
    {
        var ourDistros = new[] { "MainguardEnv", "MainguardEnv-staging" };
        foreach (var builder in VmUpgradeCommands.AllBuilders())
        {
            Assert.DoesNotContain("--shutdown", builder);

            // Every lifecycle verb names one of OUR two distros (never a user's, never VM-wide)…
            foreach (var verb in new[] { "--terminate", "--unregister", "--import", "--import-in-place" })
            {
                var i = builder.ToList().IndexOf(verb);
                if (i >= 0)
                    Assert.Contains(builder[i + 1], ourDistros);
            }

            // …and every in-distro command runs inside one of OUR two distros only.
            if (builder[0] == "-d")
                Assert.Contains(builder[1], ourDistros);
        }

        Assert.Equal(new[] { "--terminate", "MainguardEnv" }, VmUpgradeCommands.TerminateOld());
        Assert.Equal(new[] { "--unregister", "MainguardEnv" }, VmUpgradeCommands.UnregisterOld());
        Assert.Equal(new[] { "--terminate", "MainguardEnv-staging" }, VmUpgradeCommands.TerminateStaging());
        Assert.Equal(new[] { "--unregister", "MainguardEnv-staging" }, VmUpgradeCommands.UnregisterStaging());
        // Staging is imported alongside — the old distro is untouched at import time.
        Assert.Contains("MainguardEnv-staging", VmUpgradeCommands.ImportStaging(@"C:\x", @"C:\y.tar.gz"));
    }

    private static void SeedRepo(string dir, string file, string content)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, file), content);
    }

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mainguard-upgrade-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}
