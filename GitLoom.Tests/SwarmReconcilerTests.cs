using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GitLoom.Core.Agents;
using Mainguard.Git.Models;
using Xunit;

namespace GitLoom.Tests;

/// <summary>
/// P2-08 test contract #7/#8 — the boot swarm reconciler. Docker is the sole source of truth: dead
/// containers are pruned + marked Dead, orphan live containers adopt-or-stop per policy, and deleting
/// the on-disk expected table yields an identical outcome. No PID/lock-file reads anywhere.
/// </summary>
public class SwarmReconcilerTests
{
    private sealed class FakeWorktreeManager : IAgentWorktreeManager
    {
        public List<(string Repo, string Agent, bool Force)> Removed { get; } = new();

        public string CreateAgentWorktree(string repoHash, string agentId) => $"/wt/{repoHash}/{agentId}";

        public void RemoveAgentWorktree(string repoHash, string agentId, bool force) =>
            Removed.Add((repoHash, agentId, force));

        public void Prune(string repoHash) { }

        public IReadOnlyList<WorktreeItem> List(string repoHash) => Array.Empty<WorktreeItem>();
    }

    private static Func<CancellationToken, Task<IReadOnlyList<AgentContainerState>>> Docker(
        params AgentContainerState[] containers) => _ => Task.FromResult<IReadOnlyList<AgentContainerState>>(containers);

    private static AgentContainerState Live(string agentId, string repo = "repo1") =>
        new(agentId, repo, $"cid-{agentId}", Running: true);

    [Fact]
    public async Task DeadContainer_IsPrunedAndMarkedDead_LiveAgentsRetained()
    {
        var expected = new InMemoryExpectedAgentStore();
        expected.Upsert("repo1", "a1", "Live");
        expected.Upsert("repo1", "a2", "Live");
        expected.Upsert("repo1", "dead", "Live");
        var worktrees = new FakeWorktreeManager();

        var reconciler = new SwarmReconciler(
            Docker(Live("a1"), Live("a2")), expected, worktrees);

        var report = await reconciler.ReconcileAsync();

        Assert.Equal(new[] { "dead" }, report.Pruned);
        Assert.Contains(("repo1", "dead", true), worktrees.Removed); // pruned with force
        Assert.DoesNotContain(worktrees.Removed, r => r.Agent == "a1");

        var dead = expected.All().Single(a => a.AgentId == "dead");
        Assert.Equal("Dead", dead.Disposition);
        Assert.False(string.IsNullOrWhiteSpace(dead.DisposalReason));
        Assert.All(new[] { "a1", "a2" }, id =>
            Assert.NotEqual("Dead", expected.All().Single(a => a.AgentId == id).Disposition));
    }

    [Fact]
    public async Task OrphanLiveContainer_IsAdopted_UnderDefaultPolicy()
    {
        var expected = new InMemoryExpectedAgentStore();
        var reconciler = new SwarmReconciler(
            Docker(Live("orphan")), expected, new FakeWorktreeManager(), policy: OrphanPolicy.Adopt);

        var report = await reconciler.ReconcileAsync();

        Assert.Equal(new[] { "orphan" }, report.Adopted);
        Assert.Empty(report.Stopped);
        Assert.Equal("Adopted", expected.All().Single(a => a.AgentId == "orphan").Disposition);
    }

    [Fact]
    public async Task OrphanLiveContainer_IsStopped_UnderStopPolicy()
    {
        var expected = new InMemoryExpectedAgentStore();
        var stopped = new List<string>();

        var reconciler = new SwarmReconciler(
            Docker(Live("orphan")), expected, new FakeWorktreeManager(),
            stopContainer: (id, _) => { stopped.Add(id); return Task.CompletedTask; },
            policy: OrphanPolicy.Stop);

        var report = await reconciler.ReconcileAsync();

        Assert.Equal(new[] { "orphan" }, report.Stopped);
        Assert.Contains("cid-orphan", stopped);
        Assert.Empty(report.Adopted);
    }

    [Fact]
    public async Task RebootWith3Live1Dead_Adopts3Prunes1()
    {
        // The daemon's expected table survived only the (now dead) agent; Docker shows 3 live jails.
        var expected = new InMemoryExpectedAgentStore();
        expected.Upsert("repo1", "dead", "Live");
        var worktrees = new FakeWorktreeManager();

        var reconciler = new SwarmReconciler(
            Docker(Live("a1"), Live("a2"), Live("a3")), expected, worktrees);

        var report = await reconciler.ReconcileAsync();

        Assert.Equal(new[] { "dead" }, report.Pruned);
        Assert.Equal(new[] { "a1", "a2", "a3" }, report.Adopted.OrderBy(x => x).ToArray());
        Assert.Contains(("repo1", "dead", true), worktrees.Removed);
    }

    [Fact]
    public async Task ShouldTrustDockerOnly_DeletingExpectedState_YieldsIdenticalOutcome()
    {
        // Reboot with the on-disk expected table wiped: the outcome is driven purely by Docker.
        var wiped = new InMemoryExpectedAgentStore();
        var reconciler = new SwarmReconciler(
            Docker(Live("a1"), Live("a2"), Live("a3")), wiped, new FakeWorktreeManager());

        var report = await reconciler.ReconcileAsync();

        // Every live container is adopted; nothing is pruned because Docker is the truth.
        Assert.Empty(report.Pruned);
        Assert.Equal(new[] { "a1", "a2", "a3" }, report.Adopted.OrderBy(x => x).ToArray());
        Assert.Equal(3, wiped.All().Count);
        Assert.All(wiped.All(), a => Assert.Equal("Adopted", a.Disposition));
    }

    [Fact]
    public void BootSequence_RunsMergeReconcileBeforeSwarm_RtD1Ordering()
    {
        var reconciler = new SwarmReconciler(
            Docker(), new InMemoryExpectedAgentStore(), new FakeWorktreeManager());

        var sequence = DaemonBootSequence.Build(reconciler);

        // RT-D1: the merge-reconcile slot is FIRST (empty until P2-10), then the swarm reconcile.
        Assert.Equal(new[] { "merge-reconcile", "swarm-reconcile" }, sequence.TaskNames);
    }

    [Fact]
    public async Task BootSequence_RunsTasksInOrder()
    {
        var order = new List<string>();
        var sequence = new DaemonBootSequence(new IBootTask[]
        {
            new RecordingTask("first", order),
            new RecordingTask("second", order),
        });

        await sequence.RunAsync();

        Assert.Equal(new[] { "first", "second" }, order);
    }

    private sealed class RecordingTask : IBootTask
    {
        private readonly List<string> _order;

        public RecordingTask(string name, List<string> order)
        {
            Name = name;
            _order = order;
        }

        public string Name { get; }

        public Task RunAsync(CancellationToken ct)
        {
            _order.Add(Name);
            return Task.CompletedTask;
        }
    }
}
