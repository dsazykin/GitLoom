using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mainguard.Agents.Agents;
using Mainguard.Git.Exceptions;
using Mainguard.Server.Tests.Fixtures;
using Xunit;

namespace Mainguard.Server.Tests.Agents;

/// <summary>
/// TI-P2-06 worktree + quarantine-remote tests on the <see cref="DualRepoFixture"/>: add/remove/
/// prune round-trip, duplicate-id + dirty-remove typed failures, quarantine-only remotes, the
/// agent-push-lands-in-bare invariant, the byte-identical Windows↔VM round-trip, the pnpm hook,
/// and the SC-2 resolved-name path.
/// </summary>
public sealed class AgentWorktreeManagerTests
{
    [Fact]
    public void Worktree_AddRemovePrune_RoundTrip()
    {
        using var env = new WorktreeEnv();
        var hash = env.Provision();

        var path = env.Worktrees.CreateAgentWorktree(hash, "a1");
        Assert.True(Directory.Exists(path));

        // Listed via the porcelain parser; the agent branch exists.
        var listed = env.Worktrees.List(hash);
        Assert.Contains(listed, w => w.Branch == "agent/a1");
        Assert.Equal("commit",
            AgentTestGit.RunChecked(env.BarePath(hash), "cat-file", "-t", "refs/heads/agent/a1").Trim());

        env.Worktrees.RemoveAgentWorktree(hash, "a1", force: false);
        env.Worktrees.Prune(hash);

        Assert.False(Directory.Exists(path));
        Assert.DoesNotContain(env.Worktrees.List(hash), w => w.Branch == "agent/a1");
        // The agent branch is gone (no residue).
        Assert.NotEqual(0, AgentTestGit.Run(env.BarePath(hash), "rev-parse", "--verify", "--quiet", "refs/heads/agent/a1").Code);
    }

    [Fact]
    public void Worktree_DuplicateAgentId_ThrowsTyped_NoResidue()
    {
        using var env = new WorktreeEnv();
        var hash = env.Provision();

        env.Worktrees.CreateAgentWorktree(hash, "a1");
        var before = env.Worktrees.List(hash).Count;

        Assert.Throws<AgentWorktreeConflictException>(() => env.Worktrees.CreateAgentWorktree(hash, "a1"));

        // No new worktree/branch left behind by the refused call.
        Assert.Equal(before, env.Worktrees.List(hash).Count);
    }

    [Fact]
    public void Worktree_DirtyRemove_ForceSemantics()
    {
        using var env = new WorktreeEnv();
        var hash = env.Provision();
        var path = env.Worktrees.CreateAgentWorktree(hash, "a1");

        // Make the worktree dirty.
        File.WriteAllText(Path.Combine(path, "dirty.txt"), "uncommitted\n");

        Assert.Throws<AgentWorktreeConflictException>(() => env.Worktrees.RemoveAgentWorktree(hash, "a1", force: false));
        Assert.True(Directory.Exists(path)); // refused, still there

        env.Worktrees.RemoveAgentWorktree(hash, "a1", force: true);
        Assert.False(Directory.Exists(path)); // force cleans
    }

    [Fact]
    public void QuarantineRemote_IsExactlyTheDaemonBareRepo()
    {
        using var env = new WorktreeEnv();
        var hash = env.Provision();
        var path = env.Worktrees.CreateAgentWorktree(hash, "a1");
        var bare = env.BarePath(hash);

        // Exactly one configured remote, named origin, pointing at the bare mirror.
        var remotes = AgentTestGit.RunChecked(path, "remote").Trim()
            .Split('\n', System.StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(new[] { "origin" }, remotes);

        var originUrl = AgentTestGit.RunChecked(path, "remote", "get-url", "origin").Trim();
        Assert.Equal(bare, originUrl);

        // Not the user's real remote (the fixture's work repo or its separate mirror).
        Assert.NotEqual(env.Fixture.WorkRepoPath, originUrl);
        Assert.NotEqual(env.Fixture.BareMirrorPath, originUrl);

        // The mirror itself denies rewrites/deletes.
        Assert.Equal("true", AgentTestGit.RunChecked(bare, "config", "receive.denyNonFastForwards").Trim());
        Assert.Equal("true", AgentTestGit.RunChecked(bare, "config", "receive.denyDeletes").Trim());
    }

    [Fact]
    public void AgentPush_LandsInBareRepo_NeverUpstream()
    {
        using var env = new WorktreeEnv();
        var hash = env.Provision();
        var path = env.Worktrees.CreateAgentWorktree(hash, "a1");

        // The fixture's separate mirror stands in for the user's real remote.
        var upstreamBefore = DualRepoFixture.CaptureRefState(env.Fixture.BareMirrorPath);

        AgentTestGit.SetIdentity(path);
        File.WriteAllText(Path.Combine(path, "agent.txt"), "from-agent\n");
        AgentTestGit.RunChecked(path, "add", "agent.txt");
        AgentTestGit.RunChecked(path, "commit", "-m", "agent work");
        AgentTestGit.RunChecked(path, "push", "origin", "agent/a1");

        // The push moved the quarantine bare's ref...
        Assert.Equal("commit",
            AgentTestGit.RunChecked(env.BarePath(hash), "cat-file", "-t", "refs/heads/agent/a1").Trim());
        // ...and left the "real remote" completely untouched.
        var upstreamAfter = DualRepoFixture.CaptureRefState(env.Fixture.BareMirrorPath);
        Assert.Equal(upstreamBefore, upstreamAfter);
    }

    [Fact]
    public void WindowsVm_CommitRoundTrip_ByteIdentical()
    {
        using var env = new WorktreeEnv();
        var hash = env.Provision();
        var path = env.Worktrees.CreateAgentWorktree(hash, "a1");
        var content = "round-trip payload\n";

        AgentTestGit.SetIdentity(path);
        File.WriteAllText(Path.Combine(path, "agent.txt"), content);
        AgentTestGit.RunChecked(path, "add", "agent.txt");
        AgentTestGit.RunChecked(path, "commit", "-m", "agent round trip");
        var agentSha = AgentTestGit.RunChecked(path, "rev-parse", "HEAD").Trim();
        AgentTestGit.RunChecked(path, "push", "origin", "agent/a1");

        // Windows side: register the SC-2-resolved sync remote and fetch + merge the agent branch.
        var remote = env.Env.ResolveSyncRemote(hash);
        AgentTestGit.Run(env.Fixture.WorkRepoPath, "remote", "remove", remote.Name); // idempotent
        AgentTestGit.RunChecked(env.Fixture.WorkRepoPath, "remote", "add", remote.Name, remote.Url);
        AgentTestGit.RunChecked(env.Fixture.WorkRepoPath, "fetch", remote.Name);
        AgentTestGit.RunChecked(env.Fixture.WorkRepoPath, "merge", "--ff-only", $"{remote.Name}/agent/a1");

        // The merged commit is byte-identical (same SHA), and the blob matches.
        var windowsSha = AgentTestGit.RunChecked(env.Fixture.WorkRepoPath, "rev-parse", "HEAD").Trim();
        Assert.Equal(agentSha, windowsSha);
        Assert.Equal(content, File.ReadAllText(Path.Combine(env.Fixture.WorkRepoPath, "agent.txt")));
    }

    [Fact]
    public void SyncRemote_NameIsResolvedNotHardcoded_RoundTripUsesCloudName()
    {
        using var env = new WorktreeEnv(syncRemoteName: "gitloom-cloud");
        var hash = env.Provision();
        var path = env.Worktrees.CreateAgentWorktree(hash, "a1");

        var remote = env.Env.ResolveSyncRemote(hash);
        Assert.Equal("gitloom-cloud", remote.Name); // the resolved name, not a hardcoded gitloom-vm

        AgentTestGit.SetIdentity(path);
        File.WriteAllText(Path.Combine(path, "agent.txt"), "cloud\n");
        AgentTestGit.RunChecked(path, "add", "agent.txt");
        AgentTestGit.RunChecked(path, "commit", "-m", "cloud round trip");
        AgentTestGit.RunChecked(path, "push", "origin", "agent/a1");

        AgentTestGit.RunChecked(env.Fixture.WorkRepoPath, "remote", "add", remote.Name, remote.Url);
        AgentTestGit.RunChecked(env.Fixture.WorkRepoPath, "fetch", remote.Name);
        AgentTestGit.RunChecked(env.Fixture.WorkRepoPath, "merge", "--ff-only", $"{remote.Name}/agent/a1");

        Assert.Equal("cloud\n", File.ReadAllText(Path.Combine(env.Fixture.WorkRepoPath, "agent.txt")));
    }

    [Fact]
    public void Pnpm_InstallFailure_NonFatal_WorktreeStillCreated()
    {
        using var fixture = new DualRepoFixture();
        var vmRoot = AgentTestGit.NewVmRoot();
        try
        {
            // Seed a lockfile so the pnpm hook fires.
            fixture.Commit("pnpm-lock.yaml", "lockfileVersion: '9.0'\n", "add lockfile");

            var warnings = new List<string>();
            var provisioner = new RepoProvisioner(vmRoot);
            var worktrees = new WorktreeManager(
                vmRoot,
                pnpmRunner: _ => (1, "boom"),      // simulate failure
                warningSink: warnings.Add);

            var hash = provisioner.Provision(fixture.WorkRepoPath).RepoHash;
            var path = worktrees.CreateAgentWorktree(hash, "a1");

            Assert.True(Directory.Exists(path));                    // still created
            Assert.Contains(warnings, w => w.Contains("pnpm"));     // warning surfaced
        }
        finally
        {
            AgentTestGit.DeleteTree(vmRoot);
        }
    }

    [Fact]
    public void Pnpm_Install_RunsOnlyWhenLockfilePresent()
    {
        using var fixture = new DualRepoFixture();
        var vmRoot = AgentTestGit.NewVmRoot();
        try
        {
            string? ranIn = null;
            var provisioner = new RepoProvisioner(vmRoot);
            var worktrees = new WorktreeManager(vmRoot, pnpmRunner: dir => { ranIn = dir; return (0, string.Empty); });

            // No lockfile in the seed repo → the hook does not fire.
            var hash = provisioner.Provision(fixture.WorkRepoPath).RepoHash;
            worktrees.CreateAgentWorktree(hash, "a1");
            Assert.Null(ranIn);

            // Commit a lockfile, re-provision (incremental fetch), then a new worktree fires the hook.
            fixture.Commit("pnpm-lock.yaml", "lockfileVersion: '9.0'\n", "add lockfile");
            provisioner.Provision(fixture.WorkRepoPath);
            var path = worktrees.CreateAgentWorktree(hash, "a2");

            Assert.Equal(path, ranIn); // issued, in the a2 worktree
        }
        finally
        {
            AgentTestGit.DeleteTree(vmRoot);
        }
    }

    /// <summary>A test substrate facade: real provisioner + worktree manager, resolvable remote name.</summary>
    private sealed class FakeAgentEnvironment : IAgentEnvironment
    {
        private readonly string _syncRemoteName;
        private readonly RepoProvisioner _provisioner;

        public FakeAgentEnvironment(string syncRemoteName, RepoProvisioner provisioner, IAgentWorktreeManager worktrees)
        {
            _syncRemoteName = syncRemoteName;
            _provisioner = provisioner;
            Repos = provisioner;
            Worktrees = worktrees;
        }

        public string SubstrateId => "test";
        public SubstrateCapabilities Capabilities { get; } = new(false, false, "ext4-native", "test");
        public IRepoProvisioner Repos { get; }
        public IAgentWorktreeManager Worktrees { get; }

        // P2-07 seam members: this worktree-only double never touches sandboxes/egress.
        public Mainguard.Agents.Agents.Sandbox.ISandboxEngine Sandboxes =>
            throw new System.NotSupportedException("FakeAgentEnvironment covers worktrees only.");
        public Mainguard.Agents.Agents.Sandbox.IEgressPolicy Egress =>
            throw new System.NotSupportedException("FakeAgentEnvironment covers worktrees only.");

        // Resolves to the LOCAL bare path (the test's "windows-facing" handle) under the given name.
        public SyncRemote ResolveSyncRemote(string repoHash)
            => new(_syncRemoteName, _provisioner.BareRepoPathFor(repoHash));
    }

    /// <summary>Bundles a fixture + temp VM root + wired services, cleaned up on dispose.</summary>
    private sealed class WorktreeEnv : System.IDisposable
    {
        private readonly string _vmRoot;

        public WorktreeEnv(string syncRemoteName = "gitloom-vm")
        {
            Fixture = new DualRepoFixture();
            _vmRoot = AgentTestGit.NewVmRoot();
            var provisioner = new RepoProvisioner(_vmRoot);
            Worktrees = new WorktreeManager(_vmRoot);
            Env = new FakeAgentEnvironment(syncRemoteName, provisioner, Worktrees);
        }

        public DualRepoFixture Fixture { get; }
        public IAgentEnvironment Env { get; }
        public WorktreeManager Worktrees { get; }

        public string Provision() => Env.Repos.Provision(Fixture.WorkRepoPath).RepoHash;
        public string BarePath(string hash) => Path.Combine(_vmRoot, "repos", hash + ".git");

        public void Dispose()
        {
            Fixture.Dispose();
            AgentTestGit.DeleteTree(_vmRoot);
        }
    }
}
