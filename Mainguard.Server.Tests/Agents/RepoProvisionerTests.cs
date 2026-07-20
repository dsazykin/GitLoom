using System.IO;
using Mainguard.Agents.Agents;
using Mainguard.Server.Tests.Fixtures;
using Xunit;

namespace Mainguard.Server.Tests.Agents;

/// <summary>
/// TI-P2-06 provisioner tests on the <see cref="DualRepoFixture"/> (Linux CI leg): first-run
/// bare mirror, incremental fetch, manual-delete re-clone, and path-with-spaces/Unicode.
/// </summary>
public sealed class RepoProvisionerTests
{
    [Fact]
    public void Provision_FirstRun_CreatesHardenedBareMirror_AtSha256Path()
    {
        using var fixture = new DualRepoFixture();
        var vmRoot = AgentTestGit.NewVmRoot();
        try
        {
            var provisioner = new RepoProvisioner(vmRoot);

            var result = provisioner.Provision(fixture.WorkRepoPath);

            var expectedHash = RepoPathHasher.Hash(fixture.WorkRepoPath);
            Assert.Equal(expectedHash, result.RepoHash);
            Assert.Equal(Path.Combine(vmRoot, "repos", expectedHash + ".git"), result.BareRepoPath);
            Assert.True(Directory.Exists(result.BareRepoPath));

            // Bare repo.
            Assert.Equal("true", AgentTestGit.RunChecked(result.BareRepoPath, "rev-parse", "--is-bare-repository").Trim());
            // core.untrackedCache set from the template.
            Assert.Equal("true", AgentTestGit.RunChecked(result.BareRepoPath, "config", "core.untrackedCache").Trim());
            // Mirror hardened: non-FF and deletes denied (§3.4).
            Assert.Equal("true", AgentTestGit.RunChecked(result.BareRepoPath, "config", "receive.denyNonFastForwards").Trim());
            Assert.Equal("true", AgentTestGit.RunChecked(result.BareRepoPath, "config", "receive.denyDeletes").Trim());
        }
        finally
        {
            AgentTestGit.DeleteTree(vmRoot);
        }
    }

    [Fact]
    public void Provision_SecondRun_FetchesIncrementally_NotReclone()
    {
        using var fixture = new DualRepoFixture();
        var vmRoot = AgentTestGit.NewVmRoot();
        try
        {
            var provisioner = new RepoProvisioner(vmRoot);
            var first = provisioner.Provision(fixture.WorkRepoPath);

            // Drop a marker inside the objects dir; a re-clone would wipe it, a fetch preserves it.
            var marker = Path.Combine(first.BareRepoPath, "objects", "gitloom-not-recloned.marker");
            File.WriteAllText(marker, "keep");

            // A new commit on the source must arrive via the incremental fetch.
            var newSha = fixture.Commit("second.txt", "second\n", "second commit");

            var second = provisioner.Provision(fixture.WorkRepoPath);

            Assert.Equal(first.BareRepoPath, second.BareRepoPath);
            Assert.True(File.Exists(marker)); // no re-clone happened
            // The fetched mirror now contains the new commit object...
            Assert.Equal("commit",
                AgentTestGit.RunChecked(second.BareRepoPath, "cat-file", "-t", newSha).Trim());
            // ...and the branch head actually advanced to it (not just objects in FETCH_HEAD).
            var headRef = AgentTestGit.RunChecked(second.BareRepoPath, "symbolic-ref", "--short", "HEAD").Trim();
            Assert.Equal(newSha, AgentTestGit.RunChecked(second.BareRepoPath, "rev-parse", headRef).Trim());
        }
        finally
        {
            AgentTestGit.DeleteTree(vmRoot);
        }
    }

    [Fact]
    public void Provision_BareRepoManuallyDeleted_ReclonesCleanly()
    {
        using var fixture = new DualRepoFixture();
        var vmRoot = AgentTestGit.NewVmRoot();
        try
        {
            var provisioner = new RepoProvisioner(vmRoot);
            var first = provisioner.Provision(fixture.WorkRepoPath);

            AgentTestGit.DeleteTree(first.BareRepoPath);
            Assert.False(Directory.Exists(first.BareRepoPath));

            var second = provisioner.Provision(fixture.WorkRepoPath);

            Assert.True(Directory.Exists(second.BareRepoPath));
            Assert.Equal("true", AgentTestGit.RunChecked(second.BareRepoPath, "rev-parse", "--is-bare-repository").Trim());
        }
        finally
        {
            AgentTestGit.DeleteTree(vmRoot);
        }
    }

    [Fact]
    public void Provision_PathWithSpacesAndUnicode_HashesAndProvisionsCorrectly()
    {
        using var fixture = new DualRepoFixture();
        var vmRoot = AgentTestGit.NewVmRoot();

        // Copy the fixture work repo into a source dir whose name has spaces + Unicode.
        var spacey = Path.Combine(vmRoot, "Ünï cödé repo with spaces");
        CopyDir(fixture.WorkRepoPath, spacey);

        try
        {
            var provisioner = new RepoProvisioner(vmRoot);

            var result = provisioner.Provision(spacey);

            Assert.Equal(RepoPathHasher.Hash(spacey), result.RepoHash);
            Assert.True(Directory.Exists(result.BareRepoPath));

            // Idempotent: a second provision of the same path does not error and keeps one mirror.
            var again = provisioner.Provision(spacey);
            Assert.Equal(result.BareRepoPath, again.BareRepoPath);
        }
        finally
        {
            AgentTestGit.DeleteTree(vmRoot);
        }
    }

    private static void CopyDir(string source, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var dir in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(dir.Replace(source, dest));
        }

        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            File.Copy(file, file.Replace(source, dest), overwrite: true);
        }
    }
}
